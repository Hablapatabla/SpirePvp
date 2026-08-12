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
/// The relic chest, made to understand that the opponent is not standing at it. Four separate
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
/// **3. Hands that should never have been on screen.** The reaching hand is a co-op affordance
/// — it shows where the *other* player is pointing — and vanilla draws none when you are alone.
/// A race is that situation wearing a two-player run state, so it draws none either.
///
/// This is worth reading as a correction rather than a feature. Two narrower patches came
/// first: one re-pointed the local hand at slot 0, because NHandImage rotates by `Index % 4`
/// and a slot-1 client reached in from the side of the screen; the other suppressed the
/// opponent's, which appeared as a phantom hand groping across the host's chest. Both were
/// compensating for drawing something singleplayer would not draw at all. Checking what
/// vanilla does when alone replaced both with one line.
///
/// **4. The client could not leave the chest.** Covered on LocalRelicHolderFocus below — the
/// slot-indexing assumption again, this time throwing rather than merely looking wrong.
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

        int skipped = RaceSolo.SatisfyAbsentPlayers(
            __instance._playerCollection,
            __instance._votes.Count,
            slot =>
            {
                TreasureRoomRelicSynchronizer.PlayerVote vote = __instance._votes[slot];
                if (vote.voteReceived)
                {
                    return false;
                }

                // null index == skipped. Not a random pick: that would have them contest our
                // relic and turn it into a relic fight.
                vote.index = null;
                vote.voteReceived = true;
                return true;
            });

        if (skipped > 0)
        {
            Log.Info($"[SpirePvp] race: marked {skipped} absent player relic vote(s) as skipped " +
                     "so the local pick resolves without them");
        }
    }

    /// <summary>
    /// (3) No reaching hands at all — vanilla draws none when alone
    /// (NHandImageCollection.Initialize returns before creating any if Players.Count &lt;= 1, and
    /// _Input early-returns on the same test).
    ///
    /// Suppressing the reveal rather than declining to construct the node: GetHand returns null
    /// for a hand that was never added and OnInputStateChanged dereferences it unguarded, so
    /// not building it trades a cosmetic bug for an NRE. IsShown starts false and only AnimateIn
    /// sets it, so never calling it leaves every lookup valid and nothing on screen.
    /// </summary>
    [HarmonyPatch(typeof(NHandImage), nameof(NHandImage.AnimateIn))]
    [HarmonyPrefix]
    public static bool NoHandsInASoloChest()
    {
        return !DuelSession.IsRaceActive;
    }

    /// <summary>
    /// (4) Controller/keyboard focus, which is what actually blocked the client.
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
    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), nameof(NTreasureRoomRelicCollection.DefaultFocusedControl), MethodType.Getter)]
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
