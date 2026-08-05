using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.TopBar;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Shows the chess clocks in the top bar by borrowing the run timer.
///
/// NRunTimer is a Control whose whole update is one line — it writes
/// TimeFormatting.Format(RunManager.Instance.RunTime) into _timerLabel on a Godot Timer
/// tick. Rewriting that label during a match gets correct position, font and top-bar styling
/// for nothing: no scene work, no .pck changes, no BaseLib.
///
/// Both clocks share the single label ("YOU 2:31 · OPP 1:47"), local player first. A proper
/// two-element HUD is M7 polish.
///
/// Ticking happens here rather than on a timer of our own, but DuelClockService measures
/// wall-clock deltas, so the clock neither drifts nor depends on this cadence.
/// </summary>
[HarmonyPatch(typeof(NRunTimer))]
public static class DuelClockHudPatch
{
    [HarmonyPostfix]
    [HarmonyPatch("OnTimerTimeout")]
    public static void AfterTimeout(NRunTimer __instance)
    {
        DuelClockService.Tick();

        string? text = DuelClockService.FormatForHud();
        if (text != null)
        {
            __instance._timerLabel.SetTextAutoSize(text);
        }
    }

    /// <summary>
    /// Vanilla hides the timer unless the player enabled it in prefs or is on the map. A
    /// running clock has to stay on screen regardless.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch("RefreshVisibility")]
    public static void AfterRefreshVisibility(NRunTimer __instance)
    {
        if (DuelClockService.Enabled && DuelClockService.Local != null)
        {
            __instance.Visible = true;
        }
    }
}
