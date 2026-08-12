using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
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
            // Already built — but the clocks may have changed since, by preset or by hand, so
            // the preset row still needs re-syncing. This runs on every ModifiersChanged.
            SyncPresetRow(list);
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

        BuildPresetRow(panel, list);

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

            Heading(panel, locKey);

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

        // Everything not promoted keeps its place in the container, directly below this panel,
        // behind a heading that collapses it.
        //
        // Collapsed by default: the three groups above *are* the match, and the rest is a long
        // list of run modifiers that has nothing to do with configuring a duel. Still reachable
        // rather than removed, because a duel is genuinely a Custom run underneath and every one
        // of those modifiers remains legal — some are interesting in a race.
        BuildCollapsible(panel, "SPIREPVP_LOBBY.advanced", tickboxes.Except(promoted).ToList());

        SyncPresetRow(list);

        Log.Warn($"[SpirePvp] duel lobby: promoted {promoted.Count} duel modifier(s) into " +
                 $"{Groups.Length} groups, {tickboxes.Count - promoted.Count} left below");
    }

    /// <summary>
    /// A row of named time controls that set both clocks at once.
    ///
    /// The three groups below are the truth and stay editable — a preset is a shortcut to a
    /// common pair, not a mode. Picking one ticks the corresponding clocks, and picking a clock
    /// by hand afterwards simply leaves the lobby somewhere between presets, which is fine and
    /// needs no state of its own to represent.
    ///
    /// **Applied by merging rather than replacing.** SetTickedModifiers takes the complete set of
    /// what should be ticked, so handing it two clocks would untick the turn model and any other
    /// custom modifier the host had chosen — a preset would quietly wipe the rest of the lobby.
    /// So the current selection is read back, its clocks dropped, and the preset's added.
    /// </summary>
    private static void BuildPresetRow(Control panel, NCustomRunModifiersList list)
    {
        Heading(panel, "SPIREPVP_LOBBY.preset");

        HBoxContainer row = new HBoxContainer
        {
            Name = "SpirePvpPresetRow",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        panel.AddChildSafely(row);

        _presetChips.Clear();

        List<NRunModifierTickbox> chips = new List<NRunModifierTickbox>();

        foreach ((string locKey, ModifierModel race, ModifierModel duel) in DuelHostFlow.Presets)
        {
            NRunModifierTickbox? chip = MakeChip(row, Loc(locKey));
            if (chip == null)
            {
                continue;
            }

            chips.Add(chip);
            _presetChips.Add((chip, race.GetType(), duel.GetType()));

            chip.Connect(NTickbox.SignalName.Toggled, Callable.From<NTickbox>(box =>
            {
                if (!box.IsTicked)
                {
                    // Unticking the active preset is allowed and means nothing beyond "no preset
                    // is selected" — the clocks it set stay set. There is no such thing as an
                    // un-chosen time control, so there is nothing to undo.
                    return;
                }

                // Presets are alternatives, so exactly one at a time. Vanilla's own exclusivity
                // machinery cannot help here — MutuallyExclusiveModifiers is keyed on modifier
                // types, and these chips deliberately carry no modifier at all — so the row
                // enforces it itself.
                foreach (NRunModifierTickbox other in chips)
                {
                    if (other != box)
                    {
                        other.IsTicked = false;
                    }
                }

                List<ModifierModel> ticked = list.GetModifiersTickedOn()
                    .Where(m => m is not ClockModifierBase)
                    .ToList();

                ticked.Add(race);
                ticked.Add(duel);
                list.SetTickedModifiers(ticked);
            }));
        }
    }

    /// <summary>The preset chips and the clock pair each one stands for.</summary>
    private static readonly List<(NRunModifierTickbox Chip, System.Type Race, System.Type Duel)>
        _presetChips = new();

    /// <summary>
    /// Lights the preset that matches the clocks actually selected, and no other.
    ///
    /// The clocks are the truth and the preset row is a view of them — which keeps the two from
    /// ever disagreeing, and answers the case a preset row otherwise handles badly: setting the
    /// clocks by hand to a pair that happens to *be* Rapid lights Rapid, and setting them to a
    /// pair that is nothing in particular lights nothing. There is no separate "current preset"
    /// to keep in step, because there is no such state.
    ///
    /// Safe to call whenever the modifiers change: NTickbox.IsTicked only swaps the tick images
    /// and does not raise Toggled, so this cannot re-enter the handler that applies a preset.
    /// </summary>
    private static void SyncPresetRow(NCustomRunModifiersList list)
    {
        List<ModifierModel> ticked = list.GetModifiersTickedOn().ToList();
        System.Type? race = ticked.FirstOrDefault(m => m is RaceClockModifier)?.GetType();
        System.Type? duel = ticked.FirstOrDefault(m => m is DuelClockModifier)?.GetType();

        foreach ((NRunModifierTickbox chip, System.Type presetRace, System.Type presetDuel)
                 in _presetChips)
        {
            chip.IsTicked = race == presetRace && duel == presetDuel;
        }
    }

    /// <summary>
    /// A real vanilla tickbox carrying our own text.
    ///
    /// An earlier pass claimed a mod could not build one of these and fell back to labels with
    /// their mouse filter opened up. That was wrong, and the labels looked it — no hover, no
    /// pressed state, no tick, just text that happened to respond to clicks.
    /// `NRunModifierTickbox.Create` is nothing more than instantiating
    /// `modifier_tickbox.tscn` and assigning `Modifier`, and the scene path is a plain resource
    /// any mod can load. **Leaving `Modifier` null is the trick**: `_Ready` guards its label
    /// setup on `Modifier != null`, so the widget builds completely — highlight, tick, hover,
    /// focus — and simply leaves the text to us.
    ///
    /// Text is set *after* the node enters the tree, because `_label` is resolved in `_Ready`
    /// and is null until then.
    /// </summary>
    private static NRunModifierTickbox? MakeChip(Control parent, string text)
    {
        NRunModifierTickbox chip = PreloadManager.Cache
            .GetScene("res://scenes/screens/custom_run/modifier_tickbox.tscn")
            .Instantiate<NRunModifierTickbox>(PackedScene.GenEditState.Disabled);

        chip.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        parent.AddChildSafely(chip);

        if (chip._label == null)
        {
            Log.Warn("[SpirePvp] duel lobby: tickbox scene had no description label");
            return chip;
        }

        chip._label.Text = $"[color=green]{text}[/color]";
        return chip;
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

        // Under a "Race clock" heading, a chip reading "Race Clock: 10 min" says it twice — and
        // the repetition costs the width that made the row cramped. So chips prefer a `.short`
        // label ("10 min") where one exists.
        //
        // The full title is left alone rather than shortened at the source, because it is right
        // everywhere else: in the top bar during a run there is no heading above it, and "10 min"
        // on its own does not say *which* clock. Same modifier, two contexts, two names.
        LocString shortLabel = new LocString("modifiers", modifier.Id.Entry + ".short");
        string title = shortLabel.Exists()
            ? shortLabel.GetFormattedText()
            : modifier.Title.GetFormattedText();

        label.Text = $"[color={colour}]{title}[/color]";
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

    /// <summary>
    /// A heading that shows and hides the modifiers beneath it.
    ///
    /// Built from the heading label rather than a checkbox, because a mod cannot construct
    /// vanilla's tickboxes: `NRunModifierTickbox.Create` demands a `ModifierModel` and `NTickbox`
    /// has no factory at all, so a real toggle widget would mean authoring a scene for one
    /// control. A `MegaLabel` is a `Control`, so giving it `MouseFilterEnum.Stop` and listening
    /// for a click costs nothing and keeps the heading styling already established above.
    ///
    /// The caret carries the state, since a label cannot show a tick.
    /// </summary>
    /// <summary>
    /// Shows and hides the modifiers below it.
    ///
    /// A tickbox rather than a label, which is what it always was semantically — "show me the
    /// rest" is a boolean — and it means the control looks and behaves like every other control
    /// on the screen instead of like underlined text on a web page.
    /// </summary>
    private static void BuildCollapsible(Control panel, string locKey,
                                         List<NRunModifierTickbox> hidden)
    {
        string text = Loc(locKey);
        bool expanded = false;

        // A disclosure caret rather than a tickbox. A tickbox was tried and reads wrong here:
        // it looks like a *choice*, sitting among rows of real choices, when this only reveals
        // things. The caret says "there is more below" and nothing else.
        MegaLabel label = Heading(panel, locKey);
        label.MouseFilter = Control.MouseFilterEnum.Stop;
        label.MouseDefaultCursorShape = Control.CursorShape.PointingHand;

        void Refresh()
        {
            label.Text = (expanded ? "▾  " : "▸  ") + text;
            foreach (NRunModifierTickbox tickbox in hidden)
            {
                tickbox.Visible = expanded;
            }
        }

        // **Hover has to be a font colour, not Modulate.** The first attempt brightened via
        // Modulate above 1.0, which does nothing to text that is already white — which is why
        // the hover was reported as still missing after it was supposedly added.
        label.Connect(Control.SignalName.MouseEntered, Callable.From(() =>
            label.AddThemeColorOverride("font_color", HoverColour)));
        label.Connect(Control.SignalName.MouseExited, Callable.From(() =>
            label.RemoveThemeColorOverride("font_color")));

        label.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(inputEvent =>
        {
            if (inputEvent is InputEventMouseButton { Pressed: true } click
                && click.ButtonIndex == MouseButton.Left)
            {
                expanded = !expanded;
                Refresh();
            }
        }));

        Refresh();
    }

    /// <summary>Vanilla's gold, so a hovered heading matches the rest of the menu.</summary>
    private static readonly Color HoverColour = new Color(1f, 0.82f, 0.4f);

    /// <summary>
    /// Renames the lobby's own title when it is hosting a duel.
    ///
    /// The screen says "Custom Mode" because a duel *is* a Custom run underneath — true, and not
    /// what someone who pressed Duel wants to read. Set on every ModifiersChanged rather than
    /// once, because the submenu stack reuses this screen node: a lobby opened later through the
    /// plain Custom entry would otherwise inherit the previous run's title.
    /// </summary>
    public static void SetTitle(NCustomRunScreen screen, bool isDuel)
    {
        MegaLabel? title = screen.GetNodeOrNull<MegaLabel>("%CustomModeTitle");
        title?.SetTextAutoSize(Loc(isDuel
            ? "SPIREPVP_LOBBY.title"
            : "CUSTOM_RUN_SCREEN.CUSTOM_MODE_TITLE"));
    }

    private static MegaLabel Heading(Control parent, string locKey)
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

        // **A MegaLabel with no theme font override throws on every layout pass.** Vanilla says
        // so outright — "has no theme font override. Please set one to avoid a Godot engine bug"
        // — and a freshly constructed one has none, so each heading logged an exception every
        // time it measured itself. Harmless in effect and deafening in the log: they were the
        // first several hundred errors of the playtest, which is a real cost when the log is the
        // primary diagnostic tool for this project.
        //
        // Borrowed from the tickboxes we are sitting among rather than loaded separately, so the
        // headings inherit whatever the screen is themed with instead of pinning a font of their
        // own.
        // Added before the font is resolved, because an override can only be copied from the
        // theme once the node is in the tree to inherit one.
        parent.AddChildSafely(label);

        Font? font = label.GetThemeFont("font");
        if (font != null)
        {
            label.AddThemeFontOverride("font", font);
        }

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
