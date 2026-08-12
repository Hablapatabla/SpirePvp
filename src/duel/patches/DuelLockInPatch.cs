using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using SpirePvp.Duel.Turns;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// The three places the lock-in turn model has to touch the engine (DESIGN §3.1b).
///
/// All are inert under blitz, because they ask the model rather than the mode — the seam
/// `DuelTurnModelPatch` already established.
/// </summary>
[HarmonyPatch]
public static class DuelLockInPatch
{
    private static LockInTurnModel? Model => DuelTurnModel.Current as LockInTurnModel;

    /// <summary>
    /// Ending your turn is locking in.
    ///
    /// **Reusing end-turn rather than adding a button is the whole reason model B is cheap.** The
    /// round already ends when both players have readied (`AllPlayersReadyToEndTurn`), so the
    /// engine's existing both-players gate *is* the both-locked-in gate; the model only has to
    /// hand its buffer over before that readiness travels.
    ///
    /// A prefix, so the plays are submitted before the peer is told we are ready — the ordering
    /// `DuelLockInMessage` depends on.
    /// </summary>
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToEndTurn))]
    [HarmonyPrefix]
    public static void BeforeSetReadyToEndTurn(Player player)
    {
        if (Model is not LockInTurnModel model || !DuelSession.IsDuelActive
            || !LocalContext.IsMe(player))
        {
            return;
        }

        model.LockIn();
    }

    /// <summary>
    /// The host holds a client's plays instead of enqueuing them as they arrive.
    ///
    /// Without this the host would order the round by arrival time, which is blitz — the thing
    /// model B exists to remove. Holding them costs nothing extra on the wire: they travelled by
    /// the engine's own request path and are already `GameAction`s by the time this runs.
    /// </summary>
    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.HandleRequestEnqueueActionMessage))]
    [HarmonyPrefix]
    public static bool BeforeHandleRequestEnqueue(
        ActionQueueSynchronizer __instance, RequestEnqueueActionMessage message, ulong senderId)
    {
        if (Model is not LockInTurnModel model || !DuelSession.IsDuelActive)
        {
            return true;
        }

        GameAction action = __instance.NetActionToGameAction(message.action, senderId);
        if (action.ActionType != GameActionType.CombatPlayPhaseOnly)
        {
            return true;
        }

        model.HoldRemote(action);
        return false;
    }

}
