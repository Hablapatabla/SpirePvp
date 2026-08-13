using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel;

/// <summary>
/// **Who is asking for "all enemies"?** — the one question `CombatState.HittableEnemies` cannot
/// answer for itself, and the reason the whole AoE family has been broken in a duel since M2.
///
/// `HittableEnemies` is `Enemies.Where(IsHittable)`, and a duel has an empty enemy side (DESIGN
/// §3.1), so every "all enemies" effect in the game resolves against nothing. The property is a
/// bare getter handed no attacker, so HANDOFF has called it unpatchable since M1 and was right to:
/// **an answer invented locally inside sim code is a desync.** What changes that is not the
/// property — it is having an *actor* that the simulation itself defines, identically on both
/// clients, at the moment of the read. That is what this class maintains.
///
/// # The two tiers, and why one alone is not enough
///
/// The queue proposed resolving the actor from `ActionExecutor.CurrentlyRunningAction` and noted
/// the hole: Bag of Marbles applies from `BeforeSideTurnStart`, outside any action, where
/// `CurrentlyRunningAction` is null. **Reading the decompile makes that hole bigger, and the
/// bigger version is the important finding:** at hook time the running action is not merely
/// *absent*, it is frequently *wrong*. `CorrosiveWavePower.AfterCardDrawn`,
/// `PanachePower.AfterCardPlayed`, `LostWisp.AfterCardPlayed` and a dozen more fire while the
/// *other* duelist's `PlayCardAction` is executing. Resolving the actor from that action would
/// have handed your poison to your own side — silently, and only when the opponent moved. A fix
/// built on tier B alone would have looked correct in every solo test.
///
/// So:
///
/// - **Tier A — the model being dispatched a hook.** A relic knows its `Owner`, a power knows the
///   creature it sits on, a card knows whose it is. `DuelHookListenerScopePatch` wraps
///   `CombatState.IterateHookListeners` so the model currently being handed a hook is ambient for
///   the duration of that hook, and only for that duration. This is the tier that answers Bag of
///   Marbles, and it answers it regardless of *which* combat-state reference the model reads
///   (Bag of Marbles reads the hook's `combatState` parameter, not `base.CombatState`).
/// - **Tier B — the running action's owner**, for effects raised by a card, potion or orb rather
///   than by a hook: `Thunderclap.OnPlay` runs inside the `PlayCardAction` that plays it, and that
///   action's `OwnerId` is the player who played the card. Every one of these read sites is
///   reached from an action owned by exactly the model's owner, which is what makes tier B sound
///   *here* and unsound at hook time.
/// - **Neither resolves → vanilla's empty list stands**, and the read is reported once. Failing
///   back to today's behaviour is the whole safety argument: a case this cannot name behaves
///   exactly as it does now, rather than guessing at a duelist.
///
/// # Why this is deterministic and cannot desync
///
/// Both inputs are properties of the shared simulation, not of the machine reading them. Hook
/// listeners are enumerated from combat state in a fixed order and dispatched one at a time, and
/// the action stream is host-ordered and identical on both peers by construction — the same
/// argument `DuelPlanEnergyPatch` and `DuelTurnModel` already rely on. Two clients therefore
/// resolve the same actor for the same read.
///
/// **The known imprecision, and why it is bounded:** a hook body that `await`s leaves the ambient
/// set across the await, so anything that runs in that window — a UI repaint asking a card whether
/// it should glow, a targeting visual — sees the hook's actor rather than its own. Those reads are
/// presentation, they change no state, and they are the only readers that are not on the sim's own
/// thread of execution. A *sim* read cannot land in that window, because the executor runs one
/// action at a time and hooks are awaited inline within it.
/// </summary>
public static class DuelAoeActor
{
    /// <summary>
    /// The model currently being handed a combat hook, or null outside a hook dispatch.
    ///
    /// Maintained only by <see cref="ScopeEach"/>. Deliberately not public to set: a second writer
    /// is how an ambient like this stops being trustworthy.
    /// </summary>
    private static AbstractModel? _hookModel;

    /// <summary>Run-scoped like the rest of the mod's static state — cleared in `DuelMatch.OnRunEnded`.</summary>
    public static void Reset()
    {
        _hookModel = null;
    }

