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

        if (custom.Duplicate() is not NSubmenuButton duel)
        {
            Log.Error("[SpirePvp] duel menu: cloning the Custom button did not produce a button");
            return;
        }

        duel.Name = "DuelButton";
        parent.AddChildSafely(duel);

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
        duel.SetEnabled(true);

        // SetIconAndLocalization does not touch the icon despite its name — without this the
        // clone keeps Custom's texture.
        if (ResourceLoader.Exists(IconPath))
        {
            duel._icon.Texture = PreloadManager.Cache.GetTexture2D(IconPath);
        }
        else
        {
            Log.Warn($"[SpirePvp] duel menu: {IconPath} missing, keeping the cloned icon");
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
