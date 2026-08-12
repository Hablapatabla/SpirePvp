using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using SpirePvp.Net;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Puts duel badges on the result screen in place of the run's.
///
/// Vanilla's badges are run achievements measured against fixed thresholds; a duel's are
/// comparisons against the opponent (see <see cref="DuelBadges"/>). Replacing rather than
/// appending matters for a second reason: vanilla's `AnimateBadges` also calls
/// `SaveBadgesToProgress`, writing what it awarded into the profile's badge statistics. Duel
/// badges are not vanilla badges and have no `Badge` models behind them, and a PvP match has no
/// business editing the single-player achievement record. Skipping the method avoids that
/// entirely.
///
/// `AnimateBadges` is `private async Task`, so the skipping prefix **must** assign `__result` —
/// the rule that has cost this project two multi-session hunts. It is handed our own async
/// method, so the caller awaits our animation exactly as it would vanilla's.
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen.AnimateBadges))]
public static class DuelBadgesPatch
{
    public static bool Prefix(NGameOverScreen __instance, ref Task __result)
    {
        if (DuelSession.Phase != DuelPhase.Complete)
        {
            return true;
        }

        __result = ShowDuelBadges(__instance);
        return false;
    }

    private static async Task ShowDuelBadges(NGameOverScreen screen)
    {
        try
        {
            DuelStatsMessage mine = DuelStats.BuildLocal();
            List<DuelBadges.Award> awards =
                DuelBadges.For(mine, DuelStats.Opponent, DuelSession.LocalPlayerWon);

            foreach (DuelBadges.Award award in awards)
            {
                NBadge? badge = NBadge.Create(award.Id, award.Rarity);
                if (badge != null)
                {
                    screen._badgeContainer.AddChildSafely(badge);
                }
            }

            Log.Warn($"[SpirePvp] duel badges: {awards.Count} awarded" +
                     (DuelStats.HasOpponent ? "" : " (no opponent stats — nothing to compare)"));

            // Vanilla's cadence: a beat before the first badge, then one at a time.
            await Cmd.Wait(0.25f);

            foreach (NBadge badge in screen._badgeContainer.GetChildren().OfType<NBadge>())
            {
                await badge.AnimateIn();
            }
        }
        catch (Exception e)
        {
            Log.Error($"[SpirePvp] duel badges failed: {e}");
        }
    }
}
