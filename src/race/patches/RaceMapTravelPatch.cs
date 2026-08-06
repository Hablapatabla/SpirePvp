using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// M5 spike, blocker 1 of 2 (DESIGN I3): map traversal.
///
/// Vanilla moves the party as one. Clicking a node does not move you — it enqueues a
/// VoteForMapCoordAction, and MapSelectionSynchronizer sits on it until *every* player has
/// voted. Only then does the host pick a single destination (randomly, among tied votes) and
/// enqueue one MoveToMapCoordAction, whose ExecuteAction runs
/// NMapScreen.TravelToMapCoord on every client. One destination, everybody travels.
///
/// So during a race the vote is exactly the wrong mechanism: it blocks on a peer who is not
/// coming, and if it ever unblocked it would drag both players to one node.
///
/// The fix is smaller than I3 feared. RunState.CurrentMapCoord is global *within a client*,
/// but each client owns its own RunState — they agree only because that one action executes
/// everywhere. Skip the vote, call TravelToMapCoord locally, and the two positions diverge
/// naturally with no engine surgery. There is no shared authority to fight.
///
/// TravelToMapCoord is the same call the vanilla action ends up making, so the animation,
/// the fade, RunManager.EnterMapCoord and the visited-coords bookkeeping all behave exactly
/// as they do in a normal run.
///
/// Fire-and-forget via RunSafely matches the original's own handling of that task.
/// </summary>
[HarmonyPatch(typeof(NMapScreen), "OnMapPointSelectedLocally")]
public static class RaceMapTravelPatch
{
    public static bool Prefix(NMapPoint point)
    {
        if (!DuelSession.IsRaceActive)
        {
            return true;
        }

        NMapScreen? screen = NMapScreen.Instance;
        if (screen == null)
        {
            return true;
        }

        // The arena is the exception to independent traversal: clicking it announces arrival
        // and waits for the opponent rather than entering the room. DuelRendezvous opens the
        // deck-review screen once both are present, and DuelEntry takes it from there.
        if (DuelRendezvous.IsArenaCoord(point.Point.coord))
        {
            DuelRendezvous.ArriveLocal();
            return false;
        }

        Log.Info($"[SpirePvp] race: travelling locally to {point.Point.coord} (no vote)");
        RaceProgress.ReportLocalMove(point.Point.coord);
        TaskHelper.RunSafely(screen.TravelToMapCoord(point.Point.coord));
        return false;
    }
}
