using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Duel.Turns;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// You cannot plan more than you can pay for, and you cannot plan at all once you have locked in.
///
/// **Energy is spent when a play executes, not when it is submitted.** In blitz those are a frame
/// apart and nobody notices; buffered, they are a whole round apart, so without this a player plans
/// ten Strikes on three energy and watches seven fizzle at resolution — which reads as the mod
/// eating cards rather than as their own overspend.
///
/// **The question mirrors vanilla's exactly.** `PlayerCombatState.HasEnoughResourcesFor` compares
/// `EnergyCost.GetWithModifiers(CostModifiers.All)` clamped to 0 against `Energy` and raises
/// `EnergyCostTooHigh`; this asks the identical question with the buffered plays already paid for.
/// Using the same expression matters — a second, slightly different notion of what a card costs
/// would disagree with the one that actually charges you, and only on cards with cost modifiers.
/// Raising vanilla's own reason rather than a new one is what gets the presentation for free: the
/// cost turns red through `CardCostHelper.GetEnergyCostColor`, and `NCard` suppresses the
/// unplayable icon for resource reasons, so an unaffordable card looks unaffordable rather than
/// forbidden.
///
/// **Stars are deliberately not reserved.** `HasEnoughResourcesFor` charges them too, and a card
/// that overspends stars will still fizzle at resolution. Energy is what the duel's card pool
/// actually costs; adding stars means also reproducing the hook that pays excess energy *with*
/// stars, and a wrong predicate here makes cards unplayable, which is worse than the gap.
///
/// **It only ever makes a playable card unplayable**, never the reverse, so it cannot let through
/// anything vanilla refused.
///
/// # Why this cannot desync, which is the only reason it is allowed to touch `CanPlay`
///
/// `CanPlay` is not a UI predicate. `PlayCardAction.ExecuteAction` re-checks it and cancels the
/// play, `CardSelectCmd` filters a choice list with it, and `WhisperingEarring` picks which card to
/// auto-play from it — all sim code, which must answer identically on both clients or the two
/// simulations diverge. A reservation is *local by construction*: your buffer is yours, so an
/// unguarded postfix here would answer differently on the two machines.
///
/// Two things close that, and both are conditions rather than correlates:
///
/// - **It answers only while nothing is executing.** Every sim caller above runs inside an
///   executing action, so `ActionExecutor.CurrentlyRunningAction` is non-null for all of them and
///   this patch is invisible to them. Held plays are the reason that is true: with every
///   `CombatPlayPhaseOnly` action buffered, *nothing* executes during planning, so the two windows
///   do not overlap.
/// - **Both clients hold an empty buffer whenever the sim runs.** The round is released on both
///   sides the moment both players lock in (`LockInTurnModel.TryFlush`), which is before the first
///   action of the round executes on either. The remaining sim caller that is not inside an action
///   — a relic auto-playing at turn start — reads it after that release, and after vanilla's own
///   turn-start reset.
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.CanPlay),
    new[] { typeof(UnplayableReason), typeof(AbstractModel) },
    new[] { ArgumentType.Out, ArgumentType.Out })]
public static class DuelPlanEnergyPatch
{
    public static void Postfix(CardModel __instance, ref bool __result, ref UnplayableReason reason)
    {
        // Vanilla already refused: leave its reason alone rather than adding a second one to a
        // card nobody was going to play.
        if (!__result || !DuelSession.IsDuelActive)
        {
            return;
        }

        if (DuelTurnModel.Current is not IPlanningTurnModel model)
        {
            return;
        }

        // Planning-time only — see the desync note above. This is the guard, not a shortcut.
        if (RunManager.Instance?.ActionExecutor.CurrentlyRunningAction != null)
        {
            return;
        }

        // The opponent's cards are drawn on our screen and their playability is theirs to decide.
        Player owner = __instance.Owner;
        if (!LocalContext.IsMe(owner))
        {
            return;
        }

        // A planned card is not competing with the plan it is already in. It cannot be planned
        // twice through the UI — the play queue takes it out of the hand — so this only ever
        // answers for a card the engine is asking about on its way to resolving.
        if (model.IsPlanned(__instance))
        {
            return;
        }

        if (model.HandIsClosed)
        {
            // Locked in means locked in. Without this the hand stays live while you wait for the
            // opponent, and anything played then bypasses the batch entirely: on the host it goes
            // straight to the queue and resolves alone, on a client it lands in the host's buffer
            // after the batch it belongs to. `BlockedByCardLogic` rather than a resource reason,
            // because the cards are affordable — it is the batch that is closed.
            //
            // **A resolving batch is closed too, and that is not just tidiness.** The clocks stop
            // while a batch plays out, so a hand left live there would be free thinking time — plan
            // everything during the animation and your clock never moves. The same flag drives
            // both, so the hand and the clock cannot disagree about whether you are on the move.
            //
            // It asks the model rather than the action queue, and that distinction was a bug: the
            // queue is also busy during a *planning* window, because a card that paused for a
            // player choice resumes after the drain that carried it finished. Keying on the queue
            // greyed a whole hand while a straggler resolved — reported as "cards showing
            // unplayable when they are playable".
            reason |= UnplayableReason.BlockedByCardLogic;
            __result = false;
            return;
        }

        int cost = Math.Max(0, __instance.EnergyCost.GetWithModifiers(CostModifiers.All));
        int unreserved = (owner.PlayerCombatState?.Energy ?? 0) - model.ReservedEnergy;
        if (cost > unreserved)
        {
            reason |= UnplayableReason.EnergyCostTooHigh;
            __result = false;
        }
    }
}
