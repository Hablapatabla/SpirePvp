using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using SpirePvp.Duel.Turns;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Marks the cards you have already queued when something puts your hand in front of you to choose
/// from.
///
/// **A queued card is still in your hand pile until it resolves**, which is the same fact
/// `PlayCardAction` relies on when it cancels a play whose pile has changed — so a card-selection
/// effect offers it like any other. Reported 2026-08-12 with the case that shows why that matters:
/// play Survivor with a Defend queued and a Strike loose, and the discard screen shows you two
/// cards with nothing to say that discarding the Defend also throws away a play you have already
/// paid for and planned around.
///
/// **Marking, not filtering.** Removing queued cards from the list is the obvious fix and it would
/// desync: a card selection travels as a player choice keyed by *index*, so a list one client
/// builds differently means the peer applies the choice to a different card. The selection is
/// right to offer the card; the player just has to be able to see which one it is.
///
/// Uses vanilla's own `HighlightCard`, so it is the same ring the game already draws around a
/// called-out card — no art, and no second visual language for "this one is special". The grid
/// re-applies highlights from `_highlightedCards` every time it assigns a row, so registering once
/// as the grid is filled survives scrolling and relayout.
///
/// **The queue is the source of truth here, not the turn model's buffer.** By the time a selection
/// opens mid-resolution the batch has been flushed and `_local` is empty — the model has already
/// forgotten. What is still true is that the card's node is sitting in the play queue waiting to
/// execute, which is exactly the state being marked.
/// </summary>
[HarmonyPatch]
public static class DuelQueuedCardHighlightPatch
{
    /// <summary>
    /// Purple, so a queued card does not read as one the game has called out for some other reason.
    ///
    /// Vanilla's ring is cyan or gold depending on why it was raised (`NCardHighlight.playableColor`
    /// and `gold`), and both already mean something. A colour nothing else uses says "this one is
    /// yours and it is spoken for" without a legend.
    /// </summary>
    private static readonly Color QueuedColor = new Color(0.78f, 0.35f, 0.98f, 0.98f);

    [HarmonyPatch(typeof(NCardGrid), nameof(NCardGrid.SetCards))]
    [HarmonyPostfix]
    public static void AfterSetCards(NCardGrid __instance, IReadOnlyList<CardModel> cardsToDisplay)
    {
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not IPlanningTurnModel)
        {
            return;
        }

        NCardPlayQueue? queue = NCardPlayQueue.Instance;
        if (queue == null)
        {
            return;
        }

        foreach (CardModel card in cardsToDisplay)
        {
            if (queue.GetCardNode(card) != null)
            {
                __instance.HighlightCard(card);
            }
        }
    }

    /// <summary>
    /// Colours the ring as it appears, rather than when the grid is filled.
    ///
    /// **The grid raises highlights again on every relayout** — `AssignCardsToRow` re-reads
    /// `_highlightedCards` and calls `AnimShow` — and card nodes are pooled and reassigned as you
    /// scroll. A colour set once when the grid was built would therefore be missed by nodes that
    /// did not exist yet, and stranded on nodes that later belong to a different card. Setting it
    /// at the moment the ring is shown is the only point where the node and the card it is
    /// currently displaying are both known.
    ///
    /// White for everything else, which is the default this node ships with: vanilla only overrides
    /// the modulate on reward screens, so restoring it cannot erase a colour anything in a duel set.
    /// </summary>
    [HarmonyPatch(typeof(NCardHighlight), nameof(NCardHighlight.AnimShow))]
    [HarmonyPostfix]
    public static void AfterHighlightShown(NCardHighlight __instance)
    {
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not IPlanningTurnModel)
        {
            return;
        }

        NCardPlayQueue? queue = NCardPlayQueue.Instance;
        CardModel? card = OwningCard(__instance)?.Model;
        if (queue == null || card == null)
        {
            return;
        }

        __instance.Modulate = queue.GetCardNode(card) != null ? QueuedColor : Colors.White;
    }

    /// <summary>The card this ring belongs to. `%Highlight` is a nested unique name, so walk up.</summary>
    private static NCard? OwningCard(Node node)
    {
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
        {
            if (parent is NCard card)
            {
                return card;
            }
        }

        return null;
    }
}
