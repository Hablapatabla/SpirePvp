using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// One shopper at the merchant, not two. The co-located-party pattern's second shape (see
/// <see cref="RaceSolo"/>), and the third room to show it after the rest site and the chest —
/// exactly the recurrence DESIGN I3 predicted for "rest sites, shops and events".
///
/// NMerchantRoom.AfterRoomIsLoaded instantiates one NMerchantCharacter per player and lays them
/// out in a grid, dimming everyone past the first row. In a race the opponent is off buying
/// from their own copy of this merchant, so drawing them here is fiction.
///
/// Hidden rather than not created, on the same reasoning as the campfire and the chest: a node
/// that exists is always safe to look up. Here that costs nothing, because PlayerVisuals is only
/// ever iterated — NGameOverScreen foreaches it and nothing indexes it — so this could have
/// filtered the list instead. Consistency wins; the failure mode of the other choice is an
/// exception, and the failure mode of this one is a wasted allocation.
///
/// Index 0 is the local player by construction, not by luck. AfterRoomIsLoaded opens with
/// `_players.Remove(me); _players.Insert(0, me);` precisely so the local shopper is drawn front
/// and centre at (0, 0), and the characters are appended in that order. That also means no
/// slot-0 correction is needed here, unlike the rest site — vanilla already did it.
/// </summary>
[HarmonyPatch(typeof(NMerchantRoom), "AfterRoomIsLoaded")]
public static class RaceSoloShopPatch
{
    public static void Postfix(NMerchantRoom __instance)
    {
        if (!DuelSession.IsRaceActive)
        {
            return;
        }

        for (int i = 1; i < __instance.PlayerVisuals.Count; i++)
        {
            NMerchantCharacter visual = __instance.PlayerVisuals[i];
            visual.Visible = false;
        }
    }
}
