using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

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
