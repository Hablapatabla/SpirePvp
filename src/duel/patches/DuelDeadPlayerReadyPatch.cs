using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Stops the duel hanging the moment someone dies.
///
/// The player phase ends only when every player is ready:
/// AllPlayersReadyToEndTurn compares PlayersReadyToEndTurn.Count to State.Players.Count. A
/// dead player never signals ready, so with one duelist down the count can never be reached,
/// the round never rolls over, and CheckWinCondition — which would notice the duel is over
/// and end combat — never runs. The result in game is a duel that stops responding after the
/// kill instead of ending.
///
/// Vanilla already knows dead players cannot act: the turn loop auto-readies them, logging
/// "Setting player N to ready at start of turn". But it only does so at the *start* of a
/// turn, and nothing re-evaluates a player who dies mid-turn — which in a duel is the only
/// way anyone ever dies.
///
/// So this postfix applies vanilla's own rule continuously: ignore the dead when deciding
/// whether everyone is ready. The multiplayer side-check is preserved rather than
/// short-circuited, so this cannot end an enemy phase early.
///
/// Reads under turnState.ReadyLock, matching the original.
/// </summary>
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.AllPlayersReadyToEndTurn), typeof(CombatTurnState))]
public static class DuelDeadPlayerReadyPatch
{
    public static void Postfix(CombatTurnState turnState, ref bool __result)
    {
        if (__result || !DuelSession.IsDuelActive)
        {
            return;
        }

        bool everyLivingPlayerReady = true;
        using (turnState.ReadyLock.EnterScope())
        {
            foreach (Player player in turnState.State.Players)
            {
                if (player.Creature.IsDead)
                {
                    continue;
                }

                if (!turnState.PlayersReadyToEndTurn.Contains(player))
                {
                    everyLivingPlayerReady = false;
                    break;
                }
            }
        }

        if (!everyLivingPlayerReady)
        {
            return;
        }

        __result = RunManager.Instance.IsSingleplayerOrFakeMultiplayer
            || turnState.State.CurrentSide == CombatSide.Player;
    }
}
