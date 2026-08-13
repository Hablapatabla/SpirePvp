using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
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
/// **That divergence taught the actual rule, which is not about placement:** the pre-combat sync does
/// **not** carry your own state to the peer — it fixes *your* copy of *them*. So a self-heal before
/// it is invisible to the opponent's machine, forever, and the first checksum catches it.
///
/// **Second build ran it after `WaitForSync` and healed both duelists on both clients**, which is
/// safe — after the sync both machines hold identical state, so identical arithmetic lands on
/// identical numbers. It was also too late to be seen: **you can read both players' HP on the deck
/// review**, which opens before arena entry, so the review showed numbers that were about to change.
///
/// **Third build, and the one here: heal on arrival, locally, and *send* the result.** The healed HP
/// rides on `DuelArrivedMessage` beside the deck, which is there for the identical reason — the race
/// decouples the runs, so anything read about the opponent is stale and what the peer needs must be
/// sent rather than looked up. The pre-combat sync still runs at arena entry and is still
/// authoritative; this only has to be right for the screen in between, and now it is.
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
    public static void HealLocalDuelist(IRunState? state)
    {
        if (state == null)
        {
            return;
        }

        Player? me = LocalContext.GetMe(state);
        if (me == null || me.Creature.IsDead)
        {
            // A duelist who died on the way here has a result on the way (`DuelRaceDeath`), and
            // healing a corpse is the more confusing of the two outcomes.
            return;
        }

        Creature creature = me.Creature;
        int before = creature.CurrentHp;
        int amount = (int)HealRestSiteOption.GetBaseHealAmount(creature);
        creature.CurrentHp = Math.Min(creature.MaxHp, creature.CurrentHp + amount);

        _healed.Add(me.NetId);
        Log.Warn($"[SpirePvp] arena rest: healed {me.NetId} {before} -> "
                 + $"{creature.CurrentHp} / {creature.MaxHp} on arrival");

        PlayRestCue();
    }

    /// <summary>
    /// Vanilla's own rest-site cue, so the heal reads as a rest rather than as free HP appearing.
    ///
    /// **Borrowed, not built** — the same reasoning as the initiative label and the play queue. These
    /// are the exact three things `HealRestSiteOption.DoLocalPostSelectVfx` plays when you rest at a
    /// campfire, so a player already knows what it means, and it costs no art and no `.pck` change.
    ///
    /// **Purely local, and that is what makes it safe here.** It is presentation: it changes nothing
    /// either sim reads, so unlike the heal itself it has no ordering or agreement requirement. It
    /// deliberately does *not* await the 1.5-2.5s wait vanilla uses at a real rest site — that wait
    /// exists to pace a screen the player is sitting on, and here it would hold the arena's fade-in.
    ///
    /// Parented to the combat room rather than `NRestSiteRoom.Instance`, which is null in an arena;
    /// if there is no room node yet the cue is simply skipped rather than the heal failing.
    /// </summary>
    /// <summary>
    /// Brings both duelists to the HP a rest would have left them at, after the pre-combat sync.
    ///
    /// **Idempotent by construction**, which is what lets it sit alongside the arrival heal: it does
    /// not add 30% again, it computes the same target the arrival heal aimed at and assigns it. A
    /// duelist already healed is already at that number and nothing moves.
    ///
    /// It exists because the arrival heal is only *locally* applied plus *sent*, and on 2026-08-13
    /// the send silently failed — leaving each machine with its own duelist healed and the opponent
    /// stale, which diverged on the duel's first checksum. Running after the sync, over both
    /// duelists, on both machines, is the placement that provably agrees.
    /// </summary>
    public static void ReconcileAfterSync(IRunState? state)
    {
        if (state == null)
        {
            return;
        }

        foreach (Player player in state.Players)
        {
            Creature creature = player.Creature;
            if (creature.IsDead)
            {
                continue;
            }

            int target = Math.Min(creature.MaxHp,
                                  creature.CurrentHp + (int)HealRestSiteOption.GetBaseHealAmount(creature));
            if (_healed.Contains(player.NetId))
            {
                // Already rested on arrival on this machine: its HP is the target, not the input.
                continue;
            }

            creature.CurrentHp = target;
            Log.Warn($"[SpirePvp] arena rest: reconciled {player.NetId} to {creature.CurrentHp}"
                     + $" / {creature.MaxHp} after the sync");
        }

        _healed.Clear();
    }

    private static readonly HashSet<ulong> _healed = new HashSet<ulong>();

    private static void PlayRestCue()
    {
        HealRestSiteOption.PlayRestSiteHealSfx();

        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null)
        {
            Log.Info("[SpirePvp] arena rest: no combat room node yet, skipping the rest cue");
            return;
        }

        NDesaturateTransitionVfx? desaturate = NDesaturateTransitionVfx.Create();
        if (desaturate != null)
        {
            room.AddChildSafely(desaturate);
        }

        NRestSmokeVfx? smoke = NRestSmokeVfx.Create();
        if (smoke != null)
        {
            room.AddChildSafely(smoke);
        }
    }
}
