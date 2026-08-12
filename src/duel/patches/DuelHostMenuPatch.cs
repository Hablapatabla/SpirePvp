using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// M7: a **Duel** entry beside Standard, Daily and Custom on the multiplayer host menu.
///
/// `NMultiplayerHostSubmenu` fetches its three buttons from its own scene by node name —
/// `StandardButton`, `DailyButton`, `CustomRunButton` — and a mod cannot edit that `.tscn`. It
/// does not need to: duplicating the Custom button gives us an `NSubmenuButton` that already has
/// the right art, sizing, focus behaviour and lock overlay, which is most of what would
/// otherwise make a bolted-on entry read as bolted on. Retitle it, repoint its icon, and connect
/// it somewhere new.
///
/// **The label rides in a vanilla table**, as every string in this mod must:
/// `NSubmenuButton.RefreshLabels` resolves `new LocString("main_menu_ui", prefix + ".title")`, so
/// `DUEL_MP.title` ships in `main_menu_ui.json`. A table of our own would never be read at all
/// (`LocManager` merges only into tables vanilla already has, by filename).
///
/// **The icon is the arena's own map node.** Reusing it means the same art marks the mode in the
/// menu and the destination on the map, which is a better result than a bespoke icon would have
/// been — and it costs nothing, since it already ships in the `.pck`. Note `SetIconAndLocalization`
/// is misleadingly named: it sets only the loc prefix, and the icon comes from the scene, so the
/// texture has to be assigned separately or we would silently inherit Custom's.
///
/// **Not gated behind `CustomAndSeedsEpoch`**, unlike the Custom button it is cloned from. That
/// epoch is checked in exactly two places, both of them buttons, and nothing on the run-creation
/// path consults it — so gating is presentational and ours to decide. Hiding a PvP mod's only
/// entry point behind an unrelated vanilla unlock would mean a fresh install cannot play the
/// thing it installed. The one consequence accepted knowingly: hosting a duel opens a Custom
/// lobby, which exposes the seed field that epoch protects. Same-seed rematches are a stated
/// design goal (DESIGN §5b), so that is part of the mode rather than a leak.
/// </summary>
[HarmonyPatch(typeof(NMultiplayerHostSubmenu), nameof(NMultiplayerHostSubmenu._Ready))]
public static class DuelHostMenuPatch
{
    private const string IconPath = "res://SpirePvp/map/duel_node.png";

