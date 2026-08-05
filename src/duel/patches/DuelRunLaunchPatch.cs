using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// The moment the local player exists.
///
/// `RunManager.Launch` assigns `LocalContext.NetId` and then raises `RunStarted`, so it is the
/// earliest point at which `LocalContext.IsMe` and `GetMe` mean anything. Modifiers'
/// `AfterRunCreated` runs *before* this, during `RunState.CreateForNewRun` — which is why match
/// setup is split in two (see `DuelMatch.OnRunCreated` / `OnRunLaunched`).
///
/// Getting this wrong failed quietly rather than loudly: with no local identity, `IsMe` was
/// false for everyone, so the race deactivated hooks for *both* players instead of just the
/// opponent, and the clocks never started because `GetMe` returned null. Nothing threw. The
/// only evidence was the log deactivating "remote player 1" and "remote player 1001" on the
/// same client.
///
/// Postfix, not prefix: the identity must already be assigned when we run.
/// </summary>
[HarmonyPatch(typeof(RunManager), "Launch")]
public static class DuelRunLaunchPatch
{
    public static void Postfix(RunState __result)
    {
        DuelMatch.OnRunLaunched(__result);
    }
}
