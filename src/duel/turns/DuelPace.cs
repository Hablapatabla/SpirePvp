using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// A readable beat between the cards of a resolving round.
///
/// **The problem is legibility, not fairness** (DESIGN §7, M8.5). A locked-in round arrives as a
/// burst: six cards resolve as fast as the queue can drain, damage numbers stack up, and neither
/// player can say afterwards what was played at them. Cards queue and resolve correctly and you
/// still cannot follow the fight. Reported 2026-08-12, after watching a round land.
///
/// So the round is paced: each play resolves, and then nothing happens for long enough to read it.
/// **This is the delay-the-resolution half of the choice M8.5 names**, not the lengthen-the-
/// animation half — the two look alike and only this one gives a real reading window, because the
/// next card's damage has not landed yet.
///
/// **Paced on each client, and the gap is the same length on both.** The gap is a `Cmd.Wait` —
/// vanilla's Godot-timer wait, which respects timescale and lag where `Task.Delay` would not — of
/// the model's own `BeatSeconds`. The alternative — the host releasing one action per tick — would
/// have made the host's personal preference decide the pace on both screens, and would have handed
/// the client's reading window to network jitter. Pacing locally cannot desync: it changes *when* a
/// client executes, never *what* or in which order, and the ordering is still entirely the host's.
///
/// **The beat is not scaled by Fast Mode**, and this summary claimed the opposite for a while. It is
/// a rule rather than an animation, and a display preference must not delete a mechanic — see
/// `BeatSeconds` below for the two separate reports that settled it. `Cmd.Wait` happens to make that
/// easy: it does not shorten at `Fast`, only skipping outright at `Instant` — and `Instant` cannot
/// occur inside a duel, because `DuelFastModePatch` pins both clients to `Fast` throughout.
///
/// **The pause is vanilla's own, and the unpause is the thing to be careful with.**
/// `ActionExecutor.Pause` is documented for exactly this — stopping the action queue without
/// pausing the rest of combat — and the executor's loop waits on it at the top of each iteration,
/// so a pause taken as one action finishes is a gap before the next one starts. Leaving it paused
/// would be a hard lock, with no action ever executing again, so the unpause sits in a `finally`
/// and <see cref="Reset"/> clears it unconditionally on run teardown.
/// </summary>
public static class DuelPace
{
    /// <summary>How long to wait for a flushed batch to start executing, in frames (~2s at 60fps).</summary>
    private const int StartFrames = 120;

    /// <summary>
    /// How often a beat re-asks whether the opponent has answered, in seconds.
    ///
    /// Small enough that the cut-off is not itself felt, large enough not to be a per-frame poll of
    /// a list. It bounds the error on the cross-player gap, not the gap itself.
    /// </summary>
    private const float SliceSeconds = 0.05f;

    private static bool _armed;

    private static bool _watching;

    /// <summary>Owners of plays enqueued and not yet executed. See <see cref="OnActionEnqueued"/>.</summary>
    private static readonly List<ulong> _queuedPlays = new List<ulong>();

    /// <summary>Owners of plays that have executed since the last sweep, retired from the list above.</summary>
    private static readonly List<ulong> _executedPlays = new List<ulong>();

    /// <summary>
    /// Armed at run start with every other handler, and against the run's own executor — a new run
    /// builds a new `ActionExecutor`, so a subscription kept across runs would be pointing at a
    /// dead one while `_armed` still claimed otherwise.
    /// </summary>
    public static void Arm()
    {
        if (_armed)
        {
            return;
        }

        ActionExecutor? executor = RunManager.Instance?.ActionExecutor;
        if (executor == null)
        {
            return;
        }

        executor.AfterActionExecuted += OnActionExecuted;

        // Armed here with everything else rather than on first use, and against this run's own
        // queue set — `RunManager` builds a new one per run alongside the executor above.
        ActionQueueSet? queues = RunManager.Instance?.ActionQueueSet;
        if (queues != null)
        {
            queues.ActionEnqueued += OnActionEnqueued;
        }

        _armed = true;
    }

