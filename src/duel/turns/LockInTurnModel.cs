using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Net;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// Simultaneous turn-based: both players plan privately, lock in, and the round resolves as one
/// interleaved stream (DESIGN §3.1b, model B).
///
/// **What changes is only *when* actions are submitted.** Execution is untouched: plays already go
/// through the shared queue one at a time, host-ordered, and they still do. Blitz submits as you
/// click and lets host-arrival order decide; this holds your plays locally, submits them all at
/// lock-in, and lets the host decide the order deliberately. The card patches, the win condition,
/// powers and damage-over-time never learn a turn model exists.
///
/// **The merge rule is interleaved — A1, B1, A2, B2 — starting on fixed slot order.** Settled in
/// DESIGN §3.1b after finding that "submission order" does not define a single stream for two
/// players who queue simultaneously. Interleaving looks symmetric and is not: "mine first, then
/// theirs" reads the same from both seats, so somebody's first card genuinely resolves first, and
/// that choice decides matches — `[Strike, Block]` against `[Block, Strike]` has opposite winners
/// depending on who starts. What interleaving buys is that the choice stops being *decisive*: one
/// card of advantage instead of a whole hand.
///
/// **Fixed slot order is arbitrary on purpose. Do not tune it** (DESIGN §3.1b): the seam is
/// <see cref="StartsTheRound"/>, and the candidate replacement is M9's — whoever reached the arena
/// first starts the alternation, alternating each round after.
///
/// **Energy is reserved while planning** (`ReservedEnergy`, spent by `DuelPlanEnergyPatch`), and
/// planned cards sit in vanilla's own play queue (`LockInPlanView`). Both exist because a buffered
/// play is otherwise indistinguishable from a click that did nothing.
///
/// **Only the host flushes.** Clients cannot spoof an action's owner (the host derives it from
/// `senderId`), and two clients ordering a shared stream independently is how a sim desyncs. The
/// host holds both buffers and enqueues every play with `EnqueueAction(action, ownerId)` — I5's
/// finding, proven during M3's research and unused until now.
///
/// **Both sides let go of the round at the same moment, and that symmetry is load-bearing.** The
/// host clears when it flushes; a client has nothing to enqueue but clears on the same condition
/// (both locked in), which it learns from the host's `DuelLockInMessage` — sent before the flush on
/// a reliable ordered transport, so it cannot arrive after the actions it precedes. Until
/// 2026-08-12 the client never cleared at all: `BeginNextRound` was reached only through the
/// host-only branch of <see cref="TryFlush"/>, so a client stayed locked in for the rest of the
/// duel and its buffer kept round 1 forever. The match survived it — the host holds a client's
/// plays regardless, so the round still resolved — which is exactly why it went unnoticed through a
/// five-round playtest. It stops being survivable the moment anything *reads* the buffer, and
/// `ReservedEnergy` reads it: a client would have been charged round 1's cards for the whole match.
/// </summary>
public sealed class LockInTurnModel : IDuelTurnModel
{
    public string Name => "turn-based (lock-in)";

    /// <summary>Our own plays, in the order we chose them, waiting for lock-in.</summary>
    private readonly List<GameAction> _local = new List<GameAction>();

    /// <summary>The opponent's plays, held by the host until both sides are in.</summary>
    private readonly List<GameAction> _remote = new List<GameAction>();

    /// <summary>
    /// The two end-turn actions, held apart from the plays and enqueued after them.
    ///
    /// **They cannot go through early and they cannot be dropped.** Letting them through means the
    /// turn rolls over before the round's cards resolve; dropping them means nobody is ever ready
    /// and the round never ends. So the round is one ordered thing — every play, interleaved, then
    /// both players ending their turn.
    /// </summary>
    private GameAction? _localEnd;

    private GameAction? _remoteEnd;

    private bool _localLockedIn;
    private bool _remoteLockedIn;
    private bool _flushing;

    /// <summary>How many plays we are holding, for the HUD and the logs.</summary>
    public int PendingCount => _local.Count;

