using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Duel.Turns;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Nothing resolves while a duelist is inside a card selection — the whole batch waits, rather than
/// the opponent's cards running on without them.
///
/// # What happened
///
/// Measured in the first Steam duel, 2026-08-13, and it decided the match. The round was committed
/// perfectly interleaved — `Blessing(them), Skill Potion(us), Perfected Strike(them), Uppercut(us),
/// Strike(them), Sword Boomerang(us)` — and then:
///
/// ```
/// Pausing action UsePotionAction … for player choice
/// Action UsePotionAction … at front of player queue …070256419 is waiting for player choice
/// Action PlayCardAction CARD.PERFECTED_STRIKE with id 13 … becomes new ready action
/// Action PlayCardAction CARD.STRIKE_IRONCLAD  with id 15 … becomes new ready action
/// Combat state becomes NotInCombat. Cancelling deferred actions
/// Cancelling action PlayCardAction card: CARD.UPPERCUT
/// ```
///
/// The Skill Potion gathered a choice, which parks it at the front of its owner's queue.
/// `ActionQueueSet.GetReadyAction` walks the queues and `continue`s past any whose front action is
/// `GatheringPlayerChoice` — so it skipped that player entirely and took the opponent's next two
/// cards back to back. **Uppercut sat at id 14, between their 13 and 15, and could not run** because
/// it was behind the potion in its own queue. The two strikes killed its owner, combat ended, and
/// the batch's remainder was cancelled unexecuted.
///
/// **Vanilla is right and this is a duel-only disagreement.** Pausing one queue is exactly what co-op
/// wants: one player opening a card selection must not freeze everyone else. In a duel the interleave
/// *is* the fairness rule — it is what "one of yours, one of theirs" means — so a stall that lets one
/// side keep swinging is the mode failing at the moment it matters most.
///
/// # What this fixes, and what it does not
///
/// **Fixes:** the opponent cannot act at all while you are in a selection. The window that killed a
/// player — two cards landing while they were in a menu, with their answer already committed and
/// unable to run — is closed.
///
/// **Does not fix, and this is the half worth knowing about.**
/// `ActionQueueSet.ResumeActionWithoutSynchronizing` resumes the choice-gathering action with
/// `ResumeAfterGatheringPlayerChoice(GetAndIncrementActionId())` — **a new id, higher than everything
/// already queued.** So once the choice is made, the resumed action goes to the *back* of the
/// ordering and its owner's later plays, which sit behind it in their own queue, go with it. In the
/// measured round that still leaves both of the opponent's strikes ahead of the Uppercut; they simply
/// no longer land while their owner is helpless.
///
/// Closing that half means keeping the original id across a resume, which is surgery on the one thing
/// both simulations order themselves by, so it wants its own change and its own playtest rather than
/// riding along here. **The cheap alternative, if it proves annoying before then, is to resolve a
/// choice at plan time** — DESIGN already weighs that for draw cards, and the same objection applies.
///
/// # Why this cannot desync
///
/// It removes an action from consideration; it never invents or reorders one. Both simulations see
/// the same `GatheringPlayerChoice` state, because the choice is synchronised — the peer's copy waits
/// on `PlayerChoiceSynchronizer` for the same choice id — so both stall and both release together.
/// And the executor is restarted for free: `ResumeActionWithoutSynchronizing` raises
/// `ActionQueueChanged`, which `ActionExecutor.ActionQueueChanged` answers by running `ExecuteActions`
/// whenever it is not already running. That is what makes returning null here safe rather than a
/// hang, and it was checked before this was written.
/// </summary>
[HarmonyPatch(typeof(ActionQueueSet), nameof(ActionQueueSet.GetReadyAction))]
public static class DuelChoiceStallPatch
{
    /// <summary>
    /// Who is currently inside a card selection, or 0 when nobody is.
    ///
    /// **Exposed because the clocks need the same fact.** Lucas, 2026-08-13: *"the time spent
    /// picking your choice should count towards your timer and your opponent's should freeze."*
    /// That is the right rule and it falls straight out of this state — a choice is thinking time,
    /// and a chess clock charges thinking time to whoever is doing it. `DuelClockService` reads this
    /// rather than deriving its own answer, so the stall and the billing can never disagree about
    /// who is deciding.
    /// </summary>
    public static ulong PlayerGatheringChoice()
    {
        ActionQueueSet? queues = RunManager.Instance?.ActionQueueSet;
        if (queues == null)
        {
            return 0;
        }

        foreach (ActionQueueSet.ActionQueue queue in queues._actionQueues)
        {
            if (queue.actions.Count > 0
                && queue.actions[0].State == GameActionState.GatheringPlayerChoice)
            {
                return queue.ownerId;
            }
        }

        return 0;
    }

