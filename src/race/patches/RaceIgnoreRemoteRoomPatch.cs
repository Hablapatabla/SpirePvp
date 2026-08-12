using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// Drops the opponent's room traffic during a race — rest sites, rewards, events and one-offs.
///
/// **This is the campfire break, and it was caused by the fix for the campfire hang.**
/// `RaceSoloRestSitePatch` resolves the absent opponent's rest site up front so leaving the room
/// does not wait on them: it completes their task source and clears their options. That is
/// correct, and it is only half the story, because their messages still arrive:
///
///     InvalidOperationException: Player ... attempted to choose rest site option index 1, but
///     the rest site has already been completed!
///     ArgumentOutOfRangeException at NRestSiteRoom.OnPlayerChangedHoveredRestSiteOption
///       &lt;- RestSiteSynchronizer.HandleRestSiteOptionHoveredMessage
///
/// One throws because we completed their rest site; the other because we emptied the options
/// list their hover indexes into. Both are vanilla faithfully applying a peer's choice to state
/// we had already told it to forget.
///
/// **Why the messages arrive at all**, when the location buffer is supposed to hold them: the
/// two runs share a seed, so they share a map, so both players sit at the *same coord* when they
/// each hit that campfire. The buffer gates on location, not on identity — same location, so
/// deliver. The race decouples the runs but not the map, which is precisely what makes this
/// family of bug reachable at all.
///
/// The same log shows the same shape in rewards, which had gone unnoticed:
///
///     Tried to select reward for player ..., but they are not currently viewing any reward set!
///     Reward set ... is already finished (state Completed)!
///
/// So this is not "a rest site bug". It is **every per-room synchronizer**, and the one already
/// handled — the action queue, via RaceIgnoreRemoteActionsPatch — was handled because M5 tripped
/// over it first. This is the rest of that list.
///
/// Dropped by *sender* rather than wholesale: these handlers take the peer's id, so the local
/// player's own traffic is untouched and only the opponent's is discarded. During a race their
/// choices are events in a run we are not playing, and applying them to ours can only corrupt
/// it — the same reasoning that made RaceIgnoreRemoteActionsPatch drop card plays.
///
/// This also closes the event information leak reported alongside it: you could watch which
/// option your opponent was picking, because their choice was being applied to your copy of the
/// event.
/// </summary>
[HarmonyPatch]
public static class RaceIgnoreRemoteRoomPatch
{
    /// <summary>
    /// True when a message from <paramref name="senderId"/> should be discarded.
    ///
    /// Race-only. The duel deliberately wants the opposite: from the arena onward both clients
    /// are in one shared combat and every peer message matters.
    /// </summary>
    private static bool Drop(ulong senderId) =>
        DuelSession.IsRaceActive && LocalContext.NetId != senderId;

    [HarmonyPatch(typeof(RestSiteSynchronizer),
                  nameof(RestSiteSynchronizer.HandleRestSiteOptionChosenMessage))]
    [HarmonyPrefix]
    public static bool RestSiteChosen(ulong senderId) => !Drop(senderId);

    [HarmonyPatch(typeof(RestSiteSynchronizer),
                  nameof(RestSiteSynchronizer.HandleRestSiteOptionHoveredMessage))]
    [HarmonyPrefix]
    public static bool RestSiteHovered(ulong senderId) => !Drop(senderId);

    [HarmonyPatch(typeof(RestSiteSynchronizer),
                  nameof(RestSiteSynchronizer.HandleRestSiteSkippedMessage))]
    [HarmonyPrefix]
    public static bool RestSiteSkipped(ulong senderId) => !Drop(senderId);

    [HarmonyPatch(typeof(RewardsSetSynchronizer),
                  nameof(RewardsSetSynchronizer.HandleRewardSelectedMessage))]
    [HarmonyPrefix]
    public static bool RewardSelected(ulong senderId) => !Drop(senderId);

    [HarmonyPatch(typeof(RewardsSetSynchronizer),
                  nameof(RewardsSetSynchronizer.HandleRewardSetSkippedMessage))]
    [HarmonyPrefix]
    public static bool RewardSkipped(ulong senderId) => !Drop(senderId);

    [HarmonyPatch(typeof(EventSynchronizer),
                  nameof(EventSynchronizer.HandleEventOptionChosenMessage))]
    [HarmonyPrefix]
    public static bool EventOptionChosen(ulong senderId) => !Drop(senderId);

    [HarmonyPatch(typeof(EventSynchronizer),
                  nameof(EventSynchronizer.HandleVotedForSharedEventOptionMessage))]
    [HarmonyPrefix]
    public static bool EventVoted(ulong senderId) => !Drop(senderId);

    [HarmonyPatch(typeof(EventSynchronizer),
                  nameof(EventSynchronizer.HandleSharedEventOptionChosenMessage))]
    [HarmonyPrefix]
    public static bool SharedEventChosen(ulong senderId) => !Drop(senderId);

    [HarmonyPatch(typeof(OneOffSynchronizer),
                  nameof(OneOffSynchronizer.HandleTreasureChestOpenedMessage))]
    [HarmonyPrefix]
    public static bool TreasureChestOpened(ulong senderId) => !Drop(senderId);

    [HarmonyPatch(typeof(OneOffSynchronizer),
                  nameof(OneOffSynchronizer.HandleMerchantCardRemoval))]
    [HarmonyPrefix]
    public static bool MerchantCardRemoval(ulong senderId) => !Drop(senderId);

    [HarmonyPatch(typeof(OneOffSynchronizer),
                  nameof(OneOffSynchronizer.HandleCrystalSphereRewardsMessage))]
    [HarmonyPrefix]
    public static bool CrystalSphereRewards(ulong senderId) => !Drop(senderId);
}
