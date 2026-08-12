using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// **A PvP result screen presents as an ordinary solo death: your character, alone.** Decided
/// 2026-08-12; the opponent is kept out of it on every ending, including a duel you just lost to
/// them in the arena.
///
/// The reported bug was narrower — a *draw* drew both duelists spawning and dying, side by side,
/// on a result nobody died in — and an earlier pass fixed only that, on the reasoning that the
/// arena is the one screen where two figures is the truth. That reasoning was overruled by play:
/// the result screen is the run's epitaph, not the duel's group photo, and the opponent standing
/// in it reads as a co-op wipe whichever way the match went.
///
/// `NGameOverScreen.MoveCreaturesToDifferentLayerAndDisableUi` assembles that tableau, and it
/// needs telling twice because it builds from two different sources:
///
/// **The player list**, in the rest-site branch and in the `else` branch — no room instance, i.e.
/// the map screen — which creates a visual per player, plays `die` on each and spreads them
/// across the screen. This is where the draw bug lived, because a race-clock expiry reaches the
/// result screen with no combat room at all. Hidden by shortening the run's player list for the
/// duration of the call, which lets vanilla take its own singleplayer path rather than having us
/// reimplement the layout — `RaceSolo`'s standing rule, and here it also re-centres the survivor
/// for free, since the branch computes its spacing from the list length.
///
/// **The combat room's creature nodes**, in the remaining branch, which reparents every one of
/// them above the game-over backstop. The player list does not reach this branch at all, so the
/// opponent's visuals are hidden directly instead. Their *pets* go with them —
/// `DuelLayout.BelongsToOpponent` resolves `Player ?? PetOwner`, which is the same test that
/// decides who is drawn on the enemy side during the duel, so a summon cannot be left standing
/// on a screen its owner has been removed from.
///
/// Hiding rather than reparenting back: only `NCreature.Visuals` is moved to the game-over layer,
/// so making that node invisible removes the opponent from the screen completely, and everything
/// else about them is already behind the backstop. The run is over, so nothing shows the combat
/// room again.
///
/// **Why masking the player list is safe here, when `DuelNeowOptionsPatch` shows how badly a mask
/// can go wrong.** That patch's trouble was never masking; it was that its *guard* asked a
/// question the mask itself answered, so a mask already in place made the next caller invisible
/// to it. This guard asks `IsPvpRun`, which reads the run's modifiers, and the mask covers the
/// player list — two unrelated pieces of state, with no circularity to fall into. The method is
/// synchronous with no awaits, so nothing else can observe the list while it is short.
///
/// The restore is a **finalizer** rather than a postfix, because a postfix does not run when the
/// original throws — and leaving a run permanently one player short would be far worse than the
/// cosmetic bug being fixed. `_players` is restored by content rather than by removing and
/// re-adding, so slot indices (`GetPlayerSlotIndex` is `Players.IndexOf`) come back identical.
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen),
              nameof(NGameOverScreen.MoveCreaturesToDifferentLayerAndDisableUi))]
public static class DuelSoloGameOverPatch
{
    /// <summary>The character's idle animation, confirmed against `NUnlockCharacterScreen`, which
    /// drives a standalone `CreateVisuals()` node exactly as the game-over screen does.</summary>
    private const string IdleAnimation = "idle_loop";

    /// <summary>
    /// Where a lone survivor stands. Copied from the `else` branch's own arithmetic for a
    /// one-creature list, so a duel ending lands the character in the same place a race ending
    /// already does rather than somewhere merely similar.
    /// </summary>
    private static Vector2 CentreOf(Control container) =>
        container.Size * 0.5f + new Vector2(0f, 200f);

    /// <summary>What the prefix has to undo, and what the postfix has to fix up.</summary>
    public sealed class State
    {
        public List<Player>? SavedPlayers;
        public List<NCreatureVisuals> OpponentVisuals = new();

        /// <summary>The local player's creatures already in the room — combat branch only.</summary>
        public List<NCreatureVisuals> OwnVisuals = new();

        /// <summary>The local *player's* own creature, the anchor the group is centred on.</summary>
        public NCreatureVisuals? OwnPlayerVisual;

        /// <summary>Container children before the call, so the freshly spawned ones can be told apart.</summary>
        public HashSet<Node> PreExistingChildren = new();
    }

