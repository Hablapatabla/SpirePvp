using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// The co-located-party pattern again, at the rest site — the recurrence DESIGN I3 predicted
/// in as many words ("rest sites, shops and events each have their own synchronizer"). Found
/// by playtest 2026-08-11.
///
/// Symptom: the client rests, picks an option, clicks the next map node, and the game stops
/// dead on a faded map with no room. Nothing is logged, nothing errors, and the client keeps
/// answering the network the whole time, so it looks alive. The host plays on, unaffected.
/// It does not recover when the host finishes what it was doing, because the host was never
/// going to be the thing that unblocked it.
///
/// Mechanism, and it is a barrier rather than a message this time:
///
///     RestSiteSynchronizer.BeginRestSite()  -> one PlayerRestSite per player in the run,
///                                              each with its own TaskCompletionSource
///     RestSiteRoom.Exit()                   -> await AfterAllRestSitesCompleted()
///     AfterAllRestSitesCompleted()          -> await EVERY player's completionTaskSource
///
/// In a race the opponent is off in their own run and never chooses an option here, so their
/// source is never completed and Exit never returns. That await sits in front of everything:
/// RunManager.EnterMapPointInternal does `await ExitCurrentRooms()` *before*
/// CombatStateSynchronizer.StartSync, which is why the log shows the travel line and then no
/// sync broadcast, no preload, no room — the giveaway that it died in the exit, not the entry.
///
/// The fix mirrors vanilla's own handling of the identical situation. OnPeerDisconnected
/// exists precisely because a player who will never answer would otherwise "hang forever" on
/// AfterAllRestSitesCompleted (its words), and it resolves that by clearing the absent
/// player's options and completing their source. A racing opponent is that same absent player,
/// so we do the same thing at BeginRestSite: the local player's rest site is untouched and
/// gates the exit exactly as vanilla intends, and the opponent's is pre-completed.
///
/// **Complete in place rather than shortening the list.** _restSites is indexed by player slot
/// (LocalOptionHovered, HandleRestSiteOptionHoveredMessage and ChooseOption all do
/// _restSites[GetPlayerSlotIndex(...)]), so dropping the opponent's entry would turn this hang
/// into an IndexOutOfRangeException the moment the client — who is slot 1 — hovered an option.
/// Same reason RaceSoloCombatPatch adds a player rather than filtering a list.
///
/// A postfix, because it needs the entries vanilla builds.
/// </summary>
[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.BeginRestSite))]
public static class RaceSoloRestSitePatch
{
    public static void Postfix(RestSiteSynchronizer __instance)
    {
        if (!DuelSession.IsRaceActive)
        {
            return;
        }

        int completed = 0;

        foreach (Player player in __instance._playerCollection.Players)
        {
            if (player.NetId == __instance._localPlayerId)
            {
                continue;
            }

            int slot = __instance._playerCollection.GetPlayerSlotIndex(player);
            if (slot < 0 || slot >= __instance._restSites.Count)
            {
                continue;
            }

            RestSiteSynchronizer.PlayerRestSite restSite = __instance._restSites[slot];
            if (restSite.completionTaskSource.Task.IsCompleted)
            {
                continue;
            }

            restSite.options.Clear();
            restSite.completionTaskSource.TrySetResult();
            completed++;
        }

        if (completed > 0)
        {
            Log.Info($"[SpirePvp] race: pre-completed {completed} absent player rest site(s) " +
                     "so leaving the room does not wait on the opponent");
        }
    }
}
