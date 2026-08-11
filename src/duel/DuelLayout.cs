using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
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
    /// Which side of the screen a creature belongs on.
    ///
    /// Pets are the reason this is not simply `creature.Player`. A summon — the Necrobinder's
    /// Osty, say — is a creature in its own right with a **null `Player`** and its owner in
    /// `PetOwner`, so asking only about players quietly left the opponent's summon standing on
    /// your side of the arena, fighting for the wrong team as far as the screen was concerned.
    /// </summary>
    public static bool BelongsToOpponent(Creature? creature)
    {
        Player? owner = creature?.Player ?? creature?.PetOwner;
        return owner != null && !LocalContext.IsMe(owner);
    }

    /// <summary>
    /// Reparents the local player's opponent(s) into the enemy container and re-runs the
    /// vanilla positioning for both sides. Safe to call when there is no combat room yet.
    ///
    /// Walks `Allies` rather than `PlayerCreatures`: everything on the player side has to be
    /// sorted onto one screen half or the other, and pets are on that side without being
    /// players.
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

        foreach (Creature creature in state.Allies)
        {
            NCreature? node = room.GetCreatureNode(creature);
            if (node == null)
            {
                continue;
            }

            if (!BelongsToOpponent(creature))
            {
                remainingAllies.Add(node);
                continue;
            }

            // Keep the visual transform stable across the reparent; PositionEnemies
            // overwrites it immediately after, but this avoids a one-frame jump.
            node.Reparent(room._enemyContainer, keepGlobalTransform: true);
            Mirror(node, mirrored: true);

            // Remote players and their pets start with their bar hidden (co-op shows it on
            // hover only, gated on the same _isRemotePlayerOrPet flag). Bring it up now;
            // DuelHealthBarPatch stops it going away again.
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

        Log.Warn($"[SpirePvp] duel layout: moved {moved.Count} opponent creature(s) to the enemy side, " +
                 $"{remainingAllies.Count} stayed on yours");
    }

    /// <summary>
    /// Each moved node's authored horizontal facing, captured the first time we touch it.
    ///
    /// Static state outliving a run is the trap DuelRunCleanupPatch exists for, so this is
    /// released in DuelMatch.OnRunEnded via <see cref="Reset"/>. Nodes do not survive a combat
    /// either, so holding them across one would be a leak as well as a correctness problem.
    /// </summary>
    private static readonly Dictionary<NCreature, float> _naturalFacing = new();

    /// <summary>Drops the captured facings. See DuelMatch.OnRunEnded.</summary>
    public static void Reset()
    {
        _naturalFacing.Clear();
    }

    /// <summary>
    /// Mirrors a creature's body art horizontally so the duelists face each other.
    ///
    /// **Mirrors relative to the creature's own authored facing rather than forcing a sign.**
    /// The original wrote `scale.X = faceLeft ? -|x| : |x|`, which encodes the assumption that
    /// positive means "facing right". That holds for player creatures — the comment said as
    /// much — but not for summons, so the opponent's Osty came out backwards while its owner
    /// mirrored correctly: forcing a sign either did nothing or flipped it the wrong way,
    /// depending on how that particular art was authored.
    ///
    /// Capturing the natural value and negating *that* is correct for any convention, and it
    /// stays idempotent, which the absolute version was getting for free and which `duel on` /
    /// `duel off` rely on to be re-runnable.
    ///
    /// Nothing here is per-creature: it works for Osty, Pael's Legion, Byrdonis and every other
    /// PetOwner summon without naming any of them.
    ///
    /// Flips the body node rather than NCreature.Visuals: Visuals.Scale feeds Bounds and the
    /// aspect-ratio fit in AdjustCreatureScaleForAspectRatio, and a negative scale there
    /// would poison that arithmetic. The health bar is a sibling (_stateDisplay), so it is
    /// unaffected either way — text stays readable.
    /// </summary>
    private static void Mirror(NCreature node, bool mirrored)
    {
        Node2D? body = node.Visuals?.GetCurrentBody();
        if (body == null)
        {
            return;
        }

        bool captured = _naturalFacing.TryGetValue(node, out float natural);
        if (!captured)
        {
            natural = body.Scale.X;
            _naturalFacing[node] = natural;
        }

        body.Scale = new Vector2(mirrored ? -natural : natural, body.Scale.Y);

        // Diagnostic, and the reason it exists: facing came out correct on one client and
        // backwards on the other from this identical code, which means the two arrive here with
        // different scale state rather than the arithmetic being wrong. Print what each side
        // actually sees so the two logs can be diffed, instead of guessing at pixels a second
        // time. Remove once the asymmetry is understood.
        Log.Warn($"[SpirePvp] facing: {node.Entity?.Name} " +
                 $"pet={node.Entity?.PetOwner != null} " +
                 $"natural={natural:0.###}{(captured ? " (cached)" : " (captured now)")} " +
                 $"applied={body.Scale.X:0.###} pos={node.Position}");
    }


    /// <summary>
    /// Puts every player-side creature back in the ally container, so `duel off` really does
    /// restore normal presentation instead of stranding the opponent on the right.
    ///
    /// `Allies`, to match what MoveOpponentToEnemySide moved — otherwise an opponent's pet is
    /// left behind on the enemy side with nothing to bring it home.
    /// </summary>
    public static void RestoreAllySide(ICombatState state)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }

        List<NCreature> allies = new List<NCreature>();
        foreach (Creature creature in state.Allies)
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

            Mirror(node, mirrored: false);
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
