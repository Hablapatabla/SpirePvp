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
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndCombatInternal), typeof(CombatTurnState))]
public static class DuelEndCombatPatch
{
    public static bool Prefix(CombatManager __instance, CombatTurnState turnState, ref Task __result)
    {
        // Complete counts as well as DuelActive, and leaving it out cost exactly the NRE this
        // patch exists to prevent. Ending the duel runs DuelResult.DeclareWinner, which moves
        // the phase to Complete — so the *second* time anything asks, this guard said "not a
        // duel" and handed vanilla the arena it cannot cope with. Something always asks again:
        // the room is not exited while the result screen is up, so a trailing action in
        // ActionExecutor re-runs CheckWinCondition, IsCombatEnding is still true with the loser
        // dead, and EndCombatInternal fires a second time.
        //
        // "The duel is over" is not the same question as "this is not a duel".
        if (DuelSession.Phase is not (DuelPhase.DuelActive or DuelPhase.Complete))
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

        // **Required, and its absence was the `duel over` NullReferenceException.**
        // EndCombatInternal is `async Task`. Skipping it without assigning __result leaves the
        // caller holding null and doing `await null`, which throws — inside
        // `CheckWinCondition`, with no EndCombatInternal frame on the stack, because the method
        // never ran. That is why the trace looked like inlining had eaten the frames and why
        // two sessions of reading CheckWinCondition found nothing wrong with it.
        //
        // It only ever appeared on duels decided by HP: a duel decided on the clock ends
        // through DuelFlag -> DuelResult.DeclareWinner without IsCombatEnding ever going true,
        // so EndCombatInternal is not called at all and there is nothing to skip.
        //
        // Harmless in practice — everything above already ran, so the result screen was up and
        // the winner correct — but it threw once per duel on both clients and made every log
        // read start by discounting a real exception.
        __result = Task.CompletedTask;

        // Skip the original entirely.
        return false;
    }
}