    public static void Prefix(NGameOverScreen __instance, out State? __state)
    {
        __state = null;

        RunState? runState = __instance._runState;
        if (runState == null || !DuelMatch.IsPvpRun(runState))
        {
            return;
        }

        __state = new State();

        foreach (Node child in __instance._creatureContainer.GetChildren())
        {
            __state.PreExistingChildren.Add(child);
        }

        // The combat branch. Captured before the call because afterwards these nodes have been
        // reparented and there is nothing left tying a visual back to the creature it came from —
        // `NCreatureVisuals` has no back-reference to its `Creature`.
        NCombatRoom? room = NCombatRoom.Instance;
        if (room != null)
        {
            foreach (NCreature node in room.CreatureNodes)
            {
                if (DuelLayout.BelongsToOpponent(node.Entity))
                {
                    __state.OpponentVisuals.Add(node.Visuals);
                    continue;
                }

                __state.OwnVisuals.Add(node.Visuals);

                // Pets are on your side too, so the anchor has to be the player specifically —
                // centring on whichever of yours came first would put a summon in the spotlight.
                if (node.Entity.Player != null)
                {
                    __state.OwnPlayerVisual = node.Visuals;
                }
            }
        }

        // The player-list branches.
        Player? local = runState.Players.FirstOrDefault(LocalContext.IsMe);

        // No local player is not a situation to improvise in — leaving the list alone means
        // vanilla's own behaviour, which is the safe direction to fail in for a cosmetic patch.
        if (local != null && runState.Players.Count > 1)
        {
            __state.SavedPlayers = new List<Player>(runState._players);
            runState._players.Clear();
            runState._players.Add(local);
        }

        Log.Warn("[SpirePvp] result screen: presenting solo — "
                 + $"{__state.SavedPlayers?.Count ?? 1} run player(s) masked to 1, "
                 + $"{__state.OpponentVisuals.Count} opponent creature(s) hidden");
    }

    public static void Postfix(NGameOverScreen __instance, State? __state)
    {
        if (__state == null)
        {
            return;
        }

        foreach (NCreatureVisuals visuals in __state.OpponentVisuals)
        {
            if (visuals.IsValid())
            {
                visuals.Visible = false;
            }
        }

        CentreSurvivor(__instance, __state);
        StandIfVictorious(__instance, __state);
    }

    /// <summary>
    /// Puts a duel's survivor where a race's survivor already stands.
    ///
    /// The combat branch does no layout at all — it reparents the existing creature nodes and
    /// they keep their arena positions, which is why a duel ended with the character stranded on
    /// the **left**: that is the player side of the combat screen, and it only looked centred
    /// while an opponent was standing opposite. Once the opponent is hidden, one figure sitting
    /// off to one side is all that is left of the composition.
    ///
    /// The *player* is what gets centred and the rest of your side moves by the same delta, so
    /// pets keep their formation relative to you instead of scattering.
    /// </summary>
    private static void CentreSurvivor(NGameOverScreen screen, State state)
    {
        NCreatureVisuals? anchor = state.OwnPlayerVisual;
        if (anchor == null || !anchor.IsValid())
        {
            return;
        }

        Vector2 delta = CentreOf(screen._creatureContainer) - anchor.Position;

        foreach (NCreatureVisuals visuals in state.OwnVisuals)
        {
            if (visuals.IsValid())
            {
                visuals.Position += delta;
            }
        }
    }

    /// <summary>
    /// A winner should not be watching themselves die.
    ///
    /// The non-combat branches play `die` on every creature they spawn, because vanilla only ever
    /// reaches this screen when the run is over and the run being over means the party is dead.
    /// A PvP match breaks that: **you can arrive here having won** — the opponent resigned, or
    /// their race clock ran out — and the screen dutifully killed the victor.
    ///
    /// Only the spawned visuals are touched, because the combat branch must keep whatever combat
    /// left behind: a duel win already shows you standing over a body, and forcing an idle there
    /// would restart the animation of someone who is simply still alive.
    ///
    /// **Diffing the container's children is not enough to tell those apart**, which the log
    /// caught before anyone saw it: the combat branch *reparents* its creatures into the same
    /// container, so they arrive as new children too, and a duel win logged this twice while
    /// nothing had been spawned at all. The creatures already in the room are known from the
    /// prefix, so they are excluded by identity rather than inferred from where they ended up.
    /// </summary>
    private static void StandIfVictorious(NGameOverScreen screen, State state)
    {
        if (DuelSession.Outcome != DuelOutcome.Won)
        {
            return;
        }

        foreach (Node child in screen._creatureContainer.GetChildren())
        {
            if (state.PreExistingChildren.Contains(child) || child is not NCreatureVisuals visuals)
            {
                continue;
            }

            if (state.OwnVisuals.Contains(visuals) || state.OpponentVisuals.Contains(visuals))
            {
                continue;
            }

            visuals.SpineAnimation.SetAnimation(IdleAnimation);
            Log.Warn("[SpirePvp] result screen: victor stands rather than dies");
        }
    }

    public static void Finalizer(NGameOverScreen __instance, State? __state)
    {
        if (__state?.SavedPlayers == null)
        {
            return;
        }

        List<Player> players = __instance._runState._players;
        players.Clear();
        players.AddRange(__state.SavedPlayers);
    }
}
