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

        if (DuelSession.LocalPlayerWon)
        {
            __instance._banner.label.SetTextAutoSize("VICTORY");
            __instance._deathQuote.Text = "You won the duel.";
        }
        else
        {
            __instance._banner.label.SetTextAutoSize("DEFEATED");
            __instance._deathQuote.Text = "Your opponent won the duel.";
        }
    }
}
