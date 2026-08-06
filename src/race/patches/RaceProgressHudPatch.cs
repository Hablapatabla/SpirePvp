using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace SpirePvp.Race.Patches;

/// <summary>
/// Rebuilds the race HUD whenever the map screen is.
///
/// `RaceProgressHud` otherwise only redraws when a `RaceProgressMessage` arrives, and the map
/// screen is created and destroyed repeatedly across a run — every room entry closes it and
/// every return rebuilds it, taking our cloned label with it. Without this hook the readout
/// would appear once, vanish on the next combat, and not come back until the opponent happened
/// to move again: present or absent depending on their timing rather than yours.
///
/// The HUD creates its label lazily and no-ops outside a PvP run, so a postfix that simply asks
/// it to refresh is the whole integration.
/// </summary>
[HarmonyPatch(typeof(NMapScreen), "_Ready")]
public static class RaceProgressHudPatch
{
    public static void Postfix() => RaceProgressHud.Refresh();
}
