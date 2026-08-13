using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using SpirePvp.Duel.Turns;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Keeps the play queue honest about a card that was filed a round early.
///
/// `LockInPlanView` files a held play as it is planned; vanilla files it when the flush enqueues
/// it. Both would draw the same card, and the queue removes by *first* match, so the survivor
/// would hang over the play area for the rest of the combat.
///
/// **Everything here follows from one fact: the queue identifies a play by action identity, and a
/// planned play crosses the wire.** The host flushes the very objects it buffered, so its queue
/// item already holds the action that will run. A client's plays travel to the host and come back
/// as `ActionEnqueuedMessage`, which `HandleActionEnqueuedMessage` deserializes into a *new*
/// `GameAction` — so on a client the queue item and the running action are different objects that
/// mean the same play, and every lookup keyed on identity misses.
///
/// Two of those lookups matter and they are not equally serious:
///
/// - `RemoveCardFromQueueForExecution` keys on the **card model**, so a normal play is filed away
///   correctly on both sides with no help.
/// - `RemoveCardFromQueueForCancellation` keys on the **action**, so a play that is cancelled at
///   resolution — a card whose target died, or a hand discarded by an earlier card in the round —
///   would never be removed on a client, and the card would sit there for the rest of the duel.
///   <see cref="AfterCancellation"/> falls back to the card model for exactly that case.
///
/// `UpdateCardBeforeExecution` also keys on the action and is left alone. What it does — reassign
/// the item's model to the one it already holds, refresh the preview target, drop a hand holder
/// that was dropped at planning time — is cosmetic here, and a second identity fallback for it
/// would be more patch than it is worth.
/// </summary>
[HarmonyPatch]
public static class DuelPlanQueuePatch
{
    /// <summary>
    /// Vanilla must not file a card the plan has already filed.
    ///
    /// Scoped to a card *we* are showing as planned: the opponent's plays and anything this model
    /// never held take vanilla's path untouched, which is how the queue keeps looking like the
    /// queue.
    /// </summary>
    [HarmonyPatch(typeof(NCardPlayQueue), nameof(NCardPlayQueue.OnActionEnqueued))]
    [HarmonyPrefix]
    public static bool BeforeActionEnqueued(NCardPlayQueue __instance, GameAction action) =>
        PlannedNodeIn(__instance, action) == null;

    /// <summary>
    /// Cancelling a planned play that came back from the host as a different object.
    ///
    /// Vanilla's own lookup runs first and finds nothing on a client, so this repeats the removal
    /// by card model. On the host the item does match by identity, so vanilla has already removed
    /// it and `GetCardNode` returns null — this is a no-op there rather than a second removal.
    /// </summary>
    [HarmonyPatch(typeof(NCardPlayQueue), nameof(NCardPlayQueue.RemoveCardFromQueueForCancellation),
        new[] { typeof(PlayCardAction) })]
    [HarmonyPostfix]
    public static void AfterCancellation(NCardPlayQueue __instance, PlayCardAction action)
    {
        NCard? node = PlannedNodeIn(__instance, action);
        if (node != null)
        {
            __instance.RemoveCardFromQueueForCancellation(node);
        }
    }

    /// <summary>
    /// Fans the opponent's queued cards out to the *right* of the play area, leaving yours on the
    /// left.
    ///
    /// Vanilla stacks every queued card into one strip, which is right for co-op — four teammates
    /// queueing into a shared pile — and wrong for a duel, where the only thing you need at a glance
    /// is whose card is whose. Requested 2026-08-12: "can we put the two card player queues on the
    /// two sides of the screen instead of all together on the left". Side beats colour, because
    /// side needs no legend.
    ///
    /// Vanilla's own offset is `Vector2.Left * 300 * num`, so mirroring is adding twice that to the
    /// right: the fan keeps vanilla's spacing and curve exactly, on the other side of the play
    /// position. Recomputing `num` here rather than reading it back out of the result is what keeps
    /// the two in step if vanilla ever retunes the curve — a wrong constant would show up as a
    /// slowly diverging pair of fans rather than as anything obviously broken.
    ///
    /// The index is shared between the two sides, so each fan skips positions when the players
    /// alternate. That is cosmetic — the cards still sit at distinct offsets — and fixing it means
    /// reaching into `_playQueue` to count per owner, which is a private nested type and not worth
    /// it for even spacing.
    /// </summary>
    [HarmonyPatch(typeof(NCardPlayQueue), nameof(NCardPlayQueue.GetPositionForQueueIndex))]
    [HarmonyPostfix]
    public static void AfterQueuePosition(NCard card, int index, ref Vector2 __result)
    {
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not LockInTurnModel)
        {
            return;
        }

        CardModel? model = card.Model;
        if (model == null || LocalContext.IsMe(model.Owner))
        {
            return;
        }

        int slot = index + 1;
        float spread = (float)slot / (slot + 2);
        __result += Vector2.Right * 600f * spread;
    }

    /// <summary>
    /// The node this action's card is already occupying in the queue, because we planned it.
    ///
    /// Null for everything else, which is most things: no duel, blitz, not a card play, or a card
    /// the queue is not holding.
    /// </summary>
    private static NCard? PlannedNodeIn(NCardPlayQueue queue, GameAction action)
    {
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not LockInTurnModel)
        {
            return null;
        }

        if (action is not PlayCardAction play)
        {
            return null;
        }

        CardModel? card = play.NetCombatCard.ToCardModelOrNull();
        return card == null ? null : queue.GetCardNode(card);
    }
}