    /// <summary>
    /// Energy already promised to buffered plays.
    ///
    /// **Energy is spent when a play executes, not when it is submitted** — invisible in blitz,
    /// where the two are a frame apart, and the whole round apart once plays are buffered. Without
    /// this a player plans ten Strikes on three energy and watches seven fizzle at resolution,
    /// which reads as the mod eating cards rather than as their own overspend.
    ///
    /// Summed on demand from the buffer rather than accumulated, so an undo or a cleared round
    /// cannot leave a stale reservation behind — the failure mode would be a hand that refuses to
    /// play anything, with no way to tell why.
    ///
    /// **The card comes from `NetCombatCard`, not from `PlayCardAction._card`.** That field is
    /// assigned in `ExecuteAction` and is therefore null for every action this class holds — a
    /// buffered play has by definition not executed. Read off it, this property returned 0 for the
    /// whole of its first day. `NetCombatCard.ToCardModelOrNull` resolves the live model by combat
    /// index, which is what vanilla's own queue does with an action it has not run yet.
    /// </summary>
    public int ReservedEnergy
    {
        get
        {
            int total = 0;
            foreach (GameAction action in _local)
            {
                CardModel? card = PlannedCard(action);
                if (card != null)
                {
                    total += Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
                }
            }

            return total;
        }
    }

    /// <summary>The card a buffered play will resolve, or null for anything that is not a card.</summary>
    private static CardModel? PlannedCard(GameAction action) =>
        action is PlayCardAction play ? play.NetCombatCard.ToCardModelOrNull() : null;

    /// <summary>
    /// Whether this card is already in the plan.
    ///
    /// The energy reservation asks, because a planned card must not be charged against its own
    /// reservation. That is not hypothetical: a queued card keeps `PileType.Hand`, so the queue
    /// repaints it through the same `CanPlay` the hand uses, and without this every planned card
    /// would draw itself as unaffordable the moment it was planned.
    /// </summary>
    public bool IsPlanned(CardModel card)
    {
        foreach (GameAction action in _local)
        {
            if (ReferenceEquals(PlannedCard(action), card))
            {
                return true;
            }
        }

        return false;
    }

    public bool LockedIn => _localLockedIn;

    /// <summary>
    /// Whether the opponent has locked in, for the icon over the end turn button.
    ///
    /// Set from their end turn on the host and from their message on a client — the same split
    /// <see cref="HoldRemote"/> and <see cref="RemoteLockedIn"/> document, and the reason this is a
    /// property rather than each of them setting a display flag of its own.
    /// </summary>
    public bool OpponentLockedIn => _remoteLockedIn;

    /// <summary>
    /// Whose first card resolves first. Fixed slot order for now: the lower net id starts, which
    /// is the host.
    ///
    /// **The whole of the tiebreak lives here**, deliberately, so M9 replaces one expression rather
    /// than unpicking a merge loop.
    /// </summary>
    private static ulong StartsTheRound(IRunState state)
    {
        ulong first = ulong.MaxValue;
        foreach (Player player in state.Players)
        {
            if (player.NetId < first)
            {
                first = player.NetId;
            }
        }

        return first;
    }

