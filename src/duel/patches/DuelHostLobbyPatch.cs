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
/// **Ticking the boxes is the whole job — do not also set the lobby's modifiers.**
/// `SetTickedModifiers` ends by emitting `ModifiersChanged`, which `NCustomRunScreen` has
/// already wired to `OnModifiersListChanged`, which calls
/// `Lobby.SetModifiers(_modifiersList.GetModifiersTickedOn())`. So one call updates the view the
/// host sees *and* broadcasts `LobbyModifiersChangedMessage`, which is what puts the agreed time
/// control in front of the joining player before they commit (DESIGN §5b).
///
/// Setting the lobby directly as well looks harmless and is not. The tickboxes hold **mutable
/// copies** — `GetAllModifiers` does `yield return item.ToMutable()` — whereas `ModelDb.Modifier
/// &lt;T&gt;()` hands back the canonical instance, and `SetModifiers` serialises what it is given:
///
///     CanonicalModelException: Canonical model of type SpirePvp.Modifiers.DuelBlitz used in
///     incorrect place.  at ModifierModel.ToSerializable() -> AssertMutable()
///
/// That threw out of `InitializeMultiplayerAsHost` and surfaced as an "internal error" popup on
/// pressing Duel. Routing through the tickboxes means the lobby only ever receives the instances
/// vanilla built for it, which is also why matching by `IsEquivalent` is enough to pass canonical
/// models *in*.
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

        // Emits ModifiersChanged, which syncs the lobby through vanilla's own handler.
        __instance._modifiersList.SetTickedModifiers(DuelHostFlow.BlitzPreset);

        Log.Warn("[SpirePvp] duel lobby: opened from the Duel entry, blitz preset applied " +
                 "(real-time, race 10 min, duel 2 min)");
    }
}
