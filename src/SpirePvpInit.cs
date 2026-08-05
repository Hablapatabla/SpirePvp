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
        new Harmony(Id).PatchAll();
    }
}
