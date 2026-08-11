using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// The map split-vote animation must not run during a race.
///
/// NMapScreen.TravelToMapCoord opens with `await new MapSplitVoteAnimation(...).TryPlay(coord)`.
/// It exists to resolve a *contested vote*: when co-op players pick different nodes, it spins
/// through their portraits and lands on a winner. A race has no votes at all —
/// RaceMapTravelPatch skips voting entirely — so there is nothing for it to resolve, and it
/// should be skipped rather than made to work.
///
/// It ran anyway because RaceProgress repurposes PlayerVoteDictionary to mean "where the
/// opponent is" rather than "where they voted", and TryPlay reads that dictionary as votes.
/// Two players at two different coords look exactly like a split vote, so it animated on every
/// local move. Measured 2026-08-11: **80 exceptions in one run, zero in every run before it**:
///
///     InvalidOperationException at NMultiplayerVoteContainer.SetPlayerHighlighted
///       at MapSplitVoteAnimation.HighlightPlayer / TickSplitVoteAnimation
///
/// SetPlayerHighlighted throws when the container holds no icon for that player
/// (`_votes.FirstOrDefault(...)` is null), and RaceMapPositionPatch had just made the displayed
/// icons follow real positions instead of the dictionary. The animation drives off the
/// dictionary, the display no longer did, and the two disagreed once per tween tick.
///
/// Re-coupling them would mean restoring the wrong display to keep a vote animation happy for a
/// vote that does not exist. Skipping the animation removes the whole disagreement, and takes
/// a gratuitous ~1.2s pause off every map move with it.
///
/// **TryPlay is `async Task`, so this prefix assigns `__result`.** Returning false without it
/// leaves TravelToMapCoord awaiting null, which throws in *the caller* with no frame for the
/// method that was patched — the trap that has cost this project two multi-session hunts
/// (RaceStarsWithoutCombatPatch, then DuelEndCombatPatch). Task.CompletedTask, not null.
/// </summary>
[HarmonyPatch(typeof(MapSplitVoteAnimation), nameof(MapSplitVoteAnimation.TryPlay))]
public static class RaceNoSplitVotePatch
{
    public static bool Prefix(ref Task __result)
    {
        if (!DuelSession.IsRaceActive)
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}
