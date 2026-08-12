using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// Takes the co-op furniture off a racer's screen: the teammate overlay, the opponent's mouse
/// cursor, Mend at the campfire, and shared events.
///
/// All four are the same root as everything else in <see cref="RaceSolo"/> — the engine reading
/// `Players.Count > 1` as "co-op" — but they are grouped separately because they are not merely
/// cosmetic. Three of them **leak information the mode is built to withhold** (DESIGN §1): your
/// opponent's HP and deck, where their cursor is hovering, and what they are picking. A race in
/// which you can watch your opponent's every move is a different, worse game than one where you
/// have to infer it, which is exactly the argument that cut the progress HUD down to a debug
/// tool in M6.
///
/// Investigation I6 predicted this precisely: *"What is missing is only a renderer — the
/// consumers are co-op surfaces that the arena does not currently surface. Anything that later
/// shows a player panel turns the leak visible. Suppress at the broadcast, not at the display."*
/// The race surfaces them, and where a broadcast exists this suppresses there.
/// </summary>
[HarmonyPatch]
public static class RaceNoCoopSurfacesPatch
{
    /// <summary>
    /// The teammate overlay, which shows every other player's HP and deck.
    ///
    /// Vanilla builds one panel per player and skips the whole thing when alone
    /// (`Players.Count <= 1`), so a race should look like the alone case. Hidden rather than not
    /// built: `HighlightPlayer`, `FlashPlayerReady` and friends look panels up by player, and a
    /// missing entry turns a cosmetic problem into an exception — the same trade made for the
    /// campfire seat, the merchant shopper and the treasure hand.
    /// </summary>
    [HarmonyPatch(typeof(NMultiplayerPlayerStateContainer),
                  nameof(NMultiplayerPlayerStateContainer.Initialize))]
    [HarmonyPostfix]
    public static void HideTeammateOverlay(NMultiplayerPlayerStateContainer __instance)
    {
        if (!DuelSession.IsRaceActive)
        {
            return;
        }

        foreach (NMultiplayerPlayerState node in __instance._nodes)
        {
            node.Visible = false;
        }
    }

    /// <summary>
    /// The opponent's mouse cursor, suppressed at the send rather than the draw.
    ///
    /// I6's instruction, and it is the right one: the position was always on the wire and only
    /// wanted a renderer, so hiding the renderer would leave a client able to reconstruct it.
    /// Not broadcasting is the only version that is actually private.
    ///
    /// Controller focus and mouse-down travel the same path and go with it — a cursor you cannot
    /// see but whose clicks you can is not meaningfully hidden.
    /// </summary>
    [HarmonyPatch(typeof(PeerInputSynchronizer), nameof(PeerInputSynchronizer.SyncLocalMousePos))]
    [HarmonyPrefix]
    public static bool NoMousePosBroadcast() => !DuelSession.IsRaceActive;

    [HarmonyPatch(typeof(PeerInputSynchronizer),
                  nameof(PeerInputSynchronizer.SyncLocalControllerFocus))]
    [HarmonyPrefix]
    public static bool NoControllerFocusBroadcast() => !DuelSession.IsRaceActive;

    [HarmonyPatch(typeof(PeerInputSynchronizer), nameof(PeerInputSynchronizer.SyncLocalMouseDown))]
    [HarmonyPrefix]
    public static bool NoMouseDownBroadcast() => !DuelSession.IsRaceActive;

    /// <summary>
    /// Mend, which exists only to heal a teammate.
    ///
    /// `RestSiteOption.Generate` adds it on `player.RunState.Players.Count > 1` — the
    /// content-level twin of the co-located-party bug, and the same test that offered co-op
    /// cards and Massive Scroll's blessing to a racer. Offering it here is worse than clutter:
    /// it is a campfire choice that cannot do anything, spending a rest the player does not get
    /// back.
    ///
    /// Removed from the generated list rather than blocked on selection, so it never appears.
    /// </summary>
    [HarmonyPatch(typeof(RestSiteOption), nameof(RestSiteOption.Generate))]
    [HarmonyPostfix]
    public static void NoMendInARace(ref List<RestSiteOption> __result)
    {
        if (!DuelSession.IsRaceActive || __result == null)
        {
            return;
        }

        __result.RemoveAll(option => option is MendRestSiteOption);
    }

    /// <summary>
    /// Shared events — the ones written for two players deciding together.
    ///
    /// `EventModel.IsShared` marks them, and the engine treats them as genuinely collaborative:
    /// `EventSynchronizer` votes across players and resolves once for everyone, and their RNG is
    /// deliberately seeded without the player slot offset so both sides roll identically. None of
    /// that has meaning when the two runs are independent, and it was reachable — a racer met one
    /// and could watch the other pick.
    ///
    /// Filtered through `IsAllowed`, which is the seam vanilla already uses to keep content out
    /// of a pool it does not suit, and the same one `RaceNoCoopCardsPatch` uses for Massive
    /// Scroll. Filtering here means the event is never rolled rather than being rolled and then
    /// awkwardly suppressed.
    /// </summary>
    [HarmonyPatch(typeof(EventModel), nameof(EventModel.IsAllowed))]
    [HarmonyPostfix]
    public static void NoSharedEvents(EventModel __instance, ref bool __result)
    {
        if (!DuelSession.IsRaceActive || !__result)
        {
            return;
        }

        if (__instance.IsShared)
        {
            __result = false;
        }
    }
}
