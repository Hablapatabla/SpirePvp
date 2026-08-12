using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// M5 spike, the other half of blocker 3 — a time bomb rather than a visible freeze.
///
/// Once each side enqueues its own actions locally (RaceLocalActionPatch), the peer's action
/// traffic becomes actively harmful. The host still broadcasts ActionEnqueuedMessage for its
/// own plays, and the client still holds them in RunLocationTargetedMessageBuffer — which
/// releases on *visited* locations, not current ones. So the moment the client reaches a coord
/// the host already cleared, the buffer flushes and the host's card plays execute inside the
/// client's run.
///
/// That would not look like a network bug. It would look like cards playing themselves, and it
/// would land minutes after the actual cause, in a different room. Worth killing outright.
///
/// So while racing, drop peer action traffic in both directions:
///   - the *Enqueued handlers, so neither side replays the other's actions;
///   - the Request* handlers, so the host does not arbitrate a client request that a stale
///     buffer eventually delivers.
///
/// Mod messages are unaffected: DuelMessages do not implement IRunLocationTargetedMessage and
/// do not travel this path, so race/duel coordination still works.
///
/// This is race-only. The duel needs every one of these handlers live — it is a single shared
/// combat whose determinism depends on exactly this traffic.
/// </summary>
public static class RaceIgnoreRemoteActionsPatch
{
    internal static bool ShouldDrop() => DuelSession.IsRaceActive;
}

[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.HandleActionEnqueuedMessage))]
public static class RaceIgnoreActionEnqueuedPatch
{
    public static bool Prefix() => !RaceIgnoreRemoteActionsPatch.ShouldDrop();
}

[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.HandleHookActionEnqueuedMessage))]
public static class RaceIgnoreHookActionEnqueuedPatch
{
    public static bool Prefix() => !RaceIgnoreRemoteActionsPatch.ShouldDrop();
}

[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.HandleRequestEnqueueActionMessage))]
public static class RaceIgnoreRequestEnqueuePatch
{
    public static bool Prefix() => !RaceIgnoreRemoteActionsPatch.ShouldDrop();
}

[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.HandleRequestEnqueueHookActionMessage))]
public static class RaceIgnoreRequestEnqueueHookPatch
{
    public static bool Prefix() => !RaceIgnoreRemoteActionsPatch.ShouldDrop();
}
