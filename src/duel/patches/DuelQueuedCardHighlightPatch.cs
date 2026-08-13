using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
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
    /// Colours the ring in the **hand**, which is where a discard selection actually happens.
    ///
    /// **The first version patched `NCardHighlight.AnimShow` and never showed at all**, and Lucas
    /// named the reason from the outside before I found it: "could that be overriding it?". It
    /// could. `NHandCardHolder.UpdateCard` calls `AnimShow()` and *then* assigns
    /// `Modulate = playableColor`, so anything set from the show was painted over a line later.
    /// Postfixing the method that does the assigning is the only place after it.
    ///
    /// The other half of that miss is worth keeping too: a card-selection effect like Survivor
    /// brings the cards **back into the hand** to be picked from, rather than opening a grid — so
    /// the `NCardGrid` patch below was watching a widget the discard never used. Both are patched
    /// now, because a duel reaches both: the hand for in-combat selections, the grid for the deck
    /// review.
    ///
    /// Vanilla's own colours all mean something already — cyan for playable, red and gold for the
    /// glow states a card asks for — so this only overrides the cyan, and leaves red and gold
    /// alone. A card that is both queued and shouting for other reasons keeps the louder signal.
    /// </summary>
    [HarmonyPatch(typeof(NHandCardHolder), nameof(NHandCardHolder.UpdateCard))]
    [HarmonyPostfix]
    public static void AfterHandCardUpdated(NHandCardHolder __instance)
    {
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not IPlanningTurnModel)
        {
            return;
        }

        NCard? node = __instance.CardNode;
        NCardPlayQueue? queue = NCardPlayQueue.Instance;
        if (node?.Model == null || queue == null)
        {
            return;
        }

        if (queue.GetCardNode(node.Model) != null && node.CardHighlight.Modulate == NCardHighlight.playableColor)
        {
            node.CardHighlight.Modulate = QueuedColor;
        }
    }
}
