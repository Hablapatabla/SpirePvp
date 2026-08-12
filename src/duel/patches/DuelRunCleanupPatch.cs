using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// The moment a run stops existing.
///
/// `RunManager.CleanUp` is what tears a run down on the way back to the menu — it disposes every
/// synchronizer, disconnects the net service and nulls out `State` and `LocalContext.NetId`. It
/// is the counterpart to `DuelRunLaunchPatch`, and the place for this mod to let go of a match.
///
/// **Prefix, not postfix.** CleanUp's `finally` block nulls the state and the local identity, and
/// its body disconnects the net service — so by the time a postfix ran there would be nothing
/// left to unregister our message handlers from. Running first means we hand them back to a
/// service that is still alive.
///
/// Not conditional on the run being a PvP match. The whole point is the run that comes *after* a
/// match: an ordinary co-op run inherits nothing if we have already let go, and it was exactly
/// that run which caught the host still broadcasting clock syncs into it.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class DuelRunCleanupPatch
{
    public static void Prefix()
    {
        DuelMatch.OnRunEnded();
    }
}