    public bool ShouldDefer(GameAction action)
    {
        // The flush re-enters this path on the host as it enqueues, and a buffer that swallowed its
        // own release would hold the round forever.
        if (_flushing || _localLockedIn)
        {
            return false;
        }

        // **Requesting an end turn *is* the lock-in, and it is the trigger rather than a play.**
        // Catching it here rather than at `SetReadyToEndTurn` is what gets the ordering right: this
        // runs when the button is clicked, so the buffered plays are handed over *before* the
        // end-turn request leaves, and a host reading its inbox sees plays then end-turn. Hooked at
        // `SetReadyToEndTurn` instead, the client's end turn had to survive a round trip and be
        // executed before it locked in, which put its own plays after its end turn.
        //
        // Undo is let straight through: backing out is a decision about the round, not a play
        // within it.
        if (action is UndoEndPlayerTurnAction)
        {
            return false;
        }

        if (action is EndPlayerTurnAction)
        {
            // **Recorded before locking in, because locking in can flush the round immediately.**
            // If the opponent is already waiting, `LockIn` runs the flush inside itself — and a
            // flush that happens before this assignment appends only *their* end turn, so we are
            // never marked ready and the round hangs with every card already spent. Measured:
            // `resolving round — 3 then 3` and then, thirty lines later,
            // `holding EndPlayerTurnAction for player 1` — the host's own end turn arriving after
            // the round it belonged to had gone.
            //
            // The host holds its own so the flush can put it after the plays; a client lets it go
            // to the host, which holds it there for the same reason.
            bool isHost = RunManager.Instance?.NetService.Type == NetGameType.Host;
            if (isHost)
            {
                _localEnd = action;
            }

            LockIn();
            return isHost;
        }

        // Exactly vanilla's own test for "this belongs to the play phase", reused rather than
        // reinvented: it is the same category the engine already defers during an enemy turn.
        if (action.ActionType != GameActionType.CombatPlayPhaseOnly)
        {
            return false;
        }

        _local.Add(action);
        LockInPlanView.ShowPlanned(action);
        Log.Info($"[SpirePvp] lock-in: holding {action} ({_local.Count} planned, "
                 + $"{ReservedEnergy} energy reserved)");
        return true;
    }

    /// <summary>
    /// Called when the local player ends their turn, which *is* the lock-in.
    ///
    /// Plays are sent before the lock-in message, and the ordering is load-bearing: the transport
    /// is reliable and ordered, so a host holding the message knows the buffer that preceded it is
    /// complete. See <see cref="DuelLockInMessage"/>.
    /// </summary>
    public void LockIn()
    {
        if (_localLockedIn)
        {
            return;
        }

        _localLockedIn = true;
        RunManager? run = RunManager.Instance;
        if (run == null)
        {
            return;
        }

        Log.Warn($"[SpirePvp] lock-in: locking in {_local.Count} play(s)");

        // Your own icon over the end turn button, on the same footing as the opponent's. Vanilla
        // would show it when `EndPlayerTurnAction` executes, which under this model is a whole
        // round later — so without this the one moment the button has something to say about you
        // is the one moment it says nothing.
        LockInPlanView.RefreshLockInIcons();

        // A client hands its plays over through the engine's ordinary request path; the host holds
        // them rather than enqueuing, via DuelLockInPatch. The host has its own buffer already.
        if (run.NetService.Type == NetGameType.Client)
        {
            foreach (GameAction action in _local)
            {
                run.ActionQueueSynchronizer.RequestEnqueue(action);
            }
        }

        run.NetService.SendMessage(new DuelLockInMessage { playCount = _local.Count });
        TryFlush();
    }

    /// <summary>
    /// The opponent says they have locked in.
    ///
    /// **Informational on the host, authoritative on a client**, and the asymmetry is the point.
    /// The host decides from their end turn *arriving* (see <see cref="HoldRemote"/>) because this
    /// message is sent before the plays it announces and could flush a round whose end turn is
    /// still in flight. A client holds no buffer to flush and no round to order, so the same
    /// message is safe there — and it is the only signal a client gets, since the host's end turn
    /// never travels.
    /// </summary>
    public void RemoteLockedIn(int playCount)
    {
        Log.Warn($"[SpirePvp] lock-in: opponent announced {playCount} play(s), {_remote.Count} held");

        if (RunManager.Instance?.NetService.Type != NetGameType.Client)
        {
            return;
        }

        _remoteLockedIn = true;
        LockInPlanView.RefreshLockInIcons();
        TryFlush();
    }

