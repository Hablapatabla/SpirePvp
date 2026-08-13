using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// Dying during the race loses the match, which nothing was arbitrating.
///
/// Reported 2026-08-12: *"I died to the boss and it didn't end the run, giving the opponent the
/// victory."* Exactly right, and the cause is a route rather than a mechanic — `DuelResult.Arm()` is
/// called from `DuelArena`, i.e. on **arena entry**, so for the whole of the race nothing at all was
/// watching a duelist die. The match simply carried on with a corpse in it.
///
/// **Why vanilla does not end the run for you.** `CreatureCmd.Kill` gates the game-over screen on
/// `runState.Players.All(p =&gt; p.Creature.IsDead)` — a party wipe. In a race the opponent is alive
/// and off in their own combat, so the party has not wiped and the run continues by vanilla's own
/// correct rule. This is the same "the engine assumes the party is co-located" family that has
/// produced a bug in every race-phase room; here it produces one in the *ending*.
///
/// # Why this declares locally rather than asking the host
///
/// It mirrors `DuelResign` deliberately, and for the same reason: **your death in a race is a fact
/// only you can see.** The two runs are decoupled, your opponent is not in your combat, and no
/// checksum or state sync covers a room they are not standing in — so "a message the peer needs must
/// be sent, not looked up". The dying client broadcasts the result and declares its own loss, which
/// is exactly the shape resignation already uses and is proven on the wire.
///
/// That is not a breach of "the host decides outcomes": the host-authority rule exists so two sims
/// cannot reach *different* conclusions from shared state. Here there is no shared state to disagree
/// about — one client observed a death the other could not have — so the only client able to report
/// it is the one it happened to.
///
/// # On `CombatEnded` rather than polling for a dead creature
///
/// `IsDead` goes true the moment the blow lands, and revival effects run *inside* combat — so a
/// probe that watched the flag would declare a loss for a player about to stand back up. Waiting for
/// the combat to end costs nothing and makes the death final before it is acted on. It also means
/// this reads the same event `DuelResult` reads, one phase earlier.
/// </summary>
public static class DuelRaceDeath
{
    private static bool _armed;
    private static bool _declared;

    /// <summary>
    /// Armed at run start with every other handler — never lazily, and never at arena entry, which
    /// is the mistake this class exists to correct. The race is precisely the window it has to cover.
    /// </summary>
    public static void Arm()
    {
        if (_armed)
        {
            return;
        }

        CombatManager.Instance.CombatEnded += OnCombatEnded;

        // **`CombatEnded` is not enough, and the first build proved it.** Measured 2026-08-13: the
        // host died to the Waterfall Giant and the combat simply never ended — vanilla auto-readies
        // a dead player every turn (`Setting player 1 to ready at start of turn. IsDead: True`) and
        // goes on waiting for the rest of the party, who in a race are not in this combat at all.
        // So turns kept passing over a corpse and nothing was ever raised. The telemetry probe
        // caught it exactly: `resultArmed=False` beside a dead duelist, then that turn-start line
        // repeating.
        //
        // `TurnStarted` is the event that *does* keep firing there, and a death is final by the time
        // a new turn begins — revival effects resolve inside the turn that killed you.
        CombatManager.Instance.TurnStarted += OnTurnStarted;
        _armed = true;
    }

    public static void Disarm()
    {
        if (!_armed)
        {
            return;
        }

        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        CombatManager.Instance.TurnStarted -= OnTurnStarted;
        _armed = false;
        _declared = false;
    }

    private static void OnTurnStarted(CombatState state) => CheckForDeath();

    private static void OnCombatEnded(CombatRoom room) => CheckForDeath();

    private static void CheckForDeath()
    {
        // The duel owns its own ending (`DuelResult`), including a death in the arena. This is the
        // race's half of the same question and must not answer for both.
        if (_declared || DuelSession.IsDuelActive)
        {
            return;
        }

        RunManager? run = RunManager.Instance;
        IRunState? state = run?.State;
        if (run == null || state == null || !DuelMatch.IsPvpRun(state))
        {
            return;
        }

        // A run already over, or one being abandoned, has an ending of its own on the way. Guard on
        // the condition rather than on the routes into it — there is always another route.
        if (state.IsGameOver || run.IsAbandoned)
        {
            return;
        }

        Player? me = LocalContext.GetMe(state);
        if (me?.Creature.IsDead != true)
        {
            return;
        }

        ulong opponent = 0;
        foreach (Player player in state.Players)
        {
            if (player.NetId != me.NetId)
            {
                opponent = player.NetId;
                break;
            }
        }

        if (opponent == 0)
        {
            // Nobody to award it to. Leave the run alone rather than declaring a win no one receives.
            Log.Warn("[SpirePvp] race death: no opponent found — leaving the run to vanilla");
            return;
        }

        _declared = true;
        Log.Warn($"[SpirePvp] race death: died before reaching the arena — {opponent} wins");

        // Broadcast first, declare second: declaring runs `RunManager.OnEnded`, and doing that before
        // the message is on the wire risks the teardown taking the transport with it. Same ordering
        // as `DuelResign`, and for the same reason.
        run.NetService.SendMessage(new DuelResultMessage
        {
            winnerId = opponent,
            reason = DuelEndReason.RaceDeath
        });

        DuelResult.DeclareWinner(localPlayerWon: false, DuelEndReason.RaceDeath);
    }
}
