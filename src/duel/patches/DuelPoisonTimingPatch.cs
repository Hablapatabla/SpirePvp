using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Puts poison back on vanilla's felt timing: end of the player phase, not a round later.
///
/// Hook.AfterSideTurnStart fires once per side with that side's creatures, and PoisonPower
/// triggers when the participants contain its owner. In vanilla the poisoned creature is a
/// monster, so poison resolves at ENEMY side turn start — right after you end your turn and
/// before the monster acts. That is why poison reads as end-of-turn damage and can kill
/// something before it swings.
///
/// In a duel the poisoned creature is a player, so the same code resolves at PLAYER side turn
/// start instead — a whole round boundary late. Nothing about poison changed; the creature
/// just changed sides. Same class of artefact as the targeting side checks.
///
/// The empty enemy phase still runs every round ("After enemy turn start action" in the
/// combat log), so it remains the correct moment. This moves duelists' poison onto it:
/// suppressed at player-side start, fired at enemy-side start.
///
/// Symmetric by construction — both duelists' poison resolves at the same round boundary,
/// derived only from combat state, so both clients compute it identically and the sim stays
/// in agreement.
///
/// Scoped to poison on purpose. Any other power keying off AfterSideTurnStart has the same
/// latent skew, but they need auditing case by case rather than a blanket side rewrite:
/// some genuinely should fire at the start of your own turn.
/// </summary>
[HarmonyPatch(typeof(PoisonPower), nameof(PoisonPower.AfterSideTurnStart))]
public static class DuelPoisonTimingPatch
{
    public static void Prefix(PoisonPower __instance, ref CombatSide side,
        ref IReadOnlyList<Creature> participants, ICombatState combatState)
    {
        Creature? owner = __instance.Owner;
        if (!DuelSession.IsDuelActive || owner == null || !owner.IsPlayer)
        {
            return;
        }

        if (side == CombatSide.Player)
        {
            // Too early in a duel — drop the owner so vanilla's Contains check fails.
            participants = Array.Empty<Creature>();
            return;
        }

        if (side == CombatSide.Enemy)
        {
            // The moment vanilla would have ticked poison on a monster.
            participants = new List<Creature> { owner };
        }
    }
}
