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

    /// <summary>
    /// How long a burst's next card waits at the release point for the opponent to answer, in
    /// milliseconds. See <see cref="HoldingForContest"/>.
    ///
    /// **Sized against what the log actually missed by**, not picked for feel: the two turns that
    /// failed had the client's card arrive a beat after the host's next card was released, while the
    /// turn that worked had it arrive just before. 150ms is wide enough to catch that margin and
    /// short enough to sit under the 400ms cooldown it shares a purpose with, so it never becomes
    /// the thing pacing a burst. It costs nothing at all to a player answering rather than bursting,
    /// because a `#0` is never held.
    /// </summary>
    private const double ContestWindowMs = 150;

    private sealed class Pending
    {
        public required GameAction Action;
        public required ulong Owner;

        /// <summary>
        /// When this play happens on the duel's shared timeline: the moment it was made, pushed
        /// later only by this player's own cooldown.
        /// </summary>
        public required DateTime PlayAt;

        /// <summary>
        /// Position in this player's own run of plays this turn, and **the primary ordering key** —
        /// see <see cref="Release"/>. It was demoted to logging for a spell while the clock ordered,
        /// and reinstated 2026-08-13 after a contest showed chronology handing the slot to whoever
        /// was already mid-burst. Reset only at the turn boundary.
        /// </summary>
        public required int Index;

        /// <summary>
        /// When this play reached the pool, so a release can say how long it actually waited.
        ///
        /// **Diagnostic only, and it exists because the felt complaint was unmeasurable.** Reported
        /// 2026-08-13: "the ironclad's defend waited for silent's strike instead of just going
        /// through". The log could not answer it — the `+Nms` on a booking is the *cooldown* offset,
        /// the game's log lines carry no timestamps, and nothing recorded the gap between a booking
        /// and its release. So "it waited" and "it went straight through" produced identical logs.
        /// </summary>
        public required DateTime BookedAt;
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
    public static void OnTurnStarted()
    {
        _tiesThisTurn = 0;

        // Both players start the turn even. This is the only place the per-player positions reset —
        // see the note in `PumpAsync` about why a drained pool is not such a place.
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
        _pending.Add(new Pending
        {
            Action = action, Owner = ownerId, PlayAt = playAt, Index = index, BookedAt = now
        });

        // **Whether the board was busy is the whole story of a "why did my card wait".** An idle
        // board means Release runs synchronously below and the card is committed to the executor
        // inside this very click — so nothing the opponent does afterwards can get ahead of it, and
        // no ordering rule is involved in what follows. A busy board is the only state in which two
        // plays can meet in the pool and actually be arbitrated.
        bool busy = RunManager.Instance?.ActionExecutor.IsRunning == true;
        double heldMs = (playAt - now).TotalMilliseconds;
        Log.Info($"[SpirePvp] queue: {ownerId}'s play #{index} pending at +{heldMs:F0}ms "
                 + $"({_pending.Count} waiting, board {(busy ? "BUSY" : "idle")})");
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
                // The first half keeps the choice live: while a card is queued or executing,
                // nothing further is committed to an id, so a card played during someone else's
                // run can still take the next slot instead of queueing behind all of it.
                //
                // The second half is what makes the cooldown mean anything. Releasing a burst as
                // fast as the executor allowed would put its second card into the slot that a card
                // played at 0.3s should get, and the interleave could never happen — the opponent's
                // play had not arrived yet, so the choice was made before there was a choice. So a
                // play waits for its own place on the line even when the board is idle.
                //
                // **Not `ActionExecutor.IsRunning`, and that distinction is the whole of a bug.**
                // `IsRunning` tracks the queue-drain task, which stays alive across
                // `WaitForUnpause` — so an executor merely *paused* for `DuelPace`'s readable beat
                // reads as busy. Gating on it made the host's own beat hold the client's card back:
                // measured 2026-08-13, `releasing 1001's play #0 after 579ms` with nothing running.
                //
                // It also deadlocked the beat's own cut-off against this loop. The beat shortens
                // itself once the opponent's play is *enqueued*; the play could not be enqueued
                // until the beat ended. Each waited for the other, so `pace: cut` fired only in the
                // cases where the card happened to be queued before the beat began.
                //
                // These two ask what was always meant: `IsEmpty` covers a card released and not yet
                // executed (a release enqueues immediately, so exactly one card leaves per drain,
                // and a *cancelled* action is dropped from the queue by the executor's own skip
                // rather than blocking here forever), and `CurrentlyRunningAction` covers one that
                // is genuinely mid-execution. Neither is true during a beat, which is a gap in the
                // presentation and not in the sim.
                DateTime freeSince = DateTime.MaxValue;
                while (true)
                {
                    bool busy = QueueIsBusy();
                    if (busy)
                    {
                        freeSince = DateTime.MaxValue;
                    }
                    else if (freeSince == DateTime.MaxValue)
                    {
                        freeSince = DateTime.UtcNow;
                    }

                    if (!busy && !NothingIsDueYet() && !HoldingForContest(freeSince))
                    {
                        break;
                    }

                    await Engine.GetMainLoop().ToSignal(Engine.GetMainLoop(), SceneTree.SignalName.ProcessFrame);
                }

                Release();
            }

            // **The indices deliberately survive a drained pool.** They used to be cleared here, on
            // the theory that a still board ended the exchange — but the pool drains between almost
            // every pair of plays, so every card in the duel was booked as `#0` and the per-player
            // position the whole rule is built on never existed. See `Release`. They reset at the
            // turn boundary instead, where energy refills and the exchange genuinely restarts.
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
    /// Whether to hold a burst's next card a moment longer, in case the opponent is answering.
    ///
    /// **The last gap between "my first card jumped their third" and "my first card waited for it",
    /// and the log named it exactly.** Measured 2026-08-13 across three turns of one match: in turn
    /// 3 the client's `#0` arrived while the host's `#2` was still pending and won on position in
    /// 50ms (`beat 1#2`); in turns 1 and 2 the identical play arrived `(1 waiting)` — *alone* — a
    /// moment after the host's `#2` had been released, and waited 901ms and 763ms behind it. An
    /// enqueued card cannot be preempted, so the whole difference was which side of one release
    /// instant the click landed on. Position ordering is powerless there: there is nothing to order.
    ///
    /// So a card that would *extend a burst* waits out a short window before being committed, and
    /// the opponent's first card can arrive inside it and take the slot instead.
    ///
    /// **A player's own first card is never held**, which is Lucas's rule almost verbatim
    /// (2026-08-13): *"their card play should come out instantly for the first card play and after
    /// that there is essentially a global cooldown."* The responsive case pays nothing; only the
    /// player already several cards deep waits, and only while they hold the stream alone.
    ///
    /// Dropped the moment the opponent has anything pending — then a contest exists and
    /// <see cref="Release"/> can do its job, so there is nothing left to wait for.
    /// </summary>
    private static bool HoldingForContest(DateTime freeSince)
    {
        if (freeSince == DateTime.MaxValue
            || (DateTime.UtcNow - freeSince).TotalMilliseconds >= ContestWindowMs)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        ulong soleOwner = 0;
        int lowestDue = int.MaxValue;
        foreach (Pending candidate in _pending)
        {
            if (candidate.PlayAt > now)
            {
                continue;
            }

            if (soleOwner == 0)
            {
                soleOwner = candidate.Owner;
            }
            else if (candidate.Owner != soleOwner)
            {
                // Both players are waiting: this is the contest the window exists to allow, so stop
                // waiting for it and go and arbitrate.
                return false;
            }

            if (candidate.Index < lowestDue)
            {
                lowestDue = candidate.Index;
            }
        }

        // Index 0 is a player's opening card of the turn and is never held.
        return lowestDue > 0 && lowestDue != int.MaxValue;
    }

    /// <summary>
    /// Whether the sim is still working: a card queued and not yet executed, or one executing now.
    ///
    /// Deliberately blind to <see cref="ActionExecutor.IsPaused"/> — see the wait loop above. A
    /// paused executor with an empty queue is a *reading gap*, and the next card may be chosen
    /// during it.
    /// </summary>
    private static bool QueueIsBusy()
    {
        RunManager? run = RunManager.Instance;
        if (run == null)
        {
            return false;
        }

        return !run.ActionQueueSet.IsEmpty || run.ActionExecutor.CurrentlyRunningAction != null;
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

        // **Only plays whose own cooldown has come round may be chosen.** Ordering below is by
        // position rather than by clock, so unlike a pure-chronology pick this has to say so:
        // otherwise a player's not-yet-due second card could be released ahead of its own beat.
        DateTime now = DateTime.UtcNow;
        List<Pending> due = new List<Pending>();
        foreach (Pending candidate in _pending)
        {
            if (candidate.PlayAt <= now)
            {
                due.Add(candidate);
            }
        }

        if (due.Count == 0)
        {
            return;
        }

        // **Your Nth card of the turn races their Nth card. Position, not chronology.**
        //
        // This restores the rule the class was written around and that its own summary still
        // describes — "round-robin by each player's own position in their own queue" — after a spell
        // ordering purely by clock. Both are Lucas's, and the second is what four playtests running
        // reported as wrong: *"the Ironclad's defend waited for Silent's strike instead of just going
        // through because it's the first card."*
        //
        // **Chronology looks fair and is not, because a burst buys its own priority.** Measured
        // 2026-08-13, the first genuine contest any log has held: the host's *third* card was booked
        // at `+261ms` (its own cooldown pushing it later) and the client's *first* was due at `+0ms`
        // — and the third card still won, because it had been *clicked* earlier and cooldown time is
        // still time. `releasing 1's play #0 [earliest] after 830ms — beat 1001#0`. The player
        // already monopolising the stream is exactly the player whose next card is nearest on the
        // clock, so chronology compounds a lead rather than arbitrating it.
        //
        // **The rule this replaced was argued from a case that position also gets right.** Lucas,
        // 2026-08-12: "player 2 plays a card at .3 seconds, that should beat player 1's second card
        // at .5" — player 2's card is their *first* and player 1's is their *second*, so position
        // gives player 2 as well. Position satisfies both reports; chronology satisfies only that
        // one. That is the whole argument for the change.
        //
        // What it genuinely gives up, stated plainly: your `#0` beats their `#1` even when yours was
        // clicked slightly later. Bounded by the cooldown, and it is the trade "your first card
        // cannot be buried by their burst" is made of.
        int lowest = int.MaxValue;
        foreach (Pending candidate in due)
        {
            if (candidate.Index < lowest)
            {
                lowest = candidate.Index;
            }
        }

        List<Pending> contenders = new List<Pending>();
        foreach (Pending candidate in due)
        {
            if (candidate.Index == lowest)
            {
                contenders.Add(candidate);
            }
        }

        // Same position on both sides: now the clock is the right question, because neither player
        // is ahead of the other in the exchange.
        DateTime earliest = DateTime.MaxValue;
        foreach (Pending candidate in contenders)
        {
            if (candidate.PlayAt < earliest)
            {
                earliest = candidate.PlayAt;
            }
        }

        Pending best = contenders[0];
        int tied = 0;
        foreach (Pending candidate in contenders)
        {
            if ((candidate.PlayAt - earliest).TotalMilliseconds <= SimultaneousMs)
            {
                tied++;
            }
        }

        string reason;
        if (tied <= 1 || leader == 0)
        {
            foreach (Pending candidate in contenders)
            {
                if (candidate.PlayAt == earliest)
                {
                    best = candidate;
                    break;
                }
            }

            reason = $"position #{lowest}";
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

            // Over `contenders`, not `_pending`: a tie is only a tie between plays at the *same*
            // position that are also due. Scanning everything pending here would let initiative hand
            // the slot to a card that had already lost on position, or to one whose own cooldown has
            // not come round.
            Pending? pick = null;
            foreach (Pending candidate in contenders)
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
            reason = $"position #{lowest}, tie {_tiesThisTurn} this turn → "
                     + $"{(leaderTakesIt ? "initiative" : "alternated")}";
        }

        _pending.Remove(best);

        // **Name who lost, before the enqueue.** A release that beat nothing and a release that beat
        // the opponent's card look identical otherwise, and `EnqueueAction` below runs the action to
        // completion synchronously, so anything logged after it prints a long way down the file.
        double waitedMs = (DateTime.UtcNow - best.BookedAt).TotalMilliseconds;
        string beaten = _pending.Count == 0
            ? "uncontested"
            : "beat " + string.Join(", ", _pending.Select(p => $"{p.Owner}#{p.Index}"));

        Log.Info($"[SpirePvp] queue: releasing {best.Owner}'s play #{best.Index} "
                 + $"[{reason}] after {waitedMs:F0}ms — {beaten} ({_pending.Count} still waiting)");

        run.ActionQueueSynchronizer.EnqueueAction(best.Action, best.Owner);
    }
}
