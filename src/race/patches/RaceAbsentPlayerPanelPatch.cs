using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace SpirePvp.Race.Patches;

/// <summary>
/// M5 spike, blocker 4 — the black screen on entering the first race combat.
///
/// NMultiplayerPlayerState is the co-op teammate panel. Its OnCombatSetUp binds to the *other*
/// player's combat state:
///
///     Player.PlayerCombatState.EnergyChanged += OnEnergyChanged;   // unguarded
///
/// PlayerCombatState is only populated for players in the combat (SetUpCombat calls
/// PopulateCombatState over state.Players). RaceSoloCombatPatch deliberately enrols only the
/// local player, so the absent opponent has none and this NREs. The throw propagates out of
/// CombatManager.SetUpCombat -> CombatRoom.StartCombat -> EnterRoom, so the room never
/// finishes loading: hence a black screen rather than an error in game.
///
/// Vanilla clearly anticipated the null — its own OnCombatEnded teardown guards with
/// `if (Player.PlayerCombatState != null)`. Only the setup path assumes it cannot happen,
/// which in co-op is true, because every run player is in every combat.
///
/// So this applies vanilla's own guard to the setup side: if the panel's player is not in
/// this combat, there is nothing to bind and the panel keeps its hidden state. Deliberately
/// not gated on race mode — dereferencing a null combat state is never correct, and the
/// symmetric teardown already agrees.
/// </summary>
[HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState.OnCombatSetUp))]
public static class RaceAbsentPlayerPanelPatch
{
    public static bool Prefix(NMultiplayerPlayerState __instance)
    {
        Player player = __instance.Player;
        return player?.PlayerCombatState != null;
    }
}
