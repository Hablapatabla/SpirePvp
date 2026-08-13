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
/// The host's side of the lock-in turn model (DESIGN §3.1b): a client's plays arrive here, and
/// they must be held rather than ordered on arrival.
///
/// Inert under blitz, because it asks the model rather than the mode — the seam
/// `DuelTurnModelPatch` already established.
/// </summary>
[HarmonyPatch]
public static class DuelLockInPatch
{
    private static LockInTurnModel? Model => DuelTurnModel.Current as LockInTurnModel;

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
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not IPlanningTurnModel)
        {
            return true;
        }

        GameAction action = __instance.NetActionToGameAction(message.action, senderId);
        if (action.ActionType != GameActionType.CombatPlayPhaseOnly)
        {
            return true;
        }

        // **The paced model books it a tick instead of ordering it by arrival.** Same reason the
        // lock-in model holds it: the host must not let the moment a packet landed decide when a
        // play resolves. See DuelTickScheduler.
        if (DuelTurnModel.Current is TickTurnModel)
        {
            DuelTickScheduler.Submit(action, senderId);
            return false;
        }

        if (Model is not LockInTurnModel model)
        {
            return true;
        }

        // **Held means held: vanilla must not see it.** This returned the inverse until
        // 2026-08-12 — `action is not UndoEndPlayerTurnAction`, which passes every *play* through
        // and swallows only the undo, i.e. exactly backwards. The host therefore held each of the
        // client's plays in the round buffer *and* enqueued it on arrival, so the client's whole
        // round resolved the moment it locked in, in arrival order, while the host's own plays
        // correctly waited for the flush. Both logs say it in three consecutive lines:
        //
        //     lock-in: holding opponent's PlayCardAction CARD.DEFEND_SILENT (1 held)
        //     [ActionQueueSynchronizer] Enqueueing action PlayCardAction CARD.DEFEND_SILENT
        //     [ActionExecutor] Executing action: PlayCardAction CARD.DEFEND_SILENT
        //
        // The flush then enqueued the same plays a second time, where they no-opped because the
        // cards had already left the hand (`PlayCardAction.ExecuteAction` returns early when the
        // pile is not Hand). **So the interleaved merge had never actually run**, on either of the
        // two sessions this model has been played in: `resolving round — 3 then 3` was three real
        // plays and three already-spent ones. It did not desync only because both sims took their
        // ordering from the same host stream, which is also why nothing in the logs looked wrong.
        //
        // An undo does still reach vanilla, which is what the inverted expression was reaching
        // for: it is the engine that un-readies a player. Note that path is currently unreachable
        // from the UI — the end turn button is disabled from the click until the flush, so nobody
        // can request one (reported 2026-08-12, and backing out of a lock-in wants a decision
        // before it is built).
        model.HoldRemote(action);
        return action is UndoEndPlayerTurnAction;
    }

}
