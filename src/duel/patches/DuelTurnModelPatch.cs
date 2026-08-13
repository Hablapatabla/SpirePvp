using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using SpirePvp.Duel.Turns;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// The gate the turn model sits behind (DESIGN §3.1b).
///
/// `ActionQueueSynchronizer.RequestEnqueue` is the single point every play passes through on its
/// way to the shared queue, which is what makes one prefix enough to change the whole model —
/// and it is where **vanilla already defers actions**, parking a `CombatPlayPhaseOnly` action
/// requested during the enemy turn until player-turn start. A lock-in model changes only the
/// release condition on a mechanism the engine already runs.
///
/// **Live from the day blitz is the only model, deliberately.** `BlitzTurnModel.ShouldDefer`
/// always answers false, so this patch currently changes nothing — which is the point. A seam that
/// only executes once its alternative exists is a seam that has never been tested, and this
/// project has been bitten enough times by code whose first real run was also its first run in a
/// match that mattered.
/// </summary>
[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue))]
public static class DuelTurnModelPatch
{
    /// <summary>
    /// **This line prints after the model has already acted, and that has misled one fix.**
    /// `ShouldDefer` does the deferring — and for the paced model's instant first play the whole
    /// release runs synchronously inside it, so the card can be booked, released, and be mid-
    /// execution by the time we get here. The log then reads:
    ///
    ///     Executing action: PlayCardAction CARD.FALLING_STAR (9423456)
    ///     Player 1 playing card FALLING_STAR (targeting PlayerId 1001)
    ///     turn model: deferred PlayCardAction CARD.FALLING_STAR (9423456)
    ///
    /// which looks exactly like a resolving card enqueueing a fresh play and having it held — and
    /// was read that way on 2026-08-12, producing a guard that cost the client a card of tempo per
    /// exchange (see `DuelTurnModel.ShouldDefer`). **Check the card id before concluding anything
    /// from this line's position**: the same id above and below is one play logged out of order,
    /// while a genuine re-enqueue would carry a different one.
    /// </summary>
    public static bool Prefix(GameAction action)
    {
        if (!DuelTurnModel.ShouldDefer(action))
        {
            return true;
        }

        Log.Info($"[SpirePvp] turn model: deferred {action} (kept from vanilla's queue)");
        return false;
    }
}
