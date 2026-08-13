using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Telemetry only, added 2026-08-12: names the members of the AoE family as they actually turn up
/// in play, instead of guessing which of the 70 models that read `HittableEnemies` matter.
///
/// **It reads and never writes**, which is the whole point. `CombatState.HittableEnemies` is
/// `Enemies.Where(e =&gt; e.IsHittable)` and a duel has an empty enemy side, so every "all enemies"
/// effect resolves against nothing — reported as Bag of Marbles applying no Vulnerable, and open in
/// `DuelTargetingPatch`'s notes since M2. Making this getter *return* something is the fix people
/// reach for and is the one thing that must not happen here: the property is handed no attacker, so
/// any answer it invented would be a local guess inside sim code, i.e. a desync. `GetOpponentsOf`
/// is the patchable chokepoint precisely because it is told who is asking.
///
/// The report is keyed by the sim's currently running action and printed once per distinct key, so
/// a card played ten times costs one line. Watch for reports carrying `&lt;no running action&gt;`:
/// those are hook-time effects like Bag of Marbles' `BeforeSideTurnStart`, and they are the reason
/// "resolve the actor from `CurrentlyRunningAction`" cannot be the whole fix.
/// </summary>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.HittableEnemies), MethodType.Getter)]
public static class DuelAoeProbePatch
{
    public static void Postfix(IReadOnlyList<Creature> __result)
    {
        if (__result.Count == 0)
        {
            DuelTelemetry.NoteEmptyAoe();
        }
    }
}
