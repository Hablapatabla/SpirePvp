using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
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
    /// The opponent's potions, sitting one row above their relics.
    ///
    /// Asked for that way 2026-08-19, once the draft started handing potions out: *"can they be a
    /// row on top of the relic view at the bottom of the screen?"* Two rows rather than one mixed
    /// row, because they are two different kinds of thing and the eye should not have to sort them.
    /// </summary>
    private static HFlowContainer? _potionRow;

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
        //
        // **The width comes from the screen, not from the caption, and that was the bug.** It used
        // `label.Size.X`, and `_infoLabel`'s *node* is only 88px wide — its text is centred and
        // overflows it. An `HFlowContainer` given 88px wraps after the first relic, so six relics
        // drew as a vertical stack running down into the caption. Reported 2026-08-13: "the
        // opponent relic bar is vertical and should be horizontal".
        //
        // The caption is centred on the screen, so a full-width row with centred alignment lines up
        // with it and stays lined up at any resolution — which is the same reason the position is
        // read off the caption rather than hard-coded.
        Vector2 captionTopLeft = label.GlobalPosition;
        float width = screen.Size.X > 0 ? screen.Size.X : label.Size.X;

        row.Alignment = FlowContainer.AlignmentMode.Center;
        row.Size = new Vector2(width, RowHeightAboveCaption);
        row.GlobalPosition = new Vector2(screen.GlobalPosition.X,
                                         captionTopLeft.Y - RowHeightAboveCaption);

        Log.Warn($"[SpirePvp] duel entry — {drawn} opponent relic(s) drawn at {row.GlobalPosition} "
                 + $"across {width:F0}px, above the caption at {captionTopLeft} "
                 + $"(caption node size {label.Size})");

        ShowPotions(screen, width, row.GlobalPosition.Y);
    }

    /// <summary>
    /// Draws the opponent's potions in their own row, immediately above the relic row.
    ///
    /// **Stacked off the relic row's own position rather than off the caption**, so the two stay
    /// together if the caption ever moves. The relic row is placed first and hands its Y down.
    ///
    /// `NPotionHolder` needs the same two things the draft's row needed and neither is optional:
    /// the holder must be in the tree before it is filled, because `AddPotion` writes `_emptyIcon`
    /// which `_Ready` assigns; and the potion needs `Position = (-30, -30)` to sit inside the
    /// holder, which is what `NPotionContainer` does and `AddPotion` does not. Both cost a round
    /// trip each in the draft — see `DuelDraftPotionScreenPatch`.
    ///
    /// Non-interactive throughout: this is a review, so the holders ignore the mouse for clicks and
    /// keep only the hover tip, which is the one thing you actually want from them here.
    /// </summary>
    private static void ShowPotions(NDeckCardSelectScreen screen, float width, float relicRowY)
    {
        IReadOnlyList<SerializablePotion>? potions = DuelRendezvous.OpponentPotions;
        if (potions == null || potions.Count == 0)
        {
            return;
        }

        HFlowContainer row = new HFlowContainer
        {
            Name = "SpirePvpOpponentPotions",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        int drawn = 0;
        foreach (SerializablePotion saved in potions)
        {
            try
            {
                PotionModel model = PotionModel.FromSerializable(saved);
                NPotionHolder holder = NPotionHolder.Create(isUsable: false);
                NPotion? sprite = NPotion.Create(model);
                if (sprite == null)
                {
                    continue;
                }

                sprite.Position = new Vector2(-30f, -30f);
                row.AddChild(holder);
                holder.AddPotion(sprite);
                holder.MouseDefaultCursorShape = Control.CursorShape.Help;
                drawn++;
            }
            catch (Exception e)
            {
                Log.Warn($"[SpirePvp] duel entry — could not draw an opponent potion: {e.Message}");
            }
        }

        if (drawn == 0)
        {
            row.QueueFreeSafely();
            return;
        }

        screen.AddChildSafely(row);
        _potionRow = row;

        row.Alignment = FlowContainer.AlignmentMode.Center;
        row.Size = new Vector2(width, RowHeightAboveCaption);
        row.GlobalPosition = new Vector2(screen.GlobalPosition.X,
                                         relicRowY - RowHeightAboveCaption);

        Log.Warn($"[SpirePvp] duel entry — {drawn} opponent potion(s) drawn at {row.GlobalPosition}, "
                 + $"above the relic row at y={relicRowY:F0}");
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

        _potionRow?.QueueFreeSafely();
        _potionRow = null;
    }
}
