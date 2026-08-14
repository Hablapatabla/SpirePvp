using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
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

        int queued = 0;
        foreach (CardModel card in cardsToDisplay)
        {
            if (queue.GetCardNode(card) != null)
            {
                __instance.HighlightCard(card);
                queued++;
            }
        }

        Log.Warn($"[SpirePvp] highlight: grid showed {cardsToDisplay.Count} card(s), {queued} of them "
                 + $"queued (choice owner {DuelChoiceStallPatch.PlayerGatheringChoice()})");
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
    /// <summary>Rate-limits the diagnostic to one line per selection rather than per repaint.</summary>
    private static bool _loggedThisSelection;

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

        // **Instrumented because this has now been reasoned about wrongly twice.** The mark still
        // does not appear, and the remaining candidates cannot be told apart from the outside: the
        // selection may not route through a hand holder at all (`NCard.GetNodeForCard` prefers the
        // *play queue's* node for a queued card, so there may be no `NHandCardHolder` for it), the
        // queue may no longer hold the node by the time a selection opens, or the choice may not be
        // registered as gathering at paint time. One line per selection says which.
        ulong chooser = DuelChoiceStallPatch.PlayerGatheringChoice();
        if (chooser != 0 && !_loggedThisSelection)
        {
            _loggedThisSelection = true;
            Log.Warn($"[SpirePvp] highlight: hand holder repainted during a choice — card "
                     + $"{node.Model.Id.Entry}, in play queue: {queue.GetCardNode(node.Model) != null}, "
                     + $"chooser {chooser}");
        }
        else if (chooser == 0)
        {
            _loggedThisSelection = false;
        }

        if (queue.GetCardNode(node.Model) == null)
        {
            return;
        }

        // **During a selection the ring is not showing, and that is why this never appeared.**
        // Reported 2026-08-14: "still not getting a purple highlight for cards in queue that are
        // eligible for a choice, like being an exhaust target for Burning Pact." The condition used
        // to be "recolour the ring if it is currently cyan" — but a selection happens *during*
        // resolution, when `CanPlay` is false and `NHandCardHolder.UpdateCard` calls `AnimHide()`
        // rather than showing a playable ring. There was never anything to recolour.
        //
        // So when a choice is open the ring is *raised* rather than merely repainted. This is the
        // moment the mark exists for: your queued cards are back in the hand as candidates, and
        // discarding or exhausting one silently is exactly what it is meant to prevent.
        if (DuelChoiceStallPatch.PlayerGatheringChoice() != 0)
        {
            node.CardHighlight.AnimShow();
            node.CardHighlight.Modulate = QueuedColor;
            return;
        }

        if (node.CardHighlight.Modulate == NCardHighlight.playableColor)
        {
            node.CardHighlight.Modulate = QueuedColor;
        }
    }
}
