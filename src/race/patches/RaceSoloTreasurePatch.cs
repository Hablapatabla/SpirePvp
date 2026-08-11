using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// The relic chest, made to understand that the opponent is not standing at it. Three separate
/// engine assumptions, all the co-located-party pattern, all found in one playtest (2026-08-11)
/// and grouped here because they are one concern.
///
/// **1. Two relics instead of one.** TreasureRoomRelicSynchronizer.BeginRelicPicking generates
/// a relic per participating player, so a racer was offered both and could take both.
/// Suppressing Hook.ShouldGenerateTreasure for non-local players is the surgical fix: that loop
/// also builds _votes, which is indexed by player slot everywhere else in the class and must
/// keep an entry each, so reimplementing it in a prefix would mean keeping that half in sync
/// with vanilla forever. The hook is the seam vanilla already put between "is there a player
/// here" and "generate their relic".
///
/// The other call site wants the same answer: OneOffSynchronizer.DoTreasureRoomRewards runs for
/// the *remote* player on TreasureChestOpenedMessage (it throws if handed the local player) and
/// grants them gold. In a race their gold is their own client's business, and our copy of their
/// Player is stale and re-synced at the arena anyway.
///
/// Consequence worth having: both racers now pull one relic from their own mirrored grab bag,
/// so both are offered the *same* relic — the mirror-match fairness §4 asks for, which the
/// two-relic version was quietly breaking.
///
/// **2. The relic could not be taken.** Fixing (1) exposed a barrier that the double-relic bug
/// had been hiding: picking a relic is a *vote*, and PickRelic bails out at
///
///     if (!_votes.All(v => v.voteReceived)) return;
///
/// before ever reaching AwardRelics. The local vote registered — the player's icon appeared on
/// the relic, which is exactly what the playtest reported — and then nothing, because the
/// opponent will never vote at a chest they are not at. With two relics each pick had resolved
/// immediately, so this only became reachable once there was one relic to vote on.
///
/// Vanilla has this mechanism already, for its own fake-multiplayer mode: it marks absent
/// players' votes received. We do the same but with **index = null**, i.e. "skipped", rather
/// than vanilla's random pick — a random pick would have the opponent vote for our only relic
/// and trigger a relic *fight* over it. Null makes AwardRelics see one voter, award
/// OnlyOnePlayerVoted, and correctly exclude the opponent from the consolation-prize list
/// (which tests `voteReceived &amp;&amp; !index.HasValue`).
///
/// **3. The arm came in from the wrong side.** NHandImage rotates by player slot —
/// `Index % 4` maps 0 to upright and 1 to a quarter turn — so the client, being slot 1, reached
/// for the relic from the side of the screen. Vanilla never shows this in singleplayer
/// (NHandImageCollection.Initialize returns early on Players.Count &lt;= 1), so slot 1 is only ever
/// meant to be seen alongside slot 0. In a race each player is alone at their own chest and
/// should get the upright hand.
///
/// Only the *local* hand is redirected. The opponent's NHandImage is deliberately left
/// constructed: GetHand returns null for a missing hand and OnInputStateChanged dereferences it
/// without a check, so not building it converts a cosmetic bug into an NRE. Vanilla's own
/// visibility gate already keeps it off screen unless the opponent is on a relic-picking screen
/// at the same moment — so a ghost hand remains possible in the narrow case where you are both
/// at chests simultaneously. Known, and left alone on purpose.
/// </summary>
[HarmonyPatch]
public static class RaceSoloTreasurePatch
{
    /// <summary>(1) Generate a relic for the local player only.</summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.ShouldGenerateTreasure))]
    [HarmonyPostfix]
    public static void OnlyGenerateLocalTreasure(Player player, ref bool __result)
    {
        if (!DuelSession.IsRaceActive || !__result)
        {
            return;
        }

        if (!LocalContext.IsMe(player))
        {
            __result = false;
        }
    }

