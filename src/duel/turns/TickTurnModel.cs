using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// Real time, paced: your first play goes instantly, everything after it leaves on a cooldown, and
/// what you click in between is queued rather than lost (DESIGN §7, M8.5 — slice 1 of 3).
///
/// **This replaces blitz rather than joining it.** Decided 2026-08-12: "I think that's what the
/// real time mode should actually be". Note the name collision before touching anything — *Blitz*
/// and *Rapid* in the Duel lobby are **clock presets**, chess terms for how much time each bank
/// gets, and have nothing to do with this. The turn model that happens to be called `DuelBlitz` in
/// code is what is being replaced, and its lobby entry already reads "Real-Time".
///
/// **The problem it solves is legibility, not fairness.** In unpaced blitz the opponent's effects
/// arrive as things that simply happen — damage lands, a power appears — with no readable moment
/// where a card was played, so a duel is two people clicking rather than two people fencing. A
/// cooldown makes each play an event; the queue means the cooldown costs you nothing you clicked.
///
/// **The first play is deliberately instant**, so opening speed still decides the first exchange
/// and the mode keeps blitz's texture at the moment it matters most. The cooldown only shapes what
/// follows.
///
/// **What this slice is not.** Two further pieces are specified and unbuilt, and the mode is
/// half-finished without them: the host does not yet quantise *resolution* into ticks (so the
/// ordering of two plays is still arrival order, with the host's inherent half-RTT edge), and the
/// opponent's queue is not yet on the wire (so you cannot read theirs, which is the whole point of
/// the pacing). Slice 2 buckets by tick and breaks ties inside a bucket by initiative — the M9 rule
/// — which is what actually removes the latency edge rather than merely shortening it.
///
/// **The cooldown is wall-clock, not `Cmd.Wait`.** That helper skips its wait entirely at
/// `FastModeType.Instant`, which is right for an animation and wrong for a *rule*: a display
/// preference must not delete a gameplay mechanic. `DuelPace` uses it because that gap is
/// presentation; this one is not.
/// </summary>
public sealed class TickTurnModel : IPlanningTurnModel
{
    public string Name => "real-time (paced)";

    /// <summary>
    /// Plays handed to the scheduler and not yet resolved, in the order they were clicked.
    ///
    /// **In flight, not waiting.** There was a 400ms submission cooldown here until 2026-08-12, and
    /// it was the reason the scheduler could never be fair — see <see cref="ShouldDefer"/>. The list
    /// stays because energy reservation and the queued-card highlight both need to know what you
    /// have committed but not yet seen resolve; it simply no longer gates anything.
    /// </summary>
    private readonly List<GameAction> _queued = new List<GameAction>();

    /// <summary>Re-entrancy guard: a release submits through the same path it defers on.</summary>
    private bool _releasing;

    /// <summary>
    /// Energy promised to queued plays.
    ///
    /// **A play stops being counted when it is released, not when it resolves**, which leaves a
    /// window of up to one cooldown where a card has been submitted, has not yet spent its energy,
    /// and is no longer reserved. It errs toward letting you queue, never toward refusing a card you
    /// can afford. Closing it means tracking in-flight plays through to execution, which is slice
    /// 2's business — it needs the same bookkeeping to bucket them.
    /// </summary>
    public int ReservedEnergy
    {
        get
        {
            int total = 0;
            foreach (GameAction action in _queued)
            {
                CardModel? card = CardOf(action);
                if (card != null)
                {
                    total += Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
                }
            }

            return total;
        }
    }

