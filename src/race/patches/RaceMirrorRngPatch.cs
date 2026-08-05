using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;

namespace SpirePvp.Race.Patches;

/// <summary>
/// I4: makes the race a mirror match.
///
/// Vanilla seeds each player's personal RNG with a per-player offset:
///
///     PlayerRng = new PlayerRngSet(GetDeterministicHashCode(seed) + (ulong)GetPlayerSlotIndex(this));
///
/// That slot index is the *only* reason two players on one shared run seed diverge, and it
/// was measured doing exactly that — seeds ...799 and ...800 for slots 0 and 1. It drives
/// Neow options, card rewards, shop stock and event rolls, so without this the two racers are
/// running different games on the same map, and the race decides nothing.
///
/// Dropping the offset seeds both players from the run seed alone.
///
/// **Unconditional by design, and harmless outside PvP.** In singleplayer the local player is
/// always slot 0, so the offset is already zero and this changes nothing at all. It only has
/// an effect with two or more players — which, for a mod that cannot even join an unmodded
/// lobby (see HANDOFF), means a SpirePvp match.
///
/// Applied at seeding time, which happens once at run creation. A run already under way when
/// race mode is switched on has therefore already drawn its Neow options from the old seeds;
/// `RaceCoordinator.MirrorExistingRun` re-seeds in that case so mid-run `race on` still
/// mirrors everything from that point. Once M6 starts the race automatically at run start,
/// only this patch will matter.
/// </summary>
[HarmonyPatch(typeof(Player), "InitializeSeed")]
public static class RaceMirrorRngPatch
{
    public static bool Prefix(Player __instance, string seed)
    {
        __instance.PlayerRng = new PlayerRngSet(StringHelper.GetDeterministicHashCode(seed));
        __instance.PlayerOdds = new PlayerOddsSet(__instance.PlayerRng);
        return false;
    }
}
