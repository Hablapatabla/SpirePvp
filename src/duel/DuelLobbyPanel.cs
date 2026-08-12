using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.addons.mega_text;
using SpirePvp.Modifiers;

namespace SpirePvp.Duel;

/// <summary>
/// Turns the Custom lobby's flat modifier list into a duel-first one: the three choices a match
/// is actually made of, promoted to the top under headings, and everything else demoted below
/// them.
///
/// **This re-dresses vanilla's screen rather than replacing it, and that is the whole design.**
/// `NCustomRunScreen` is not just a modifier list — it owns character select, the seed field, the
/// ascension panel, the remote player container, the ready/confirm flow, and it implements
/// `IStartRunLobbyListener`'s nine lifecycle callbacks (players connecting and changing,
/// ascension, seed, modifiers, disconnects, `BeginRun`). A bespoke duel screen would mean
/// reimplementing all of that on both host and client, and getting it wrong looks like the
/// failure modes this project already knows too well: a lobby that hangs, or two clients
/// disagreeing. The visuals were never the expensive part.
///
/// **The tickboxes are moved, not recreated.** `UntickMutuallyExclusiveModifiersForTickbox` and
/// `GetModifiersTickedOn` both iterate `_modifierTickboxes` — the *list* — rather than the
/// container's children, so reparenting a tickbox into another layout leaves exclusivity, the
/// `ModifiersChanged` signal and the ticked-on query all working untouched. They are also
/// genuinely vanilla widgets, so the result looks native for free and there is no second
/// implementation of "a modifier you can tick" to keep in step.
///
/// The panel is built *inside* the existing scroll content rather than floating over the screen,
/// which inherits the scrolling, sizing and theming that container already has.
/// </summary>
public static class DuelLobbyPanel
{
    private const string PanelName = "SpirePvpDuelPanel";

    /// <summary>The three decisions a match is made of, in the order they are made.</summary>
    private static readonly (string LocKey, System.Type Group)[] Groups =
    {
        ("SPIREPVP_LOBBY.turnModel", typeof(DuelModifierBase)),
        ("SPIREPVP_LOBBY.raceClock", typeof(RaceClockModifier)),
        ("SPIREPVP_LOBBY.duelClock", typeof(DuelClockModifier))
    };

