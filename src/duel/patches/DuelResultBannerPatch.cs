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

        switch (DuelSession.Outcome)
        {
            case DuelOutcome.Won:
                __instance._banner.label.SetTextAutoSize("VICTORY");
                __instance._deathQuote.Text = "You won the duel.";
                break;

            // No duel was played: the race deadline passed with neither player at the arena.
            case DuelOutcome.Draw:
                __instance._banner.label.SetTextAutoSize("DRAW");
                __instance._deathQuote.Text = "Time ran out before either of you reached the arena.";
                break;

            default:
                __instance._banner.label.SetTextAutoSize("DEFEATED");
                __instance._deathQuote.Text = "Your opponent won the duel.";
                break;
        }
    }
}
