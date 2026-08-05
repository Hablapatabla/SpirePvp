using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// M2 (DESIGN §3.1 group 1, the half M1 deferred): AOE, random and automatic targeting.
///
/// CombatState.GetOpponentsOf(creature) returns everything on the opposite side, which for a
/// duelist is the empty enemy side. Everything that attacks something other than a manually
/// picked target runs through it, which is why AOE cards "played" and hit nothing, and why
/// Hellraiser's auto-played Strikes all whiffed:
///
///   AttackCommand.GetPossibleTargets  -> TargetingAllOpponents / TargetingRandomOpponents
///   LightningOrb, PoisonPower, Brimstone, PhilosophersStone
///   NControllerCardPlay + NPotionHolder target candidates
///
/// This is the correct chokepoint rather than CombatState.HittableEnemies, which M1 flagged
/// as unpatchable: HittableEnemies is a bare property with no idea who is attacking, whereas
/// GetOpponentsOf is handed the attacker. It is also derived purely from combat state with no
/// reference to the local player, so both clients compute the same answer and the
/// deterministic sim stays in agreement.
///
/// Returns the other duelist(s) unfiltered, matching vanilla — GetCreaturesOnSide does not
/// drop dead or unhittable creatures either, and callers do their own filtering.
/// </summary>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.GetOpponentsOf))]
public static class DuelOpponentsPatch
{
    public static void Postfix(CombatState __instance, Creature creature, ref IReadOnlyList<Creature> __result)
    {
        if (!DuelSession.IsDuelActive || creature == null || !creature.IsPlayer)
        {
            return;
        }

        List<Creature> opponents = new List<Creature>(1);
        foreach (Creature other in __instance.PlayerCreatures)
        {
            if (other != creature)
            {
                opponents.Add(other);
            }
        }

        __result = opponents;
    }
}
