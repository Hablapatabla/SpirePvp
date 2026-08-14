using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using SpirePvp.Duel.Turns;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Freezes a hand card's cost visuals while a batch resolves, so they stop pulsing between playable
/// and unplayable.
///
/// **Reported twice, and the first fix was aimed at the wrong half.** 2026-08-14: *"during the turn
/// resolution the cards in hand are jumping between playable and unplayable"*, then *"nah they're
/// still pulsing, especially if you still have energy."*
///
/// The alternation is `DuelPlanEnergyPatch`'s desync guard seen from outside. That patch answers only
/// while `ActionExecutor.CurrentlyRunningAction` is null — it must, because
/// `PlayCardAction.ExecuteAction` re-checks `CanPlay` on the card it is currently playing and a "no"
/// there would cancel the play. So *between* actions it reports the hand closed and every card
/// unplayable, and *during* an action it stands aside and vanilla's plain affordability answer shows
/// through. **"Especially if you still have energy" is the tell**: with none left the two answers
/// agree and there is nothing to see.
///
/// The first attempt stopped `LockInPlanView.RefreshPlannedCosts` from repainting the hand during
/// resolution. That was necessary and insufficient — **vanilla repaints on its own**, through
/// `NPlayerHand.OnCombatStateChanged`, every time a resolving card spends energy or moves a pile.
/// Chasing repaint sites was never going to end; the sampling had to stop instead.
///
/// # Why the visuals rather than the answer
///
/// Making `CanPlay` answer consistently during resolution means answering while an action is
/// executing, which is exactly what the guard forbids — and not only for the card being played:
/// `CardSelectCmd` filters a choice list with it and `WhisperingEarring` picks a card to auto-play
/// from it. Reaching into any of those to stop a flicker would trade a cosmetic problem for a
/// gameplay one, and this project has a long enough list of "a predicate that merely correlates"
/// already.
///
/// So nothing is asked and nothing is answered differently: the card simply keeps the look it had.
/// That look is the *right* one, because it was painted when planning last closed and it is correct
/// again the moment planning reopens — resolution is the only stretch in between, and the hand is not
/// interactive for any of it.
///
/// **Hand pile only.** The same method draws a card in the play queue and in every other pile, and
/// those are not flickering — the guard's two answers only differ for a card you might otherwise be
/// able to play. Narrowing it also keeps the frozen window as small as the problem.
/// </summary>
[HarmonyPatch(typeof(NCard), nameof(NCard.UpdateEnergyCostVisuals))]
public static class DuelHandVisualFreezePatch
{
    public static bool Prefix(NCard __instance, PileType pileType)
    {
        if (pileType != PileType.Hand || !DuelSession.IsDuelActive)
        {
            return true;
        }

        // **Only a card actually sitting in the hand pile.** The same method draws card *previews* —
        // the library, a reward screen, a hover blow-up — and those are passed `PileType.Hand` while
        // their models live elsewhere. Freezing those showed a stale cost on a screen that has
        // nothing to do with the duel: reported 2026-08-14 as Burning Pact reading 2 energy when its
        // base cost is 1. A frozen card is only ever the one you are holding.
        if (__instance.Model?.Pile?.Type != PileType.Hand)
        {
            return true;
        }

        // **A card selection is the exception, and it is the whole reason this is not blanket.**
        // During a selection the hand *is* interactive — Burning Pact brings your cards back to be
        // picked from — so freezing there would grey out the thing you are being asked to choose.
        // Same predicate the stall and the clocks use, so all three agree about who is deciding.
        if (DuelChoiceStallPatch.PlayerGatheringChoice() != 0)
        {
            return true;
        }

        // `HandIsClosed` is the model's own "a batch is committed or resolving", and it is the same
        // flag that greys the hand — so the freeze and the greying cannot disagree about whether you
        // are on the move.
        if (DuelTurnModel.Current is IPlanningTurnModel { HandIsClosed: true })
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Hides the playable glow on hand cards while a batch resolves, for the same reason the cost is
/// frozen — and it is a *second* visual, on a different node, sampling the same unstable answer.
///
/// **Reported after the cost freeze landed:** *"energy costs on cards are frozen now but the
/// 'playable' highlight is still popping in and out whether or not they actually are."* Right:
/// `DuelHandVisualFreezePatch` covers `NCard.UpdateEnergyCostVisuals`, which owns the cost colour and
/// the unplayable icon. The glow is `NHandCardHolder.UpdateCard`, which ends in
/// `if (CardNode.Model.CanPlay() || ShouldGlowRed || ShouldGlowGold)` → `CardHighlight.AnimShow()`
/// and otherwise `AnimHide()`. Same `CanPlay`, same alternation, different node — so freezing one
/// left the other pulsing.
///
/// **Hidden rather than held at its last value.** A glow means "you can play this", and during a
/// resolving batch you cannot play anything — so keeping a stale glow would be the one reading that
/// is actively wrong, and it would contradict the greyed hand the model already shows. Locking in
/// closes the hand, the glows go out, and they stay out until planning reopens.
///
/// A postfix rather than a skip, because `UpdateCard` also calls `UpdateVisuals`, which is what
/// renders a card *drawn during resolution* — a draw card in the batch does exactly that. Skipping
/// the method wholesale would leave those cards unrendered; letting it run and then taking the glow
/// back down leaves everything else intact.
/// </summary>
[HarmonyPatch(typeof(NHandCardHolder), nameof(NHandCardHolder.UpdateCard))]
public static class DuelHandGlowFreezePatch
{
    public static void Postfix(NHandCardHolder __instance)
    {
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not IPlanningTurnModel { HandIsClosed: true })
        {
            return;
        }

        // Leave a selection alone: the cards being offered are meant to be lit, and
        // `DuelQueuedCardHighlightPatch` marks the queued ones purple on top of that.
        if (DuelChoiceStallPatch.PlayerGatheringChoice() != 0)
        {
            return;
        }

        __instance.CardNode?.CardHighlight?.AnimHide();
    }
}
