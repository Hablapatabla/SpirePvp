using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Saves;

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

        // Piggybacked on the clock's tick rather than given a timer of its own: this is the
        // one hook already guaranteed to run throughout a match, and the watchdog measures
        // wall-clock silence, so an irregular cadence only delays detection rather than
        // distorting it.
        DuelDisconnect.Tick();

        // Telemetry only: the one hook guaranteed to run for the whole match, which is exactly what
        // a "died during the race and nothing happened" report needs watching it.
        DuelTelemetry.TickDeathProbe();

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
        DuelTelemetry.NoteClockHud(
            __instance.Visible,
            SaveManager.Instance.PrefsSave.ShowRunTimer,
            NMapScreen.Instance?.Visible ?? false);

        if (DuelClockService.Enabled && DuelClockService.Local != null)
        {
            __instance.Visible = true;

            // Vanilla repaints about once a second, which is fine for a run timer and too
            // coarse for a chess clock — the last few seconds visibly lag. The clock itself
            // is wall-clock based, so this only changes how often the label is redrawn.
            if (__instance._timer != null)
            {
                __instance._timer.WaitTime = 0.1;
            }
        }
    }
}
