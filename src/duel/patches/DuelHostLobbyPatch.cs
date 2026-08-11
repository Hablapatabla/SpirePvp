using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Pre-configures the lobby that the Duel entry opens.
///
/// The button and the lobby are two different places in the menu stack — pressing Duel starts a
/// Custom host, and `NCustomRunScreen` is what the stack pushes afterwards — so the screen has to
/// notice it was opened for a duel. `DuelHostFlow.Requested` carries that across, and is consumed
/// here so a host who backs out and starts a plain Custom run does not silently get a duel.
///
/// Both halves of the state are set, and both are needed for different reasons:
///
/// - `SetTickedModifiers` is the *view* — the tickboxes the host sees, which is the whole point
///   of the entry. Without it the modifiers would be active but invisible, which is worse than
///   the burial this replaces: settings you cannot see are settings you cannot check.
/// - `Lobby.SetModifiers` is the *state*, and it broadcasts `LobbyModifiersChangedMessage`, which
///   is what puts the agreed time control in front of the joining player before they commit
///   (DESIGN §5b). Ticking boxes locally would not tell them anything.
///
/// Client lobbies are deliberately untouched. The host owns the match configuration and the
/// client receives it over that message; a client that pre-ticked its own boxes would be
/// inventing settings that the run is not going to have.
/// </summary>
[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.InitializeMultiplayerAsHost))]
public static class DuelHostLobbyPatch
{
    public static void Postfix(NCustomRunScreen __instance)
    {
        if (!DuelHostFlow.Requested)
        {
            return;
        }

        DuelHostFlow.Requested = false;

        __instance._modifiersList.SetTickedModifiers(DuelHostFlow.BlitzPreset);
        __instance.Lobby?.SetModifiers(DuelHostFlow.BlitzPreset);

        Log.Warn("[SpirePvp] duel lobby: opened from the Duel entry, blitz preset applied " +
                 "(real-time, race 10 min, duel 2 min)");
    }
}
