using Godot;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
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
    ///
    /// **The same omission hides a second half, and it is vanilla's.** Building the panel put the
    /// right *rows* on the client's screen with every box unticked, so the client could see which
    /// decisions exist and not which ones the host had made — worse than the plain Custom list it
    /// replaced, because an unticked row reads as "no clock" rather than "not told yet".
    /// `InitializeFromMessage` fills `Lobby.Modifiers` from the join response but never calls the
    /// listener back, so `NCustomRunScreen.ModifiersChanged` — the only thing that ever reaches
    /// `SyncModifierList` — does not run for a client's opening state. Calling it here is the same
    /// path the client takes for every *later* change, so there is no second notion of "apply the
    /// host's modifiers" to keep in step.
    ///
    /// Vanilla's own guard inside it is the reason this is safe to call unconditionally:
    /// `SyncModifierList` throws in host and singleplayer mode, and `ModifiersChanged` only
    /// reaches it when the net service is a client — which, inside a postfix on
    /// `InitializeMultiplayerAsClient`, it is by construction.
    /// </summary>
    [HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.InitializeMultiplayerAsClient))]
    [HarmonyPostfix]
    public static void AfterClientJoined(NCustomRunScreen __instance)
    {
        // Ticks the boxes to match the host. Our own postfix on ModifiersChanged fires from this
        // and builds the panel; Refresh below is called anyway rather than relying on that, since
        // both are idempotent and one of them running is not worth reasoning about.
        __instance.ModifiersChanged();

        Refresh(__instance);

        // What the client believes it agreed to, in its own log. An unticked row and a row nobody
        // told the client about look identical on screen, so the distinction has to be written
        // down somewhere — and the modifiers are the match, so a client showing the wrong ones is
        // two people playing under different rules without either of them being told.
        List<ModifierModel> ticked = __instance._modifiersList?.GetModifiersTickedOn()
                                     ?? new List<ModifierModel>();
        Log.Warn("[SpirePvp] duel lobby: joined with "
                 + $"{string.Join(", ", ticked.Select(m => m.Id.Entry))}");
    }

    private static void Refresh(NCustomRunScreen __instance)
    {
        bool isDuel = __instance.Lobby != null
                      && DuelMatch.HasTurnModel(__instance.Lobby.Modifiers);

        // Set unconditionally, both ways. The submenu stack reuses this screen node, so a plain
        // Custom lobby opened after a duel would otherwise still be titled "Duel".
        DuelLobbyPanel.SetTitle(__instance, isDuel);

        // The character row is built once, before the lobby's modifiers are known, so the Random
        // button cannot decide for itself whether it belongs — see DuelRandomCharacterButtonPatch.
        // This is where the answer exists, and where it is re-asked whenever it changes.
        if (__instance._charButtonContainer?
                .GetNodeOrNull<Control>(DuelRandomCharacterButtonPatch.ButtonName) is Control random)
        {
            random.Visible = isDuel;
        }

        if (!isDuel)
        {
            // The submenu stack hands the same screen node to Custom and to Duel, so leaving the
            // panel up meant a plain Custom lobby opened after a duel was still wearing it.
            DuelLobbyPanel.Remove(__instance);
            return;
        }

        DuelLobbyPanel.Apply(__instance);
    }
}
