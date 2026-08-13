using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpirePvp.Duel;

/// <summary>
/// Ends the duel on a result screen instead of dumping you back into the run.
///
/// Vanilla only shows the game-over screen when the whole party wipes —
/// CreatureCmd.Kill gates it on `runState.Players.All(p =&gt; p.Creature.IsDead)`. Exactly one
/// duelist dies, so the screen never appeared and the winner was silently returned to the
/// map. Both halves of the trigger are public, so no patch is needed:
///
///   SerializableRun run = RunManager.Instance.OnEnded(isVictory);
///   NRun.Instance.ShowGameOverScreen(run);
///
/// Each client decides its own result from its own local player, so the winner sees victory
/// and the loser sees defeat off the same combat outcome, with no extra message.
///
/// Hooks CombatManager.CombatEnded rather than patching EndCombatInternal: that method is
/// async, so a Harmony postfix would run when the state machine is created rather than when
/// combat has actually finished.
///
/// M6 replaces this with a proper DuelResultScreen (per-round damage, rematch). This reuses
/// the vanilla screen with rewritten text — see DuelResultBannerPatch.
/// </summary>
public static class DuelResult
{
    private static bool _subscribed;

    /// <summary>
    /// Whether anything is watching for the end of a fight. Read by `DuelTelemetry`: this is armed
    /// from `DuelArena`, i.e. on arena entry, so it is false for the whole race — which is what a
    /// death during the race has to be diagnosed against.
    /// </summary>
    public static bool IsArmed => _subscribed;

    /// <summary>Called when a duel begins so we can catch the end of it.</summary>
    public static void Arm()
    {
        if (_subscribed)
        {
            return;
        }

        CombatManager.Instance.CombatEnded += OnCombatEnded;
        _subscribed = true;
    }

    public static void Disarm()
    {
        if (!_subscribed)
        {
            return;
        }

        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        _subscribed = false;
    }

    private static void OnCombatEnded(CombatRoom room)
    {
        Disarm();
        ShowFor(room.CombatState);
    }

    /// <summary>
    /// Declares the duel over and puts up the result screen. Called from
    /// DuelEndCombatPatch, which replaces vanilla's EndCombatInternal, and idempotent so the
    /// CombatEnded fallback cannot show the screen twice.
    /// </summary>
    public static void ShowFor(ICombatState combatState)
    {
        Disarm();

        if (!DuelSession.IsDuelActive)
        {
            return;
        }

        DeclareWinner(LocalPlayerSurvived(combatState), DuelEndReason.Hp);
    }

    /// <summary>
    /// Ends the duel with an explicit result. Used by the HP path (via ShowFor) and by
    /// DuelFlag when someone loses on time. Idempotent — once the duel is Complete, later
    /// calls are ignored, so a flag landing at the same moment as a kill cannot show two
    /// screens or overwrite the first result.
    /// </summary>
    public static void DeclareWinner(bool localPlayerWon, int reason = DuelEndReason.Hp) =>
        Declare(localPlayerWon ? DuelOutcome.Won : DuelOutcome.Lost, reason);

    /// <summary>
    /// Ends the match with no winner — either the race deadline passing with nobody at the
    /// arena, or both players agreeing. See <see cref="DuelOutcome"/>.
    /// </summary>
    public static void DeclareDraw(int reason) => Declare(DuelOutcome.Draw, reason);

    /// <summary>
    /// Why the match ended, for anything that has to describe it rather than merely score it.
    ///
    /// **The outcome is not the reason, and the result screen needs both.** Two very different
    /// endings share `DuelOutcome.Draw` — the race deadline passing with neither player at the
    /// arena, and the two of you shaking hands — and the banner was wording every draw as the
    /// first, so an agreed draw read "Time ran out before either of you reached the arena."
    /// That is the same shape as the mistake DuelClockService and DuelFlag both made: asking a
    /// question that correlates with the one you mean instead of the one you mean.
    ///
    /// The codes already existed as a wire format on DuelResultMessage; this simply keeps the
    /// one that applies locally, so the screen can say what happened.
    /// </summary>
    public static int EndReason { get; private set; } = DuelEndReason.Hp;

    private static void Declare(DuelOutcome outcome, int reason)
    {
        if (DuelSession.Phase == DuelPhase.Complete)
        {
            return;
        }

        Disarm();
        EndReason = reason;
        DuelSession.CompleteDuel(outcome);
        Log.Warn($"[SpirePvp] duel over — {outcome.ToString().ToUpperInvariant()}");

        // The match is decided, so the clocks stop for good. Without this they fell out of the
        // duel's chess-clock rule the moment the phase left DuelActive, resumed under the race's
        // "both simply run" rule, and the host went on broadcasting ClockSyncMessage twice a
        // second at a peer that had already torn its run down — hundreds of "no message handlers
        // are registered" errors on the client and "not connected" on the host. Stopping also
        // freezes the final values, which is what you want to read off the result screen.
        DuelClockService.Stop();

        // Before OnEnded, and that ordering is the whole trick. The result screen wants to
        // compare both players' numbers, but the run teardown that follows disposes the net
        // service — so a stats broadcast sent afterwards goes into a dead transport, which is
        // the failure already fixed twice here (the clocks, then the resignation). Every route
        // to a result passes through this method, so sending here covers HP, flag, resignation
        // and both kinds of draw without enumerating them.
        DuelStats.Broadcast();

        // OnEnded writes the run history the screen reads; isVictory drives which banner
        // DuelResultBannerPatch then rewrites. A draw is not a victory — the banner text is
        // corrected from DuelSession.Outcome, not from this flag.
        SerializableRun run = RunManager.Instance.OnEnded(outcome == DuelOutcome.Won);
        NRun.Instance?.ShowGameOverScreen(run);
    }

    private static bool LocalPlayerSurvived(ICombatState combatState)
    {
        Player? me = LocalContext.GetMe(combatState);
        if (me != null)
        {
            return !me.Creature.IsDead;
        }

        // Fallback: if the local player can't be identified, treat any survivor as a loss
        // rather than handing out a win we can't justify.
        foreach (Creature creature in combatState.PlayerCreatures)
        {
            if (creature.IsAlive && LocalContext.IsMe(creature.Player))
            {
                return true;
            }
        }

        return false;
    }
}
