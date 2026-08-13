using Godot;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// Gives each duelist their own cadence, by deciding — on the host, once — which tick every play
/// belongs to (DESIGN §7, M8.5 slice 2).
///
/// # The problem this exists to fix
///
/// **The engine keeps a queue per player and then flattens them by submission time.**
/// `ActionQueueSet` really does hold one `ActionQueue` per player — the log says
/// `Enqueueing action … to player queue owned by 1001` — but `GetReadyAction` walks them all and
/// takes `gameAction2.Id < gameAction.Id`, the globally lowest action id, and the host hands ids
/// out in arrival order. So the per-player structure collapses into one stream ordered by when each
/// player clicked. Reported from play: two Defends queued a clear second before the opponent's
/// Strike, and "it seemed as though they were semi queued based on time of play as opposed to each
/// player having their own queue".
///
/// # Why the obvious fix is a desync
///
/// `ActionQueue.isPaused` looks like the answer — `GetReadyAction` skips a paused queue's
/// play-phase actions and takes the other player's instead, so the engine can genuinely run two
/// queues independently. But pausing a queue changes **which action executes next**, and that is
/// sim-visible: a client pacing its own queue on its own wall clock would pick a different action
/// from the host within one card. Per-player cadence therefore has to be decided *once*, by the
/// host, and expressed in the order it assigns ids — which is what this does.
///
/// # The rule
///
/// Every play is stamped with a tick, taken from **its own player's** next free slot rather than
/// from the shared stream:
///
///     tick = max(tick now, that player's next free tick)
///
/// so a player who fires three cards in half a second occupies three consecutive ticks of their
/// own, and the other player's ticks are unaffected. Neither can push the other's cards later,
/// which is the whole of "each player has their own queue".
///
/// **Ties inside a tick go to initiative** — whoever reached the arena first, alternating each turn
/// (M9). That is the part that matters for fairness: bucketing alone removes only the *sub-tick*
/// slice of the host's advantage, because the host's own requests never cross the network. Ordering
/// a shared bucket by arrival would hand the whole problem straight back. Initiative also gives the
/// race's reward the same meaning in both modes.
///
/// # What it costs
///
/// Every play waits up to one tick before it is enqueued, including the host's. That is the
/// pacing, and it is also what makes the two clients agree: nothing is ordered by when a packet
/// happened to land.
/// </summary>
public static class DuelTickScheduler
{
    /// <summary>
    /// How long a tick is. Matches `TickTurnModel`'s submit cooldown, so a player firing at their
    /// maximum rate lands exactly one card per tick and never queues behind themselves.
    /// </summary>
    private const double TickMs = 400;

    private sealed class Slot
    {
        public required GameAction Action;
        public required ulong Owner;
        public required long Tick;
    }

    private static readonly List<Slot> _pending = new List<Slot>();
    private static readonly Dictionary<ulong, long> _nextFreeTick = new Dictionary<ulong, long>();
    private static DateTime _tickZero = DateTime.MinValue;
    private static bool _pumping;

    /// <summary>Drops everything, so the next duel starts its ticks from zero.</summary>
    public static void Reset()
    {
        _pending.Clear();
        _nextFreeTick.Clear();
        _tickZero = DateTime.MinValue;
    }

    /// <summary>
    /// Takes a play — the host's own or one that arrived from the client — and books it a tick.
    ///
    /// Host only. A client never calls this: it submits through the engine's ordinary request path
    /// and the host books it on arrival, so exactly one machine decides the order.
    /// </summary>
    public static void Submit(GameAction action, ulong ownerId)
    {
        if (_tickZero == DateTime.MinValue)
        {
            _tickZero = DateTime.UtcNow;
        }

        long now = (long)((DateTime.UtcNow - _tickZero).TotalMilliseconds / TickMs);
        long tick = Math.Max(now, _nextFreeTick.TryGetValue(ownerId, out long next) ? next : 0);
        _nextFreeTick[ownerId] = tick + 1;

        _pending.Add(new Slot { Action = action, Owner = ownerId, Tick = tick });
        Log.Info($"[SpirePvp] tick: booked {action} for {ownerId} into tick {tick} (now {now})");
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

    /// <summary>
    /// Releases each tick's plays when that tick comes due, both players' together and in
    /// initiative order.
    ///
    /// Frames rather than a timer, so the wait is wall-clock and cannot be shortened by a display
    /// preference — this is a rule, not an animation.
    /// </summary>
    private static async Task PumpAsync()
    {
        try
        {
            while (_pending.Count > 0)
            {
                long due = long.MaxValue;
                foreach (Slot slot in _pending)
                {
                    due = Math.Min(due, slot.Tick);
                }

                while ((DateTime.UtcNow - _tickZero).TotalMilliseconds < due * TickMs)
                {
                    await Engine.GetMainLoop().ToSignal(Engine.GetMainLoop(), SceneTree.SignalName.ProcessFrame);
                }

                Release(due);
            }
        }
        catch (Exception e)
        {
            // A scheduler that stops draining is a duel where nothing ever resolves, so say so
            // rather than leaving two players clicking at a dead board.
            Log.Error($"[SpirePvp] tick: scheduler stopped — {e.Message}");
        }
        finally
        {
            _pumping = false;
        }
    }

    private static void Release(long tick)
    {
        RunManager? run = RunManager.Instance;
        if (run == null)
        {
            return;
        }

        // Initiative first, then everyone else. Read per release rather than cached, because it
        // alternates each turn and a tick can straddle a turn boundary.
        ulong leader = (DuelTurnModel.Current as IPlanningTurnModel)?.CurrentLeader ?? 0;

        List<Slot> due = new List<Slot>();
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (_pending[i].Tick == tick)
            {
                due.Insert(0, _pending[i]);
                _pending.RemoveAt(i);
            }
        }

        due.Sort((a, b) =>
        {
            bool aFirst = a.Owner == leader;
            bool bFirst = b.Owner == leader;
            return aFirst == bFirst ? 0 : (aFirst ? -1 : 1);
        });

        foreach (Slot slot in due)
        {
            run.ActionQueueSynchronizer.EnqueueAction(slot.Action, slot.Owner);
        }

        if (due.Count > 1)
        {
            Log.Info($"[SpirePvp] tick {tick}: released {due.Count} plays, {leader} first");
        }
    }
}
