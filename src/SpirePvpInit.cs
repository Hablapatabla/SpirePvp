using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace SpirePvp;

[ModInitializer("OnLoaded")]
public static class SpirePvpInit
{
    public const string Id = "SpirePvp";

    public static void OnLoaded()
    {
        Log.Warn($"[{Id}] loaded — hello from the PvP mod.");
        ApplyPatches();
    }

    /// <summary>
    /// Applies each patch class independently instead of calling Harmony.PatchAll().
    ///
    /// PatchAll throws on the first bad target and abandons everything after it, so a single
    /// mistake — naming a method that a base class declares, or one the game renamed in an
    /// update — silently disables an arbitrary subset of the mod. That is a genuinely nasty
    /// failure: the mod still loads, still logs "loaded", and simply does not work, with the
    /// only clue buried in an initializer stack trace.
    ///
    /// Patching per class means one bad target costs exactly that patch, names it in the log,
    /// and leaves the rest live.
    /// </summary>
    private static void ApplyPatches()
    {
        Harmony harmony = new Harmony(Id);
        int applied = 0;
        int failed = 0;

        foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
        {
            try
            {
                // No-ops for types without Harmony attributes.
                if (harmony.CreateClassProcessor(type).Patch()?.Count > 0)
                {
                    applied++;
                }
            }
            catch (Exception e)
            {
                failed++;
                Log.Error($"[{Id}] PATCH FAILED for {type.Name}: {e.Message}");
            }
        }

        if (failed > 0)
        {
            Log.Error($"[{Id}] {applied} patch classes applied, {failed} FAILED — the mod is " +
                      "running with missing behaviour. Fix the targets above.");
        }
        else
        {
            Log.Warn($"[{Id}] {applied} patch classes applied cleanly.");
        }
    }
}
