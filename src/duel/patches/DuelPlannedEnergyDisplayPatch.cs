using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.addons.mega_text;
using SpirePvp.Duel.Turns;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// The energy orb counts down as you plan, instead of reading full until the round resolves.
///
/// **Reported as the clunkiest thing left in the duel** (2026-08-13): *"I'd like your energy to go
/// down once you've added to the queue instead of having to calc in your head."* Right, and it is the
/// same fact `DuelPlanEnergyPatch` already uses for card costs — a planned play has *committed*
/// energy it has not yet spent. `PlayerCombatState.Energy` does not move until the play executes,
/// which under a planning model is a whole round later, so the orb spent the entire planning phase
/// telling you a number you had to correct by hand.
///
/// # Why a postfix that repaints, rather than adjusting the energy
///
/// The tempting fix is to lower `PlayerCombatState.Energy` while planning and put it back. That is
/// sim state: it raises `EnergyChanged`, it is what every affordability check in the engine reads,
/// and it is exactly the sort of local mutation this project has spent the day removing (see the
/// arena heal). **Presentation is not allowed to move the number the simulation runs on.**
///
/// So this recomputes the display from `Energy - ReservedEnergy` and leaves the model alone.
///
/// **All five reads, and that is the point rather than thoroughness for its own sake.**
/// `RefreshLabel` keys the label text, the font colour, the outline colour, the orb material and the
/// `_layers` modulate on `Energy == 0` independently. Rewriting only the text would show `0/3` in
/// cream on a brightly lit orb — which reads as a rendering fault rather than as "you have spent
/// your energy", and would be worse than the honest-but-unhelpful display it replaced.
///
/// Local player only: the orb is the local player's, and the opponent's plan is not ours to show
/// here — that is `DuelIncoming`'s job.
/// </summary>
[HarmonyPatch(typeof(NEnergyCounter), nameof(NEnergyCounter.RefreshLabel))]
public static class DuelPlannedEnergyDisplayPatch
{
    public static void Postfix(NEnergyCounter __instance)
    {
        if (!DuelSession.IsDuelActive || DuelTurnModel.Current is not IPlanningTurnModel model)
        {
            return;
        }

        Player? player = __instance._player;
        PlayerCombatState? combat = player?.PlayerCombatState;
        if (player == null || combat == null || !LocalContext.IsMe(player))
        {
            return;
        }

        int reserved = model.ReservedEnergy;
        if (reserved <= 0)
        {
            return;
        }

        // Never below zero: the reservation is bounded by what you could afford when you planned,
        // but a cost modifier changing underneath it should degrade to "empty" rather than to a
        // negative number on the orb.
        int effective = Math.Max(0, combat.Energy - reserved);
        bool empty = effective == 0;

        __instance._label.SetTextAutoSize($"{effective}/{combat.MaxEnergy}");
        __instance._label.AddThemeColorOverride(ThemeConstants.Label.FontColor,
            empty ? StsColors.red : StsColors.cream);
        __instance._label.AddThemeColorOverride(ThemeConstants.Label.FontOutlineColor,
            empty ? StsColors.unplayableEnergyCostOutline : player.Character.EnergyLabelOutlineColor);

        Material? material = empty
            ? PreloadManager.Cache.GetMaterial("res://materials/ui/energy_orb_dark.tres")
            : null;

        foreach (Control layer in __instance._layers.GetChildren().OfType<Control>())
        {
            layer.Material = material;
        }

        foreach (Control layer in __instance._rotationLayers.GetChildren().OfType<Control>())
        {
            layer.Material = material;
        }

        __instance._layers.Modulate = empty ? Colors.DarkGray : Colors.White;
    }
}
