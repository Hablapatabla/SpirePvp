using MegaCrit.Sts2.Core.Context;
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
/// **Known gap, deliberately not in this slice: energy is not reserved while planning.** It is
/// spent when a play *executes*, which in blitz is a frame after the click and so invisible;
/// buffered, that gap is the whole round, so nothing currently stops a player planning ten Strikes
/// on three energy and watching seven fizzle at resolution. The fix is a postfix on
/// `CardModel.CanPlay` subtracting the buffered cost, and it was cut from this slice on purpose: a
/// wrong predicate there makes cards *unplayable*, which would make the mode untestable rather
/// than merely rough. Do it once the loop is confirmed, against `CardEnergyCost` rather than a
/// guessed accessor.
///
/// **Only the host flushes.** Clients cannot spoof an action's owner (the host derives it from
/// `senderId`), and two clients ordering a shared stream independently is how a sim desyncs. The
/// host holds both buffers and enqueues every play with `EnqueueAction(action, ownerId)` — I5's
/// finding, proven during M3's research and unused until now.
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

    public bool LockedIn => _localLockedIn;

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
            LockIn();

            // The host holds its own so the flush can put it after the plays; a client lets it go
            // to the host, which holds it there for the same reason.
            if (RunManager.Instance?.NetService.Type == NetGameType.Host)
            {
                _localEnd = action;
                return true;
            }

            return false;
        }

        // Exactly vanilla's own test for "this belongs to the play phase", reused rather than
        // reinvented: it is the same category the engine already defers during an enemy turn.
        if (action.ActionType != GameActionType.CombatPlayPhaseOnly)
        {
            return false;
        }

        _local.Add(action);
        Log.Info($"[SpirePvp] lock-in: holding {action} ({_local.Count} planned)");
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
    /// **Informational on the host**, which decides from their end turn arriving instead — see
    /// `HoldRemote`. Kept because it is what a client learns from, and a client showing "they are
    /// waiting on you" is the obvious next piece of presentation.
    /// </summary>
    public void RemoteLockedIn(int playCount) =>
        Log.Warn($"[SpirePvp] lock-in: opponent announced {playCount} play(s), {_remote.Count} held");

    /// <summary>
    /// Holds something the opponent requested, instead of letting the host enqueue it now.
    ///
    /// **Their end turn is held too, and separately.** Enqueuing it on arrival would roll the turn
    /// over before the round's cards had resolved; the first version of this let it through and
    /// the round simply ended with every play still in a buffer.
    /// </summary>
    public void HoldRemote(GameAction action)
    {
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
            TryFlush();
            return;
        }

        _remote.Add(action);
        Log.Info($"[SpirePvp] lock-in: holding opponent's {action} ({_remote.Count} held)");
    }

    /// <summary>
    /// Both sides are in, so resolve the round: interleave the two buffers and enqueue every play.
    ///
    /// Host only. A client does nothing here — it receives the host's `ActionEnqueuedMessage`
    /// stream exactly as it does in blitz, and executes the same ordering everyone else does.
    /// </summary>
    private void TryFlush()
    {
        RunManager? run = RunManager.Instance;
        IRunState? state = run?.State;
        if (run == null || state == null || _flushing
            || !_localLockedIn || !_remoteLockedIn
            || run.NetService.Type != NetGameType.Host)
        {
            return;
        }

        _flushing = true;

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
