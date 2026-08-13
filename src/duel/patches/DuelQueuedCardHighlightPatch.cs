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
[HarmonyPatch(typeof(NCardGrid), nameof(NCardGrid.SetCards))]
public static class DuelQueuedCardHighlightPatch
{
    public static void Postfix(NCardGrid __instance, IReadOnlyList<CardModel> cardsToDisplay)
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
}
