using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// M1 (DESIGN §3.1 group 1): in duel mode, "enemy" means the opponent's player creature.
///
/// NOT YET A HARMONY PATCH — the target method is undiscovered (I1). Implementation plan:
/// find where the candidate set for TargetType.AnyEnemy / AllEnemies / RandomEnemy is
/// computed (start from CombatState.HittableEnemies and its callers, plus the target
/// selection UI's candidate query) and patch that chokepoint so, while
/// DuelSession.IsDuelActive, it returns <see cref="OpponentCreatureOf"/> for the acting
/// player instead of the (empty) enemy side. One chokepoint is the goal; if targeting is
/// resolved in several places, prefer patching CombatState.HittableEnemies itself.
/// </summary>
public static class DuelTargetingPatch
{
    /// <summary>The duel-mode target set for an acting player: exactly the opponent's creature.</summary>
    public static IReadOnlyList<Creature> OpponentCreatureOf(Player actor, ICombatState combat)
    {
        List<Creature> result = new List<Creature>(1);
        foreach (Creature c in combat.PlayerCreatures)
        {
            if (c.Player != actor && c.IsHittable)
            {
                result.Add(c);
            }
        }
        return result;
    }
}