    /// <summary>(2) The absent opponent has already "skipped", so the local vote resolves alone.</summary>
    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer),
                  nameof(TreasureRoomRelicSynchronizer.BeginRelicPicking))]
    [HarmonyPostfix]
    public static void SkipAbsentPlayerVotes(TreasureRoomRelicSynchronizer __instance)
    {
        if (!DuelSession.IsRaceActive || __instance.CurrentRelics == null)
        {
            return;
        }

        int skipped = 0;

        foreach (Player player in __instance._playerCollection.Players)
        {
            if (LocalContext.IsMe(player))
            {
                continue;
            }

            int slot = __instance._playerCollection.GetPlayerSlotIndex(player);
            if (slot < 0 || slot >= __instance._votes.Count)
            {
                continue;
            }

            TreasureRoomRelicSynchronizer.PlayerVote vote = __instance._votes[slot];
            if (vote.voteReceived)
            {
                continue;
            }

            // null index == skipped. Not a random pick: that would have them contest our relic.
            vote.index = null;
            vote.voteReceived = true;
            skipped++;
        }

        if (skipped > 0)
        {
            Log.Info($"[SpirePvp] race: marked {skipped} absent player relic vote(s) as skipped " +
                     "so the local pick resolves without them");
        }
    }

    /// <summary>(3) The local player is alone at the chest, so reach from slot 0.</summary>
    [HarmonyPatch(typeof(NHandImage), nameof(NHandImage.Create))]
    [HarmonyPrefix]
    public static void UprightLocalHand(Player player, ref int slotIndex)
    {
        if (!DuelSession.IsRaceActive)
        {
            return;
        }

        if (LocalContext.IsMe(player))
        {
            slotIndex = 0;
        }
    }

    /// <summary>
    /// (4) The opponent's hand never animates in — it is the "phantom hand" the playtest saw
    /// reaching across the host's chest.
    ///
    /// The hand *object* is still built. NHandImageCollection.GetHand returns null for a hand
    /// that was never added and OnInputStateChanged dereferences it without a check, so
    /// declining to construct it would turn a cosmetic bug into an NRE. Suppressing the reveal
    /// instead leaves every lookup valid and simply never shows it: IsShown starts false and
    /// only AnimateIn sets it.
    ///
    /// Vanilla gates this on the peer being on a relic-picking screen at the same moment, which
    /// is why it appeared intermittently rather than every chest.
    /// </summary>
    [HarmonyPatch(typeof(NHandImage), nameof(NHandImage.AnimateIn))]
    [HarmonyPrefix]
    public static bool HideAbsentPlayerHand(NHandImage __instance)
    {
        if (!DuelSession.IsRaceActive)
        {
            return true;
        }

        return LocalContext.IsMe(__instance.Player);
    }

    /// <summary>
    /// (5) Controller/keyboard focus, which is what actually blocked the client.
    ///
    ///     _holdersInUse[_runState.GetPlayerSlotIndex(LocalContext.GetMe(_runState.Players))]
    ///
    /// _holdersInUse holds one entry per *relic*, and vanilla can index it by player slot only
    /// because co-op generates one relic per player, making the two counts equal. Fixing (1)
    /// broke that equality: one relic, one holder, and a client sitting at slot 1 indexes past
    /// the end.
    ///
    /// It throws inside NTreasureRoom.OpenChest, so the rest of OpenChest never runs and the
    /// client is left in the chest with no way forward — the "stuck after grabbing the relic"
    /// report. The host is slot 0, so index 0 happened to be valid and it proceeded normally:
    /// the same slot-0/slot-1 asymmetry as the arm rotation, one failing loudly and one not at
    /// all.
    ///
    /// In a race the sole holder belongs to the local player by construction, so it is index 0.
    /// </summary>
    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), "DefaultFocusedControl", MethodType.Getter)]
    [HarmonyPrefix]
    public static bool LocalRelicHolderFocus(NTreasureRoomRelicCollection __instance,
                                             ref Control? __result)
    {
        if (!DuelSession.IsRaceActive)
        {
            return true;
        }

        __result = __instance._holdersInUse.Count > 0 ? __instance._holdersInUse[0] : null;
        return false;
    }
}
