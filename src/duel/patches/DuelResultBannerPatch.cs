using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;

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
[HarmonyPatch(typeof(NGameOverScreen), "InitializeBannerAndQuote")]
public static class DuelResultBannerPatch
{
    public static void Postfix(NGameOverScreen __instance)
    {
        if (DuelSession.Phase != DuelPhase.Complete)
        {
            return;
        }

        string quote;
        switch (DuelSession.Outcome)
        {
            case DuelOutcome.Won:
                __instance._banner.label.SetTextAutoSize("VICTORY");
                quote = "You won the duel.";
                break;

            // No duel was played: the race deadline passed with neither player at the arena.
            case DuelOutcome.Draw:
                __instance._banner.label.SetTextAutoSize("DRAW");
                quote = "Time ran out before either of you reached the arena.";
                break;

            default:
                __instance._banner.label.SetTextAutoSize("DEFEATED");
                quote = "Your opponent won the duel.";
                break;
        }

        // Both, or AnimateInQuote replaces the first with the second. See the note above.
        __instance._deathQuote.Text = quote;
        __instance._encounterQuote = quote;

        // The Architect line. Empty on every outcome — vanilla only fills it on a win, but a
        // stale value surviving from a previous screen would be worse than a redundant clear.
        __instance._victoryDamageLabel.Text = string.Empty;
    }
}
