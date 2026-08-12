using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using SpirePvp.Duel;

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
/// **Scoped to PvP runs.** `RunState.CreateForNewRun` installs modifiers before the
/// `InitializeSeed` loop, so by the time seeding happens the run already knows whether it is
/// a match — which is exactly why match setup moved into the lobby (DESIGN §5b). A normal run
/// keeps vanilla's per-player offsets.
///
/// Seeding runs once at run creation, so with the modifier present Neow is drawn from
/// mirrored seeds too, and nothing has to be re-seeded after the fact.
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.InitializeSeed))]
public static class RaceMirrorRngPatch
{
    public static bool Prefix(Player __instance, string seed)
    {
        if (!DuelMatch.IsPvpRun(__instance.RunState))
        {
            return true;
        }

        __instance.PlayerRng = new PlayerRngSet(StringHelper.GetDeterministicHashCode(seed));
        __instance.PlayerOdds = new PlayerOddsSet(__instance.PlayerRng);
        return false;
    }
}
