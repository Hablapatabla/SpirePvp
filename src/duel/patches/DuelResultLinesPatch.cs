using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using SpirePvp.Net;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Replaces the run-score lines on a duel's result screen with the match's own numbers.
///
/// Vanilla scores a run: floors climbed, gold gained, elites killed, bosses slain, ascension
/// multiplier, each with a "+N" score contribution. After a duel that is meaningless at best and
/// misleading at worst — the match was decided by who was left standing, and "+42 for floors
/// climbed" invites the loser to read it as evidence they were ahead.
///
/// What replaces it is **comparative**, because a duel has an opponent: every line reads
/// `yours vs theirs`. "12 cards played" says very little; "12 · 20" says who was efficient.
///
/// **Implemented by replacing `AnimateScoreLines` wholesale**, rather than suppressing
/// `AddScoreLine` and appending afterwards. That earlier approach had a subtle flaw: vanilla
/// adds its lines and *then* awaits `AnimateIn()` on each, so lines appended after the fact are
/// never animated in and may never become visible. Owning the method means our lines get the
/// same entrance vanilla's do.
///
/// `AnimateScoreLines` is `private async Task`, so the skipping prefix **must** assign
/// `__result` — the rule this project has paid for twice (`RaceStarsWithoutCombatPatch`, then
/// `DuelEndCombatPatch`). Here `__result` is our own async method, so the caller awaits our
/// animation rather than a completed no-op.
///
/// Line labels come from `game_over_screen.json` in the mod's `.pck`. That filename is
/// load-bearing: `LocManager` merges a mod's tables only into tables vanilla already has, by
/// filename.
///
/// **No BBCode in those strings.** `NScoreLine.Create` puts both halves of a line into a
/// `MegaLabel`, which is a plain Godot `Label` — so `[gold]…[/gold]` was drawn literally, tags
/// and all, straight across the result screen. The rich-text labels on this screen
/// (`_deathQuote`, `_victoryDamageLabel`, both `MegaRichTextLabel`) do take markup, which is
/// exactly what makes the distinction easy to get wrong.
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen.AnimateScoreLines))]
public static class DuelResultLinesPatch
{
    private const string Table = "game_over_screen";

    public static bool Prefix(NGameOverScreen __instance, ref Task __result)
    {
        if (DuelSession.Phase != DuelPhase.Complete)
        {
            return true;
        }

        __result = ShowDuelLines(__instance);
        return false;
    }

    private static async Task ShowDuelLines(NGameOverScreen screen)
    {
        try
        {
            // Vanilla's AnimateScoreLines opens with exactly this, and owning the method means
            // owning its housekeeping too. Without it a screen that ever got its lines animated
            // twice would animate the first set a second time.
            screen._scoreLines.Clear();

            // **Before a single line is added.** The badges go in a sibling container that vanilla
            // does not fill until after these lines have animated, so the column reflows and shoves
            // the lines up into the quote. Creating the badges now — transparent, unanimated —
            // reserves that row's height so the layout the lines land in is the final one. See
            // DuelBadgesPatch.EnsureCreated.
            DuelBadgesPatch.EnsureCreated(screen);

            DuelStatsMessage mine = DuelStats.BuildLocal();
            DuelStatsMessage? theirs = DuelStats.Opponent;

            AddComparison(screen, "SPIREPVP_LINE.damageDealt", mine.damageDealt, theirs?.damageDealt,
                "res://images/ui/game_over_screen/score_elite.png");
            AddComparison(screen, "SPIREPVP_LINE.cardsPlayed", mine.cardsPlayed, theirs?.cardsPlayed,
                "res://images/ui/game_over_screen/score_floor.png");
            AddComparison(screen, "SPIREPVP_LINE.hpRemaining", mine.currentHp, theirs?.currentHp,
                "res://images/ui/game_over_screen/score_boss.png");
            AddComparison(screen, "SPIREPVP_LINE.goldGained", mine.goldGained, theirs?.goldGained,
                "res://images/ui/game_over_screen/score_gold.png");
            AddComparison(screen, "SPIREPVP_LINE.elitesKilled", mine.elitesKilled, theirs?.elitesKilled,
                "res://images/ui/game_over_screen/score_elite.png");
            AddComparison(screen, "SPIREPVP_LINE.deckSize", mine.deckSize, theirs?.deckSize,
                "res://images/ui/game_over_screen/score_floor.png");

            if (theirs == null)
            {
                // Say so rather than printing our own numbers as if they were a comparison.
                Log.Warn("[SpirePvp] result screen: opponent stats had not arrived — showing " +
                         "local numbers only.");
            }

            AddQuoteGap(screen);

            foreach (NScoreLine line in screen._scoreLines)
            {
                await line.AnimateIn();
            }

            DumpLayout(screen);
        }
        catch (Exception e)
        {
            // The result screen matters more than its statistics.
            Log.Error($"[SpirePvp] result screen lines failed: {e}");
        }
    }

