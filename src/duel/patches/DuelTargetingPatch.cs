using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// M1 (DESIGN §3.1 group 1): in duel mode, "enemy" means the opponent's player creature.
///
/// I1 (targeting half) RESOLVED against v0.110.1. Single-target selection funnels through
/// CardModel.IsValidTarget (~line 1772), which for TargetType.AnyEnemy is exactly
/// `target.Side != Owner.Creature.Side`. Both duelists sit on CombatSide.Player, so vanilla
/// rejects the opponent. One postfix re-admits them, and it covers both the manual play
/// path (TryManualPlay → CanPlayTargeting) and the authoritative re-check inside
/// PlayCardAction (~line 85), so the UI and the synchronised action agree on legality.
///
/// Damage/block/powers all operate on Creature and never inspect sides, so the rest of the
/// card mechanics layer comes along untouched — that is the whole trick of §3.1.
///
/// STILL OPEN — AOE and random targeting. Those read CombatState.HittableEnemies
/// (`Enemies.Where(IsHittable)`), a property with no acting-player context, so it cannot be
/// retargeted from the getter alone: it has no way to know whose opponent to return. M1
/// acceptance only needs single-target Strikes; M2 should retarget at the call sites that
/// do know the actor (card and power effects hold Owner) rather than patching the getter
/// globally.
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.IsValidTarget))]
public static class DuelTargetingPatch
{
    public static void Postfix(CardModel __instance, Creature? target, ref bool __result)
    {
        // Vanilla already allowed it, or we aren't duelling.
        if (__result || !DuelSession.IsDuelActive)
        {
            return;
        }

        if (__instance.TargetType != TargetType.AnyEnemy)
        {
            return;
        }

        __result = IsOpponentOf(__instance.Owner, target);
    }

    /// <summary>
    /// True when <paramref name="target"/> is a live player creature belonging to someone
    /// other than <paramref name="actor"/> — the duel's notion of "an enemy".
    /// </summary>
    public static bool IsOpponentOf(Player actor, Creature? target)
    {
        if (target == null || !target.IsAlive || !target.IsPlayer)
        {
            return false;
        }

        Player? owner = target.Player;
        return owner != null && owner.NetId != actor.NetId;
    }

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
