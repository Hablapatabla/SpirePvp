using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.addons.mega_text;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Rewrites the game-over banner so a duel reads as a duel.
///
/// The vanilla screen picks its text from run history: a win shows the "false victory"
/// banner about the Architect, a loss shows a death quote. Neither makes sense when the run
/// ended because someone stabbed you. InitializeBannerAndQuote sets the label through
/// _banner.label.SetTextAutoSize, so a postfix can overwrite it after vanilla has run —
/// no localization files to edit.
///
/// Keyed on DuelPhase.Complete so the normal end-of-run screen is untouched.
///
/// **Setting `_deathQuote.Text` is not enough, and that is why a duel loss reported the wrong
/// killer.** `InitializeBannerAndQuote` also stashes `_encounterQuote` — the run-history death
/// line — and `AnimateInQuote` fades our text out a moment later and writes that string in its
/// place. So the screen briefly read "Your opponent won the duel" and then settled on
/// *"The Silent was absorbed by a Skulking Colony"*: the last thing the **race** recorded, an
/// elite the player had already beaten, named as the cause of a death that happened in the duel.
/// Overwriting `_encounterQuote` too is what makes the correction stick.
///
/// The victory branch has the same shape one field over. It fills `_victoryDamageLabel` with
/// `VICTORY_DAMAGE`, "you dealt N damage to the Architect", off `StatsManager` and the run score —
/// a boss this run never fought, and a number that means nothing in a duel. Blanked rather than
/// rewritten: the duel's own numbers are already on the screen, in the score lines.
/// </summary>
[HarmonyPatch]
public static class DuelResultBannerPatch
{
    [HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen.InitializeBannerAndQuote))]
    [HarmonyPostfix]
    public static void AfterInitializeBannerAndQuote(NGameOverScreen __instance)
    {
        if (DuelSession.Phase != DuelPhase.Complete)
        {
            return;
        }

        // **The outcome says who won; only the reason says what happened.** Every line here was
        // once worded from the outcome alone, which meant each of the three read as an HP finish:
        // an agreed draw claimed time had run out, and a race resignation congratulated the
        // survivor on winning a duel that was never fought. Same trap DuelClockService and
        // DuelFlag both hit — a question that correlates with the one you mean.
        //
        // The wording itself now lives in DuelResultQuotes, which keeps that pairing and picks
        // between several phrasings of the *same* ending. The banner stays here because it is one
        // word per outcome and has no reasons to distinguish.
        switch (DuelSession.Outcome)
        {
            case DuelOutcome.Won:
                __instance._banner.label.SetTextAutoSize("VICTORY");
                break;

            case DuelOutcome.Draw:
                __instance._banner.label.SetTextAutoSize("DRAW");
                break;

            default:
                __instance._banner.label.SetTextAutoSize("DEFEATED");
                break;
        }

        (string quote, string source) =
            DuelResultQuotes.Pick(DuelSession.Outcome, DuelResult.EndReason);

        // **A win and a loss put their text in different labels, and writing to the wrong one is
        // silence.** The winner's screen had no line at all until this was traced: `AnimateInQuote`
        // fades `_deathQuote` to alpha 0, and then *only on a loss* fades it back in — on a win it
        // animates `_victoryDamageLabel` instead and never touches `_deathQuote` again. So the
        // loser's line has to go in `_deathQuote` (and in `_encounterQuote`, or the fade-in
        // overwrites it a second later), and the winner's has to go in `_victoryDamageLabel` —
        // the very label this patch was busy blanking.
        //
        // Keyed on `_history.Win` rather than on `DuelSession.Outcome` deliberately: it is the
        // exact field `AnimateInQuote` branches on, so the label written can never disagree with
        // the label shown. A draw is not a win here — `RunManager.OnEnded` is handed
        // `outcome == Won` — so a draw takes the loss path, which is the one already playtested.
        if (__instance._history.Win)
        {
            // Left empty, as vanilla leaves it on this branch: nothing brings it back on screen.
            __instance._deathQuote.Text = string.Empty;
            __instance._victoryDamageLabel.Text = quote;
            MoveVictoryLabelUnderBanner(__instance);
        }
        else
        {
            __instance._deathQuote.Text = quote;
            __instance._encounterQuote = quote;

            // The Architect line — "you dealt N damage" to a boss this run never fought. Vanilla
            // only fills it on a win, but a stale value surviving from a previous screen would be
            // worse than a redundant clear.
            __instance._victoryDamageLabel.Text = string.Empty;
        }

        // What the screen actually says, and where the words came from. The two-label trick above
        // means the text a player reports seeing may be neither the one we set nor the one
        // vanilla set, and that ambiguity is precisely what made the wrong-killer bug take a
        // playtest to pin down.
        //
        // The source half names which entry was picked, or says no loc entries were found at
        // all — separating "that line is weak" from "the .pck is stale", which is the question
        // most likely to be asked about a screen whose whole job is wording.
        Log.Warn($"[SpirePvp] result screen: {DuelSession.Outcome}, "
                 + $"reason {DuelResult.EndReason} — \"{quote}\" [{source}] "
                 + $"in {(__instance._history.Win ? "victoryDamageLabel" : "deathQuote")}");
    }

    /// <summary>
    /// Puts the winner's line back on the summary screen, which vanilla hides on the way in.
    ///
    /// **Reported 2026-08-12: the loser's line appears on the score screen and the winner's does
    /// not.** `OpenSummaryScreen` — the Continue button — begins with
    /// `_victoryDamageLabel.Visible = false` and *then* runs `AnimateInQuote`, which on a win
    /// tweens that very label's `modulate:a` and `visible_ratio`. So the animation plays on a
    /// hidden node and nothing is ever drawn, while the loss branch tweens `_deathQuote`, which
    /// nobody hid.
    ///
    /// That is correct for vanilla and wrong for us, and the difference is what the label now
    /// holds. Vanilla's `_victoryDamageLabel` is a full-screen block of run-score prose about the
    /// Architect, which would sit across the summary it is handing over to — hiding it is the
    /// right call. We have reparented it into the banner and put a one-line duel epitaph in it
    /// (see below), so for a duel it is the same line the loser keeps, in the same place, and it
    /// should survive the same transition.
    ///
    /// A postfix, so it re-shows the label after vanilla has hidden it and before the tween has
    /// anything to fade in. Gated on `Complete` like everything else here, so an ordinary run's
    /// summary screen is untouched.
    /// </summary>
    [HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen.OpenSummaryScreen))]
    [HarmonyPostfix]
    public static void AfterOpenSummaryScreen(NGameOverScreen __instance)
    {
        // `_history.Win` rather than the outcome, for the reason given above: it is the field
        // `AnimateInQuote` branches on, so this can never re-show a label the animation is not
        // going to fill.
        if (DuelSession.Phase != DuelPhase.Complete || !__instance._history.Win)
        {
            return;
        }

        __instance._victoryDamageLabel.Visible = true;

        Log.Warn("[SpirePvp] result screen: victory line re-shown on the summary screen "
                 + "(OpenSummaryScreen hides it for vanilla's Architect prose)");
    }

    /// <summary>
    /// Where a loss quote comes to rest, taken from vanilla's own tween in `AnimateInQuote`
    /// (`position:y` to 156 from 90) rather than chosen to look right.
    /// </summary>
    private const float QuoteRestY = 156f;

    /// <summary>
    /// Puts the winner's line where the loser's line goes — under the banner, not over the
    /// character.
    ///
    /// The two labels are not interchangeable in position, only in purpose.
    /// `_victoryDamageLabel` holds a block of run-score prose ("you dealt N damage to the
    /// Architect", plus an ascension unlock note) and is placed low, where a wall of text has
    /// room; `_deathQuote` is a single line tucked under the banner. Borrowing the victory label
    /// because it is the only one that animates in on a win therefore borrows its placement too,
    /// and a one-line duel epitaph landed across the victor's own sprite.
    ///
    /// **Measured, not guessed**, and the measurement is why the first attempt did nothing:
    ///
    ///     quote   pos=(97.5, 150) size=(459, 40)    parent=Banner
    ///     victory pos=(0, 0)      size=(1920, 1080) parent=Ui
    ///
    /// The two labels are not siblings and are not even the same *kind* of thing. `_deathQuote`
    /// is a small box living **inside the banner**; `_victoryDamageLabel` is a **full-screen**
    /// label under `Ui`, so its text centres in the middle of the screen — which is precisely
    /// where the character stands. Copying coordinates between them was meaningless, and the
    /// guard that noticed they had different parents is the only reason nothing worse happened.
    ///
    /// So the label is **moved into the banner** and given the death quote's own box. That makes
    /// the winner's line and the loser's line the same line in the same place, rather than two
    /// placements kept in step by hand. Anchors are reset first: a full-rect anchored control
    /// re-stretches to its new parent on the next layout pass and would silently undo the
    /// position.
    ///
    /// **The geometry is logged rather than trusted.** Two placement bugs in this project were
    /// "corrected" from screenshots and one of those corrections was wrong and had to be
    /// reverted; what settled it was logging both sides and diffing them. Pixels are not exempt
    /// from reading the logs — this fix exists because the log said the first one could not have
    /// worked.
    /// </summary>
    private static void MoveVictoryLabelUnderBanner(NGameOverScreen screen)
    {
        Control quote = screen._deathQuote;
        Control victory = screen._victoryDamageLabel;
        Node? banner = quote.GetParent();

        if (banner == null)
        {
            Log.Warn("[SpirePvp] result screen: death quote has no parent; victory line left "
                     + "where vanilla puts it");
            return;
        }

        if (victory.GetParent() != banner)
        {
            victory.Reparent(banner, keepGlobalTransform: false);
        }

        // Before the position, or the anchors win: this label is anchored full-rect, and a
        // full-rect control recomputes its own offsets from the parent on every layout pass.
        victory.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        victory.Position = new Vector2(quote.Position.X, QuoteRestY);
        victory.Size = quote.Size;

        // **And the font, or the box is the wrong size for what goes in it.** Taking the death
        // quote's position and dimensions without its type size put a one-line epitaph in a box
        // scaled for a different one, so the winner's line wrapped after a handful of words while
        // the loser's did not. `_victoryDamageLabel` is built for a paragraph of run-score prose
        // sitting lower on the screen, where a bigger face is fine; borrowed up here it is simply
        // too large for 459 pixels.
        //
        // Copied from the quote rather than picked, for the same reason the geometry is: the two
        // lines should be indistinguishable, and a number chosen by eye would drift the moment
        // the screen was restyled.
        int quoteFontSize = quote.GetThemeFontSize(ThemeConstants.RichTextLabel.NormalFontSize);
        if (quoteFontSize > 0)
        {
            victory.AddThemeFontSizeOverride(ThemeConstants.RichTextLabel.NormalFontSize,
                                             quoteFontSize);
        }

        Log.Warn($"[SpirePvp] result screen: victory line moved into {banner.Name} at "
                 + $"pos={victory.Position} size={victory.Size} font={quoteFontSize} "
                 + $"(quote sits at pos={quote.Position} size={quote.Size}, "
                 + $"was font={victory.GetThemeFontSize(ThemeConstants.RichTextLabel.NormalFontSize)})");
    }
}
