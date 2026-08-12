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
    private const string RematchButtonName = "SpirePvpRematchButton";

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
            NReturnToMainMenuButton? menuButton = __instance._mainMenuButton;
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
            NReturnToMainMenuButton rematch = (NReturnToMainMenuButton)menuButton.Duplicate(
                (int)(Node.DuplicateFlags.Scripts | Node.DuplicateFlags.Groups));
            rematch.Name = RematchButtonName;

            // **The hover glow lives in a ShaderMaterial, and a duplicate shares it by reference.**
            // `NReturnToMainMenuButton._Ready` caches `GetNode<TextureRect>("Image").Material` as
            // `_hsv`, and every hover tween writes that material's `s` and `v` parameters directly
            // — so two buttons holding one material are one button as far as illumination goes.
            // Reported 2026-08-12: hovering Rematch lit Main Menu and back, with the glow going
            // janky moving between them, which is two tweens driving one parameter against each
            // other.
            //
            // **This project has met this exact bug before**, on M7's Duel menu entry
            // (`DuelHostMenuPatch`, `bgMaterialWasShared=True`), and the fix carries the same
            // ordering constraint: it must happen **before** the clone enters the tree, because
            // `_hsv` is resolved during `_Ready`. Replacing the material afterwards leaves the
            // cached reference pointing at the shared one and changes nothing visible.
            TextureRect? menuImage = menuButton.GetNodeOrNull<TextureRect>("Image");
            TextureRect? rematchImage = rematch.GetNodeOrNull<TextureRect>("Image");
            bool sharedMaterial = menuImage?.Material != null
                                  && ReferenceEquals(menuImage.Material, rematchImage?.Material);
            if (sharedMaterial && rematchImage != null)
            {
                rematchImage.Material = (Material)rematchImage.Material.Duplicate();
            }

            parent.AddChild(rematch);
            parent.MoveChild(rematch, menuButton.GetIndex());

            // **After AddChild, because `_Ready` overwrites it.** `NReturnToMainMenuButton._Ready`
            // sets its own label from `_mainMenuLoc`, so the label has to be written once the node
            // has entered the tree and that has already run.
            MegaLabel? label = rematch.GetNodeOrNull<MegaLabel>("Label");
            label?.SetTextAutoSize(
                new LocString("game_over_screen", "SPIREPVP_REMATCH.title").GetFormattedText());

            // **Where the button comes to rest, rather than where it sits now.** This class hides
            // itself in `_Ready` — transparent, shifted 140px left — and `OnEnable` tweens it back
            // to `_showPosition`, which it captured from its own pre-shift position. Our duplicate
            // captured *the source's already-shifted* position, so left alone it would animate to
            // 140px left of the menu button and onto the same row. Setting the rest position is
            // what makes the shared animation land it in the right place.
            float step = menuButton.Size.X > 1f ? menuButton.Size.X + 40f : RematchButtonGap;
            rematch._showPosition = menuButton._showPosition + new Vector2(-step, 0f);

            // **Start it disabled, or the enable that reveals it does nothing.**
            // `NClickableControl._isEnabled` is declared `= true`, and `Enable()` is guarded by
            // `if (!_isEnabled)` — so a fresh duplicate believes it is already enabled, the guard
            // swallows the call, `OnEnable` never runs, and the button keeps the transparent,
            // pre-tween state `NReturnToMainMenuButton._Ready` left it in. That is precisely what
            // the placement dump caught: ours `mod=(0, 0, 0, 0)` at the unshifted `(678, 909)`
            // against the menu button's `mod=(1, 1, 1, 1)` at `(818, 909)` — one property apart,
            // and invisible for it.
            //
            // `Disable()` puts it in the state vanilla's button is genuinely in at this point
            // (`_Ready` ends with `_mainMenuButton.Disable()`), so the mirrored enable later is a
            // real transition and plays the real animation.
            rematch.Disable();

            rematch.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(OnRematchPressed));

            // Not shown here, deliberately — see MirrorMenuButtonEnable. `_Ready` ends with
            // `_mainMenuButton.Disable()`, so the button we copied was already hidden and the copy
            // inherits that. Forcing it visible now would put Rematch on screen during the death
            // intermission, before vanilla offers any way off the screen at all.
            Log.Warn($"[SpirePvp] rematch: button added beside {menuButton.Name}, resting at "
                     + $"{rematch._showPosition} (menu button rests at {menuButton._showPosition}, "
                     + $"width {menuButton.Size.X}, step {step}, "
                     + $"hoverMaterialWasShared={sharedMaterial})");
        }
        catch (Exception e)
        {
            // The result screen matters more than the button on it.
            Log.Error($"[SpirePvp] rematch: could not add the button: {e}");
        }
    }

    /// <summary>
    /// How far to the *left* of the Main Menu button the Rematch button sits.
    ///
    /// Horizontal rather than stacked, at Lucas's request 2026-08-12, and it also leaves the row
    /// open for a third button without pushing anything off the bottom of the screen.
    ///
    /// Used only when the menu button reports no width of its own — at `_Ready` a control that has
    /// not been through a layout pass can still be `Size = (0, 0)`, and a gap measured from zero
    /// would stack the two buttons on the same pixel, which reads exactly like the button being
    /// missing.
    /// </summary>
    private const float RematchButtonGap = 260f;

    /// <summary>
    /// Shows and hides the Rematch button exactly when vanilla shows and hides Main Menu.
    ///
    /// **Reported 2026-08-12: only the main menu button appeared.** The button was built and
    /// placed — the log said so — but invisible, because `NGameOverScreen._Ready` ends with
    /// `_mainMenuButton.Disable()`, and the duplicate was taken *after* that and inherited
    /// `Visible = false`. Vanilla then re-enables its own button later, from two different places
    /// (the summary transition and the leaderboard path), and neither knows ours exists.
    ///
    /// Rather than pick one of those methods to patch — and be wrong on whichever path the run
    /// happens to take — this rides the *button's own* enable. Our copy is the same class, so the
    /// call that reveals Main Menu reveals Rematch beside it, with the same tween, on every route.
    /// The name check is what stops it recursing on our own button.
    ///
    /// **`Enable`/`Disable` rather than the `OnEnable`/`OnDisable` they call**, because those two
    /// are `protected override` and the publicizer is configured with
    /// `IncludeVirtualMembers="false"` — so they cannot be named with `nameof`, and a string target
    /// would give up the property that makes a moved method a build error instead of a silent
    /// `PATCH FAILED`. The public pair is what the screen actually calls anyway.
    /// </summary>
    [HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl.Enable))]
    [HarmonyPostfix]
    public static void MirrorMenuButtonEnable(NClickableControl __instance) =>
        MirrorTo(__instance, enable: true);

    [HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl.Disable))]
    [HarmonyPostfix]
    public static void MirrorMenuButtonDisable(NClickableControl __instance) =>
        MirrorTo(__instance, enable: false);

    private static void MirrorTo(NClickableControl source, bool enable)
    {
        // Enable() is called on every clickable control in the game, so this narrows to the one
        // button we sit beside before touching anything.
        if (source is not NReturnToMainMenuButton || source.Name == RematchButtonName
            || !source.IsValid())
        {
            return;
        }

        if (source.GetParent()?.GetNodeOrNull<NReturnToMainMenuButton>(RematchButtonName)
            is not NReturnToMainMenuButton rematch)
        {
            return;
        }

        if (enable)
        {
            // Belt and braces against the guard above: if anything has left it believing it is
            // already enabled, the call would be swallowed and the button would stay invisible
            // with nothing in the log to say why.
            if (rematch.IsEnabled)
            {
                rematch.Disable();
            }

            rematch.Visible = true;
            rematch.Enable();
        }
        else
        {
            rematch.Disable();
        }

        Log.Warn($"[SpirePvp] rematch: button {(enable ? "shown" : "hidden")} with the menu button");

        if (enable)
        {
            // **Both sides, dumped and diffed.** The button reports itself added, placed and
            // shown, and is not on screen — so the next question is which property differs from
            // the one beside it, and that is not answerable from a screenshot. Two placement bugs
            // in this project were "corrected" from screenshots and one of those corrections was
            // wrong; what settled them was logging both and diffing. Deferred a frame because
            // `Enable` starts a half-second tween, and the interesting values are the ones it
            // lands on.
            SceneTreeTimer? settled = source.GetTree()?.CreateTimer(0.75);
            if (settled != null)
            {
                settled.Timeout += () => DumpPlacement(source, rematch);
            }
        }
    }

    /// <summary>Every property that could make a placed, enabled control invisible.</summary>
    private static void DumpPlacement(NClickableControl menu, NClickableControl rematch)
    {
        if (!menu.IsValid() || !rematch.IsValid())
        {
            return;
        }

        Node? parent = menu.GetParent();
        Log.Warn($"[SpirePvp] rematch placement — parent={parent?.Name} ({parent?.GetType().Name})"
                 + (parent is Control pc
                        ? $" size={pc.Size} clip={pc.ClipContents} global={pc.GlobalPosition}"
                        : ""));
        Log.Warn($"[SpirePvp] rematch placement — MENU  pos={menu.Position} global={menu.GlobalPosition} "
                 + $"size={menu.Size} scale={menu.Scale} vis={menu.Visible} mod={menu.Modulate} "
                 + $"z={menu.ZIndex} idx={menu.GetIndex()}");
        Log.Warn($"[SpirePvp] rematch placement — OURS  pos={rematch.Position} global={rematch.GlobalPosition} "
                 + $"size={rematch.Size} scale={rematch.Scale} vis={rematch.Visible} mod={rematch.Modulate} "
                 + $"z={rematch.ZIndex} idx={rematch.GetIndex()}");
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
