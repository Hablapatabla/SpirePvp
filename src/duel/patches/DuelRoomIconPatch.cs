using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Points the arena's room icon at art that exists.
///
/// `ImageHelper.GetRoomIconSuffix` returns `modelId.Entry.ToLowerInvariant()` for anything
/// carrying a model id, so our encounter resolves to
/// `res://images/ui/run_history/duel_encounter.png` — a vanilla-owned directory a mod cannot
/// write to. The file is simply not there.
///
/// **This is not the once-per-run cosmetic log line it was recorded as.** Measured 2026-08-06
/// over three matches: 19 failures per client per session, and the reason it repeats is that
/// `AssetCache` logs a cache *miss*, attempts the load, fails, and never caches the failure —
/// so every repaint of the top-bar boss icon re-attempts a resource lookup that throws,
/// synchronously, on the UI path. `NTopBarBossIcon.RefreshBossIcon` runs it twice per call
/// (icon + outline) and again for the second boss slot, which is us.
///
/// The redirect targets the two textures the mod already ships for the map node, so this needs
/// no new assets and no `.pck` re-export. They are node art rather than icon art and will look
/// slightly large in the top bar, which is a presentation nit to settle in the M6 asset pass —
/// unlike the errors, it costs nothing at runtime.
///
/// Both methods are patched rather than the shared private `GetRoomIconSuffix`: the suffix is
/// concatenated into vanilla's `ui/run_history/` path, so returning a different suffix could
/// only ever name another file in a directory we still cannot write to. The public path
/// methods are the level where the answer can leave that directory.
/// </summary>
[HarmonyPatch(typeof(ImageHelper))]
public static class DuelRoomIconPatch
{
    /// <summary>
    /// Matches <see cref="DuelEncounter"/>'s generated model id. Derived from the class name by
    /// the model database, so it changes only if the class is renamed.
    /// </summary>
    private const string DuelEncounterEntry = "DUEL_ENCOUNTER";

    private const string IconPath = "res://SpirePvp/map/duel_node.png";

    private const string OutlinePath = "res://SpirePvp/map/duel_node_outline.png";

    private static bool IsDuelEncounter(ModelId? modelId) =>
        modelId != null &&
        string.Equals(modelId.Entry, DuelEncounterEntry, StringComparison.OrdinalIgnoreCase);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(ImageHelper.GetRoomIconPath))]
    public static void AfterRoomIconPath(ModelId? modelId, ref string? __result)
    {
        if (IsDuelEncounter(modelId))
        {
            __result = IconPath;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(ImageHelper.GetRoomIconOutlinePath))]
    public static void AfterRoomIconOutlinePath(ModelId? modelId, ref string? __result)
    {
        if (IsDuelEncounter(modelId))
        {
            __result = OutlinePath;
        }
    }
}
