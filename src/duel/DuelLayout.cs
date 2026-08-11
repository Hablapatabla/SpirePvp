using System.Linq;
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

            // Only do the one-time work for creatures not already on the enemy side. This is
            // re-entrant by design — DuelLateSummonLayoutPatch calls it again for every creature
            // that appears mid-duel — and re-reparenting or replaying the health-bar animation
            // on each call would be visible.
            if (node.GetParent() != room._enemyContainer)
            {
                // Keep the visual transform stable across the reparent; PositionEnemies
                // overwrites it immediately after, but this avoids a one-frame jump.
                node.Reparent(room._enemyContainer, keepGlobalTransform: true);

                // Remote players and their pets start with their bar hidden (co-op shows it on
                // hover only, gated on the same _isRemotePlayerOrPet flag). Bring it up now;
                // DuelHealthBarPatch stops it going away again.
                node._stateDisplay.AnimateIn(HealthBarAnimMode.FromHidden);
                node._stateDisplay.ZIndex = 1;
            }

            // Idempotent: the natural facing is captured once and re-applied, so calling this
            // on an already-mirrored node is a no-op rather than a flip back.
            Mirror(node, mirrored: true);

            moved.Add(node);
        }

        if (moved.Count == 0)
        {
            return;
        }

        float scaling = room._visuals.Encounter.GetCameraScaling();
        room.PositionEnemies(moved, scaling);
        NCombatRoom.PositionPlayersAndPets(remainingAllies, scaling, room._visuals.Encounter.FullyCenterPlayers);
        MirrorSummonsAboutTheirOwner(moved);
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

        if (!_naturalFacing.TryGetValue(node, out float natural))
        {
            natural = body.Scale.X;
            _naturalFacing[node] = natural;
        }

        body.Scale = new Vector2(mirrored ? -natural : natural, body.Scale.Y);
    }

    /// <summary>
    /// Reflects each opponent summon across its owner, so it stands on the same side of them
    /// that yours stands on you.
    ///
    /// Mirroring the opponent is not only about art. On your side a summon is placed *forward*
    /// of its owner — toward the middle of the screen, where the fight is — by the owner-aware
    /// arithmetic in PositionPlayersAndPets. PositionEnemies has no notion of owners at all, so
    /// it lays the opponent's group out in a row and the same offset points the wrong way:
    /// their summon ends up on the far side of them, behind its own necrobinder, while yours is
    /// in front of you. Both screens showed it, symmetrically and identically wrong.
    ///
    /// Reflecting whatever placement PositionEnemies chose, rather than recomputing one,
    /// deliberately keeps the engine's spacing decisions — which respond to creature count and
    /// size — and only corrects their handedness. Centres are reflected rather than origins,
    /// since Position is a corner and owner and summon are rarely the same width.
    ///
    /// Owner-agnostic: Osty, Pael's Legion, Byrdonis and every other PetOwner summon, with none
    /// of them named. Note this deliberately does not reproduce PositionLocalPlayerOsty's fixed
    /// nudge, which is gated on LocalContext.IsMe and would fight the enemy-side layout.
    ///
    /// Safe under the re-entrancy DuelLateSummonLayoutPatch introduces because it always runs
    /// immediately after PositionEnemies, on freshly computed positions — it reflects a fresh
    /// layout each time rather than re-reflecting its own output back.
    /// </summary>
    private static void MirrorSummonsAboutTheirOwner(List<NCreature> moved)
    {
        (float min, float max) before = HorizontalSpan(moved);

        foreach (NCreature summon in moved)
        {
            Creature? entity = summon.Entity;
            Player? owner = entity?.PetOwner;
            if (entity == null || owner == null)
            {
                continue;
            }

            NCreature? ownerNode = moved.FirstOrDefault(n => n.Entity?.Player == owner);
            if (ownerNode == null)
            {
                continue;
            }

            float summonWidth = summon.Visuals?.Bounds.Size.X ?? 0f;
            float ownerWidth = ownerNode.Visuals?.Bounds.Size.X ?? 0f;

            float summonCentre = summon.Position.X + summonWidth * 0.5f;
            float ownerCentre = ownerNode.Position.X + ownerWidth * 0.5f;

            float reflected = 2f * ownerCentre - summonCentre;

            // Handedness was only half of it. Your own summon is also *lifted* relative to you,
            // which is what makes it read as standing behind rather than in front — measured on
            // the host, yours sits at offsetFromOwner=(510.7, -75) while theirs came out at
            // (-313, 0). PositionEnemies lays its row out on one baseline and has no reason to
            // raise anything, so the vertical has to be reapplied.
            //
            // GetOstyOffsetFromPlayer is vanilla's own answer for how far a summon sits from its
            // owner, and it is not a constant: half the owner's hitbox plus an offset lerped
            // between Osty.MinOffset and MaxOffset by the summon's max HP, so a grown summon
            // stands further out. Taking the Y from it means the opponent's summon is raised by
            // exactly what yours is, at whatever size it currently is.
            float lift = NCreature.GetOstyOffsetFromPlayer(entity).Y;

            summon.Position = new Vector2(reflected - summonWidth * 0.5f,
                                          ownerNode.Position.Y + lift);
        }

        // Put the group back where PositionEnemies put it.
        //
        // PositionEnemies chooses the group's placement and spacing from its members' combined
        // width, having laid the summon out to the *right* of its owner. Reflecting the summon
        // to the left moves it without moving the owner, so the group's span slides left by
        // roughly twice the offset while its owner stays put — which drags the opponent away
        // from their side of the arena and toward the middle, looking cramped and too far from
        // the end-turn button. That is what the reflection did on its own, and it is a pure
        // bookkeeping error rather than a judgement about where enemies belong.
        //
        // So measure the span before and after and translate the whole group back onto its
        // original centre. The engine keeps its say over *where* the opponent stands and how far
        // apart they are; this only changes which side of their owner the summon is on.
        (float min, float max) after = HorizontalSpan(moved);
        float shift = (before.min + before.max) * 0.5f - (after.min + after.max) * 0.5f;

        if (shift == 0f)
        {
            return;
        }

        foreach (NCreature node in moved)
        {
            node.Position = new Vector2(node.Position.X + shift, node.Position.Y);
        }
    }

    /// <summary>Leftmost and rightmost screen edge of a group, widths included.</summary>
    private static (float Min, float Max) HorizontalSpan(List<NCreature> nodes)
    {
        float min = float.MaxValue;
        float max = float.MinValue;

        foreach (NCreature node in nodes)
        {
            float width = node.Visuals?.Bounds.Size.X ?? 0f;
            min = Math.Min(min, node.Position.X);
            max = Math.Max(max, node.Position.X + width);
        }

        return nodes.Count == 0 ? (0f, 0f) : (min, max);
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
