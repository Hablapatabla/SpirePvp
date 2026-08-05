using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// M1 (DESIGN §3.1 group 2, I1): duel win condition.
///
/// A duel combat has an empty enemy side, so vanilla concludes "victory" the instant it
/// starts. Overriding that needs no patch on the win check itself: CombatManager's
/// IsCombatEnding (v0.110.1, ~line 395) offers every hook listener a veto via
/// Hook.ShouldStopCombatFromEnding before it concludes "no primary enemies alive ⇒ over".
/// We postfix that hook and vote "keep going" while both duelists are standing.
///
/// Ending the duel is then the vanilla path for free: once a duelist dies the veto drops,
/// IsCombatEnding goes true, and the turn loop's CheckWinCondition closes combat out.
///
/// I1 (win-condition half) RESOLVED against v0.110.1. The hook aggregates
/// AbstractModel.ShouldStopCombatFromEnding over CombatState.IterateHookListeners() —
/// powers, relics, monsters. An invisible PowerModel on each duelist would be the more
/// native mechanism, but registering a custom model pulls in BaseLib; this postfix needs
/// no dependency and states the intent in one place.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldStopCombatFromEnding))]
public static class DuelWinConditionPatch
{
    public static void Postfix(ICombatState combatState, ref bool __result)
    {
        // Someone else already voted to keep combat alive, or we aren't duelling.
        if (__result || !DuelSession.IsDuelActive)
        {
            return;
        }

        int standing = 0;
        foreach (Creature creature in combatState.PlayerCreatures)
        {
            if (creature.IsAlive)
            {
                standing++;
            }
        }

        // Both duelists up: the duel is still on, whatever the empty enemy side implies.
        __result = standing > 1;
    }
}