    /// <summary>
    /// Wraps a hook-listener enumeration so that each model is ambient for exactly the stretch in
    /// which it is being dispatched.
    ///
    /// The shape matters. `IterateHookListeners` builds a `List` and returns it, so this can wrap
    /// the result in an ordinary iterator rather than trying to patch a compiler-generated state
    /// machine. Every hook dispatcher in the engine — 74 loops in `Hook.cs`, directly or through
    /// `Hook.IterateCombatHookListeners` — enumerates that one method, so one wrap covers all of
    /// them without naming a single hook.
    ///
    /// **The previous value is saved and restored rather than nulled**, so a hook that dispatches
    /// another hook nests correctly instead of the inner dispatch erasing the outer's actor. The
    /// restore is in a `finally` because a caller that breaks out of the loop early disposes the
    /// enumerator without running the rest of it.
    /// </summary>
    public static IEnumerable<AbstractModel> ScopeEach(IEnumerable<AbstractModel> inner)
    {
        AbstractModel? previous = _hookModel;
        try
        {
            foreach (AbstractModel model in inner)
            {
                _hookModel = model;
                yield return model;
            }
        }
        finally
        {
            _hookModel = previous;
        }
    }

    /// <summary>
    /// The duelist on whose behalf an "all enemies" read is being made, or null when the mod
    /// cannot say — in which case the caller must leave vanilla's answer alone.
    /// </summary>
    public static Creature? Resolve(ICombatState state)
    {
        // Tier A. A hook is running: the model being dispatched owns the effect, whoever's action
        // happens to be executing underneath it.
        AbstractModel? model = _hookModel;
        if (model != null)
        {
            Creature? fromModel = OwnerCreatureOf(model);
            if (fromModel != null)
            {
                return fromModel;
            }
        }

        // Tier B. No hook: a card, potion or orb effect running inside its own action.
        GameAction? running = RunManager.Instance?.ActionExecutor.CurrentlyRunningAction;
        if (running == null)
        {
            return null;
        }

        Player? owner = state.GetPlayer(running.OwnerId);
        return owner?.Creature;
    }

    /// <summary>
    /// The player creature an arbitrary hook listener belongs to.
    ///
    /// `AbstractModel` has no common owner — a power's is a `Creature`, a relic's, potion's, orb's
    /// and card's is a `Player`, and an enchantment or affliction owns nothing but the card it is
    /// attached to — so this is an allow-list of the shapes that exist, in the same spirit as
    /// `DuelTurnModel.IsPlayerInitiated`: anything a future game version adds resolves to null and
    /// therefore to vanilla behaviour, rather than to a guess.
    ///
    /// `IsMutable` is checked first because the owner accessors call `AssertMutable()` and throw on
    /// a canonical model. Live combat never yields one of those from `IterateHookListeners`, but
    /// this runs inside a property getter that the whole engine calls, and a throw there would be
    /// attributed to whatever vanilla code was reading it.
    /// </summary>
    private static Creature? OwnerCreatureOf(AbstractModel model)
    {
        if (!model.IsMutable)
        {
            return null;
        }

        switch (model)
        {
            // A power's owner is a creature, which may be a pet — Bound Phylactery's Osty carries
            // powers of its own. A pet fights for its owner, so its opponents are its owner's.
            case PowerModel power:
                return PlayerCreatureOf(power.Owner);

            case RelicModel relic:
                return relic.Owner?.Creature;

            case CardModel card:
                return card.Owner?.Creature;

            case PotionModel potion:
                return potion.Owner?.Creature;

            case OrbModel orb:
                return orb.Owner?.Creature;

            // An enchantment or affliction acts through the card it is attached to.
            case EnchantmentModel enchantment:
                return enchantment.HasCard ? enchantment.Card?.Owner?.Creature : null;

            case AfflictionModel affliction:
                return affliction.HasCard ? affliction.Card?.Owner?.Creature : null;

            default:
                return null;
        }
    }

    /// <summary>The player creature behind a creature: itself, or the owner of a pet.</summary>
    private static Creature? PlayerCreatureOf(Creature? creature)
    {
        if (creature == null)
        {
            return null;
        }

        return creature.IsPlayer ? creature : creature.PetOwner?.Creature;
    }
}
