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

        DeclareWinner(LocalPlayerSurvived(combatState));
    }

    /// <summary>
    /// Ends the duel with an explicit result. Used by the HP path (via ShowFor) and by
    /// DuelFlag when someone loses on time. Idempotent — once the duel is Complete, later
    /// calls are ignored, so a flag landing at the same moment as a kill cannot show two
    /// screens or overwrite the first result.
    /// </summary>
    public static void DeclareWinner(bool localPlayerWon)
    {
        if (DuelSession.Phase == DuelPhase.Complete)
        {
            return;
        }

        Disarm();
        DuelSession.CompleteDuel(localPlayerWon);
        Log.Warn($"[SpirePvp] duel over — local player {(localPlayerWon ? "WON" : "LOST")}");

        // OnEnded writes the run history the screen reads; isVictory drives which banner
        // DuelResultBannerPatch then rewrites.
        SerializableRun run = RunManager.Instance.OnEnded(localPlayerWon);
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
