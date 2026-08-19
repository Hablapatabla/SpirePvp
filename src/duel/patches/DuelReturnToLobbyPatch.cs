using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Adds **Return to Lobby** to the result screen, beside Rematch.
///
/// **Built by duplicating the Main Menu button**, the same trick Rematch and the M7 Duel entry both
/// use: the clone inherits the screen's art, sizing, focus behaviour and show/hide animation for
/// free, which is most of what makes an added control look like it belongs. Nothing here ships a
/// scene.
///
/// **Positioned two steps left rather than one**, because Rematch already took the first. The step
/// is measured off the menu button's own width for the same reason Rematch measures it: the result
/// screen lays its buttons out itself, and a hard-coded gap is wrong the first time the font or the
/// language changes.
///
/// **The caption carries the state instead of a popup.** Rematch established this and it is the
/// better surface here too — the result screen is already a screen full of buttons, and a modal
/// over it to ask "shall we?" is a second thing to dismiss. So the label reads *Return to Lobby*,
/// then *Waiting…* once you have asked, and *To lobby?* when they have asked you; pressing it in
/// that last state is the acceptance. A marker mirrors the peer's intent, exactly as Rematch's does.
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen._Ready))]
public static class DuelReturnToLobbyPatch
{
    /// <summary>Public so `DuelRematchPatch`'s enable mirror can find it. See `MirrorTo`.</summary>
    public const string ButtonName = "SpirePvpReturnToLobbyButton";
    private const string Table = "game_over_screen";

    /// <summary>Fallback spacing when the menu button has not been laid out yet. Matches Rematch.</summary>
    private const float ButtonGap = 260f;

    /// <summary>Greyed rather than hidden, so the row does not reflow when it goes unavailable.</summary>
    private static readonly Color DisabledTint = new Color(1f, 1f, 1f, 0.35f);

    private static NReturnToMainMenuButton? _button;

    [HarmonyPostfix]
    public static void AfterReady(NGameOverScreen __instance)
    {
        if (!DuelReturnToLobby.CanOffer)
        {
            return;
        }

        try
        {
            NReturnToMainMenuButton? menuButton = __instance._mainMenuButton;
            if (!menuButton.IsValid() || menuButton!.GetParent() is not Node parent)
            {
                Log.Warn("[SpirePvp] return to lobby: no main-menu button to sit beside — none added");
                return;
            }

            NReturnToMainMenuButton button = (NReturnToMainMenuButton)menuButton.Duplicate(
                (int)(Node.DuplicateFlags.Scripts | Node.DuplicateFlags.Groups));
            button.Name = ButtonName;

            // The clone inherits the Main Menu button's hotkey glyph, which would then be a second
            // control claiming the same key. Same removal Rematch makes.
            if (button.GetNodeOrNull("HotkeyIcon") is Node hotkeyIcon)
            {
                button.RemoveChild(hotkeyIcon);
                hotkeyIcon.QueueFree();
            }

            // `Duplicate` copies the *reference* to the hover material, so hovering either button
            // would light both. Measured on the Duel entry in M7 and true again here.
            TextureRect? menuImage = menuButton.GetNodeOrNull<TextureRect>("Image");
            TextureRect? ourImage = button.GetNodeOrNull<TextureRect>("Image");
            bool sharedMaterial = menuImage?.Material != null
                                  && ReferenceEquals(menuImage.Material, ourImage?.Material);
            if (sharedMaterial && ourImage != null)
            {
                ourImage.Material = (Material)ourImage.Material.Duplicate();
            }

            parent.AddChild(button);
            parent.MoveChild(button, menuButton.GetIndex());

            SetLabel(button, "SPIREPVP_RETURN_LOBBY.title", "Return to Lobby");

            // **To the right of Main Menu**, so the row reads Rematch · Main Menu · Return to Lobby.
            // Asked for 2026-08-18. Rematch sits one step *left* of the anchor, so putting this one
            // step right of it keeps Main Menu where players already expect to find it rather than
            // shifting the whole row along by one every time a button is added.
            float step = menuButton.Size.X > 1f ? menuButton.Size.X + 40f : ButtonGap;
            button._showPosition = menuButton._showPosition + new Vector2(step, 0f);

            _button = button;
            DuelReturnToLobby.StateChanged += RefreshFromState;

            // Unsubscribed on the node's way out, not on run teardown. The button dies with the
            // screen, and a static event holding a freed node is the leak this project already
            // documents for the initiative arrow.
            button.TreeExiting += () =>
            {
                DuelReturnToLobby.StateChanged -= RefreshFromState;
                _button = null;
            };

            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(OnPressed));

            Log.Warn($"[SpirePvp] return to lobby: button added, resting at {button._showPosition} "
                     + $"(menu button at {menuButton._showPosition}, step {step}, "
                     + $"hoverMaterialWasShared={sharedMaterial})");
        }
        catch (Exception e)
        {
            Log.Error($"[SpirePvp] return to lobby: could not add the button: {e}");
        }
    }

    /// <summary>
    /// One press means three different things, and which one is read off the offer state.
    ///
    /// Accepting *their* offer is checked first: if both sides have asked, the crossing is already
    /// agreement and `Offer` would answer it anyway — this just makes the button do the obvious
    /// thing rather than depending on that.
    /// </summary>
    private static void OnPressed(NButton _)
    {
        if (DuelReturnToLobby.IncomingOfferPending)
        {
            DuelReturnToLobby.Respond(accept: true);
            return;
        }

        DuelReturnToLobby.Offer();
    }

    private static void RefreshFromState()
    {
        NReturnToMainMenuButton? button = _button;
        if (!button.IsValid())
        {
            return;
        }

        bool live = DuelReturnToLobby.CanOffer;
        if (!live && button!.IsEnabled)
        {
            button.Disable();
            button.Visible = true;
            button.Modulate = DisabledTint;
            Log.Warn("[SpirePvp] return to lobby: button greyed out — nobody to go back with");
            return;
        }

        if (DuelReturnToLobby.IncomingOfferPending)
        {
            SetLabel(button!, "SPIREPVP_RETURN_LOBBY.offered", "To lobby?");
            return;
        }

        SetLabel(button!,
                 DuelReturnToLobby.OfferPending ? "SPIREPVP_RETURN_LOBBY.waiting"
                                                : "SPIREPVP_RETURN_LOBBY.title",
                 DuelReturnToLobby.OfferPending ? "Waiting…" : "Return to Lobby");
    }

    /// <summary>
    /// Writes the caption, falling back to plain English when the key is missing.
    ///
    /// The key ships in the `.pck` and the code that reads it ships in the DLL, so a build without
    /// a re-export has one and not the other. `LocManager` throws for a key it does not have, and a
    /// throw here would take the whole result screen down over a caption.
    /// </summary>
    private static void SetLabel(NReturnToMainMenuButton button, string key, string fallback)
    {
        LocString loc = new LocString(Table, key);
        string text = loc.Exists() ? loc.GetFormattedText() : fallback;
        button.GetNodeOrNull<MegaLabel>("Label")?.SetTextAutoSize(text);
    }
}
