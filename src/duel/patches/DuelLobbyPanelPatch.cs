using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Applies the duel-first layout to a lobby once it is known to be a duel.
///
/// `NCustomRunScreen.ModifiersChanged` is the right hook because it is the one point both sides
/// pass through, for different reasons:
///
/// - On the **host**, ticking the preset raises the list's `ModifiersChanged` signal, which
///   `OnModifiersListChanged` turns into `Lobby.SetModifiers`, which calls back into the screen
///   here.
/// - On the **client**, the host's `LobbyModifiersChangedMessage` arrives and the lobby calls the
///   same method. The client never presses anything, so this is the *only* moment it could learn
///   the lobby is a duel.
///
/// Keying on the modifiers rather than on `DuelHostFlow.Requested` is what makes the client work
/// at all — that flag is host-side and one-shot. It also means a host who reaches the same
/// configuration by hand, through the plain Custom entry, gets the same organised screen, which
/// is the right behaviour: the layout describes what the lobby *is*, not how it was opened.
///
/// `DuelLobbyPanel.Apply` is idempotent — it returns immediately if the panel already exists —
/// which matters because this fires on every subsequent modifier change too.
/// </summary>
[HarmonyPatch]
public static class DuelLobbyPanelPatch
{
    /// <summary>Any later change to the lobby's modifiers, on either side.</summary>
    [HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.ModifiersChanged))]
    [HarmonyPostfix]
    public static void AfterModifiersChanged(NCustomRunScreen __instance) => Refresh(__instance);

    /// <summary>
    /// The client's *first* sight of the lobby, and the case ModifiersChanged cannot cover.
    ///
    /// A joining client showed the ordinary Custom Mode screen — right modifiers, wrong
    /// presentation — and the logs said why by omission: no `Received ModifiersChangedMessage`
    /// anywhere. That message is a broadcast sent when the host *changes* something, and the
    /// host had picked the preset before anyone joined. The client's opening state arrives in
    /// its `ClientLobbyJoinResponseMessage` instead, which `InitializeFromMessage` unpacks
    /// without the listener callback ever firing.
    ///
    /// So the panel only appeared on a client if the host happened to touch a modifier after
    /// they joined — which is the one thing a host with a preset already applied has no reason
    /// to do.
    ///
    /// A postfix, because Lobby.Modifiers is not populated until InitializeFromMessage has run
    /// inside this method.
    /// </summary>
    [HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.InitializeMultiplayerAsClient))]
    [HarmonyPostfix]
    public static void AfterClientJoined(NCustomRunScreen __instance) => Refresh(__instance);

    private static void Refresh(NCustomRunScreen __instance)
    {
        bool isDuel = __instance.Lobby != null
                      && DuelMatch.HasTurnModel(__instance.Lobby.Modifiers);

        // Set unconditionally, both ways. The submenu stack reuses this screen node, so a plain
        // Custom lobby opened after a duel would otherwise still be titled "Duel".
        DuelLobbyPanel.SetTitle(__instance, isDuel);

        if (!isDuel)
        {
            return;
        }

        DuelLobbyPanel.Apply(__instance);
    }
}
