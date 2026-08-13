using MegaCrit.Sts2.Core.Entities.Creatures;
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
/// # It desynced, and the reason is the interesting part
///
/// **First build (2026-08-12) healed the local duelist only, before the pre-combat sync, on the
/// theory that the sync would carry the result.** It did not. Measured 2026-08-13 on the first
/// match to try it: host `healed 56 -> 70`, client `healed 52 -> 66`, and then
/// `State divergence detected! ... Context: After player turn start. Local: 1853225010. Remote:
/// 2289814259` — **checksum ID 0**, before either player had played a card. Whatever the sync
/// carries, it did not leave the two machines agreeing about two creatures that had each been
/// mutated locally a moment earlier.
///
/// The mistake is more general than a placement: **a state mutation applied outside the ordered
/// action stream has to be applied from state both sims already agree on, or it is a guess.** Before
/// the sync they do not agree — that is what the sync is *for*.
///
/// So the heal now runs **after `WaitForSync` completes**, and heals **both** duelists on **both**
/// clients. That sounds like the more dangerous option and is the safer one: after the sync the two
/// machines hold identical state for both creatures, so the same arithmetic applied to both on each
/// machine lands on the same numbers by construction. Healing only your own before the sync was the
/// version that could not agree.
///
/// **Arithmetic, not `ExecuteRestSiteHeal`.** Vanilla's helper runs the rest-site hook chain, and
/// hooks are exactly the kind of thing vanilla routes through `RestSiteSynchronizer` *because* they
/// are not safe to run independently on two machines. The trade is that relics which change what a
/// rest is worth no longer apply here; the amount is still vanilla's 30% of max HP, taken from
/// `HealRestSiteOption.GetBaseHealAmount`, so the number a player sees is the number a rest has
/// always given. If relic interaction is wanted later it has to come from the host as data.
/// </summary>
public static class DuelArenaRest
{
    /// <summary>
    /// Heals both duelists by a rest's worth, once the two machines agree on their state.
    ///
    /// Called from `DuelArena` after `WaitForSync`, never before it — see the note above.
    /// </summary>
    public static void HealBothDuelists(IRunState? state)
    {
        if (state == null)
        {
            return;
        }

        foreach (Player player in state.Players)
        {
            Creature creature = player.Creature;

            // A duelist who died on the way here has a result on the way (`DuelRaceDeath`), and
            // healing a corpse is the more confusing of the two outcomes. Skipped identically on
            // both machines, because both have just synced.
            if (creature.IsDead)
            {
                continue;
            }

            int before = creature.CurrentHp;
            int amount = (int)HealRestSiteOption.GetBaseHealAmount(creature);
            creature.CurrentHp = Math.Min(creature.MaxHp, creature.CurrentHp + amount);

            Log.Warn($"[SpirePvp] arena rest: healed {player.NetId} {before} -> "
                     + $"{creature.CurrentHp} / {creature.MaxHp} before the duel");
        }
    }
}