    /// <summary>
    /// Space between the quirky line under the banner and the first score row.
    ///
    /// **Reserving the badge row was not enough, and the log said so before the screenshot did:**
    /// `container min height 0` at the moment the badges were parked. Godot sizes containers on the
    /// *next* layout pass, so adding children reserves nothing on the frame you add them — the
    /// column still settles later, just earlier than before. What that fix bought was consistency;
    /// what it did not buy was room, because the settled spacing is the tight one all along.
    ///
    /// So this adds the room outright, as a sized spacer in front of the grid rather than by
    /// nudging anyone's position — a position would be undone by the next layout pass, which is
    /// the same mistake one level down.
    /// </summary>
    private const float QuoteGapHeight = 28f;

    private const string QuoteGapName = "SpirePvpQuoteGap";

    /// <summary>Pushes the score grid down, once, by inserting a spacer above it.</summary>
    private static void AddQuoteGap(NGameOverScreen screen)
    {
        if (screen._scoreLineContainer?.GetParent() is not Node parent
            || parent.GetNodeOrNull<Control>(QuoteGapName) != null)
        {
            return;
        }

        Control gap = new Control
        {
            Name = QuoteGapName,
            CustomMinimumSize = new Vector2(0f, QuoteGapHeight),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        parent.AddChild(gap);
        parent.MoveChild(gap, screen._scoreLineContainer.GetIndex());
    }

    /// <summary>
    /// Everything between the banner and the badges, with its parent chain.
    ///
    /// The spacing above is a number chosen once and then corrected from measurements, not from
    /// screenshots — this project has twice "fixed" a placement by eye and had to revert one of
    /// them. Logged after the lines have animated, so these are the values the player is looking at.
    /// </summary>
    private static void DumpLayout(NGameOverScreen screen)
    {
        Control? grid = screen._scoreLineContainer;
        Control? badges = screen._badgeContainer;
        Control? quote = screen._deathQuote;

        Log.Warn($"[SpirePvp] result layout — parent={grid?.GetParent()?.Name} "
                 + $"({grid?.GetParent()?.GetType().Name})");
        Log.Warn($"[SpirePvp] result layout — quote  pos={quote?.Position} size={quote?.Size} "
                 + $"global={quote?.GlobalPosition} parent={quote?.GetParent()?.Name}");
        Log.Warn($"[SpirePvp] result layout — grid   pos={grid?.Position} size={grid?.Size} "
                 + $"global={grid?.GlobalPosition} idx={grid?.GetIndex()}");
        Log.Warn($"[SpirePvp] result layout — badges pos={badges?.Position} size={badges?.Size} "
                 + $"global={badges?.GlobalPosition} children={badges?.GetChildCount()}");
    }

    /// <summary>
    /// One line, `yours · theirs`. The score column is where vanilla puts "+42"; here it
    /// carries the opponent's number, so the two sit side by side on every row.
    /// </summary>
    private static void AddComparison(
        NGameOverScreen screen, string locKey, int mine, int? theirs, string iconPath)
    {
        string opponent = theirs?.ToString() ?? "—";
        screen.AddScoreLine(locKey, "Amount", mine, opponent, iconPath);
    }
}
