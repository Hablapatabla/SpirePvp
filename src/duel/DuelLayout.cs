using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.UI;
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
            FaceLeft(node, faceLeft: true);

            // Remote players start with their bar hidden (co-op shows it on hover only).
            // Bring it up now; DuelHealthBarPatch stops it going away again.
            node._stateDisplay.AnimateIn(HealthBarAnimMode.FromHidden);
            node._stateDisplay.ZIndex = 1;

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
    /// Mirrors a creature's body art horizontally so the duelists face each other. Player
    /// creatures are drawn facing right because they always stand on the left; once moved
    /// across they need flipping or both fighters stare the same way.
    ///
    /// Flips the body node rather than NCreature.Visuals: Visuals.Scale feeds Bounds and the
    /// aspect-ratio fit in AdjustCreatureScaleForAspectRatio, and a negative scale there
    /// would poison that arithmetic. The health bar is a sibling (_stateDisplay), so it is
    /// unaffected either way — text stays readable.
    /// </summary>
    private static void FaceLeft(NCreature node, bool faceLeft)
    {
        Node2D? body = node.Visuals?.GetCurrentBody();
        if (body == null)
        {
            return;
        }

        float magnitude = Math.Abs(body.Scale.X);
        body.Scale = new Vector2(faceLeft ? -magnitude : magnitude, body.Scale.Y);
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

            FaceLeft(node, faceLeft: false);
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
