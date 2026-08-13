using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace SpirePvp.Duel;

/// <summary>
/// The opponent's relics, drawn on the duel entry screen beside their deck.
///
/// Wanted after the 2026-08-12 session — the review revealed their cards and said nothing about
/// their relics, which decide as much of a duel as the deck does. It is the same information rule
/// the decklist serves (DESIGN §1), and it falls under the same trap: **the race decouples the two
/// runs, so your copy of their relics is stale.** They travel on `DuelArrivedMessage` for exactly
/// the reason the deck does, and the ordering is free because the review opens once both arrivals
/// are in hand.
///
/// # Everything here is vanilla's, including the pattern
///
/// `NRelicHistory.LoadRelics` is the worked example and this mirrors it step for step:
/// `RelicModel.FromSerializable`, fall back to `DeprecatedRelic` when the id is unknown, **assign
/// `Owner`**, then `NRelicBasicHolder.Create`. The owner assignment is not decoration — the holder's
/// hover tip reads through the model, and vanilla sets one before building a holder in the one
/// place it displays relics that are not attached to a live player. Nothing is added to the
/// opponent's actual relic list; these models exist to be drawn, exactly like the rebuilt cards
/// next to them.
///
/// `NRelicBasicHolder` rather than a bare `NRelic` because it brings the hover tip and the hover
/// scale with it. A row of nameless icons would technically satisfy "show their relics" and would
/// not answer the question a player is actually asking.
///
/// # Placement is derived, not guessed
///
/// The row is parented to the screen root and positioned from `_infoLabel`'s own global rect at the
/// moment the overlay is shown, rather than from constants. The screen root is a plain `Control`
/// (it resolves its children by unique name), so adding a child cannot disturb the grid's layout —
/// which parenting into a container alongside the label could.
///
/// **This is unplayed and nobody has seen where it lands.** The resolved rect is logged for that
/// reason: this project has already spent a session "correcting" two placements from screenshots
/// and reverting one of them, and what settled it was logging the positions and diffing. If the row
/// sits wrong, the log line says exactly what it was told to sit above.
/// </summary>
public static class DuelEntryRelics
{
    /// <summary>Height reserved above the caption for the row, in the screen's own units.</summary>
    private const float RowHeightAboveCaption = 96f;

    private static HFlowContainer? _row;

    /// <summary>
    /// Builds the row onto an open entry screen. Safe to call twice — the second call rebuilds,
    /// which is what a reopened screen needs and what a stale row would otherwise survive.
    /// </summary>
    public static void Show(NDeckCardSelectScreen screen, Player? opponent)
    {
        Clear();

        IReadOnlyList<SerializableRelic>? relics = DuelRendezvous.OpponentRelics;
        if (relics == null || relics.Count == 0 || opponent == null)
        {
            // No arrival message means the legacy `duel start` path, which enters from a live
            // combat where the local copy is already correct — but it is also the path with no
            // rendezvous, so there is nothing to draw from and nothing worth inventing.
            Log.Info("[SpirePvp] duel entry — no opponent relics to show "
                     + $"(relics={relics?.Count ?? -1}, opponent={(opponent == null ? "null" : "known")})");
            return;
        }

        Control? label = screen._infoLabel;
        if (label == null)
        {
            return;
        }

        HFlowContainer row = new HFlowContainer
        {
            Name = "SpirePvpOpponentRelics",
            // Mouse events must reach the holders; the container itself must not eat them.
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        int drawn = 0;
        foreach (SerializableRelic saved in relics)
        {
            RelicModel model;
            try
            {
                model = RelicModel.FromSerializable(saved);
            }
            catch (Exception e)
            {
                // Vanilla's own fallback in this exact situation: an id this build does not know
                // still occupies a slot, so the count stays honest.
                Log.Warn($"[SpirePvp] duel entry — unknown opponent relic ({e.Message})");
                model = (RelicModel)ModelDb.Relic<DeprecatedRelic>().ToMutable();
            }

            try
            {
                model.Owner = opponent;
            }
            catch (Exception e)
            {
                // A relic that refuses an owner can still be drawn; only its hover tip suffers.
                Log.Warn($"[SpirePvp] duel entry — could not own a display relic ({e.Message})");
            }

            NRelicBasicHolder? holder = NRelicBasicHolder.Create(model);
            if (holder == null)
            {
                continue;
            }

            holder.MouseDefaultCursorShape = Control.CursorShape.Help;
            row.AddChildSafely(holder);
            drawn++;
        }

        if (drawn == 0)
        {
            row.QueueFreeSafely();
            return;
        }

        screen.AddChildSafely(row);
        _row = row;

        // Position from the caption rather than from constants — see the note above. Done after
        // the row is in the tree so its own size is real.
        Vector2 captionTopLeft = label.GlobalPosition;
        row.Size = new Vector2(label.Size.X, RowHeightAboveCaption);
        row.GlobalPosition = new Vector2(captionTopLeft.X, captionTopLeft.Y - RowHeightAboveCaption);

        Log.Warn($"[SpirePvp] duel entry — {drawn} opponent relic(s) drawn at {row.GlobalPosition}, "
                 + $"above the caption at {captionTopLeft} (size {label.Size})");
    }

    /// <summary>
    /// Takes the row down. Called wherever the entry screen goes away, because the row is our node
    /// on vanilla's screen: the overlay stack frees the screen, and a child we never let go of is a
    /// node held past its parent — the shape behind the `NCard` double-frees already on the books.
    /// </summary>
    public static void Clear()
    {
        _row?.QueueFreeSafely();
        _row = null;
    }
}
