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
    public static bool Prefix(GameAction action)
    {
        if (!DuelTurnModel.ShouldDefer(action))
        {
            return true;
        }

        Log.Info($"[SpirePvp] turn model: holding {action} until lock-in");
        return false;
    }
}
