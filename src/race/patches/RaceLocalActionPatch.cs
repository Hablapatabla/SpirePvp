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
/// Every action goes through ActionQueueSynchronizer.RequestEnqueue, which is asymmetric:
///
///     Host / Singleplayer -> EnqueueAction(action, NetId)      // immediate, local
///     Client              -> SendMessage(RequestEnqueueActionMessage)  // ask the host
///
/// The client's request is an IRunLocationTargetedMessage, so RunLocationTargetedMessageBuffer
/// holds it until the *host* reaches the client's location. In a race the host is off on its
/// own node and never will, so the client's card plays are never arbitrated and its first turn
/// never ends. The host, arbitrating its own actions locally, is unaffected — exactly the
/// asymmetry the playtest saw. Those held requests are the "still N messages for other
/// locations" errors in the log.
///
/// During a race there is nothing to arbitrate: each client owns its own run, and the duel
/// re-syncs authoritatively from scratch on entry (DESIGN §4). So the correct behaviour is
/// for each side to act like singleplayer and enqueue locally.
///
/// The CombatPlayPhaseOnly deferral is deliberately left to vanilla: we return true for those
/// so the original buffers them in _requestedActionsWaitingForPlayerTurn, and when it retries
/// them at player-turn start it comes back through this prefix and takes the local path.
///
/// EnqueueAction is private; Krafs.Publicizer (already on for sts2) makes it callable.
/// </summary>
[HarmonyPatch(typeof(ActionQueueSynchronizer), "RequestEnqueue")]
public static class RaceLocalActionPatch
{
    public static bool Prefix(ActionQueueSynchronizer __instance, GameAction action)
    {
        if (!DuelSession.IsRaceActive)
        {
            return true;
        }

        // Let vanilla defer play-phase-only actions queued during the enemy turn.
        if (action.ActionType == GameActionType.CombatPlayPhaseOnly
            && __instance.CombatState == ActionSynchronizerCombatState.NotPlayPhase)
        {
            return true;
        }

        __instance.EnqueueAction(action, __instance._netService.NetId);
        return false;
    }
}
