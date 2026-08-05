using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace SpirePvp.Duel;

/// <summary>
/// Presentation-only: draws the opponent on the enemy side of the screen during a duel.
///
/// This deliberately does NOT touch CombatSide. Moving a player creature onto
/// CombatSide.Enemy would buy targeting and layout for free, but the engine treats
/// "enemy" as "monster" in places that are not guarded — CombatManager.AfterCreatureAdded
/// dereferences `creature.Monster.RollMove(...)` for any enemy-side creature, and Monster is
/// null for players. The intent system and enemy-turn loop carry the same assumption. So the
/// duel keeps both duelists on CombatSide.Player (DESIGN §3.1) and this class moves only the
/// Godot node.
///
/// Each client moves its own opponent, so both players see themselves on the left.
///
/// Runs on duel activation rather than at combat setup because `duel on` happens mid-combat,
/// by which point CreateAllyNodes/CreateEnemyNodes have already parented everything.
/// </summary>
public static class DuelLayout
{
    /// <summary>
    /// Reparents the local player's opponent(s) into the enemy container and re-runs the
    /// vanilla positioning for both sides. Safe to call when there is no combat room yet.
    /// </summary>
    public static void MoveOpponentToEnemySide(ICombatState state)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }

        List<NCreature> moved = new List<NCreature>();
        List<NCreature> remainingAllies = new List<NCreature>();

        foreach (Creature creature in state.PlayerCreatures)
        {
            NCreature? node = room.GetCreatureNode(creature);
            if (node == null)
            {
                continue;
            }

            if (LocalContext.IsMe(creature.Player))
            {
                remainingAllies.Add(node);
                continue;
            }

            // Keep the visual transform stable across the reparent; PositionEnemies
            // overwrites it immediately after, but this avoids a one-frame jump.
            node.Reparent(room._enemyContainer, keepGlobalTransform: true);
            moved.Add(node);
        }

        if (moved.Count == 0)
        {
            return;
        }

        float scaling = room._visuals.Encounter.GetCameraScaling();
        room.PositionEnemies(moved, scaling);
        NCombatRoom.PositionPlayersAndPets(remainingAllies, scaling, room._visuals.Encounter.FullyCenterPlayers);
        room.UpdateCreatureNavigation();

        Log.Warn($"[SpirePvp] duel layout: moved {moved.Count} opponent creature(s) to the enemy side");
    }

    /// <summary>
    /// Puts every player creature back in the ally container, so `duel off` really does
    /// restore normal presentation instead of stranding the opponent on the right.
    /// </summary>
    public static void RestoreAllySide(ICombatState state)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }

        List<NCreature> allies = new List<NCreature>();
        foreach (Creature creature in state.PlayerCreatures)
        {
            NCreature? node = room.GetCreatureNode(creature);
            if (node == null)
            {
                continue;
            }

            if (node.GetParent() != room._allyContainer)
            {
                node.Reparent(room._allyContainer, keepGlobalTransform: true);
            }

            allies.Add(node);
        }

        if (allies.Count == 0)
        {
            return;
        }

        NCombatRoom.PositionPlayersAndPets(allies, room._visuals.Encounter.GetCameraScaling(),
            room._visuals.Encounter.FullyCenterPlayers);
        room.UpdateCreatureNavigation();
    }
}
