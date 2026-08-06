using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Counts what the local player did in the duel, for the result screen.
///
/// Nothing in the engine accumulates cards played or damage dealt across a combat in a form
/// readable afterwards — the numbers exist only as they pass through. So they are counted here
/// as they happen.
///
/// **Prefixes, not postfixes, and that is deliberate.** Both targets are `async Task`. A
/// Harmony postfix on an async method runs when the state machine is *created* — at the first
/// await, not on completion — which is a well-known trap in this codebase (`DuelResult` avoids
/// patching `EndCombatInternal` for exactly this reason). For a counter the distinction does not
/// matter as long as it fires once per event, and a prefix fires once at invocation,
/// unambiguously. Neither prefix returns `false`, so no `__result` is involved.
///
/// Both are gated on the duel being active, so the race's own combats are not counted: these
/// are duel statistics, and folding in the Act 1 boss would make "damage dealt" meaningless.
/// </summary>
public static class DuelStatsTrackingPatch
{
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
    public static class CardsPlayed
    {
        public static void Prefix(CardPlay cardPlay)
        {
            if (!DuelSession.IsDuelActive || cardPlay?.Player == null)
            {
                return;
            }

            // Only the local player's. Every client executes every player's actions — that is
            // what a deterministic sim means — so counting without this would count both
            // players' cards on both clients and report each player their own total doubled.
            if (LocalContext.IsMe(cardPlay.Player))
            {
                DuelStats.RecordCardPlayed();
            }
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageGiven))]
    public static class DamageDealt
    {
        public static void Prefix(Creature? dealer, DamageResult results, Creature target)
        {
            if (!DuelSession.IsDuelActive || dealer?.Player == null || results == null)
            {
                return;
            }

            // Damage *to the opponent*, not all damage. Self-damage and damage to your own pets
            // are real events that should not read as offence on a result screen.
            if (!LocalContext.IsMe(dealer.Player) || target == null || LocalContext.IsMe(target.Player))
            {
                return;
            }

            DuelStats.RecordDamageDealt(results.TotalDamage);
        }
    }
}
