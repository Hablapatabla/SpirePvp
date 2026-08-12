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
[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.ModifiersChanged))]
public static class DuelLobbyPanelPatch
{
    public static void Postfix(NCustomRunScreen __instance)
    {
        if (__instance.Lobby == null || !DuelMatch.HasTurnModel(__instance.Lobby.Modifiers))
        {
            return;
        }

        DuelLobbyPanel.Apply(__instance);
    }
}
