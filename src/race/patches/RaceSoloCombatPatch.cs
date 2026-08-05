using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// M5 spike, blocker 2 of 2 — and the one I3's research missed entirely.
///
/// CombatRoom.EnterInternal enrolls the whole party into whatever combat you enter:
///
///     if (CombatState.Players.Count == 0)
///         foreach (Player item in runState.Players) CombatState.AddPlayer(item);
///
/// In a race that is fatal, and it fails in the worst way — silently, as a hang. Your solo
/// combat would contain the absent opponent's creature, and the player phase ends only when
/// every player in CombatState.Players has readied (AllPlayersReadyToEndTurn). The opponent
/// is off in their own room and will never ready, so your first turn never ends. Same class
/// of bug as the dead-duelist stall in M2, from a different direction.
///
/// The guard the vanilla code uses is also the hook: it only enrols the party when the
/// combat has no players yet. So a prefix that adds just the local player leaves
/// Players.Count == 1, the original's condition is false, and it skips its own loop. No
/// transpiler, no reimplementation of EnterInternal — we simply get there first.
///
/// Patching the outer async method is correct here: the prefix runs before the state machine
/// starts, which is well before anything reads CombatState.Players.
///
/// Note this is race-only. The duel deliberately wants both players in one combat.
/// </summary>
[HarmonyPatch(typeof(CombatRoom), "EnterInternal")]
public static class RaceSoloCombatPatch
{
    public static void Prefix(CombatRoom __instance, IRunState? runState)
    {
        if (!DuelSession.IsRaceActive || runState == null)
        {
            return;
        }

        if (__instance.CombatState.Players.Count != 0)
        {
            return;
        }

        Player? me = LocalContext.GetMe(runState.Players);
        if (me == null)
        {
            Log.Error("[SpirePvp] race: no local player found; leaving party enrolment to vanilla");
            return;
        }

        __instance.CombatState.AddPlayer(me);
        Log.Info($"[SpirePvp] race: solo combat for player {me.NetId} (party enrolment skipped)");

        // Re-assert the opponent's hook deactivation right before hooks fire.
        //
        // Deactivating once at run launch is not enough: something between launch and room
        // entry sets IsActiveForHooks back to true — the guard in RaceStarsWithoutCombatPatch
        // caught the opponent at "activeForHooks=True" long after we cleared it. The likely
        // culprit is SyncWithSerializedPlayer, which assigns IsActiveForHooks = Creature.IsAlive
        // whenever a player is synced from serialized state.
        //
        // Rather than chase every place that resets it, re-apply here, which is the last point
        // before CombatRoom.StartCombat runs Hook.AfterRoomEntered over every active player's
        // relics. Cheap and idempotent.
        RaceCoordinator.DeactivateRemotePlayerHooks(runState as RunState);
    }
}
