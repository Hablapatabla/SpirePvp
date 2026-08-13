using System.Linq;
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
/// Simultaneous turn-based: both players plan privately, lock in, and the batch resolves as one
/// interleaved stream (DESIGN §3.1b, model B).
///
/// **A turn holds as many plan→resolve batches as the players want, and that is what makes draw
/// cards work.** Locking in used to end the turn, so you planned once from your opening hand: a
/// card that drew gave you cards *after* planning was over, and the hand was discarded before you
/// could use them — you paid energy for cards you could never plan with. Reported as "draw cards
/// feel very weird and bad", and it is inherent to one-batch-per-turn rather than a bug in it.
///
/// So the end turn button commits a *batch*, and **an empty batch is what ends the turn**. Plan two
/// cards, commit, watch them resolve, and you are still in the same turn with the energy and the
/// hand you have left — including whatever you just drew. Press with nothing planned and you are
/// finished; the turn rolls when *both* players are. The button's label carries the whole rule,
/// reading `Lock In` while you hold cards and `End Turn` while you hold none.
///
/// Chosen over the alternatives (DESIGN §3.1b) because nothing is special-cased: no card is split
/// between a plan-time effect and a resolved one, nothing resolves twice, and no card needs a tag
/// saying whether it may resolve early — which is where the other two options put their desync
/// risk. "Two planning passes" is simply what this degenerates to when a turn uses two batches.
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
public sealed class LockInTurnModel : IPlanningTurnModel
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
    /// turn rolls over before the batch's cards resolve; dropping them means nobody is ever ready
    /// and the turn never ends. So the closing batch is one ordered thing — every play,
    /// interleaved, then both players ending their turn.
    ///
    /// Only a *closing* press fills these. A press that commits a batch mid-turn is a lock-in and
    /// nothing more, so its `EndPlayerTurnAction` is dropped: an end turn that reached the queue
    /// would end the turn, which is the one thing a mid-turn batch must not do.
    /// </summary>
    private GameAction? _localEnd;

    private GameAction? _remoteEnd;

    private bool _localLockedIn;
    private bool _remoteLockedIn;
    private bool _flushing;

    /// <summary>
    /// Declared finished for the whole turn, rather than for this batch — sticky until the turn
    /// rolls.
    ///
    /// **Sticky is what stops a ping-pong.** A player out of energy would otherwise have to press
    /// again for every batch the other one takes; instead they say "done" once and stay ready, so
    /// each later batch flushes the moment the still-playing player commits it. It is also what
    /// keeps the flush condition honest: `done` counts as ready, or the first batch after someone
    /// finished would wait forever on a player who has nothing left to commit.
    /// </summary>
    private bool _localDone;

    private bool _remoteDone;

    /// <summary>
    /// Set for the flush that ends the turn, so the batch watcher does not reopen planning into a
    /// turn that is rolling over. Vanilla's own turn start does that, and does it properly.
    /// </summary>
    private bool _turnRolling;

    /// <summary>
    /// Whether a committed batch is still playing out — the window where the hand is dead and both
    /// clocks are stopped.
    ///
    /// **This replaced `DuelPace.IsResolving`, which asked whether the action queue was busy.** That
    /// is a correlate, and it produced the report "cards showing unplayable when they are
    /// playable": the queue also runs *during* a planning window. A card that pauses for a player
    /// choice resumes after the drain that carried it has already finished, so the log shows
    /// `batch resolved, planning reopens` and then, two lines later,
    /// `Executing action: PlayCardAction CARD.SNAP` — a straggler resolving while its owner is
    /// planning the next batch, greying their whole hand for as long as it took.
    ///
    /// The condition meant is "I have committed and the batch has not been handed back yet", which
    /// is ours to know rather than something to infer from the engine. Set at the flush, cleared
    /// when the batch is handed back. Same flag drives the clock, so the hand and the clock can
    /// never disagree about whether you are on the move.
    /// </summary>
    public bool ResolvingBatch { get; private set; }

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

    /// <summary>
    /// Whether we have committed and are waiting — for this batch, or for the whole turn.
    ///
    /// The three things that read it want the same answer in both cases: the hand goes dead, the
    /// clock stops, and the icon appears. A player who has declared themselves finished is waiting
    /// in exactly the sense those care about.
    /// </summary>
    public bool LockedIn => _localLockedIn || _localDone;

    /// <summary>
    /// Committed, or watching a committed batch play out — either way this hand is shut.
    ///
    /// The paced model answers false to the same question, which is the whole difference between
    /// the two: there, queueing never stops.
    /// </summary>
    public bool HandIsClosed => LockedIn || ResolvingBatch;

    /// <summary>
    /// Long, because a resolved batch is a story told after the decisions are made: six plays land
    /// in a row and the only thing to do is read them.
    /// </summary>
    public float BeatSeconds => 1.2f;

    /// <summary>
    /// **The same beat, deliberately.** A resolving round is interleaved by design — one of yours,
    /// one of theirs, alternating — so almost every gap in it is a cross-player gap. Shortening
    /// those would drain the round at nearly full speed and restore the exact "six plays resolved
    /// and neither player could say what happened" report `DuelPace` exists to answer. Nothing here
    /// is a live exchange: you are reading a round you already committed to.
    /// </summary>
    public float CrossPlayerBeatSeconds => BeatSeconds;

    /// <summary>Declared finished for the turn, which the button label reads to stop offering a lock-in.</summary>
    public bool Done => _localDone;

    /// <summary>
    /// Whether the opponent has committed, for the icon over the end turn button.
    ///
    /// Set from their end turn on the host and from their message on a client — the same split
    /// <see cref="HoldRemote"/> and <see cref="RemoteLockedIn"/> document, and the reason this is a
    /// property rather than each of them setting a display flag of its own.
    /// </summary>
    public bool OpponentLockedIn => _remoteLockedIn || _remoteDone;

    /// <summary>
    /// Who reached the arena first, from the host — the opening turn's initiative (M9).
    ///
    /// Zero until the duel starts, and zero for the legacy `duel on` path, which never passes
    /// through the rendezvous and so has no arrival order to report. Both fall back to slot order.
    /// </summary>
    private ulong _firstInitiative;

    /// <summary>How many turns have closed, which is what the alternation counts.</summary>
    private int _turnsClosed;

    /// <summary>Set once, from `DuelStartMessage`, on both sides.</summary>
    public void SetInitiative(ulong netId)
    {
        _firstInitiative = netId;
        Log.Warn($"[SpirePvp] lock-in: opening initiative to {netId} (reached the arena first)");
    }

    /// <summary>
    /// Whose first card resolves first this turn.
    ///
    /// **M9's rule, replacing fixed slot order: whoever reached the arena first leads, and it
    /// alternates every turn after.** Proposed by Lucas, and the argument for it is that it is
    /// *earned* — it gives the race a tactical consequence rather than only a material one, while
    /// alternating stops it being a first-strike advantage in every turn of the duel. It is
    /// explicitly not a random tiebreak: DESIGN §1 works hardest to make sure no player can be
    /// luckier than the other, and the relic-contention animation is the right way to *display*
    /// priority, not to decide it.
    ///
    /// **Alternating per turn, not per batch.** Per batch reads more granular and is worse: a
    /// player could commit a throwaway one-card batch purely to flip who leads the next one, which
    /// turns initiative into something you manipulate by splitting your turn rather than something
    /// you earned in the race. Per turn, the number of batches you take cannot change who leads.
    ///
    /// **The whole of the tiebreak still lives here**, so whatever replaces it replaces one
    /// expression rather than unpicking a merge loop.
    /// </summary>
    private ulong StartsTheRound(IRunState state)
    {
        ulong opening = _firstInitiative != 0 ? _firstInitiative : LowestNetId(state);
        if (_turnsClosed % 2 == 0)
        {
            return opening;
        }

        foreach (Player player in state.Players)
        {
            if (player.NetId != opening)
            {
                return player.NetId;
            }
        }

        return opening;
    }

    /// <summary>Slot order, the fallback when nobody's arrival was recorded.</summary>
    private static ulong LowestNetId(IRunState state)
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

    /// <summary>Who leads the current turn, for the indicator over their head.</summary>
    public ulong CurrentLeader
    {
        get
        {
            IRunState? state = RunManager.Instance?.State;
            return state == null ? 0 : StartsTheRound(state);
        }
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
            // **The button commits a batch; an empty batch is what ends the turn.** Pressing with
            // cards planned resolves those cards and leaves you in the same turn, with the energy
            // and the hand you have left — which is the whole point, because cards drawn by a play
            // then arrive in time to be planned. Pressing with nothing planned says you are
            // finished. The label says which one the press will do, so the rule is readable off the
            // button rather than learned.
            bool closing = _local.Count == 0;

            // **Recorded before locking in, because locking in can flush the batch immediately.**
            // If the opponent is already waiting, `LockIn` runs the flush inside itself — and a
            // flush that happens before this assignment appends only *their* end turn, so we are
            // never marked ready and the turn hangs with every card already spent. Measured:
            // `resolving round — 3 then 3` and then, thirty lines later,
            // `holding EndPlayerTurnAction for player 1` — the host's own end turn arriving after
            // the round it belonged to had gone.
            //
            // The host holds its own so the flush can put it after the plays; a client lets it go
            // to the host, which holds it there for the same reason.
            bool isHost = RunManager.Instance?.NetService.Type == NetGameType.Host;
            if (closing)
            {
                _localDone = true;
                if (isHost)
                {
                    _localEnd = action;
                }
            }

            LockIn();

            // The host swallows either way: a closing press is held for the flush, a batch press
            // has done its work by triggering the lock-in. A client must let both reach the host,
            // which is the only signal the host gets that a batch was committed — and the host
            // reads *closing* off the arrival finding an empty buffer, so the two sides agree
            // without a message saying so.
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

        // The button now commits a batch rather than the turn, and says so from the first card
        // planned. Vanilla puts its own text back at the next planning window, so this only ever
        // has to change it in one direction.
        LockInPlanView.ShowLockInLabel();
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

        // **A count of zero is the host declaring themselves finished for the turn.** The host
        // reads the same fact off an empty buffer when the client's end turn arrives; a client has
        // no buffer of the host's to read, so it reads the count that was already on the wire.
        // Both sides therefore agree on when the turn ends without a field that says so.
        if (playCount == 0)
        {
            _remoteDone = true;
        }
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
            // leaves — so the message can reach the host first and flush a batch whose end turn is
            // still in flight, leaving nobody marked ready and the turn hung. The end turn is the
            // last thing a player sends, so treating its arrival as the lock-in cannot be early.
            //
            // **And an empty buffer at this moment is how the host knows they are closing**, with
            // no flag on the wire. Their plays travel before their end turn on an ordered
            // transport, so what is held here is exactly the batch they committed: nothing held
            // means nothing planned means they are finished for the turn. Same rule the local side
            // applies to itself, read off the same fact.
            _remoteEnd = action;
            _remoteLockedIn = true;
            if (_remote.Count == 0)
            {
                _remoteDone = true;
            }

            Log.Warn("[SpirePvp] lock-in: opponent's end turn arrived — they are "
                     + (_remoteDone ? "finished for the turn" : $"locked in with {_remote.Count} play(s)"));
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

        // **Done counts as ready for every later batch of the turn.** Without that, the first batch
        // after one player finished would wait forever on someone who has nothing left to commit —
        // and the turn would hang with the other player still holding energy.
        bool localReady = _localLockedIn || _localDone;
        bool remoteReady = _remoteLockedIn || _remoteDone;
        if (run == null || state == null || _flushing || !localReady || !remoteReady)
        {
            return;
        }

        _flushing = true;
        ResolvingBatch = true;

        // The turn ends when *both* have declared themselves finished, and not before. Every other
        // flush resolves a batch and hands the turn back to whoever is still playing.
        bool endsTurn = _localDone && _remoteDone;
        _turnRolling = endsTurn;

        if (run.NetService.Type != NetGameType.Host)
        {
            Log.Warn($"[SpirePvp] lock-in: batch closed — {_local.Count} play(s) handed over, "
                     + $"waiting on the host's ordering{(endsTurn ? "; turn ends" : "")}");
            BeginNextBatch(endsTurn);
            DuelPace.WatchBatch();
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

        Log.Warn($"[SpirePvp] lock-in: resolving batch — {first.Count} then {second.Count}, "
                 + $"{(localStarts ? "we" : "they")} start the alternation"
                 + (endsTurn ? "; turn ends after it" : ""));

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

        // Both end turns last, and **only on the batch that ends the turn**, so the turn rolls over
        // once every play in it has resolved. Without them the players are never marked ready and
        // the turn hangs with the cards already spent — the failure that looks most like "the mod
        // ate my turn". With them on every batch, the turn would end after the first one, which is
        // the model this replaced.
        if (endsTurn)
        {
            if (_localEnd != null)
            {
                run.ActionQueueSynchronizer.EnqueueAction(_localEnd, me);
            }

            if (_remoteEnd != null)
            {
                run.ActionQueueSynchronizer.EnqueueAction(_remoteEnd, opponent);
            }
        }

        Log.Warn($"[SpirePvp] lock-in: batch enqueued{(endsTurn ? ", both end turns appended" : "")}");
        BeginNextBatch(endsTurn);

        // Planning stays shut until these have actually resolved, which is a different moment from
        // "they have been enqueued" — and on a client a very different one, since the batch has yet
        // to arrive.
        DuelPace.WatchBatch();
    }

    /// <summary>
    /// Ends the turn on the player's behalf when the batch they just resolved used everything they
    /// had.
    ///
    /// **Reported as "having to click end turn when nothing's playable and couldn't play in the
    /// first place".** It is the batch model charging the common case for the rare one: energy is a
    /// *turn* resource but planning is a *batch* activity, so a turn with no draw in it is "plan up
    /// to your energy, commit, then press a second time with a dead hand to say the obvious". One
    /// press per turn is vanilla's rhythm and there was no reason to take it away.
    ///
    /// **Only after a batch, never at turn start.** Starting a turn with nothing playable already
    /// costs exactly one press, the same as vanilla, so there is nothing there to remove — and
    /// closing a turn nobody has looked at yet would be taking something away rather than giving it
    /// back. The redundant press exists only after a commit, which is the only place this fires.
    ///
    /// **A potion stops it.** `HasCardsToPlay` is about cards, and drinking is exactly what a player
    /// does once the energy is gone, so closing the turn under them would delete the decision. A
    /// player holding anything drinkable still presses; everyone else stops having to.
    ///
    /// Goes through the button rather than around it — `CallReleaseLogic` is public for precisely
    /// this ("we can call the End Turn button in numerous ways") — so an automatic close is the
    /// same event as a press: same guard, same `EndPlayerTurnAction`, same signal to the peer. A
    /// close invented here would be a second closing path, which is the shape of bug this mode has
    /// already produced twice.
    /// </summary>
    private void CloseIfNothingLeftToDo()
    {
        Player? me = LocalContext.GetMe(RunManager.Instance?.State?.Players);
        if (me?.PlayerCombatState == null || me.Potions.Any() || me.PlayerCombatState.HasCardsToPlay())
        {
            return;
        }

        Log.Warn("[SpirePvp] lock-in: nothing left to play — closing the turn for you");
        LockInPlanView.PressEndTurn();
    }

    /// <summary>
    /// Clears the batch so the next planning window starts empty.
    ///
    /// **What survives a batch is what belongs to the turn**: whether each player has declared
    /// themselves finished, and the end-turn actions that go with that declaration. Clearing those
    /// per batch would let a finished player be waited on again, and would throw away the very
    /// action the closing flush needs.
    /// </summary>
    public void BeginNextBatch(bool endsTurn)
    {
        _local.Clear();
        _remote.Clear();
        _localLockedIn = false;
        _remoteLockedIn = false;
        _flushing = false;

        if (endsTurn)
        {
            _localEnd = null;
            _remoteEnd = null;
            _localDone = false;
            _remoteDone = false;

            // The alternation counts turns, not batches — see StartsTheRound.
            _turnsClosed++;
        }
    }

    /// <summary>
    /// The batch has finished resolving, so planning reopens — unless the turn is rolling over, in
    /// which case vanilla's own turn start does it and does it properly.
    ///
    /// Driven by <see cref="DuelPace"/>, which watches the action queue drain. Reopening at *flush*
    /// time instead would have handed both players a free planning window during the resolution
    /// they are supposed to be reading — and, now that the clocks stop while a batch resolves, a
    /// free *thinking* window too, which is the kind of hole a competitive mode gets played through.
    /// </summary>
    public void OnBatchResolved()
    {
        ResolvingBatch = false;

        if (_turnRolling)
        {
            _turnRolling = false;
            return;
        }

        Log.Info($"[SpirePvp] lock-in: batch resolved, planning reopens"
                 + (_localDone ? " (we are finished for the turn)" : ""));
        LockInPlanView.ReopenPlanning(!_localDone);

        // Reopening puts vanilla's own "End Turn" back on the button, which would be a lie if plays
        // were queued while the batch resolved — the window between committing and the batch
        // arriving is small, but it is real on a client.
        if (_local.Count > 0)
        {
            LockInPlanView.ShowLockInLabel();
        }

        if (!_localDone)
        {
            CloseIfNothingLeftToDo();
        }
    }
}