    /// <summary>
    /// Rebuilds the panel for <paramref name="screen"/>. Safe to call repeatedly — the lobby
    /// refreshes on every modifier change, and on the client that is how the panel first appears.
    /// </summary>
    public static void Apply(NCustomRunScreen screen)
    {
        NCustomRunModifiersList? list = screen._modifiersList;
        Control? container = list?._container;
        if (list == null || container == null)
        {
            return;
        }

        if (container.GetNodeOrNull(PanelName) != null)
        {
            return;
        }

        List<NRunModifierTickbox> tickboxes = list._modifierTickboxes;
        if (tickboxes.Count == 0)
        {
            return;
        }

        // Fill the container's width and never exceed it. A child's minimum width propagates
        // outward through containers, so a wide heading would widen this panel, which widens the
        // tickboxes inside it, which pushes them past the scroll mask and clips their
        // descriptions on the right — the panel dragging vanilla's own widgets out of the visible
        // area with it. Headings wrap for the same reason.
        VBoxContainer panel = new VBoxContainer
        {
            Name = PanelName,
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            ClipContents = true
        };
        container.AddChildSafely(panel);
        container.MoveChildSafely(panel, 0);

        List<NRunModifierTickbox> promoted = new List<NRunModifierTickbox>();

        foreach ((string locKey, System.Type group) in Groups)
        {
            List<NRunModifierTickbox> members = tickboxes
                .Where(t => t.Modifier != null && group.IsInstanceOfType(t.Modifier))
                // Nested groups: every clock is also a DuelModifierBase, so the turn-model group
                // has to exclude them or it would swallow all three rows into the first.
                .Where(t => group != typeof(DuelModifierBase)
                            || t.Modifier is not ClockModifierBase)
                .ToList();

            if (members.Count == 0)
            {
                continue;
            }

            panel.AddChildSafely(Heading(locKey));

            // One row per group, so a decision reads as a row of alternatives rather than as
            // four more entries in a vertical list. This is what makes the three groups look
            // like three choices.
            HBoxContainer row = new HBoxContainer
            {
                Name = $"SpirePvpRow_{group.Name}",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            panel.AddChildSafely(row);

            foreach (NRunModifierTickbox tickbox in members)
            {
                tickbox.GetParent()?.RemoveChild(tickbox);
                CompactForRow(tickbox);
                row.AddChildSafely(tickbox);
                promoted.Add(tickbox);
            }
        }

        if (promoted.Count == 0)
        {
            panel.QueueFreeSafely();
            return;
        }

        // Everything not promoted keeps its place in the container, which is directly below this
        // panel — so a heading is all that is needed to separate them. No collapsing widget: the
        // vanilla tickbox has no constructor a mod can call (NRunModifierTickbox.Create needs a
        // ModifierModel and NTickbox has no factory at all), so a toggle would mean authoring a
        // scene for one checkbox. The ordering is what made the real choices hard to find; the
        // list being long underneath is a much smaller problem.
        //
        // Left visible rather than hidden, deliberately: a duel is still a Custom run, so every
        // one of those modifiers remains legal and some are interesting in a race. This stops
        // them being the *first* thing the screen says.
        panel.AddChildSafely(Heading("SPIREPVP_LOBBY.advanced"));

        Log.Warn($"[SpirePvp] duel lobby: promoted {promoted.Count} duel modifier(s) into " +
                 $"{Groups.Length} groups, {tickboxes.Count - promoted.Count} left below");
    }

    /// <summary>
    /// Shrinks a tickbox to a chip so several fit on one row.
    ///
    /// The widget is list-shaped by construction: `NRunModifierTickbox._Ready` builds one
    /// `MegaRichTextLabel` holding the modifier's title *and* its full description in a single
    /// BBCode string. Five of those side by side is a wall of text, which is why the first pass
    /// left them stacked.
    ///
    /// So the label is cut back to the coloured title and the description moves to the hover
    /// tooltip. **Nothing is lost** — the descriptions are the only place the options explain
    /// themselves ("A fresh 2 minute bank each when the duel begins"), and they matter most to
    /// someone meeting the mode for the first time, so they have to remain reachable rather than
    /// be deleted for tidiness.
    ///
    /// Green because every duel modifier registers into `GoodModifiers`, which is the same test
    /// vanilla applies when it colours these; asked the same way rather than hardcoded, so a
    /// modifier moved to another list keeps matching its neighbours.
    /// </summary>
    private static void CompactForRow(NRunModifierTickbox tickbox)
    {
        ModifierModel? modifier = tickbox.Modifier;
        MegaRichTextLabel? label = tickbox._label;
        if (modifier == null || label == null)
        {
            return;
        }

        string colour = ModelDb.GoodModifiers.Any(m => m.GetType() == modifier.GetType())
            ? "green"
            : ModelDb.BadModifiers.Any(m => m.GetType() == modifier.GetType())
                ? "red"
                : "blue";

        label.Text = $"[color={colour}]{modifier.Title.GetFormattedText()}[/color]";
        tickbox.TooltipText = modifier.Description.GetFormattedText();

        // Share the row evenly, so the options line up as a segmented control instead of each
        // one taking the width of its own label.
        tickbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
    }

    /// <summary>
    /// How large a section heading is drawn.
    ///
    /// A heading that does not obviously outrank the tickboxes under it is not a heading — it
    /// reads as another list entry, which is the problem this panel exists to fix.
    /// </summary>
    private const int HeadingFontSize = 44;

    /// <summary>Space above each heading, so the groups read as blocks rather than a run-on list.</summary>
    private const int HeadingTopMargin = 28;

    private static Control Heading(string locKey)
    {
        // **Do not use SetTextAutoSize here.** It shrinks the text to fit the control's rect,
        // and a fresh label in a VBoxContainer has no rect worth speaking of — which is exactly
        // how these came out microscopic. Autosize is for fitting a known box; this wants a
        // fixed size and a box that grows to hold it.
        MegaLabel label = new MegaLabel
        {
            Name = "SpirePvpHeading",
            AutoSizeEnabled = false,
            Text = Loc(locKey),

            // Left-aligned and wrapping, because the alternative is what shipped first: a
            // centred, non-wrapping label wider than the column, clipped at *both* ends so the
            // heading read as "…odel". Headings are short now — the tickboxes beneath each one
            // already explain the options, so the heading does not need to — but wrapping means
            // a longer translation degrades into two lines instead of into nonsense.
            HorizontalAlignment = HorizontalAlignment.Left,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };

        label.SetFontSize(HeadingFontSize);

        // VBoxContainer spaces every child equally, so the gap that separates one group from the
        // next has to come from the heading itself.
        label.AddThemeConstantOverride("margin_top", HeadingTopMargin);
        label.CustomMinimumSize = new Vector2(0, HeadingFontSize + HeadingTopMargin);

        return label;
    }

    private static string Loc(string key)
    {
        return new LocString("main_menu_ui", key).GetFormattedText();
    }
}