    /// <summary>
    /// Holds something the opponent requested, instead of letting the host enqueue it now.
    ///
    /// **Their end turn is held too, and separately.** Enqueuing it on arrival would roll the turn
    /// over before the round's cards had resolved; the first version of this let it through and
    /// the round simply ended with every play still in a buffer.
    /// </summary>
    public void HoldRemote(GameAction action)
    {
        // **Backing out un-locks them.** Undo is not a play and must not be buffered as one; it
        // also means the opponent is no longer ready, so the round must stop being flushable or a
        // later lock-in would resolve against a stale buffer.
        if (action is UndoEndPlayerTurnAction)
        {
            _remoteEnd = null;
            _remoteLockedIn = false;
            Log.Warn("[SpirePvp] lock-in: opponent backed out of their end turn");
            return;
        }

        if (action is EndPlayerTurnAction)
        {
            // **Their end turn *is* their lock-in, and using it as the signal removes a race.**
            // `DuelLockInMessage` is sent from `LockIn`, which runs before the end-turn request
            // leaves — so the message can reach the host first and flush a round whose end turn is
            // still in flight, leaving nobody marked ready and the round hung. The end turn is the
            // last thing a player sends, so treating its arrival as the lock-in cannot be early.
            _remoteEnd = action;
            _remoteLockedIn = true;
            Log.Warn("[SpirePvp] lock-in: opponent's end turn arrived — they are locked in");
            LockInPlanView.RefreshLockInIcons();
            TryFlush();
            return;
        }

        _remote.Add(action);
        Log.Info($"[SpirePvp] lock-in: holding opponent's {action} ({_remote.Count} held)");
    }

    /// <summary>
    /// Both sides are in, so the round is closed: the host interleaves the two buffers and enqueues
    /// every play; a client only lets go of its own.
    ///
    /// **A client has nothing to enqueue and still has something to do.** It receives the host's
    /// `ActionEnqueuedMessage` stream exactly as it does in blitz and executes the ordering
    /// everyone else does — but the round it planned is over, so the buffer it planned into has to
    /// be released *here*, on the same condition the host uses, rather than at some later local
    /// event. Anything asking what is planned — the energy reservation, the queue view, the hand —
    /// then gets the same answer on both clients at the same point in the stream, which is the only
    /// version of this that cannot decide a card differently on the two sims.
    /// </summary>
    private void TryFlush()
    {
        RunManager? run = RunManager.Instance;
        IRunState? state = run?.State;
        if (run == null || state == null || _flushing || !_localLockedIn || !_remoteLockedIn)
        {
            return;
        }

        _flushing = true;

        if (run.NetService.Type != NetGameType.Host)
        {
            Log.Warn($"[SpirePvp] lock-in: round closed — {_local.Count} play(s) handed over, "
                     + "waiting on the host's ordering");
            BeginNextRound();
            return;
        }

        ulong me = LocalContext.NetId ?? 0UL;
        ulong opponent = 0UL;
        foreach (Player player in state.Players)
        {
            if (!LocalContext.IsMe(player))
            {
                opponent = player.NetId;
                break;
            }
        }

        bool localStarts = StartsTheRound(state) == me;
        List<GameAction> first = localStarts ? _local : _remote;
        List<GameAction> second = localStarts ? _remote : _local;
        ulong firstOwner = localStarts ? me : opponent;
        ulong secondOwner = localStarts ? opponent : me;

        Log.Warn($"[SpirePvp] lock-in: resolving round — {first.Count} then {second.Count}, "
                 + $"{(localStarts ? "we" : "they")} start the alternation");

        // A1, B1, A2, B2 … and whichever list is longer simply runs on at the end.
        for (int i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count)
            {
                run.ActionQueueSynchronizer.EnqueueAction(first[i], firstOwner);
            }

            if (i < second.Count)
            {
                run.ActionQueueSynchronizer.EnqueueAction(second[i], secondOwner);
            }
        }

        // Both end turns last, so the round rolls over only once every play in it has resolved.
        // Without these the players are never marked ready and the round hangs with the cards
        // already spent — which is the failure that looks most like "the mod ate my turn".
        if (_localEnd != null)
        {
            run.ActionQueueSynchronizer.EnqueueAction(_localEnd, me);
        }

        if (_remoteEnd != null)
        {
            run.ActionQueueSynchronizer.EnqueueAction(_remoteEnd, opponent);
        }

        Log.Warn("[SpirePvp] lock-in: round enqueued, both end turns appended");
        BeginNextRound();
    }

    /// <summary>Clears the round so the next planning phase starts empty.</summary>
    public void BeginNextRound()
    {
        _local.Clear();
        _remote.Clear();
        _localEnd = null;
        _remoteEnd = null;
        _localLockedIn = false;
        _remoteLockedIn = false;
        _flushing = false;
    }
}