    public static void Postfix(NMultiplayerHostSubmenu __instance)
    {
        NSubmenuButton? custom = __instance._customButton;
        Node? parent = custom?.GetParent();
        if (custom == null || parent == null)
        {
            Log.Error("[SpirePvp] duel menu: no Custom button to clone; the Duel entry is missing");
            return;
        }

        // **Signals are deliberately not duplicated.** Every connection an `NClickableControl`
        // relies on is made in `ConnectSignals` as `Callable.From(<instance method>)` — a managed
        // delegate bound to *that* button. Godot cannot remap a callable it cannot introspect, so
        // any copied connection would drive the original Custom button from the clone's events.
        // `UseInstantiation` is kept: it is what makes the clone come from the button's own scene
        // rather than a bare property copy, which is what keeps the scene-unique names (`%Title`,
        // `%Description`) resolving against an owner inside the clone.
        Node.DuplicateFlags flags = Node.DuplicateFlags.Groups
                                    | Node.DuplicateFlags.Scripts
                                    | Node.DuplicateFlags.UseInstantiation;

        if (custom.Duplicate((int)flags) is not NSubmenuButton duel)
        {
            Log.Error("[SpirePvp] duel menu: cloning the Custom button did not produce a button");
            return;
        }

        duel.Name = "DuelButton";

        // **The hover glow lives in a ShaderMaterial, and a duplicate can share it by reference.**
        // `NSubmenuButton.ConnectSignals` caches `BgPanel.Material` as `_hsv` and every hover
        // tween writes its `v` parameter directly, so two buttons holding one material are one
        // button as far as illumination is concerned: hovering Duel lights Custom, and moving
        // between them leaves two instances tweening the same parameter against each other, which
        // is what "the hover is unresponsive" looks like from the outside.
        //
        // Done **before** the clone enters the tree, because `_hsv` is resolved in `ConnectSignals`
        // during `_Ready` — replacing the material afterwards would leave the cached reference
        // pointing at the shared one and change nothing visible.
        Control? customBg = custom.GetNodeOrNull<Control>("BgPanel");
        Control? duelBg = duel.GetNodeOrNull<Control>("BgPanel");
        bool sharedMaterial = customBg?.Material != null
                              && ReferenceEquals(customBg.Material, duelBg?.Material);
        if (sharedMaterial && duelBg != null)
        {
            duelBg.Material = (Material)duelBg.Material.Duplicate();
        }

        parent.AddChildSafely(duel);

        // The clone is the one piece of this mod built by copying a vanilla widget rather than
        // constructing one, so the things that copying can quietly get wrong are worth stating
        // outright rather than inferring from how the button behaves. All four have a
        // presentation-only failure mode, which is exactly the kind this project has learned not
        // to diagnose from screenshots.
        Log.Warn($"[SpirePvp] duel menu: clone diagnostics — customScene='{custom.SceneFilePath}', "
                 + $"bgMaterialWasShared={sharedMaterial}, "
                 + $"titleResolved={duel._title != null}, iconResolved={duel._icon != null}, "
                 + $"hoverConnections={duel.GetSignalConnectionList(Control.SignalName.MouseEntered).Count}, "
                 + $"releaseConnections={duel.GetSignalConnectionList(NClickableControl.SignalName.Released).Count}");

        // Directly after Custom, so the four read as one list rather than an appendix.
        parent.MoveChildSafely(duel, custom.GetIndex() + 1);

        // Child order is enough only if a layout container is placing these. If the buttons are
        // positioned by hand — which the scene is free to do, and which we cannot read from here
        // — then Duplicate() copied Custom's Position too and the clone would sit exactly on top
        // of it, reading as though Duel had *replaced* Custom rather than joined it.
        //
        // Vanilla's own spacing is the only sensible source for the step, so take it from the
        // gap between the two buttons above and continue the run. Guarded on the parent not
        // being a Container, because inside one this would be overwritten anyway and setting it
        // would just be noise.
        if (parent is not Container && __instance._dailyButton != null)
        {
            Vector2 step = custom.Position - __instance._dailyButton.Position;
            if (step != Vector2.Zero)
            {
                duel.Position = custom.Position + step;

                // A hand-positioned row was centred for three buttons, so a fourth extends it
                // off-centre in whichever direction the row runs. Shift the whole row back by
                // half a step to re-centre it around the same midpoint.
                //
                // Derived from vanilla's own spacing rather than a constant, so it stays correct
                // if the menu is ever re-laid out — and applied to all four, because moving only
                // the new one would just relocate the asymmetry.
                Vector2 recentre = step * -0.5f;
                foreach (NSubmenuButton button in new[]
                         { __instance._standardButton, __instance._dailyButton, custom, duel })
                {
                    if (button != null)
                    {
                        button.Position += recentre;
                    }
                }
            }
        }

        // The submenu owns StartHost and has no singleton, so the handler closes over the
        // instance. Safe: the button is a child of that submenu, so it cannot outlive it.
        duel.Connect(NClickableControl.SignalName.Released,
                     Callable.From<NButton>(_ => OnDuelPressed(__instance)));
        duel.SetIconAndLocalization("DUEL_MP");

        // Locked when the mod did not fully apply, rather than offering a match it cannot
        // arbitrate. NSubmenuButton already draws a lock and greys the icon when disabled, and
        // RefreshLabels swaps to `.LOCKED.description` — so the reason is on screen, in vanilla's
        // own presentation, before anyone commits to a lobby.
        duel.SetEnabled(SpirePvpInit.PatchesHealthy);

        // SetIconAndLocalization does not touch the icon despite its name — without this the
        // clone keeps Custom's texture.
        //
        // `_icon` is resolved in `ConnectSignals` during `_Ready`, so it is only populated because
        // the clone is already in the tree by this point. Guarded rather than assumed: the whole
        // reason the diagnostics above exist is that a clone can arrive with its scene-relative
        // lookups unresolved, and an NRE thrown out of a menu patch is a worse way to find out.
        if (!ResourceLoader.Exists(IconPath))
        {
            Log.Warn($"[SpirePvp] duel menu: {IconPath} missing, keeping the cloned icon");
        }
        else if (duel._icon == null)
        {
            Log.Error("[SpirePvp] duel menu: the clone has no Icon node; it keeps Custom's art");
        }
        else
        {
            duel._icon.Texture = PreloadManager.Cache.GetTexture2D(IconPath);
        }

        Log.Warn("[SpirePvp] duel menu: Duel entry added to the multiplayer host menu");
    }

    private static void OnDuelPressed(NMultiplayerHostSubmenu submenu)
    {
        // Consumed by DuelHostLobbyPatch on the Custom lobby that this opens.
        DuelHostFlow.Requested = true;
        submenu.StartHost(GameMode.Custom);
    }
}
