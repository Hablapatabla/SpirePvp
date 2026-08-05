using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// M4 (DESIGN §1, I6): your hovers and selections are your business.
///
/// Co-op continuously shares what each player is looking at — PeerInputSynchronizer sends a
/// PeerInputMessage whenever the hovered or selected model changes, and again for targeting
/// state. Between teammates that is helpful. In a duel it is a tell: which card you are
/// hovering, how long you hesitate over it, and when you enter targeting all leak intent
/// that the information rules say is hidden.
///
/// Suppressed at the *broadcast*, not the display, and that distinction matters. Playtesting
/// showed no visible hover leak in the duel arena, which is misleading — the data goes over
/// the wire regardless, and only the co-op surfaces that would draw it
/// (NMultiplayerPlayerIntentHandler, NRemoteMouseCursorContainer) happen not to be on screen.
/// Suppressing the renderer would leave the information flowing and re-leak the moment any
/// UI shows a player panel; suppressing the send means there is nothing to leak.
///
/// Also covers the entry screen, where you are reading their decklist and they should not see
/// which cards you linger on.
///
/// Verifiable without a renderer: PeerInputMessage traffic should simply stop in the log once
/// a duel is active.
/// </summary>
[HarmonyPatch(typeof(PeerInputSynchronizer))]
public static class DuelHoverSuppressionPatch
{
    private static bool ShouldSuppress => DuelSession.IsDuelActive || DuelEntry.IsChoosing;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PeerInputSynchronizer.SyncLocalHoveredModel), typeof(AbstractModel))]
    public static bool BeforeSyncHovered()
    {
        return !ShouldSuppress;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PeerInputSynchronizer.SyncLocalIsTargeting), typeof(bool))]
    public static bool BeforeSyncTargeting()
    {
        return !ShouldSuppress;
    }
}
