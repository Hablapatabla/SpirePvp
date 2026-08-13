using Godot;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// Decides, on the host, whose card resolves next — so that one duelist's backlog cannot delay the
/// other's (DESIGN §7, M8.5 slice 2).
///
/// # What went wrong twice before this
///
/// **The engine keeps a queue per player and then flattens them by id.** `ActionQueueSet` really
/// does hold one `ActionQueue` per player — the log says `to player queue owned by 1001` — but
/// `GetReadyAction` takes the globally lowest action id, and ids are handed out in the order the
/// host enqueues. So whatever the host enqueues first resolves first, and the per-player structure
/// means nothing.
///
/// The first attempt bucketed plays by **wall-clock ticks**, on the theory that two players acting
/// at their own cadences would then stop colliding. It changed nothing observable, and the log said
/// why: `booked … for 1 into tick 0/4/5` and `booked … for 1001 into tick 10/11`. Two players
/// rarely act inside the same 0.4s, so bucketing by time *is* ordering by time.
///
/// The case that made it clear, reported 2026-08-12: one player had played three cards; the other
/// then played their **first**, a Defend, and it "got stiffed" — the incoming Strike "hovering in
/// the air" while their Defend waited. Nothing was out of order by time. It was out of order by
/// *fairness*: with one executor and a readable dwell after each play, three cards own the stream
/// for well over a second, so a first play arriving during them is not instant at all. It is fifth.
///
/// # The rule now
///
/// **Round-robin by each player's own position in their own queue, and never more than one card in
/// flight.** Nothing is enqueued while the executor is still working, so the choice of who goes
/// next is made fresh every time rather than frozen into ids minutes earlier. Among everything
/// pending, the card with the lowest **per-player index** wins: your first beats their second, your
/// second beats their third, and neither of you can be buried by the other's backlog.
///
/// Ties — your first against their first — go to **initiative** (M9: whoever reached the arena
/// first, alternating each turn). That is the only place the host's shorter path to its own queue
/// could still show, so it is the place the rule has to be earned rather than incidental.
///
/// Indices reset once the pool drains and the board is still, so each exchange starts even instead
/// of inheriting a lead from the last one.
///
/// # Why it must be the host, and only the host
///
/// `ActionQueue.isPaused` looks like a shortcut to the same end — `GetReadyAction` skips a paused
/// queue and takes the other player's action instead. It is a desync: pausing changes *which
/// action executes next*, which is sim-visible, so a client pacing its own queue on its own clock
/// would pick a different card from the host within one exchange. Ordering is decided once, here,
/// and every client simply executes the stream the host publishes.
/// </summary>
public static class DuelPlayScheduler
{
    private sealed class Pending
    {
        public required GameAction Action;
        public required ulong Owner;

        /// <summary>Position in this player's own run of plays — not a global sequence number.</summary>
        public required int Index;
    }

    private static readonly List<Pending> _pending = new List<Pending>();
    private static readonly Dictionary<ulong, int> _nextIndex = new Dictionary<ulong, int>();
    private static bool _pumping;

    /// <summary>Drops everything, so the next duel starts even.</summary>
    public static void Reset()
    {
        _pending.Clear();
        _nextIndex.Clear();
    }

    /// <summary>
    /// Takes a play — the host's own, or one that arrived from the client — into the pool.
    ///
    /// Host only. A client submits through the engine's ordinary request path and the host books it
    /// on arrival, so exactly one machine ever decides an order.
    /// </summary>
    public static void Submit(GameAction action, ulong ownerId)
    {
        int index = _nextIndex.TryGetValue(ownerId, out int next) ? next : 0;
        _nextIndex[ownerId] = index + 1;

        _pending.Add(new Pending { Action = action, Owner = ownerId, Index = index });
        Log.Info($"[SpirePvp] queue: {ownerId}'s play #{index} pending ({_pending.Count} waiting)");
        Pump();
    }

    private static void Pump()
    {
        if (_pumping)
        {
            return;
        }

        _pumping = true;
        TaskHelper.RunSafely(PumpAsync());
    }

    private static async Task PumpAsync()
    {
        try
        {
            while (_pending.Count > 0)
            {
                // **One card in flight at a time.** This is what keeps the choice live: while the
                // executor is working — including the readable dwell `DuelPace` adds after each
                // play — nothing new is committed to an id, so a card played during someone else's
                // run can still take the next slot instead of queueing behind all of it.
                while (RunManager.Instance?.ActionExecutor.IsRunning == true)
                {
                    await Engine.GetMainLoop().ToSignal(Engine.GetMainLoop(), SceneTree.SignalName.ProcessFrame);
                }

                Release();
            }

            // The board is still and nothing is pending: the exchange is over, so nobody carries a
            // lead into the next one.
            _nextIndex.Clear();
        }
        catch (Exception e)
        {
            // A scheduler that stops draining is a duel where nothing resolves, so say so rather
            // than leaving two players clicking at a dead board.
            Log.Error($"[SpirePvp] queue: scheduler stopped — {e.Message}");
        }
        finally
        {
            _pumping = false;
        }
    }

    private static void Release()
    {
        RunManager? run = RunManager.Instance;
        if (run == null || _pending.Count == 0)
        {
            return;
        }

        // Read per release rather than cached: initiative alternates each turn, and an exchange can
        // straddle a turn boundary.
        ulong leader = (DuelTurnModel.Current as IPlanningTurnModel)?.CurrentLeader ?? 0;

        Pending best = _pending[0];
        foreach (Pending candidate in _pending)
        {
            if (candidate.Index < best.Index
                || (candidate.Index == best.Index && candidate.Owner == leader && best.Owner != leader))
            {
                best = candidate;
            }
        }

        _pending.Remove(best);
        run.ActionQueueSynchronizer.EnqueueAction(best.Action, best.Owner);
        Log.Info($"[SpirePvp] queue: releasing {best.Owner}'s play #{best.Index} ({_pending.Count} still waiting)");
    }
}