    public static void Postfix(ActionQueueSet __instance, ref GameAction? __result)
    {
        if (__result == null || !DuelSession.IsDuelActive)
        {
            return;
        }

        foreach (ActionQueueSet.ActionQueue queue in __instance._actionQueues)
        {
            if (queue.actions.Count == 0)
            {
                continue;
            }

            GameAction front = queue.actions[0];
            if (front.State != GameActionState.GatheringPlayerChoice)
            {
                continue;
            }

            // The chooser's own resumed action is never held back by this: it is the front action
            // itself, and it leaves `GatheringPlayerChoice` before it is offered again.
            Log.Info($"[SpirePvp] batch: holding {__result} — {queue.ownerId} is inside a choice, "
                     + "and a duel's round resolves together or not at all");
            __result = null;
            return;
        }
    }
}

/// <summary>
/// A play that pauses for a card selection keeps its place in the round, instead of going to the
/// back when the choice is made.
///
/// **This is the other half of <see cref="DuelChoiceStallPatch"/>, and without it the interleave is
/// still broken — just less dangerously.** Vanilla resumes a choice-gathering action by handing it a
/// *fresh* id: `ResumeActionWithoutSynchronizing` calls
/// `ResumeAfterGatheringPlayerChoice(GetAndIncrementActionId())`, and that method assigns it to
/// `Id`. Since `ActionQueueSet.GetReadyAction` orders by lowest id, the resumed action is thereby
/// sent behind everything already queued — and its owner's later plays, which sit behind it in their
/// own queue, go with it. Measured 2026-08-13: a round committed as
/// `them, us, them, us, them, us` resolved with both of their strikes ahead of the Uppercut
/// committed between them.
///
/// **Vanilla's renumbering is right for co-op and wrong here, for one specific reason.** It exists so
/// that whatever the *other* players enqueued while you sat in a menu resolves before your resumed
/// action — they were never waiting on you, so they should not be held up. In a duel they *are*
/// waiting on you: `DuelChoiceStallPatch` stops the whole batch, so nothing of theirs is enqueued
/// during the choice, and the only thing renumbering can still do is discard an order both players
/// already committed to.
///
/// # Why this does not desync, which is the whole question
///
/// **The shared counter still advances.** `GetAndIncrementActionId()` is called by the caller
/// *before* this method and unconditionally, so it runs whether or not its value is used. This
/// changes which id the action carries, never how many ids have been handed out — which matters
/// because `ActionQueueSet._nextActionId` numbers a stream both peers execute in common, and pulling
/// it out of step is the single most expensive family of bugs this project has had (HANDOFF: the
/// `duel now` divergences).
///
/// **The id stays unique.** It is the action's own, assigned when it was enqueued and never given to
/// anything else; the action never left its queue while gathering.
///
/// **Both peers do the same thing.** The resume runs through `ActionQueueSynchronizer` on every
/// client — its own comment says it should be called from nowhere else — so both sims keep the same
/// id and order identically.
///
/// One consequence worth expecting rather than being surprised by: a resumed action can now execute
/// with an id lower than one already executed, so `Last executed action ID` in a state dump may go
/// backwards across a choice. That is symmetric, appears on both dumps, and is not a divergence.
/// </summary>
[HarmonyPatch(typeof(GameAction), nameof(GameAction.ResumeAfterGatheringPlayerChoice))]
public static class DuelChoiceKeepsPlacePatch
{
    public static void Prefix(GameAction __instance, ref uint newId)
    {
        if (!DuelSession.IsDuelActive || __instance.Id == null)
        {
            return;
        }

        Log.Info($"[SpirePvp] batch: {__instance} keeps id {__instance.Id.Value} across its choice "
                 + $"instead of taking {newId} — the round was committed in that order");
        newId = __instance.Id.Value;
    }
}

