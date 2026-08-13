using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// The AoE family: "all enemies" effects in a duel, open since M2 and reported as **Bag of Marbles
/// applies no Vulnerable**.
///
/// `CombatState.HittableEnemies` is `Enemies.Where(IsHittable)` and a duel has an empty enemy side,
/// so every effect that reads it hits nothing. This postfix answers it with the *asking duelist's*
/// opponents, resolved by `DuelAoeActor` from state the simulation itself defines — read that
/// class before changing anything here; the two tiers and the reason neither alone is sufficient
/// are documented there rather than repeated.
///
/// # What this deliberately does not do
///
/// **It does not invent an answer.** When `DuelAoeActor` cannot name an actor, vanilla's empty list
/// stands and the read is reported once. That is the same behaviour the mod has today, so an
/// unresolved case is a *gap*, not a new bug — which is what makes shipping this without a playtest
/// defensible at all.
///
/// **It answers through `GetOpponentsOf`, not with a set of its own.** The two must agree: a card's
/// damage already travels through `AttackCommand.TargetingAllOpponents` → `GetOpponentsOf` →
/// `DuelOpponentsPatch`, while its rider reads `HittableEnemies`. Thunderclap is the worked example
/// — it damages through the first and applies Vulnerable through the second — and a second,
/// slightly different target set here would have it damage the duelist and Vulnerable the duelist
/// *and their pets*. Deferring also inherits, rather than silently re-deciding, the open design
/// question of whether the opponent's pets are attackable at all (HANDOFF: "Should the opponent's
/// pet be attackable?"). If that is ever decided, it is decided in one place.
///
/// **The `IsHittable` filter is vanilla's own**, kept because the property's contract is hittable
/// enemies and callers rely on it: `GlassOrb` filters again on top, `Shiv` takes the last of them.
///
/// # Scope of the change
///
/// Gated on `DuelSession.IsDuelActive`, so the race — where the enemy side is full of real monsters
/// and this whole problem does not exist — is untouched. Inside a duel the enemy side is empty by
/// construction, so this postfix can only ever *add* creatures where vanilla returned none; it can
/// never drop or reorder a target the engine chose.
///
/// # What it fixes, from a static read of the decompile rather than from play
///
/// 70 read sites across cards, powers, relics, potions, orbs and enchantments. Two families are
/// worth knowing apart because only one of them was ever broken:
///
/// - **Damage from "all enemies" cards was already working.** `DaggerSpray`, `Cleave` and the rest
///   deal their damage through `TargetingAllOpponents`, which `DuelOpponentsPatch` has covered
///   since M2. What was missing on those cards was only their *rider* — DaggerSpray's impact VFX,
///   Thunderclap's Vulnerable.
/// - **Everything that reads the property directly was fully broken**: Bag of Marbles' Vulnerable,
///   Noxious Fumes' poison, The Bomb, Inferno, Letter Opener, Charon's Ashes, Stomp, Shockwave,
///   Piercing Wail, Outbreak, Misery, and the random-target picks (`Rng.CombatTargets.NextItem`)
///   behind Beat Down, Bouncing Flask, Tingsha, Parrying Shield and Whispering Earring.
///
/// **None of this has been played.** It compiles, and the reasoning above is a reading of the
/// decompile — which is the whole of the evidence, because Lucas's logs contain no
/// `HittableEnemies came back EMPTY` telemetry at all: the probe shipped on 2026-08-12 and the one
/// duel played on the build that carries it used no AoE effect.
/// </summary>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.HittableEnemies), MethodType.Getter)]
public static class DuelAoeTargetingPatch
{
    public static void Postfix(CombatState __instance, ref IReadOnlyList<Creature> __result)
    {
        // Vanilla found something to hit — in a duel it never does, but a monster in the arena
        // would be the engine's answer and not ours to replace.
        if (!DuelSession.IsDuelActive || __result.Count > 0)
        {
            return;
        }

        Creature? actor = DuelAoeActor.Resolve(__instance);
        if (actor == null)
        {
            DuelTelemetry.NoteUnresolvedAoe();
            return;
        }

        List<Creature> hittable = new List<Creature>(1);
        foreach (Creature opponent in __instance.GetOpponentsOf(actor))
        {
            if (opponent.IsHittable)
            {
                hittable.Add(opponent);
            }
        }

        __result = hittable;
    }
}

/// <summary>
/// Makes the model currently being handed a combat hook ambient, which is what lets
/// `DuelAoeTargetingPatch` answer a hook-time "all enemies" read correctly.
///
/// **This is the tier that fixes Bag of Marbles**, and it exists because that relic applies from
/// `BeforeSideTurnStart` — outside any action, so there is no running action to resolve an actor
/// from, and often *another duelist's* action when there is one.
///
/// `CombatState.IterateHookListeners` builds a `List` and returns it, so wrapping the result in an
/// ordinary iterator is enough; nothing here patches a compiler-generated state machine. It is the
/// single funnel every combat hook dispatch goes through — `Hook.cs` has 74 dispatch loops and all
/// of them enumerate it, directly or via `Hook.IterateCombatHookListeners` — so one patch covers
/// every hook without naming any of them, and a game update that adds a hook is covered too.
///
/// It is enumerated by a few non-hook callers as well (`CardCostHelper`, `RunState`). That is
/// harmless: the ambient changes nothing except the answer to an "all enemies" read, and those
/// callers make none.
///
/// The wrapping itself is in `DuelAoeActor.ScopeEach`, with the save/restore reasoning.
/// </summary>
[HarmonyPatch(typeof(CombatState), nameof(CombatState.IterateHookListeners))]
public static class DuelHookListenerScopePatch
{
    public static void Postfix(ref IEnumerable<AbstractModel> __result)
    {
        if (!DuelSession.IsDuelActive)
        {
            return;
        }

        __result = DuelAoeActor.ScopeEach(__result);
    }
}
