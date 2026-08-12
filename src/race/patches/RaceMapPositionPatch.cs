using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// Where each player's portrait is drawn on the map. Playtest 2026-08-11: on the client both
/// portraits sat stacked on the client's own node, while the host's map was correct.
///
/// RaceProgress shows the opponent by reusing vanilla's map-vote portraits (see its comment).
/// That works, but it inherits vanilla's fallback, which is the actual bug:
///
///     private bool ShouldDisplayPlayerVote(Player player)
///     {
///         if (_screen.PlayerVoteDictionary.TryGetValue(player, out var value) &amp;&amp; value.HasValue)
///             return value == Point.coord;
///         return _runState.MapLocation.coord == Point.coord;   // &lt;-- any player with no vote
///     }
///
/// A player with no vote entry is drawn at **the local run's** location — because in co-op the
/// party is co-located, so "no opinion yet" and "standing where I am" are the same thing. In a
/// race they are opposites.
///
/// What makes it intermittent rather than constant: NMapScreen.TravelToMapCoord ends with
/// `PlayerVoteDictionary.Clear()`, so **every local move wipes the opponent's entry**, dropping
/// them onto the fallback until their next RaceProgressMessage puts them back. So the display
/// is only correct in the window between their broadcast and your next step.
///
/// That is also the asymmetry the playtest saw, and it is worth keeping in mind as a diagnostic
/// shape: the host was in a long elite fight — not travelling, so never clearing, and receiving
/// a steady stream of the client's moves — so its map stayed right. The client travelled
/// repeatedly while the host sent nothing, so the host's portrait stuck to the client's own node
/// and had nothing to correct it. Neither client was more broken than the other; they were
/// moving at different rates.
///
/// The fix is to stop inferring position from a dictionary vanilla clears at will, and ask the
/// question we actually mean. For the opponent the authoritative answer is RaceProgress, which
/// is fed by their own broadcasts; for us it is the local run state, which is what vanilla's
/// fallback already says. Before their first report OpponentCoord is null, and they are drawn on
/// the map's starting node — where an unmoved run necessarily is — rather than on top of you,
/// which is the failure being removed. See the comment on that default in the body.
///
/// This makes the portraits independent of PlayerVoteDictionary entirely, so RaceProgress's
/// OnPlayerVoteChangedInternal call is now only doing the repaint of the two affected nodes.
/// Left in place for exactly that reason.
/// </summary>
[HarmonyPatch(typeof(NMapPoint), nameof(NMapPoint.ShouldDisplayPlayerVote))]
public static class RaceMapPositionPatch
{
    public static bool Prefix(NMapPoint __instance, Player player, ref bool __result)
    {
        if (!DuelSession.IsRaceActive)
        {
            return true;
        }

        // Arriving at the arena is a real move for display purposes even though the run has not
        // travelled there: DuelRendezvous deliberately does not enter the node on click, it
        // announces and waits. So an arrived player is at the arena and *nowhere else* — asking
        // only "where is your run standing" would both hide the waiting portrait and leave a
        // second copy of them back at their last room.
        //
        // Getting this wrong is how the first version of this patch silently removed the only
        // on-screen feedback that clicking the arena had done anything (playtest 2026-08-11:
        // "no visible change"). ShowWaitingPortrait routes through this same predicate.

        // **Before anyone has moved there is no coord to compare against, and the map drew
        // nobody.** `CurrentMapCoord` is the last entry in `_visitedMapCoords`, which is empty
        // until a room is entered, and `RaceProgress` has heard nothing because the opponent has
        // not travelled yet — so through Neow both branches below answered "nowhere". Reported
        // 2026-08-12 as seeing only your own icon at Neow.
        //
        // A run that has not moved is standing on the map's starting node, by definition. That is
        // the one position both players share *and both already know*, so it needs no message:
        // each side simply defaults the other to it until their first real report arrives. Adding
        // a broadcast for it was tried first and backed out — it announces a fact the receiver can
        // derive, and it cannot fire any earlier than this anyway.
        MapCoord? start = __instance._runState.Map?.StartingMapPoint.coord;

        if (LocalContext.IsMe(player))
        {
            MapCoord? mine = __instance._runState.MapLocation.coord ?? start;
            __result = DuelRendezvous.LocalArrived
                ? DuelRendezvous.IsArenaCoord(__instance.Point.coord)
                : mine == __instance.Point.coord;
            return false;
        }

        MapCoord? theirs = RaceProgress.OpponentCoord ?? start;
        __result = DuelRendezvous.RemoteArrived
            ? DuelRendezvous.IsArenaCoord(__instance.Point.coord)
            : theirs.HasValue && theirs.Value == __instance.Point.coord;
        return false;
    }
}