/// <summary>
/// Stops a card selection from cancelling the rest of your locked-in round.
///
/// **This is why a committed Defend never resolved.** Measured 2026-08-14, two lines apart:
///
/// ```
/// [ActionQueueSet] Cancelling action PlayCardAction card: CARD.DEFEND_IRONCLAD (49639619)
/// [SpirePvp] highlight: hand holder repainted during a choice — card DEFEND_IRONCLAD
/// ```
///
/// `ActionQueueSet.PauseActionForPlayerChoice` does this when the choice carries
/// `PlayerChoiceOptions.CancelPlayCardActions`:
///
/// ```csharp
/// CancelNonExecutingActionsOfType&lt;PlayCardAction&gt;(action.OwnerId, …);
/// queue.isCancellingPlayCardActions = true;
/// ```
///
/// — every queued play that player owns, gone. Burning Pact's exhaust selection carries that flag.
///
/// **Vanilla is right again, and for a reason that does not survive this mode.** Outside a duel
/// nothing is pre-committed: a card that gathers a choice is played on its own, so the only queued
/// plays are ones you queued *while* it was resolving, and a choice that exhausts or discards can
/// easily invalidate them. Cancelling them wholesale is cheap and safe. Under the lock-in model the
/// queue holds a **round you already committed to**, and this throws the rest of it away — the
/// player watches one card resolve and the others silently never happen.
///
/// **The targeted check it was standing in for still runs.** `PlayCardAction.ExecuteAction`
/// re-validates its own card and cancels a play whose pile has changed — the same fact the queued
/// card highlight is built on. So a play whose card really was exhausted by the choice still dies,
/// on its own merits, one card at a time. What is dropped here is only the blanket.
///
/// # A committed card is no longer offered as a target, and that is the better rule
///
/// A consequence rather than a goal, kept deliberately after Lucas saw it (2026-08-14): *"the queued
/// defend did not come back down to the hand as an option to exhaust. I wonder if this is actually
/// better design? If a card is locked in, then it's being played. It's almost like a take-back."*
///
/// Exactly that. The cancellation was what returned those cards to the hand in the first place — a
/// cancelled play releases its node — so suppressing it leaves them in the play queue, where a
/// selection cannot reach them. **The resulting rule is the coherent one:** a locked-in card is
/// mid-play, and letting a later card in the same batch exhaust it would let you retroactively
/// withdraw a commitment the opponent has already been shown. That is a take-back, and this mode has
/// exactly one sanctioned one — `LockInTurnModel.Unlock`, which is bounded by the opponent not
/// having committed.
///
/// It also shrinks `DuelQueuedCardHighlightPatch`'s job rather than removing it: the purple mark was
/// added to make an invisible choice visible, and for exhaust selections there is now no choice to
/// make. It still earns its place wherever a committed card *can* legitimately appear in a list.
///
/// # Why this cannot desync
///
/// It removes a flag from an options value both peers compute identically — the card passes it, and
/// both sims run the same card. So both cancel the same set (none), and neither invents an action or
/// changes an order. Note it deliberately does *not* touch `isCancellingPlayCardActions` afterwards:
/// that field is set inside the same `if`, so skipping the flag skips both halves together, and
/// `ResumeActionWithoutSynchronizing` clears it on the way out regardless.
/// </summary>
[HarmonyPatch(typeof(ActionQueueSet), nameof(ActionQueueSet.PauseActionForPlayerChoice))]
public static class DuelKeepBatchThroughChoicePatch
{
    public static void Prefix(ref PlayerChoiceOptions options)
    {
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not LockInTurnModel)
        {
            return;
        }

        if (!options.HasFlag(PlayerChoiceOptions.CancelPlayCardActions))
        {
            return;
        }

        Log.Warn("[SpirePvp] batch: keeping the committed round through a card selection — "
                 + "vanilla would have cancelled every queued play of its owner's");
        options &= ~PlayerChoiceOptions.CancelPlayCardActions;
    }
}

/// <summary>
/// Repaints the hand when a card selection closes, so the queued-card mark comes down with it.
///
/// **The purple ring is raised while a choice is open and taken down by the glow freeze on the next
/// repaint — but nothing repaints when the choice ends.** Reported 2026-08-14: "purple highlight is
/// rendering now, but not going away once the burning pact choice resolves."
///
/// `ResumeActionWithoutSynchronizing` is the engine's own "that choice is finished" — the same
/// method whose renumbering `DuelChoiceKeepsPlacePatch` corrects — so it is the one place that is
/// guaranteed to run exactly once per selection, on both peers, whoever was choosing.
/// </summary>
[HarmonyPatch(typeof(ActionQueueSet), nameof(ActionQueueSet.ResumeActionWithoutSynchronizing))]
public static class DuelChoiceClosedRepaintPatch
{
    public static void Postfix()
    {
        if (!DuelSession.IsDuelActive)
        {
            return;
        }

        LockInPlanView.RefreshHandNow();
    }
}
