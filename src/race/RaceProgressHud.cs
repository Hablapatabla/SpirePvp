using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.addons.mega_text;
using SpirePvp.Duel;

namespace SpirePvp.Race;

/// <summary>
/// Shows the opponent's HP and deck size on your map during the race (DESIGN §6).
///
/// The map already shows *where* they are — `RaceProgress` moves their portrait onto the node
/// they reached, reusing co-op's vote markers. What it could not show is how they are doing,
/// and that is the half that matters while you are standing at the arena deciding whether to
/// have gone for the elite. Both numbers have been on the wire since M5 and were being logged
/// and thrown away, so this is display only: no new message, no change to a positional wire
/// format, nothing to keep in sync between clients.
///
/// **The label is cloned, not constructed.** `MapLegend/Header` is the map screen's one
/// `MegaLabel`, so duplicating it inherits the font, size, outline and theme that a
/// hand-rolled `Label` would have to reproduce and would get subtly wrong. This mod ships no UI
/// assets and has never built a Godot node from scratch; the same instinct put the clocks in
/// `NRunTimer`'s label and the arena's waiting marker in the map's vote portraits, and it is
/// what keeps the `.pck` down to a node texture and two loc tables.
///
/// It deliberately reports nothing before the opponent's first move. An HP readout of `0/0`
/// during the opening seconds would look like a dead opponent rather than an absent report.
/// </summary>
public static class RaceProgressHud
{
    private const string NodeName = "SpirePvpRaceProgress";

    /// <summary>Below the legend header, in the legend's own column on the right of the map.</summary>
    private static readonly Vector2 OffsetFromHeader = new Vector2(0f, 34f);

    /// <summary>
    /// Rebuild the readout from whatever `RaceProgress` last heard.
    ///
    /// Safe to call at any time and from anywhere: it no-ops when there is no map screen, no
    /// PvP run, or nothing yet to report, and it creates the label on first need rather than
    /// depending on having been initialised at some particular moment. That matters because the
    /// map screen is created and destroyed across a run while this class is static — the
    /// mismatch between mod lifetime and node lifetime being the failure this codebase keeps
    /// paying for.
    /// </summary>
    public static void Refresh()
    {
        NMapScreen? screen = NMapScreen.Instance;
        if (screen == null)
        {
            return;
        }

        if (!DuelMatch.IsPvpRun(MegaCrit.Sts2.Core.Runs.RunManager.Instance?.State) ||
            !RaceProgress.HasOpponentReport)
        {
            Hide(screen);
            return;
        }

        try
        {
            MegaLabel? label = EnsureLabel(screen);
            label?.SetTextAutoSize(BuildText());
        }
        catch (Exception e)
        {
            // A HUD is never worth taking the map screen down for.
            Log.Error($"[SpirePvp] race HUD: {e.Message}");
        }
    }

    /// <summary>Drop the node so the next run's map screen builds a fresh one.</summary>
    public static void Clear()
    {
        NMapScreen? screen = NMapScreen.Instance;
        if (screen != null)
        {
            Hide(screen);
        }
    }

    private static string BuildText()
    {
        string position = RaceProgress.OpponentCoord is MapCoord coord
            ? $"Floor {coord.row + 1}"
            : "Not moved";

        return $"OPPONENT\n{position}\n{RaceProgress.OpponentCurrentHp}/{RaceProgress.OpponentMaxHp} HP\n" +
               $"{RaceProgress.OpponentDeckSize} cards";
    }

    private static MegaLabel? EnsureLabel(NMapScreen screen)
    {
        MegaLabel? existing = screen.GetNodeOrNull<MegaLabel>(NodeName);
        if (existing != null)
        {
            return existing;
        }

        // The legend header is the only MegaLabel on this screen, and it is the one whose
        // styling we want: same column, same treatment, one heading below.
        MegaLabel? header = screen.GetNodeOrNull<MegaLabel>("MapLegend/Header");
        if (header == null)
        {
            return null;
        }

        if (header.Duplicate() is not MegaLabel clone)
        {
            return null;
        }

        clone.Name = NodeName;
        header.GetParent()?.AddChild(clone);
        clone.Position = header.Position + OffsetFromHeader;
        clone.Visible = true;
        return clone;
    }

    private static void Hide(NMapScreen screen) =>
        screen.GetNodeOrNull<Node>(NodeName)?.QueueFree();
}
