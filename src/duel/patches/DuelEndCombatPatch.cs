using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Ends a duel without running vanilla's end-of-combat run progression.
///
/// CombatManager.EndCombatInternal assumes it is finishing a real room on the map. It reads
/// localPlayer.PlayerCombatState.TurnNumber unguarded, walks
/// runState.CurrentMapPointHistoryEntry.Rooms.Last(), inspects runState.Map's boss map
/// points, then calls SaveRun, UpdateProgressAfterCombatWon and the "defeated all enemies"
/// achievement check. The duel arena is a synthetic CombatRoom entered through EnterRoom with
/// no map point behind it, so that path threw a NullReferenceException — and because it runs
/// inside the turn loop (StartTurn -> CheckWinCondition -> EndCombatInternal) the exception
/// killed the loop. That is the freeze: not a deadlock, a crashed turn loop, which is why the
/// duel sat there forever and poison stopped ticking (AfterSideTurnStart never fired again).
///
/// None of that progression is meaningful for a duel: there is no map point to record, no
/// room reward, no run to advance — the duel pays out a winner and the run is over. So this
/// replaces the whole method with the minimum teardown needed to leave combat cleanly, then
/// shows the result screen.
///
/// Deliberately skipped and why:
///   ReviveBeforeCombatEnd  — DuelNoRevivePatch already suppresses it; the loser stays down.
///   SaveRun / progress / achievements — duel arenas are not part of run progression.
///   OfferRoomEndRewards    — DuelEncounter.ShouldGiveRewards is already false.
/// </summary>
[HarmonyPatch(typeof(CombatManager), "EndCombatInternal", typeof(CombatTurnState))]
public static class DuelEndCombatPatch
{
    public static bool Prefix(CombatManager __instance, CombatTurnState turnState)
    {
        if (!DuelSession.IsDuelActive)
        {
            return true;
        }

        Log.Warn("[SpirePvp] duel combat ending — skipping vanilla run progression");

        turnState.IsInProgress = false;
        __instance.PlayerActionsDisabled = false;
        CombatManager.SetPhaseForAllPlayers(turnState.State, PlayerTurnPhase.None);

        RunManager.Instance.ActionExecutor.Unpause();
        RunManager.Instance.ActionQueueSynchronizer.SetCombatState(ActionSynchronizerCombatState.NotInCombat);

        DuelResult.ShowFor(turnState.State);

        // Skip the original entirely.
        return false;
    }
}
