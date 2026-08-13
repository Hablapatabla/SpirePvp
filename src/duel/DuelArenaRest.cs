using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel;

/// <summary>
/// A rest on the way into the arena, so the duel is a duel rather than a coin toss.
///
/// **The problem it fixes, measured in play 2026-08-12 on a 10 min / 3 min match:** both duelists
/// arrive from the Act 1 boss at 20–30 HP, and at that pool the first player to land an attack
/// simply wins on turn one. The race bank is not the lever — it was already 10 minutes — and the
/// mode's whole content, the turn models and the initiative rule, never gets to happen. Lucas:
/// *"the heal before entering is just more fun gameplay."*
///
/// # Why a heal and not a rest site room
///
/// `RestSiteRoom` was the first design (and `RestSiteRoom.Exit` awaiting
/// `AfterAllRestSitesCompleted` is a genuinely nice both-players gate), but it carries a Smith
/// option, and **upgrading a card after the deck review makes the reveal a lie** — the exact
/// information-rule bug fixed on 2026-08-12, re-created invisibly. Fixing *that* means the rest has
/// to precede the arrival announcement that carries the deck, which means the coord move has to
/// precede the rest, which means splitting `DuelArena` around the one method this project has
/// already found six quiet omissions in.
///
/// The heal alone needs none of that, and the heal alone is what was asked for. The room version is
/// still scoped in HANDOFF if the *choice* turns out to be wanted.
///
/// # Why this cannot desync
///
/// **Each client heals only its own duelist**, and the pre-combat state sync — which runs a few
/// lines later in `DuelArena.EnterRoom` — carries the result to the peer. Healing both players
/// locally would read the opponent's HP, which is *stale by construction* during a race: the runs
/// are decoupled, so your copy of their `Player` stopped updating when the race began. That is the
/// same rule the stale decklist taught: what the peer needs must be sent, not looked up.
///
/// Placed before `StartSync` for exactly that reason. Move it after, and each client syncs an
/// un-healed opponent and then heals a copy nobody agreed on.
///
/// # The amount is vanilla's own
///
/// `HealRestSiteOption.ExecuteRestSiteHeal` rather than a number of ours, so it is 30% of max HP
/// through vanilla's own hook chain (`ModifyRestSiteHealAmount`) — which means relics that change
/// what a rest is worth keep working, and a player reading "you rest" gets what a rest has always
/// given them. It also keeps the sfx and the heal VFX.
/// </summary>
public static class DuelArenaRest
{
    /// <summary>
    /// Heals the local duelist by a rest's worth, on the way into the arena.
    ///
    /// Silent about a dead player: a duelist who died on the way here has a result on the way
    /// (`DuelRaceDeath`), and healing a corpse would be the more confusing of the two outcomes.
    /// </summary>
    public static async Task HealLocalDuelist(IRunState? state)
    {
        if (state == null)
        {
            return;
        }

        Player? me = LocalContext.GetMe(state);
        if (me == null || me.Creature.IsDead)
        {
            return;
        }

        int before = me.Creature.CurrentHp;
        await HealRestSiteOption.ExecuteRestSiteHeal(me, isMimicked: false);

        Log.Warn($"[SpirePvp] arena rest: healed {before} -> {me.Creature.CurrentHp} "
                 + $"/ {me.Creature.MaxHp} before the duel");
    }
}
