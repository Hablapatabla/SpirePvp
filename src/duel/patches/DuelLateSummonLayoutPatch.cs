using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Sorts creatures that arrive *after* the duel has already been laid out.
///
/// DuelLayout.MoveOpponentToEnemySide runs once, on duel activation, and can only sort the
/// creatures that exist at that instant. Summons do not: Bound Phylactery spawns Osty from
/// BeforeCombatStart and again from AfterEnergyResetLate, and cards like Legion of Bone and
/// Byrdonis Egg summon mid-combat whenever they are played. Every one of those arrived after
/// the sort and stayed wherever it spawned — on the *player* side, un-mirrored, laid out among
/// your own creatures.
///
/// This was found by measurement rather than by looking, and the logs said it outright:
///
///     HOST:   moved 1 opponent creature(s) to the enemy side, 2 stayed on yours
///     CLIENT: moved 2 opponent creature(s) to the enemy side, 1 stayed on yours
///
/// Same code, same duel, different counts — because the two clients reached duel activation
/// with different creatures summoned. That asymmetry is what made it look like a facing bug
/// that struck one side and not the other: the arithmetic was identical and correct on both
/// (`natural=0.27 → applied=-0.27`), and the opponent's Osty on the host had simply never been
/// moved or mirrored at all. A layout bug wearing a rendering bug's clothes.
///
/// CombatManager.AfterCreatureAdded is the right seam because of what its own comment
/// guarantees: "Called after both the Creature has been added to the room _and_ the NCreature
/// is spawned." Sorting any earlier would find no node to move.
///
/// Re-running the whole sort rather than moving the one new node keeps a single code path for
/// placement, and re-running PositionEnemies is required anyway — a new arrival changes the
/// spacing of everything already on that side. MoveOpponentToEnemySide is re-entrant for
/// exactly this caller: the reparent and the health-bar reveal are guarded on the node not
/// already being there, and the mirror is idempotent through its captured natural facing.
///
/// The one-argument overload is the public entry point; the two-argument one is a private
/// static taking an explicit CombatState, so the argument types are specified to disambiguate.
/// It returns Task but is not itself async — it just hands back the inner call's task — so a
/// postfix here runs at the normal time rather than at state-machine construction.
/// </summary>
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.AfterCreatureAdded), typeof(Creature))]
public static class DuelLateSummonLayoutPatch
{
    public static void Postfix(Creature creature)
    {
        if (!DuelSession.IsDuelActive || !DuelLayout.BelongsToOpponent(creature))
        {
            return;
        }

        ICombatState? state = creature.CombatState;
        if (state == null)
        {
            return;
        }

        DuelLayout.MoveOpponentToEnemySide(state);
    }
}