    public bool IsPlanned(CardModel card)
    {
        foreach (GameAction action in _queued)
        {
            if (ReferenceEquals(CardOf(action), card))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Never. The cooldown decides when a play *leaves*; it never decides whether you may make one,
    /// which is the difference between queueing and being locked out.
    /// </summary>
    public bool HandIsClosed => false;

    /// <summary>
    /// A dwell after each play, so a card that resolves is something you saw resolve.
    ///
    /// **This was zero for one build and that was wrong**, on the theory that the scheduler's tick
    /// already provided the rhythm. It does not: the tick paces *submission into the queue*, so a
    /// lone card whose tick is already due is enqueued and resolved in the same frame. Reported
    /// immediately — "card plays feel instantaneous again now" — and the reasoning behind the zero
    /// was the mistake, not the number. A gap between ticks is only visible when there is a queue;
    /// the gap after a card has to exist whether or not anything follows it.
    ///
    /// The cost is real and worth knowing: one executor, one gap, so two players both firing at
    /// their full 0.4s cadence generate cards faster than the stream resolves them and the
    /// resolution falls behind the clicking. That is the queue doing its job rather than a fault —
    /// it is what "can be queued" means — but if it ever feels like lag rather than like a backlog,
    /// this number is the knob: the scheduler releases one card per drain, so the dwell *is* the
    /// resolution rate.
    ///
    /// **0.55s from 2026-08-12**, Lucas's figure after playing the fixed ordering: "still a bit too
    /// quick to react". It is deliberately no longer equal to <see cref="CooldownMs"/>, and that
    /// gap is the thing to watch — you may now submit (0.4s) slightly faster than the stream
    /// resolves (0.55s), so a player clicking flat out builds a backlog of 0.15s per play. Energy
    /// bounds it in practice: a turn is about three plays, so the drift is under half a second and
    /// self-clears at the turn boundary. If it ever reads as lag, raise the cooldown to match rather
    /// than dropping this one back — the reaction window is what the mode is *for*.
    /// </summary>
    public float BeatSeconds => 0.55f;

    private ulong _firstInitiative;

    /// <summary>
    /// Turns *started*, so the first turn of the duel is 1 — not the lock-in model's turns *closed*,
    /// which is 0 for that same turn. `CurrentLeader` is where that difference bit.
    /// </summary>
    private int _turnsSeen;

    public void SetInitiative(ulong netId)
    {
        _firstInitiative = netId;
        Log.Warn($"[SpirePvp] paced: opening initiative to {netId} (reached the arena first)");
    }

    /// <summary>
    /// Whoever reached the arena first, alternating each turn — the same M9 rule the lock-in model
    /// uses, doing a different job here: breaking ties inside a tick.
    ///
    /// **The parity is on the turn number, not on the counter, and the two are not the same
    /// counter in the two models.** `LockInTurnModel` counts turns *closed*, which is 0 throughout
    /// the opening turn, so `% 2 == 0` means "the first turn" there and is right. This model counts
    /// turns *started*, and `OnTurnStarted` increments before anything reads this — so the same test
    /// made turn 1 odd and handed the opening initiative to whoever reached the arena **second**,
    /// with every turn after it inverted too. Measured 2026-08-12: `opening initiative to 1
    /// (reached the arena first)` and then `initiative: 1001 strikes first this turn` on turn 1.
    ///
    /// Not cosmetic. `DuelPlayScheduler` breaks ties on this, and because the pool drains between
    /// plays the per-player indices reset constantly — so most contested plays are #0 against #0 and
    /// **initiative decides them**. An inverted opening turn is a turn of tempo handed to the wrong
    /// player, which is what "it felt like it was waiting for the other player first" was.
    /// </summary>
    public ulong CurrentLeader
    {
        get
        {
            IRunState? state = RunManager.Instance?.State;
            if (state == null)
            {
                return 0;
            }

            ulong opening = _firstInitiative;
            if (opening == 0)
            {
                foreach (Player player in state.Players)
                {
                    if (opening == 0 || player.NetId < opening)
                    {
                        opening = player.NetId;
                    }
                }
            }

            // Clamped rather than raw: this is read before the first `TurnStarted` too — the
            // scheduler asks on every release — and the duel's opening moments belong to the same
            // player its first turn does.
            int turnNumber = Math.Max(_turnsSeen, 1);
            if (turnNumber % 2 == 1)
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
    }

    public bool ShouldDefer(GameAction action)
    {
        if (_releasing)
        {
            return false;
        }

        // **An undo cancels a queued end turn rather than racing it.** The end turn is queued behind
        // your plays, so backing out while it is still waiting means there is nothing to undo yet —
        // dropping it is the whole of the undo, and letting vanilla's undo through as well would
        // un-ready a player who was never marked ready.
        if (action is UndoEndPlayerTurnAction)
        {
            return DropQueuedEndTurn();
        }

        // Exactly vanilla's own test for "belongs to the play phase" — the same category the engine
        // defers during an enemy turn, and the same one the lock-in model holds.
        if (action.ActionType != GameActionType.CombatPlayPhaseOnly)
        {
            return false;
        }

        // **Hand it over the moment it is clicked. The pacing is the scheduler's job, not ours.**
        //
        // This used to hold a burst locally and drip it out one card per cooldown, which quietly
        // destroyed the information the scheduler needs. Measured 2026-08-12, over a whole duel:
        // **22 bookings, every one `pending (1 waiting)`, every one `#0`.** A player who fired three
        // cards had them booked as three separate `#0`s, seconds apart, each released into an idle
        // executor before the opponent's play had even arrived — so the opponent's first card was
        // ordered behind all three, which is the exact "got stiffed" case slice 2 was written to
        // fix and had been reported fixed. The fairness rule could not fire, because the backlog it
        // compares against was hidden in *this* class rather than in the pool.
        //
        // Now every play goes straight to the pool, so a burst is `#0, #1, #2` and the opponent's
        // first card is a `#0` that beats `#1` and `#2` — which is what "your first beats their
        // second" was always supposed to mean.
        //
        // **The cooldown moved to the scheduler rather than disappearing**, and the move is the
        // point. It still spaces your burst — your three clicks are played at 0, 0.4 and 0.8 on your
        // own line — but the spacing is now applied to *both* players by the one machine that sees
        // both, so an opponent's click at 0.3 lands between your first and your second instead of
        // behind all three. Held here, the same rule could only ever delay your own submissions,
        // which is how a burst came to look instantaneous to the scheduler and unbeatable to the
        // opponent.
        //
        // **Your end turn still queues behind your plays rather than jumping them**, which was the
        // gate's other job and is now the scheduler's: a player's own indices only ever increase,
        // and they are reset only when that player has nothing pending, so their end turn cannot
        // overtake their own cards. That was the mistake the lock-in model spent four attempts
        // learning (DESIGN §7).
        _queued.Add(action);
        LockInPlanView.ShowPlanned(action);
        Log.Info($"[SpirePvp] paced: submitting {action} ({_queued.Count} in flight, "
                 + $"{ReservedEnergy} energy reserved)");
        Release(action);
        return true;
    }

    /// <summary>
    /// Drops a play from the in-flight list once the sim is done with it, so its energy stops being
    /// reserved.
    ///
    /// **A cancelled play never gets here**, because the executor skips it before firing either of
    /// its events (the same fact `DuelPace.WatchBatch` is built around). Such a play stays reserved
    /// until <see cref="OnTurnStarted"/> clears the list, so the cost of that edge is bounded to one
    /// turn — and it errs toward reserving energy you have already spent rather than letting you
    /// spend energy twice, which is the safe direction of the two.
    /// </summary>
    public void OnActionResolved(GameAction action)
    {
        // **Identity is not enough, and on a client it is never enough.** A play is submitted as a
        // request and comes back as the host's ordered copy, so the object that executes here is not
        // the object we put in the list — the same mismatch `DuelPlanQueuePatch` handles for the
        // queue view, and it would leak every client-side reservation until the turn rolled. Match
        // the card model, which survives the round trip.
        CardModel? resolved = CardOf(action);
        for (int i = 0; i < _queued.Count; i++)
        {
            GameAction queued = _queued[i];
            bool same = ReferenceEquals(queued, action)
                || (resolved != null && ReferenceEquals(CardOf(queued), resolved))
                || (action is EndPlayerTurnAction && queued is EndPlayerTurnAction);

            if (!same)
            {
                continue;
            }

            _queued.RemoveAt(i);
            Log.Info($"[SpirePvp] paced: resolved {action} ({_queued.Count} still in flight)");
            return;
        }
    }

    /// <summary>
    /// Clears the in-flight list at a turn boundary, where energy refills and nothing may carry
    /// over. Also the backstop for a play the sim cancelled, which never reports back — see
    /// <see cref="OnActionResolved"/>.
    /// </summary>
    public void OnTurnStarted()
    {
        _turnsSeen++;

        if (_queued.Count > 0)
        {
            Log.Info($"[SpirePvp] paced: clearing {_queued.Count} in-flight play(s) at turn start");
            _queued.Clear();
        }
    }

    private bool DropQueuedEndTurn()
    {
        for (int i = _queued.Count - 1; i >= 0; i--)
        {
            if (_queued[i] is EndPlayerTurnAction)
            {
                _queued.RemoveAt(i);
                Log.Warn("[SpirePvp] paced: backed out of a queued end turn");
                return true;
            }
        }

        return false;
    }

    private void Release(GameAction action)
    {
        RunManager? run = RunManager.Instance;
        if (run == null)
        {
            return;
        }

        // **The host books its own plays into the tick scheduler rather than enqueueing them.**
        // Going straight to the queue would give the host's cards ids in arrival order — the very
        // advantage slice 2 removes — so both players' plays take the same route, and the only
        // difference is that a client's travels first.
        if (run.NetService.Type == NetGameType.Host)
        {
            DuelPlayScheduler.Submit(action, LocalContext.NetId ?? 0UL);
        }
        else
        {
            _releasing = true;
            try
            {
                run.ActionQueueSynchronizer.RequestEnqueue(action);
            }
            finally
            {
                _releasing = false;
            }
        }

        Log.Info($"[SpirePvp] paced: released {action} ({_queued.Count} still waiting)");
    }

    private static CardModel? CardOf(GameAction action) =>
        action is PlayCardAction play ? play.NetCombatCard.ToCardModelOrNull() : null;
}
