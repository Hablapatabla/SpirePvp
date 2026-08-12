using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// The Rematch button, and the one call that has to be held back for it to work.
///
/// **Why the button lives on this screen and nowhere else.** Leaving the result screen is what
/// disconnects — `NGameOverScreen.OnMainMenuButtonPressed` calls
/// `NetService.Disconnect(NetError.QuitGameOver)` outright — so this is the last moment two
/// players still share a connection. There is no later screen to put it on.
///
/// **And why one call is suppressed.** `RunManager.SetUpNewMultiplayer` refuses to start a run
/// while `State != null`, and only `CleanUp` nulls `State` — but `CleanUp` also disconnects. So a
/// rematch has to run the whole of vanilla's teardown while keeping the transport, which is
/// exactly one line to hold back. See <see cref="DuelRematch"/> for why suppressing one call beat
/// re-deriving the other twenty-five.
/// </summary>
[HarmonyPatch]
public static class DuelRematchPatch
{
    /// <summary>
    /// Holds the transport open through the rematch teardown.
    ///
    /// Patched on the concrete host and client services rather than on `INetGameService`, because
    /// Harmony patches methods and not interfaces — and named with `nameof` so a game update that
    /// moves either one is a build error here rather than a silent `PATCH FAILED`.
    ///
    /// The guard is `DuelRematch.Relaunching`, which is set for the length of one teardown and
    /// cleared in a `finally`. Every other disconnect in the game — quitting, abandoning, an error,
    /// the peer going away — passes through untouched, which is the point: this must not become a
    /// mod that cannot be disconnected from.
    /// </summary>
    [HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.Disconnect))]
    [HarmonyPrefix]
    public static bool BeforeHostDisconnect()
    {
        if (!DuelRematch.Relaunching)
        {
            return true;
        }

        Log.Warn("[SpirePvp] rematch: holding the host transport open through run teardown");
        return false;
    }

    [HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.Disconnect))]
    [HarmonyPrefix]
    public static bool BeforeClientDisconnect()
    {
        if (!DuelRematch.Relaunching)
        {
            return true;
        }

        Log.Warn("[SpirePvp] rematch: holding the client transport open through run teardown");
        return false;
    }

    /// <summary>
    /// Adds **Rematch** beside Main Menu once the result screen is up.
    ///
    /// Built by duplicating the main-menu button rather than by instantiating a scene, the same
    /// trick M7 used for the Duel entry on the host submenu: it inherits the screen's art, sizing
    /// and focus behaviour for free, which is most of what makes an added control look native.
    ///
    /// Gated on <see cref="DuelRematch.CanOffer"/> rather than on the phase alone, so a match that
    /// ended *because* the opponent vanished does not offer to play them again.
    /// </summary>
    [HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen._Ready))]
    [HarmonyPostfix]
    public static void AfterReady(NGameOverScreen __instance)
    {
        if (!DuelRematch.CanOffer)
        {
            return;
        }

        try
        {
            NButton? menuButton = __instance._mainMenuButton;
            if (!menuButton.IsValid() || menuButton!.GetParent() is not Node parent)
            {
                Log.Warn("[SpirePvp] rematch: no main-menu button to sit beside — no button added");
                return;
            }

            // **Signals are excluded from the copy deliberately.** Godot's default `Duplicate()`
            // carries DUPLICATE_SIGNALS, which would bring the screen's own
            // `Released -> OnMainMenuButtonPressed` connection along — so pressing Rematch would
            // also return to the main menu, disconnecting on the way out and taking the rematch
            // with it. Scripts and groups are wanted; connections are not.
            NButton rematch = (NButton)menuButton.Duplicate(
                (int)(Node.DuplicateFlags.Scripts | Node.DuplicateFlags.Groups));
            rematch.Name = "SpirePvpRematchButton";
            parent.AddChild(rematch);
            parent.MoveChild(rematch, menuButton.GetIndex());

            // **After AddChild, because `_Ready` overwrites both.** `NReturnToMainMenuButton._Ready`
            // sets its own label from `_mainMenuLoc` and leaves the button transparent and shifted
            // 140px left, expecting the screen to animate it in — an animation that only ever runs
            // for vanilla's button. So the label is written after the node has entered the tree,
            // and the modulate and position are put back by hand.
            MegaLabel? label = rematch.GetNodeOrNull<MegaLabel>("Label");
            label?.SetTextAutoSize(
                new LocString("game_over_screen", "SPIREPVP_REMATCH.title").GetFormattedText());
            rematch.Modulate = Colors.White;
            rematch.Position = menuButton.Position + new Vector2(0f, -80f);

            rematch.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(OnRematchPressed));

            Log.Warn($"[SpirePvp] rematch: button added beside {menuButton.Name} "
                     + $"at pos={rematch.Position} (menu button at {menuButton.Position})");
        }
        catch (Exception e)
        {
            // The result screen matters more than the button on it.
            Log.Error($"[SpirePvp] rematch: could not add the button: {e}");
        }
    }

    private static void OnRematchPressed(NButton _)
    {
        if (DuelRematch.IncomingOfferPending)
        {
            DuelRematch.Respond(accept: true);
            return;
        }

        DuelRematch.Offer();
    }
}
