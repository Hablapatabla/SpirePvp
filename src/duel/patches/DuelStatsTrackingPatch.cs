using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;

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
            // **Every hit, named, because "it did nothing" is not answerable from a log that never
            // records damage.** Reported 2026-08-18: a Fire Potion queued behind other plays looked
            // like it resolved and did nothing at all. The action log proves the potion *executed*
            // — `began executing`, `using potion FIRE_POTION (targeting ...)`, `finished execution`
            // — and then stops being useful, because the engine logs no damage anywhere and the
            // only surviving number is the result screen's total (`81 dmg` that game). So the
            // question "did those four damage land" had no answer in a 1.5MB log.
            //
            // Above the filters on purpose: the three below are about what belongs on a *result
            // screen*, and a hit that is excluded from the score is exactly the kind this needs to
            // see. A handful of lines per turn in a duel.
            if (DuelSession.IsDuelActive && results != null)
            {
                Log.Info($"[SpirePvp] damage: {dealer?.LogName ?? "(nobody)"} -> "
                         + $"{target?.LogName ?? "(nobody)"} for {results.TotalDamage}");
            }

            // `dealer.Player` deliberately, not the pet-aware test used on the target below: a
            // summon's damage is left uncredited to either player rather than credited to its
            // owner. That is symmetric — it holds on both clients for both duelists — so the
            // comparison stays honest either way, and "damage you dealt" reading as "damage your
            // own creature dealt" is the simpler thing to explain on a result screen.
            if (!DuelSession.IsDuelActive || dealer?.Player == null || results == null)
            {
                return;
            }

            // Damage *to the opponent*, not all damage. Self-damage and damage to your own pets
            // are real events that should not read as offence on a result screen.
            //
            // Asked through DuelLayout.BelongsToOpponent rather than `target.Player`, because a
            // pet's `Player` is null and its owner lives in `PetOwner` — so the plain test called
            // every pet in the arena "not mine" and counted damage to your own summon as offence,
            // which is the exact opposite of what the line above claims. Same trap the layout
            // code already documents.
            if (!LocalContext.IsMe(dealer.Player) || !DuelLayout.BelongsToOpponent(target))
            {
                return;
            }

            DuelStats.RecordDamageDealt(results.TotalDamage);
        }
    }
}
