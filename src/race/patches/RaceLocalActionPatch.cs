using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// M5 spike, blocker 3 — found by playtest, and the reason the client froze on turn one
/// while the host played on happily.
///
/// ActionQueueSynchronizer has *three* public entry points that ask the host to arbitrate,
/// and all three are asymmetric in exactly the same way:
///
///     Host / Singleplayer -> do it locally, right now
///     Client              -> SendMessage(...) and wait for the host to confirm
///
/// | Entry point                          | Client sends                              | Host path                     |
/// |--------------------------------------|-------------------------------------------|-------------------------------|
/// | RequestEnqueue                       | RequestEnqueueActionMessage               | EnqueueAction                 |
/// | RequestEnqueueHookAction             | RequestEnqueueHookActionMessage           | EnqueueHookAction             |
/// | RequestResumeActionAfterPlayerChoice | RequestResumeActionAfterPlayerChoiceMessage | ResumeActionAfterPlayerChoice |
///
/// Every one of those messages is an IRunLocationTargetedMessage, so
/// RunLocationTargetedMessageBuffer holds it until the *host* reaches the client's location.
/// In a race the host is off on its own node and never will, so the client's request is
/// buffered forever and whatever it was waiting on never happens. The host, arbitrating its
/// own actions locally, notices nothing — exactly the asymmetry the playtests saw.
///
/// During a race there is nothing to arbitrate: each client owns its own run, and the duel
/// re-syncs authoritatively from scratch on entry (DESIGN §4). So the correct behaviour is
/// for each side to act like singleplayer and do the work locally.
///
/// **Guard the condition, not the route.** The original patch covered only RequestEnqueue,
/// because that is the one the M5 spike happened to hit. The other two are the same bug
/// waiting for a card that uses them, and one duly arrived: playtest 2026-08-11, the client
/// played **Survivor** (discard a card — a player choice), which pauses the action and asks
/// the host to resume it. Host log:
///
///     Message RequestResumeActionAfterPlayerChoiceMessage from 1001 is for location
///     act 0 coord (1, 2) room 0, enqueueing it because we are currently at location
///     act 0 coord (3, 2) room 0
///
/// The client sat in "waiting to resume execution after player choice" for the rest of the
/// run. No error on either side — the buffer is doing its job, and during a race its chatter
/// is normally harmless opponent traffic, so nothing stands out.
///
/// RequestEnqueueHookAction had not fired in any race log when this was written (0
/// occurrences), so it is fixed on the shared reasoning rather than on a reproduction.
///
/// All three targets return void, so a prefix returning false needs no __result — the async
/// trap that cost this project two multi-session hunts does not apply here. Check that again
/// if any of them ever becomes async.
///
/// EnqueueAction / EnqueueHookAction / ResumeActionAfterPlayerChoice are private;
/// Krafs.Publicizer (already on for sts2) makes them callable.
/// </summary>
[HarmonyPatch(typeof(ActionQueueSynchronizer))]
public static class RaceLocalActionPatch
{
    [HarmonyPatch(nameof(ActionQueueSynchronizer.RequestEnqueue))]
    [HarmonyPrefix]
    public static bool RequestEnqueue(ActionQueueSynchronizer __instance, GameAction action)
    {
        if (!DuelSession.IsRaceActive)
        {
            return true;
        }

        // Let vanilla defer play-phase-only actions queued during the enemy turn. It buffers
        // them in _requestedActionsWaitingForPlayerTurn and retries at player-turn start,
        // which comes back through this prefix and takes the local path then.
        if (action.ActionType == GameActionType.CombatPlayPhaseOnly
            && __instance.CombatState == ActionSynchronizerCombatState.NotPlayPhase)
        {
            return true;
        }

        __instance.EnqueueAction(action, __instance._netService.NetId);
        return false;
    }

    [HarmonyPatch(nameof(ActionQueueSynchronizer.RequestEnqueueHookAction))]
    [HarmonyPrefix]
    public static bool RequestEnqueueHookAction(ActionQueueSynchronizer __instance,
                                                GenericHookGameAction action)
    {
        if (!DuelSession.IsRaceActive)
        {
            return true;
        }

        __instance.EnqueueHookAction(action);
        return false;
    }

    [HarmonyPatch(nameof(ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice))]
    [HarmonyPrefix]
    public static bool RequestResumeActionAfterPlayerChoice(ActionQueueSynchronizer __instance,
                                                            GameAction action)
    {
        // An action with no id is not something this patch can route locally. Hand it back to
        // vanilla rather than throwing here: vanilla's client path dereferences the same
        // Id.Value, so it fails identically and in the place the stack already expects.
        if (!DuelSession.IsRaceActive || !action.Id.HasValue)
        {
            return true;
        }

        // Vanilla's own host path. On a client it skips the broadcast (it is gated on
        // NetGameType.Host) and goes straight to ResumeActionWithoutSynchronizing, which is
        // precisely the singleplayer behaviour the race wants.
        __instance.ResumeActionAfterPlayerChoice(action.Id.Value);
        return false;
    }
}
