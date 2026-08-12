using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// Cuts the start-of-act map pan in a PvP run.
///
/// `NMapScreen.StartOfActAnim` parks the map at y=1800 and tweens it to -600 over
/// `_mapAnimDuration` — 3s normally, 1.5s in Fast mode — after `_mapAnimStartDelay` of another
/// 1s. Fine for a solo run; in a race it is four seconds where the clock is running and the
/// player can do nothing, and both players pay it at the same moment for no reason.
///
/// This puts the map straight at its destination and runs the tail of the original: the drag
/// target has to be set or the first click-drag snaps the map back, and `InitMapPrompt` is
/// what shows "Select a Starting Room", which the tween's completion callback would otherwise
/// have done.
///
/// Skipping an async method means handing back a completed task — a prefix that returns false
/// without assigning `__result` leaves it null and the caller NREs on `await`. That mistake
/// cost a debugging round earlier in this project (see RaceStarsWithoutCombatPatch).
///
/// The act banner is deliberately left alone: it is a brief overlay, it names the act, and it
/// does not gate input.
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.StartOfActAnim))]
public static class RaceSkipMapIntroPatch
{
    private static readonly Vector2 Settled = new Vector2(0f, -600f);

    public static bool Prefix(NMapScreen __instance, ref Task __result)
    {
        if (!DuelMatch.IsPvpRun(RunManagerState()))
        {
            return true;
        }

        __instance._mapContainer.Position = Settled;
        __instance._targetDragPos = Settled;
        __instance.InitMapPrompt();

        __result = Task.CompletedTask;
        return false;
    }

    private static MegaCrit.Sts2.Core.Runs.IRunState? RunManagerState()
    {
        return MegaCrit.Sts2.Core.Runs.RunManager.Instance?.State;
    }
}
