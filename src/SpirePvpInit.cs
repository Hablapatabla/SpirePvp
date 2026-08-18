using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace SpirePvp;

[ModInitializer("OnLoaded")]
public static class SpirePvpInit
{
    public const string Id = "SpirePvp";

    /// <summary>
    /// False when any patch class failed to apply, which means some of this mod is not running.
    ///
    /// **A half-applied patch set must not be allowed to host a match.** Every other kind of mod
    /// degrades gracefully when a patch is missing — a relic does nothing, a screen looks wrong —
    /// but this one arbitrates a two-player game. A missing patch here is a hang, a desync, or a
    /// match decided by a rule that only one client is enforcing, and all three read as gameplay
    /// bugs rather than as a broken install. Refusing to start is strictly better than playing a
    /// competitive match whose result cannot be trusted.
    ///
    /// This is also the failure mode a game update produces, which is the realistic case: patch
    /// targets are `nameof` and so break the build, but Harmony can still fail to bind at runtime
    /// for a signature change the compiler accepted, and one target stays a runtime-resolved
    /// string (see DuelNeowOptionsPatch).
    /// </summary>
    public static bool PatchesHealthy { get; private set; }

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
        int silent = 0;

        foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
        {
            try
            {
                // No-ops for types without Harmony attributes.
                if (harmony.CreateClassProcessor(type).Patch()?.Count > 0)
                {
                    applied++;
                }
                else if (type.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).Length > 0)
                {
                    // **A patch class that binds nothing and throws nothing.** Added 2026-08-17,
                    // after two classes were added in one build and the count went up by one: the
                    // failure was invisible because `Patch()` returning empty is not an exception,
                    // so the log said "applied cleanly" while naming no name. Counting is not
                    // enough — a count that disagrees with what you just wrote leaves you guessing
                    // which half is missing, which is exactly the state this whole file exists to
                    // prevent.
                    silent++;
                    Log.Error($"[{Id}] PATCH BOUND NOTHING for {type.Name}: the class carries a "
                              + "[HarmonyPatch] attribute but no method was patched.");
                }
            }
            catch (Exception e)
            {
                failed++;
                Log.Error($"[{Id}] PATCH FAILED for {type.Name}: {e.Message}");
            }
        }

        // **Deliberately not gated on `silent`.** A class that binds nothing is a real hole and is
        // logged as an error by name, but locking the Duel menu over one is a bigger surprise than
        // the hole — the first such class found this way was a cosmetic map guard. Name it, read
        // the log, then decide. Revisit if one ever turns out to matter to a result.
        PatchesHealthy = failed == 0;

        if (failed > 0 || silent > 0)
        {
            Log.Error($"[{Id}] {applied} patch classes applied, {failed} FAILED, {silent} bound " +
                      "nothing — duelling is " +
                      "disabled. Normal runs are unaffected; this mod is inert outside a PvP " +
                      "match. If the game has just updated, rebuild the mod: patch targets bind " +
                      "at compile time, so a rebuild will name anything that moved.");
        }
        else
        {
            // The method count alongside the class count, because they can disagree: a class is
            // counted as applied when *any* of its patches bind, so a single method attribute that
            // silently resolves to nothing leaves the reassuring "applied cleanly" line intact
            // while part of the mod is missing.
            Log.Warn($"[{Id}] {applied} patch classes applied cleanly "
                     + $"({harmony.GetPatchedMethods().Count()} methods).");
        }
    }
}
