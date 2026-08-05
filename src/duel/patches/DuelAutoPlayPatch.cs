using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Automatically played attacks (Hellraiser's free Strikes, and anything else routing through
/// CardCmd.AutoPlay with no explicit target) need a target chosen for them:
///
///   target = card.Owner.RunState.Rng.CombatTargets.NextItem(combatState.HittableEnemies);
///   if (target == null) { MoveToResultPileWithoutPlaying(...); return; }
///
/// HittableEnemies is empty in a duel, so the target came back null and every auto-played
/// Strike was quietly discarded without dealing damage. This is the one place
/// DuelOpponentsPatch could not reach — it is a direct HittableEnemies read rather than a
/// GetOpponentsOf call.
///
/// Unlike the HittableEnemies property, this call site knows the actor (card.Owner), so the
/// target can be resolved correctly. Filling the parameter in a prefix means vanilla's own
/// null-check passes and its RNG draw never happens.
///
/// Deliberately does not consume RNG: a 1v1 duel has exactly one opponent, so the choice is
/// degenerate, and not drawing keeps both clients' RNG streams identical without depending on
/// patch ordering. If the mode ever supports more than two players this needs to become a
/// synced RNG pick via Rng.CombatTargets, or the clients will diverge.
/// </summary>
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.AutoPlay))]
public static class DuelAutoPlayPatch
{
    public static void Prefix(CardModel card, ref Creature? target)
    {
        if (!DuelSession.IsDuelActive || target != null)
        {
            return;
        }

        if (card.TargetType != TargetType.AnyEnemy)
        {
            return;
        }

        ICombatState? combatState = card.CombatState ?? card.Owner?.Creature?.CombatState;
        Creature? attacker = card.Owner?.Creature;
        if (combatState == null || attacker == null)
        {
            return;
        }

        foreach (Creature candidate in combatState.GetOpponentsOf(attacker))
        {
            if (candidate.IsHittable)
            {
                target = candidate;
                return;
            }
        }
    }
}
