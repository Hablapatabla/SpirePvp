using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Duel;
using SpirePvp.Net;

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
    public static void Reset()
    {
        Disarm();
        _current = null;
    }

    /// <summary>
    /// Armed at run start with every other handler, not when the duel begins.
    ///
    /// The rule this project has paid for five times: the peer can announce something before you
    /// act on it locally, and a handler registered on first local use drops that message silently.
    /// A lock-in is precisely such an announcement — the opponent can lock in before you have
    /// played a single card.
    /// </summary>
    public static void Arm()
    {
        if (_armed)
        {
            return;
        }

        INetGameService? net = RunManager.Instance?.NetService;
        if (net == null)
        {
            return;
        }

        net.RegisterMessageHandler<DuelLockInMessage>(OnLockIn);

        // Display only: the indicator over the leader's head changes at turn boundaries, and this
        // is the engine's own event for one. Nothing about the round loop keys off it, so it
        // carries none of the ordering hazards a turn-start *reset* would.
        CombatManager.Instance.TurnStarted += OnTurnStarted;
        _armed = true;
    }

    public static void Disarm()
    {
        RunManager.Instance?.NetService?.UnregisterMessageHandler<DuelLockInMessage>(OnLockIn);
        CombatManager.Instance.TurnStarted -= OnTurnStarted;
        _armed = false;
    }

    private static void OnTurnStarted(CombatState state)
    {
        if (state.CurrentSide != CombatSide.Player)
        {
            return;
        }

        if (Current is LockInTurnModel lockIn)
        {
            LockInPlanView.ShowInitiative(lockIn.CurrentLeader());
        }

        (Current as TickTurnModel)?.OnTurnStarted();
    }

    /// <summary>The host's ruling on who takes the opening initiative, from `DuelStartMessage`.</summary>
    public static void SetInitiative(ulong netId) =>
        (Current as LockInTurnModel)?.SetInitiative(netId);

    private static void OnLockIn(DuelLockInMessage message, ulong senderId)
    {
        if (LocalContext.NetId == senderId)
        {
            return;
        }

        (Current as LockInTurnModel)?.RemoteLockedIn(message.playCount);
    }

    private static bool _armed;

    /// <summary>Convenience for the one patch that asks per action.</summary>
    public static bool ShouldDefer(GameAction action) =>
        DuelSession.IsDuelActive && Current.ShouldDefer(action);

    private static IDuelTurnModel Build(IRunState? runState)
    {
        // **Real-time means paced now** (M8.5). `BlitzTurnModel` — submit as you click, no cooldown,
        // no queue — is no longer selectable and is kept only as the seam's trivial case: it is the
        // accurate statement of what "never defer" looks like, and the thing a new model is diffed
        // against. Deciding it here rather than behind a third modifier was Lucas's call: the paced
        // version *is* what the real-time mode should be, so it takes the same lobby entry.
        return DuelMatch.IsTurnBased(runState)
            ? new LockInTurnModel()
            : (IDuelTurnModel)new TickTurnModel();
    }
}
