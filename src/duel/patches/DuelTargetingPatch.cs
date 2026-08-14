using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

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
/// AOE and random targeting were open here from M1 until 2026-08-13 (unplayed). This note used to
/// read "STILL OPEN … it cannot be retargeted from the getter alone: it has no way to know whose
/// opponent to return", and that was true of the getter *by itself* — but the missing context does
/// not have to come from the property. `DuelAoeActor` supplies an actor the simulation defines
/// (the model being handed a hook, else the running action's owner), which is identical on both
/// clients, and `DuelAoeTargetingPatch` answers the getter from it. The plan sketched here — patch
/// the call sites that hold `Owner` — was reconsidered and rejected: there are 70 of them, a third
/// iterate the list themselves rather than passing it to a command, and a fix that covered only the
/// ones reachable through `PowerCmd`/`CreatureCmd` would have looked complete while leaving Stomp,
/// Outbreak and Misery silently dead.
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.IsValidTarget))]
public static class DuelTargetingPatch
{
    public static void Postfix(CardModel __instance, Creature? target, ref bool __result)
    {
        if (!DuelSession.IsDuelActive)
        {
            return;
        }

        // **Narrowing comes first, because it is the case where vanilla says yes.** Every patch in
        // this file used to open with `if (__result) return;` — they only ever widened, admitting the
        // opponent for `AnyEnemy`. The mirror image went unnoticed until 2026-08-13: a *friendly*
        // target type in a duel resolves to the opponent, because they are a player on your own side.
        if (NarrowedFriendlyTarget(__instance.TargetType, __instance.Owner, target) is bool narrowed)
        {
            __result = narrowed;
            return;
        }

        // Vanilla already allowed it.
        if (__result || __instance.TargetType != TargetType.AnyEnemy)
        {
            return;
        }

        __result = IsOpponentOf(__instance.Owner, target);
    }

    /// <summary>
    /// The corrected answer for a target type that means "a friend", or null when the type means
    /// something else and the caller should carry on.
    ///
    /// **A duel has no allies, and the engine has no way to know that.** Reported 2026-08-13: *"I was
    /// able to use skill pot on the wrong person."* `PotionModel.IsValidTarget` answers
    /// `TargetType.AnyPlayer` with a bare `return target.IsPlayer` — right in co-op, where handing a
    /// teammate a Skill Potion is the entire point of the target type, and exactly wrong here, where
    /// the other player is who you are trying to beat. The same reading of the same fact that makes
    /// `Players.Count > 1` mean "co-op" throughout the engine (HANDOFF calls this the content-level
    /// twin of the co-located-party assumption).
    ///
    /// - `AnyPlayer` — "any player, including yourself" — becomes **yourself, only**.
    /// - `AnyAlly` — "any player *excluding* yourself" — has no valid target at all in a duel, which
    ///   is the honest answer rather than a convenient one. The effect is unplayable, exactly as
    ///   vanilla says of this type in singleplayer: "You should not see this."
    ///
    /// `AllAllies` is deliberately absent: it performs no target selection, so this is never asked
    /// about it. It resolves through `CombatState`, and belongs with the AoE family in
    /// `DuelAoeActor` rather than here.
    /// </summary>
    internal static bool? NarrowedFriendlyTarget(TargetType type, Player? owner, Creature? target)
    {
        switch (type)
        {
            case TargetType.AnyPlayer:
                return owner != null && target != null && target == owner.Creature;

            case TargetType.AnyAlly:
                return false;

            default:
                return null;
        }
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

/// <summary>
/// Same rule for potions. PotionModel.IsValidTarget is a separate method from CardModel's —
/// the game explicitly warns against unifying them, because potions pass a target for
/// TargetType.Self and cards do not — but the AnyEnemy branch is the identical
/// `target.Side != Owner.Creature.Side` check, and fails for the same reason.
/// </summary>
[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.IsValidTarget))]
public static class DuelPotionTargetingPatch
{
    public static void Postfix(PotionModel __instance, Creature? target, ref bool __result)
    {
        if (!DuelSession.IsDuelActive)
        {
            return;
        }

        // The half that matters most for potions: `AnyPlayer` is how a Skill Potion is handed to a
        // teammate in co-op, and it let one be handed to the opponent here.
        if (DuelTargetingPatch.NarrowedFriendlyTarget(__instance.TargetType, __instance.Owner, target)
            is bool narrowed)
        {
            __result = narrowed;
            return;
        }

        if (__result || __instance.TargetType != TargetType.AnyEnemy)
        {
            return;
        }

        __result = DuelTargetingPatch.IsOpponentOf(__instance.Owner, target);
    }
}

/// <summary>
/// The second half of duel retargeting, and the one that actually blocks play.
///
/// Targeting is validated twice, independently. CardModel.IsValidTarget governs the rules
/// and the synchronised action — but the mouse never gets that far: NMouseCardPlay resolves
/// its target from NTargetManager's hover signals, and NTargetManager.AllowedToTargetCreature
/// requires `creature.Side == CombatSide.Enemy` for TargetType.AnyEnemy. A player-side
/// creature is refused as a hover candidate, so _target stays null and TryPlayCard cancels
/// the play. Symptom: the card simply will not go anywhere.
///
/// So the rules patch alone is not enough; the UI needs the same permission.
/// </summary>
[HarmonyPatch(typeof(NTargetManager), nameof(NTargetManager.AllowedToTargetCreature))]
public static class DuelHoverTargetingPatch
{
    public static void Postfix(NTargetManager __instance, Creature creature, ref bool __result)
    {
        if (!DuelSession.IsDuelActive)
        {
            return;
        }

        // The UI needs the narrowing too, and for the reason this file already documents about
        // widening: targeting is validated more than once, independently, and fixing one is how you
        // conclude wrongly that nothing happened. Here it is the hover that lets you *pick* the
        // opponent for a friendly effect in the first place.
        if (DuelTargetingPatch.NarrowedFriendlyTarget(
                __instance._validTargetsType, LocalContext.GetMe(RunManager.Instance?.State?.Players
                    ?? (IEnumerable<Player>)Array.Empty<Player>()), creature) is bool narrowed)
        {
            __result = narrowed;
            return;
        }

        if (__result)
        {
            return;
        }

        // Reachable via Krafs.Publicizer (see csproj) — no reflection needed.
        if (__instance._validTargetsType != TargetType.AnyEnemy)
        {
            return;
        }

        // The opponent is a live player creature that isn't us. Self-targeting stays illegal,
        // matching how vanilla AnyAlly excludes the local player.
        __result = creature.IsPlayer && !creature.IsDead && !LocalContext.IsMe(creature.Player);
    }
}

/// <summary>Kept for AOE work in M2 (see DuelTargetingPatch's note on HittableEnemies).</summary>
internal static class DuelTargetSets
{
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
