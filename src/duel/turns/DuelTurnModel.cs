using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// Which turn model this match is played under, and the one place anything asks.
///
/// **Read off the run's modifiers, not stored.** `DuelMatch.IsTurnBased` already answers from the
/// modifiers the lobby agreed and the run carries, so a second copy of that state could only drift
/// from it — the mistake `DuelEndReason` exists to prevent one level down. The model object is
/// cached per run rather than per call because it is about to hold a buffer.
///
/// **`IsTurnBased` was read in exactly one place before this — a log line.** DESIGN §7 flagged that
/// as the consequence of shipping the modifier before the model: the lobby has offered
/// `1v1 Duel: Turn-Based` since M6 and picking it has played blitz. This is the beginning of that
/// being untrue.
/// </summary>
public static class DuelTurnModel
{
    private static IDuelTurnModel? _current;

    /// <summary>The model for the run in progress, built on first use and released with the run.</summary>
    public static IDuelTurnModel Current
    {
        get
        {
            if (_current != null)
            {
                return _current;
            }

            _current = Build(RunManager.Instance?.State);
            Log.Warn($"[SpirePvp] turn model: {_current.Name}");
            return _current;
        }
    }

    /// <summary>Released with the run, like every other piece of static match state.</summary>
    public static void Reset() => _current = null;

    /// <summary>Convenience for the one patch that asks per action.</summary>
    public static bool ShouldDefer(GameAction action) =>
        DuelSession.IsDuelActive && Current.ShouldDefer(action);

    private static IDuelTurnModel Build(IRunState? runState)
    {
        // Turn-based is not built yet, so a run configured for it still plays blitz — which is the
        // state DESIGN §7 recorded and accepted, and this is where that ends when the lock-in model
        // lands. Logged rather than silent, because "I picked turn-based and got blitz" is
        // otherwise indistinguishable from the modifier not being read at all.
        if (DuelMatch.IsTurnBased(runState))
        {
            Log.Warn("[SpirePvp] turn model: turn-based was selected, but the lock-in model is not "
                     + "built yet — playing blitz (DESIGN §3.1b, M8)");
        }

        return new BlitzTurnModel();
    }
}
