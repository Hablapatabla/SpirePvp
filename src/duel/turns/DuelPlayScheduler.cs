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
    /// <summary>
    /// The gap between one player's own plays, in milliseconds — the cooldown that spaces a burst
    /// out along the timeline.
    ///
    /// **It lives here rather than in `TickTurnModel` because it has to apply to both players, and
    /// only the host can apply it to both.** A client sends each click as it happens; the host
    /// spaces that player's plays by the same rule it spaces its own, so neither side's burst
    /// depends on how their local queue happened to drip.
    ///
    /// Lucas's figure, 2026-08-12: "maybe .4 second ish".
    /// </summary>
    private const double CooldownMs = 400;

    /// <summary>
    /// How close two plays must be to count as simultaneous rather than ordered.
    ///
    /// Below this, "who was first" is not a real question — it is scheduler jitter and a frame or
    /// two of network, and answering it by clock would just be the host's shorter path to itself
    /// wearing a timestamp. Those go to initiative instead.
    /// </summary>
    private const double SimultaneousMs = 60;

    private sealed class Pending
    {
        public required GameAction Action;
        public required ulong Owner;

        /// <summary>
        /// When this play happens on the duel's shared timeline: the moment it was made, pushed
        /// later only by this player's own cooldown.
        /// </summary>
        public required DateTime PlayAt;

        /// <summary>Position in this player's own run of plays. Logging only — the clock orders.</summary>
        public required int Index;
    }

    private static readonly List<Pending> _pending = new List<Pending>();
    private static readonly Dictionary<ulong, int> _nextIndex = new Dictionary<ulong, int>();

    /// <summary>Where each player's own line has reached, so their next play is spaced from it.</summary>
    private static readonly Dictionary<ulong, DateTime> _lastPlayAt = new Dictionary<ulong, DateTime>();

    private static bool _pumping;

    /// <summary>
    /// Ties already broken this turn. See <see cref="Release"/> — the leader takes the first, the
    /// other player the second, and so on.
    /// </summary>
    private static int _tiesThisTurn;

    /// <summary>Drops everything, so the next duel starts even.</summary>
    public static void Reset()
    {
        _pending.Clear();
        _nextIndex.Clear();
        _lastPlayAt.Clear();
        _tiesThisTurn = 0;
    }

    /// <summary>
    /// A new turn starts the tie alternation over, so initiative's first strike is the leader's in
    /// every turn rather than in every other one.
    /// </summary>
    public static void OnTurnStarted() => _tiesThisTurn = 0;

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

        // **Where the play lands on the timeline.** Now, unless this player's previous play was
        // recent enough that the cooldown pushes this one later. A burst therefore occupies 0, 0.4,
        // 0.8 *of that player's own line*, and the opponent's single click at 0.3 falls between the
        // first and the second — which is the whole model, and is what the per-player index was a
        // poor proxy for.
        DateTime now = DateTime.UtcNow;
        DateTime playAt = now;
        if (_lastPlayAt.TryGetValue(ownerId, out DateTime last))
        {
            DateTime earliest = last.AddMilliseconds(CooldownMs);
            if (earliest > playAt)
            {
                playAt = earliest;
            }
        }

        _lastPlayAt[ownerId] = playAt;
        _pending.Add(new Pending { Action = action, Owner = ownerId, PlayAt = playAt, Index = index });

        double heldMs = (playAt - now).TotalMilliseconds;
        Log.Info($"[SpirePvp] queue: {ownerId}'s play #{index} pending at +{heldMs:F0}ms "
                 + $"({_pending.Count} waiting)");
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
                // **One card in flight at a time, and never before its moment.**
                //
                // The first half keeps the choice live: while the executor is working — including
                // the readable dwell `DuelPace` adds after each play — nothing is committed to an
                // id, so a card played during someone else's run can still take the next slot
                // instead of queueing behind all of it.
                //
                // The second half is what makes the cooldown mean anything. Releasing a burst as
                // fast as the executor allowed would put its second card into the slot that a card
                // played at 0.3s should get, and the interleave could never happen — the opponent's
                // play had not arrived yet, so the choice was made before there was a choice. So a
                // play waits for its own place on the line even when the board is idle.
                while (RunManager.Instance?.ActionExecutor.IsRunning == true || NothingIsDueYet())
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

    /// <summary>
    /// Whether every pending play is still in the future. Empty counts as nothing due, so the loop
    /// that calls this must be guarded by <c>_pending.Count > 0</c> — as it is.
    /// </summary>
    private static bool NothingIsDueYet()
    {
        DateTime now = DateTime.UtcNow;
        foreach (Pending candidate in _pending)
        {
            if (candidate.PlayAt <= now)
            {
                return false;
            }
        }

        return true;
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

        // **Earliest on the timeline wins.** Not a fairness quota — a chronology. Lucas, 2026-08-12:
        // "it is a matter of laying the events out on a timeline… player 2 plays a card at .3
        // seconds, that should beat player 1's second card at .5". The per-player index this
        // replaced was a proxy for exactly that and could invert it: play at 0 and 0.5 against a
        // single play at 0.6, and the index made the later card win for having been its owner's
        // first.
        DateTime earliest = DateTime.MaxValue;
        foreach (Pending candidate in _pending)
        {
            if (candidate.PlayAt < earliest)
            {
                earliest = candidate.PlayAt;
            }
        }

        Pending best = _pending[0];
        int tied = 0;
        foreach (Pending candidate in _pending)
        {
            if ((candidate.PlayAt - earliest).TotalMilliseconds <= SimultaneousMs)
            {
                tied++;
            }
        }

        string reason;
        if (tied <= 1 || leader == 0)
        {
            foreach (Pending candidate in _pending)
            {
                if (candidate.PlayAt == earliest)
                {
                    best = candidate;
                    break;
                }
            }

            reason = "earliest";
        }
        else
        {
            // **The tie alternates within the turn, and that is the whole of M9's advantage.**
            // Measured 2026-08-12: almost every contested play is #0 against #0, because each
            // player's own cooldown spaces their plays out so they reach the pool one at a time. A
            // tie-break that always went to the initiative holder therefore did not mean "you strike
            // first this turn" — it meant "you win every trade this turn", compounding across the
            // turn's exchanges and then inverting wholesale on the next one. That is far more than
            // reaching the arena first was meant to buy.
            //
            // So the leader takes the turn's *first* tie, the other player its second, and so on.
            // Initiative then means exactly what the arrow over the duelist claims. Deterministic
            // and host-side, so it cannot desync; a seeded coin flip would be equally safe and was
            // rejected for a different reason — "why did my card lose" has to have an answer a
            // player can plan around.
            bool leaderTakesIt = _tiesThisTurn % 2 == 0;
            _tiesThisTurn++;

            Pending? pick = null;
            foreach (Pending candidate in _pending)
            {
                if ((candidate.PlayAt - earliest).TotalMilliseconds > SimultaneousMs)
                {
                    continue;
                }

                bool isLeader = candidate.Owner == leader;
                if (isLeader == leaderTakesIt)
                {
                    pick = candidate;
                    break;
                }

                pick ??= candidate;
            }

            best = pick ?? best;
            reason = $"tie {_tiesThisTurn} this turn → {(leaderTakesIt ? "initiative" : "alternated")}";
        }

        _pending.Remove(best);
        run.ActionQueueSynchronizer.EnqueueAction(best.Action, best.Owner);
        Log.Info($"[SpirePvp] queue: releasing {best.Owner}'s play #{best.Index} "
                 + $"[{reason}] ({_pending.Count} still waiting)");
    }
}
