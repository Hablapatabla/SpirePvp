using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
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

        (Current as TickTurnModel)?.OnTurnStarted();
        (Current as LockInTurnModel)?.ClearInFlight();

        // The tie alternation is per turn, so initiative's first strike is the leader's in every
        // turn rather than in every other one. Host-only in effect — a client's scheduler never
        // releases anything — but reset on both, because a counter that only one side keeps is a
        // counter that is wrong the moment the roles are reversed by a rematch.
        DuelPlayScheduler.OnTurnStarted();

        // A potion planned into a batch that never resolved would stay greyed for the rest of
        // the duel: the belt's own restore only runs when a potion is actually drunk. Both
        // models clear their in-flight lists at a turn boundary, so this belongs with them.
        LockInPlanView.RestorePlannedPotions();

        // The reservation is cleared at a turn boundary, so the costs and the orb have to be
        // asked again or they keep showing last turn's commitments.
        LockInPlanView.RefreshPlannedCosts();
        LogPowers(state);

        // **Only in the duel.** This is armed for the whole run, and `CombatManager.TurnStarted`
        // fires for every combat in the *race* too — so an ordinary Act 1 fight was drawing "You
        // move first" over a creature, and since the arrow hangs off that creature's node it then
        // rode along to the map screen. Reported 2026-08-12 with a screenshot of it sitting on the
        // map mid-race. Initiative is a duel rule and means nothing before the arena.
        //
        // The clear is unconditional for the same reason: an arrow raised by any path at all has a
        // turn boundary to be taken down at, rather than living until the run ends.
        if (DuelSession.IsDuelActive && Current is IPlanningTurnModel model)
        {
            LockInPlanView.ShowInitiative(model.CurrentLeader);
        }
        else
        {
            LockInPlanView.ClearInitiative();
        }
    }

    /// <summary>
    /// Both duelists' powers and durations, once per turn.
    ///
    /// **Nothing in this game logs a power's duration, so status-timing questions have been
    /// unanswerable from a log.** Asked 2026-08-12 — did Vulnerable and Weak tick down at turn end?
    /// — and neither the code reading nor the logs could settle it, because the only evidence was a
    /// number on screen that nobody had written down. One line per turn per client makes the next
    /// such question a diff instead of a discussion, and HANDOFF has been carrying an open note to
    /// "audit `AfterSideTurnStart` powers when one shows up" since poison needed its own patch.
    /// </summary>
    private static void LogPowers(CombatState state)
    {
        foreach (Creature creature in state.Creatures)
        {
            List<string> powers = new List<string>();
            foreach (PowerModel power in creature.Powers)
            {
                powers.Add($"{power.Id.Entry}:{power.Amount}");
            }

            if (powers.Count > 0)
            {
                Log.Info($"[SpirePvp] powers at turn start — {creature.LogName}: {string.Join(", ", powers)}");
            }
        }
    }

    /// <summary>The host's ruling on who takes the opening initiative, from `DuelStartMessage`.</summary>
    public static void SetInitiative(ulong netId) =>
        (Current as IPlanningTurnModel)?.SetInitiative(netId);

    private static void OnLockIn(DuelLockInMessage message, ulong senderId)
    {
        if (LocalContext.NetId == senderId)
        {
            return;
        }

        (Current as LockInTurnModel)?.RemoteLockedIn(message.playCount);
    }

    private static bool _armed;

    /// <summary>
    /// Whether to hold this action back. The one patch that asks, asks here.
    ///
    /// **Only a player's own click may be deferred — never something the sim raised.** A hook that
    /// enqueues while a card resolves is not a player deciding anything, and holding it puts an
    /// effect the engine has already committed to into a queue that releases it later, as if it had
    /// been clicked.
    ///
    /// **The rule is right; the first predicate for it was wrong, and it was wrong in this
    /// project's favourite way.** It asked `ActionExecutor.CurrentlyRunningAction != null` — "is
    /// something executing" — which is true both when the sim raises an action *and* when a player
    /// clicks a card while another card is resolving. In a paced real-time duel that second case is
    /// not an edge: with a beat after every play, someone else's card is resolving most of the time,
    /// so the guard let the local player's clicks past the scheduler and straight into the queue in
    /// arrival order. Measured on the host, 2026-08-12, with the client's plays #1 and #2 sitting in
    /// the pool the whole time:
    ///
    ///     Executing action: PlayCardAction CARD.DEFEND_SILENT (47148665)   ← client's card
    ///     Enqueueing action PlayCardAction CARD.NEUTRALIZE from owner 1    ← host click, no booking
    ///     Enqueueing action PlayCardAction CARD.STRIKE_SILENT from owner 1 ← host click, no booking
    ///     queue: releasing 1001's play #1 …                                ← client, two cards later
    ///
    /// A client's plays cannot take that route — they arrive over the wire at
    /// `DuelLockInPatch.BeforeHandleRequestEnqueue`, which books every one of them — so the guard
    /// was a standing advantage for whoever was hosting. That is the whole of "the client is waiting
    /// behind the host", and it defeated the cooldown at the same time: a bypassed play resolves
    /// back-to-back with the one before it.
    ///
    /// **And the evidence that motivated the original guard was a log-ordering artifact.** The
    /// deferred-log line prints *after* `ShouldDefer` returns, and the instant first play releases
    /// synchronously inside that call — so a card that was held, released and executed prints
    /// "holding …" below its own "Executing action". The FALLING_STAR pair that read as a card
    /// rescheduling itself is one card with one id doing that, and the sibling log from the same
    /// session shows the same id held 17 lines *before* it executed. See `DuelTurnModelPatch`.
    ///
    /// **So ask provenance, not timing.** The decompile makes that a closed question rather than a
    /// judgement: every `CombatPlayPhaseOnly` action is constructed in exactly two kinds of place —
    /// a `Net*Action.ToGameAction` (the peer's, which never reaches this patch) and an input node —
    /// except `GenericHookGameAction`, which only `ActionQueueSynchronizer` itself builds, and
    /// `ConsoleCmdGameAction`. Those two are the sim's and the dev console's; the rest are clicks.
    ///
    /// An allow-list rather than a deny-list, so anything a future game version adds defaults to
    /// vanilla behaviour instead of silently entering the duel's queue.
    ///
    /// Note `DuelPlanEnergyPatch` still asks `CurrentlyRunningAction`, and is right to: it needs to
    /// know whether *the caller* is sim code, which is a question about the stack rather than about
    /// the action. Same expression, different question.
    /// </summary>
    public static bool ShouldDefer(GameAction action)
    {
        if (!DuelSession.IsDuelActive)
        {
            return false;
        }

        if (!IsPlayerInitiated(action))
        {
            return false;
        }

        return Current.ShouldDefer(action);
    }

    /// <summary>
    /// Whether this action is one a player raised by hand — the only kind a turn model may hold.
    ///
    /// The set is every `CombatPlayPhaseOnly` action whose only local construction site is an input
    /// node: `CardModel.EnqueueManualPlay` (reached from `NCardPlay` alone, and named for what it
    /// is), `PotionModel`/`NPotionPopup`, and `NEndTurnButton`. The two deliberately absent are
    /// `GenericHookGameAction`, which the synchronizer raises for the sim, and `ConsoleCmdGameAction`
    /// — a dev command has no business being paced, and one turned up in the play queue as
    /// `holding ConsoleCmdGameAction … potion POISON_POTION` before this.
    /// </summary>
    /// **Internal rather than private because the host path needs it too.** A client's play
    /// arrives as a `RequestEnqueueActionMessage` and never passes through `ShouldDefer`, so
    /// `DuelLockInPatch` has to ask the same question — see the note there.
    internal static bool IsPlayerInitiated(GameAction action) =>
        action is PlayCardAction
            or UsePotionAction
            or DiscardPotionGameAction
            or EndPlayerTurnAction
            or UndoEndPlayerTurnAction;

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