    public static void Reset()
    {
        ActionExecutor? executor = RunManager.Instance?.ActionExecutor;
        if (executor != null)
        {
            executor.AfterActionExecuted -= OnActionExecuted;

            // Unconditional: a run torn down mid-beat would otherwise leave the next run's queue
            // paused, and a queue that never runs is indistinguishable from a hung game.
            executor.Unpause();
        }

        ActionQueueSet? queues = RunManager.Instance?.ActionQueueSet;
        if (queues != null)
        {
            queues.ActionEnqueued -= OnActionEnqueued;
        }

        _queuedPlays.Clear();
        _executedPlays.Clear();
        _armed = false;
        _watching = false;
    }

    /// <summary>
    /// Watches a just-flushed batch until it has finished resolving, then reopens planning.
    ///
    /// Called from the flush on both sides, which is the only moment both of them agree a batch
    /// exists. Two things make this harder than awaiting the queue:
    ///
    /// - **`FinishedExecutingActions` answers "finished" when nothing is running**, which at flush
    ///   time is exactly the state a client is in — the host's actions are still crossing the wire.
    ///   Awaiting it straight away would reopen planning into the batch it was meant to wait for.
    ///   So this waits for the drain to *start* first.
    /// - **A batch can never start at all.** The executor skips a cancelled action before firing
    ///   `BeforeActionExecuted`, so a batch whose every play was cancelled — a card whose target
    ///   died, a hand discarded by the play before it — executes nothing. Hanging on that would
    ///   leave the hand live, the button dark and no way to commit: a soft lock with no error. The
    ///   wait for the start is therefore bounded, and giving up reopens planning rather than
    ///   waiting forever.
    ///
    /// Frames rather than seconds, because `Cmd.Wait` returns instantly at `FastModeType.Instant`
    /// and this loop would spin through its whole budget in one frame.
    /// </summary>
    public static void WatchBatch()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        TaskHelper.RunSafely(WatchBatchAsync());
    }

    private static async Task WatchBatchAsync()
    {
        try
        {
            ActionExecutor? executor = RunManager.Instance?.ActionExecutor;
            if (executor == null)
            {
                return;
            }

            for (int frame = 0; frame < StartFrames && !executor.IsRunning; frame++)
            {
                await Engine.GetMainLoop().ToSignal(Engine.GetMainLoop(), SceneTree.SignalName.ProcessFrame);
            }

            await executor.FinishedExecutingActions();
        }
        catch (Exception e)
        {
            // Reopen anyway: a planning phase that never comes back is a dead game, and whatever
            // went wrong has already been logged by the executor.
            Log.Error($"[SpirePvp] pace: batch watch failed — reopening planning anyway ({e.Message})");
        }
        finally
        {
            _watching = false;
        }

        (DuelTurnModel.Current as LockInTurnModel)?.OnBatchResolved();
    }

    private static void OnActionExecuted(GameAction action)
    {
        // The paced model reserves energy for everything it has handed to the scheduler, so it needs
        // telling when the sim is finished with one. Outside the beat's own guards below, because a
        // play still has to stop being reserved in a model or a mode that takes no beat at all.
        (DuelTurnModel.Current as TickTurnModel)?.OnActionResolved(action);

        // Every model that holds plays wants a beat after each one; only the length differs, and
        // the model owns that. Unpaced blitz (`BlitzTurnModel`) is the one that does not, and
        // nothing selects it any more.
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not IPlanningTurnModel)
        {
            return;
        }

        // A beat belongs after a *play*. The end turns are the round's last two actions and the
        // turn roll has its own banner, so padding those only delays the next planning phase.
        if (action is not PlayCardAction && action is not UsePotionAction)
        {
            return;
        }

        _executedPlays.Add(action.OwnerId);
        TaskHelper.RunSafely(Beat(action.OwnerId));
    }

    /// <summary>
    /// Records a play as it is enqueued, so <see cref="Beat"/> can tell whose card is waiting.
    ///
    /// **The queue is the only place this is knowable, and only from the outside.** `GetReadyAction`
    /// is the executor's own consumer and is not something to call from a mod for a look; the
    /// enqueue event carries the same `GameAction`, and `GameAction.OwnerId` is on the base type.
    ///
    /// Entries are retired as their plays execute. **A cancelled play never executes and so leaks
    /// one entry** — the executor skips it before firing either event, the same fact
    /// `WatchBatch` is built around. The cost is bounded and one-directional: a stale foreign entry
    /// can only *shorten* a beat, never extend one, and the list is cleared on run teardown.
    /// </summary>
    private static void OnActionEnqueued(GameAction action)
    {
        if (action is PlayCardAction or UsePotionAction)
        {
            _queuedPlays.Add(action.OwnerId);
        }
    }

    /// <summary>Whether a play by anyone other than <paramref name="playedBy"/> is waiting.</summary>
    private static bool ForeignPlayWaiting(ulong playedBy)
    {
        // Retire what has already executed, so only genuinely waiting plays are considered.
        foreach (ulong owner in _executedPlays)
        {
            _queuedPlays.Remove(owner);
        }

        _executedPlays.Clear();

        foreach (ulong owner in _queuedPlays)
        {
            if (owner != playedBy)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task Beat(ulong playedBy)
    {
        ActionExecutor? executor = RunManager.Instance?.ActionExecutor;
        float seconds = BeatSeconds();
        if (executor == null || seconds <= 0f)
        {
            return;
        }

        float crossSeconds = Math.Min(CrossBeatSeconds(), seconds);

        executor.Pause();
        try
        {
            // **Waited in slices so the opponent's answer can cut it short.** Their card is usually
            // not queued yet when the beat begins — the host's scheduler releases it once the
            // executor's drain completes, which is a moment *after* this pause is taken — so a
            // decision made only at the top would take the full beat every time and the whole
            // change would be invisible. Re-asking each slice is what makes it land.
            //
            // Not `ignoreCombatEnd`: a killing blow should land and be over, rather than holding
            // the result screen behind a beat nobody is waiting to read.
            float waited = 0f;
            while (waited < seconds)
            {
                if (waited >= crossSeconds && ForeignPlayWaiting(playedBy))
                {
                    // The one line that says this worked. Without it a cut beat and a full beat are
                    // indistinguishable in the log, which is the state the whole report was stuck in.
                    Log.Info($"[SpirePvp] pace: cut {playedBy}'s beat at {waited:F2}s of {seconds:F2}s "
                             + "— the opponent has answered");
                    break;
                }

                float slice = Math.Min(SliceSeconds, seconds - waited);
                await Cmd.Wait(slice);
                waited += slice;
            }
        }
        catch (Exception e)
        {
            Log.Error($"[SpirePvp] pace: beat failed, unpausing anyway — {e.Message}");
        }
        finally
        {
            executor.Unpause();
        }
    }

    /// <summary>
    /// The gap, from the model, and **not scaled by Fast Mode**.
    ///
    /// It used to be, at Lucas's request, and that was wrong twice over. Reported 2026-08-12: "card
    /// plays are feeling quite instantaneous… I think it should be a feelable delay even in fast
    /// mode" — a preference that can shrink the beat to nothing can delete the mechanic it exists
    /// to provide. And in a real-time duel it is an *advantage*: whoever renders faster sees the
    /// board settle sooner and can answer sooner, which is a match decided partly by a settings
    /// screen. `DuelFastModePatch` now holds Fast Mode itself level for the duration of a duel, so
    /// vanilla's own animation lengths are equal too; this number is simply the model's.
    /// </summary>
    private static float BeatSeconds() =>
        DuelTurnModel.Current is IPlanningTurnModel model ? model.BeatSeconds : 0f;

    /// <summary>
    /// The gap owed before the *other* duelist's card. See
    /// <see cref="IPlanningTurnModel.CrossPlayerBeatSeconds"/> — the lock-in model returns its full
    /// beat here, so a resolving round is paced exactly as it was.
    /// </summary>
    private static float CrossBeatSeconds() =>
        DuelTurnModel.Current is IPlanningTurnModel model ? model.CrossPlayerBeatSeconds : 0f;
}
