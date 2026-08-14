using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
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
    public static bool Prefix(PileType pileType)
    {
        if (pileType != PileType.Hand || !DuelSession.IsDuelActive)
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
