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
    /// <summary>
    /// The gap between your plays leaving, in milliseconds.
    ///
    /// Lucas's figure, 2026-08-12: "maybe .4 second ish". Shorter than the 0.6s OSRS tick M8.5
    /// takes as its reference, and a playtest question rather than a design one.
    /// </summary>
    private const double CooldownMs = 400;

    public string Name => "real-time (paced)";

    /// <summary>Plays waiting for the cooldown, in the order they were clicked.</summary>
    private readonly List<GameAction> _queued = new List<GameAction>();

    private DateTime _nextRelease = DateTime.MinValue;

    /// <summary>Re-entrancy guard: the pump submits through the same path it defers on.</summary>
    private bool _releasing;

    private bool _pumping;

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
    /// **Zero, because the tick is the rhythm.** `DuelTickScheduler` releases one tick's plays at a
    /// time from the host, so both clients already see a gap between ticks and see it identically.
    /// An executor beat on top would pace the stream a second time, and worse, it would pace it
    /// *globally*: one gap shared between two players halves each player's cadence, which is the
    /// thing slice 2 exists to stop. The gap belongs between a player's own consecutive cards, and
    /// the per-player tick is exactly that.
    /// </summary>
    public float BeatSeconds => 0f;

    private ulong _firstInitiative;
    private int _turnsSeen;

    public void SetInitiative(ulong netId)
    {
        _firstInitiative = netId;
        Log.Warn($"[SpirePvp] paced: opening initiative to {netId} (reached the arena first)");
    }

    /// <summary>
    /// Whoever reached the arena first, alternating each turn — the same M9 rule the lock-in model
    /// uses, doing a different job here: breaking ties inside a tick.
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

            if (_turnsSeen % 2 == 0)
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

        // **Your end turn queues behind your plays rather than jumping them.** Ending the turn with
        // cards still waiting would roll the turn over before they resolve, which is the mistake the
        // lock-in model spent four attempts learning (DESIGN §7).
        if (_queued.Count == 0 && DateTime.UtcNow >= _nextRelease)
        {
            _nextRelease = DateTime.UtcNow.AddMilliseconds(CooldownMs);

            // **Even the instant play goes through the scheduler on the host.** Letting it reach
            // `RequestEnqueue` would enqueue it there and then, in arrival order, which is the
            // advantage slice 2 removes — and it would do it for the one play most likely to
            // decide an exchange. Booking it costs nothing: its tick is the current one, which is
            // already due, so it is released on the next frame rather than after a wait.
            if (RunManager.Instance?.NetService.Type == NetGameType.Host)
            {
                DuelTickScheduler.Submit(action, LocalContext.NetId ?? 0UL);
                return true;
            }

            return false;
        }

        _queued.Add(action);
        LockInPlanView.ShowPlanned(action);
        Log.Info($"[SpirePvp] paced: queued {action} ({_queued.Count} waiting, "
                 + $"{ReservedEnergy} energy reserved)");
        Pump();
        return true;
    }

    /// <summary>Clears the queue at a turn boundary, where energy refills and nothing may carry over.</summary>
    public void OnTurnStarted()
    {
        _turnsSeen++;

        if (_queued.Count > 0)
        {
            Log.Warn($"[SpirePvp] paced: dropping {_queued.Count} play(s) still queued at turn start");
            _queued.Clear();
        }

        _nextRelease = DateTime.MinValue;
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

    private void Pump()
    {
        if (_pumping)
        {
            return;
        }

        _pumping = true;
        TaskHelper.RunSafely(PumpAsync());
    }

    /// <summary>
    /// Releases one queued play per cooldown.
    ///
    /// Frames rather than a timer, so the wait is wall-clock and unaffected by Fast Mode; the loop
    /// costs one comparison a frame and only runs while something is queued.
    /// </summary>
    private async Task PumpAsync()
    {
        try
        {
            while (_queued.Count > 0)
            {
                while (DateTime.UtcNow < _nextRelease)
                {
                    await Engine.GetMainLoop().ToSignal(Engine.GetMainLoop(), SceneTree.SignalName.ProcessFrame);
                }

                GameAction next = _queued[0];
                _queued.RemoveAt(0);
                _nextRelease = DateTime.UtcNow.AddMilliseconds(CooldownMs);
                Release(next);
            }
        }
        catch (Exception e)
        {
            // A queue that stops draining is a hand that never resolves, so say so rather than
            // leaving the player clicking at a dead interface.
            Log.Error($"[SpirePvp] paced: pump stopped — {e.Message}");
        }
        finally
        {
            _pumping = false;
        }
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
            DuelTickScheduler.Submit(action, LocalContext.NetId ?? 0UL);
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
