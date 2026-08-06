using MegaCrit.Sts2.Core.Models.Badges;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// The badges awarded for a duel, decided by comparing the two players.
///
/// Vanilla's badges measure a run against fixed thresholds — killed N elites, ended under N
/// cards, took no damage. A duel has an opponent, so the interesting version of every one of
/// those is *relative*: not "did you take a lot of elites" but "did you take more than they
/// did". That is also why <see cref="DuelStats"/> exchanges both players' numbers; the badges
/// are the reason the result lines are comparative rather than absolute.
///
/// **All of them require the opponent's numbers.** With no snapshot there is nothing to compare
/// and the honest output is no badges at all, rather than awarding "more gold than your
/// opponent" on the strength of one number.
///
/// Ties award nothing. A badge that both players receive for drawing level says nothing about
/// either of them.
///
/// The ids are ours, so the titles and descriptions are ours (`badges.json`, merged into
/// vanilla's `badges` table). The *icons* are borrowed from vanilla badges whose artwork already
/// suits — see `DuelBadgeIconPatch` for why that indirection exists and how it becomes real art.
/// </summary>
public static class DuelBadges
{
    public readonly record struct Award(string Id, BadgeRarity Rarity);

    /// <summary>
    /// Work out which badges this client earned. Local player's perspective — each client
    /// computes its own, from the same two snapshots, so they agree without another message.
    /// </summary>
    public static List<Award> For(DuelStatsMessage mine, DuelStatsMessage? theirs, bool won)
    {
        List<Award> awards = new List<Award>();
        if (theirs is not DuelStatsMessage them)
        {
            return awards;
        }

        // Duel: what you did in the fight itself.
        if (mine.damageDealt > them.damageDealt)
        {
            awards.Add(new Award("SPIREPVP_AGGRESSOR", BadgeRarity.Gold));
        }

        // Efficiency only reads as a virtue if you actually won — fewer cards played while
        // losing is not restraint.
        if (won && mine.cardsPlayed > 0 && mine.cardsPlayed < them.cardsPlayed)
        {
            awards.Add(new Award("SPIREPVP_EFFICIENT", BadgeRarity.Gold));
        }

        // Their damage dealt is the damage you took, so a win with zero of it is untouched.
        if (won && them.damageDealt == 0)
        {
            awards.Add(new Award("SPIREPVP_FLAWLESS", BadgeRarity.Gold));
        }

        if (mine.currentHp > them.currentHp)
        {
            awards.Add(new Award("SPIREPVP_SURVIVOR", BadgeRarity.Silver));
        }

        // Race: what you did on the way there.
        if (mine.elitesKilled > them.elitesKilled)
        {
            awards.Add(new Award("SPIREPVP_ELITE_HUNTER", BadgeRarity.Silver));
        }

        if (mine.goldGained > them.goldGained)
        {
            awards.Add(new Award("SPIREPVP_PROSPECTOR", BadgeRarity.Bronze));
        }

        // A smaller deck is a deliberate choice in this game, so it is worth naming — but only
        // when you actually skipped cards, not when you simply had fewer floors to draft on.
        if (mine.deckSize > 0 && mine.deckSize < them.deckSize)
        {
            awards.Add(new Award("SPIREPVP_MINIMALIST", BadgeRarity.Bronze));
        }

        return awards;
    }
}
