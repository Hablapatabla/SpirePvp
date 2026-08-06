using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Race;

namespace SpirePvp.Duel;

/// <summary>
/// Enters the duel arena: a fresh <see cref="DuelEncounter"/> room with an empty enemy side,
/// so the duel starts from clean state — full hand, fresh energy, correct layout at setup —
/// rather than from whatever fight happened to be in progress.
///
/// Shared by the entry flow (<see cref="DuelEntry"/>, once both players confirm) and the
/// `duel start` console command.
/// </summary>
public static class DuelArena
{
    /// <summary>
    /// Ordering is load-bearing. DuelSession must be active *before* the room is entered:
    /// combat setup evaluates the win condition against zero enemies, and without the veto
    /// from DuelWinConditionPatch already in place the duel would end the instant it began.
    /// </summary>
    public static bool Enter()
    {
        RunManager? runManager = RunManager.Instance;
        RunState? runState = runManager?.DebugOnlyGetState();
        if (runManager == null || runState == null)
        {
            Log.Warn("[SpirePvp] cannot enter duel arena — no run in progress");
            return false;
        }

        EncounterModel encounter = (EncounterModel)ModelDb.Encounter<DuelEncounter>().ToMutable();
        CombatRoom room = new CombatRoom(encounter, runState);

        // Veto first, then enter. See the ordering note above.
        DuelSession.ActivateDuel(0);
        TaskHelper.RunSafely(EnterRoom(runManager, room));

        Log.Warn("[SpirePvp] entering duel arena");
        return true;
    }

    /// <summary>
    /// `RunManager.EnterRoom` is the *last* step of entering a room, not the whole thing. Every
    /// vanilla entry point — `EnterMapPointInternal` for map → room, `EnterRoomDebug` for dev
    /// commands — runs the same preamble in front of it, and calling `EnterRoom` alone silently
    /// skips all of it. This method reproduces `EnterMapPointInternal`'s preamble step for step;
    /// **keep it in sync with that method**, because each omission has failed differently and
    /// none of them failed loudly:
    ///
    /// - **`ClearScreens()`** — froze the arena outright. `DuelRendezvous` used to hide the map
    ///   with `Visible = false`, which leaves `NMapScreen.IsOpen` true, and
    ///   `ActiveScreenContext.GetCurrentScreen` tests `IsOpen` *before* the combat room. The map
    ///   stayed the active screen for the whole duel, so `NCombatRoom.OnActiveScreenUpdated`
    ///   called `Ui.Disable()`: piles off, end-turn button off, and every card play cancelled the
    ///   instant it began. Invisible, and still swallowing every click.
    /// - **`StartSync`/`WaitForSync`** — the authoritative pre-combat state sync. The race turned
    ///   it off (`RaceCoordinator.BeginRace`) and it is exactly what reconciles the two divergent
    ///   race states now that the duel is coupled again (DESIGN §4). `EndRace` has to run *before*
    ///   StartSync or the sync no-ops.
    /// - **`CombatReplayWriter.RecordInitialState`** — killed the turn loop mid-start with
    ///   `InvalidOperationException: RecordInitialState must be called first`, leaving the hand
    ///   half-dealt in the middle of the screen. The replay writer records every checksum and
    ///   enqueued action and throws if the combat never registered an initial state. It only
    ///   surfaced once the checksum tracker came back on above: the first thing `StartTurn` does
    ///   is `GenerateChecksum("After player turn start")`, well before the play phase opens.
    ///   `duel start` never hit it either, because entering from inside a live combat leaves the
    ///   previous combat's replay open — combat *end* is what calls `StopRecording`.
    ///
    /// The through-line: this arena is the first room the mod enters that was not reached through
    /// a map point, and every one of these is something the map path does for you.
    /// </summary>
    private static async Task EnterRoom(RunManager runManager, CombatRoom room)
    {
        // Tells the net layer we are loading, so a slow sync does not raise a spurious
        // "waiting for connection from host" overlay. Every vanilla entry point does this.
        using (new NetLoadingHandle(runManager.NetService))
        {
            // Same transition the map uses to enter any room (NMapScreen.TravelToMapCoord):
            // wipe sfx, fade to black, do the work unseen, fade back in. Also hides the state
            // sync, which can take a moment if one player is slower to confirm.
            SfxCmd.Play("event:/sfx/ui/wipe_map");
            await runManager.FadeOut();

            // After the room is exited, before the sync starts. Exiting the last race room is
            // still race-shaped work — it tears down a combat the opponent was never in — and
            // it is also the last thing that could touch a synchronizer counter, so EndRace's
            // reset lands on quiesced state.
            await runManager.ExitCurrentRooms();
            RaceCoordinator.EndRace();

            // Bracketed by logs on purpose: a sync that never completes is a silent wait on the
            // map, which looks nothing like a sync problem from the outside.
            Log.Warn("[SpirePvp] duel: waiting for pre-combat state sync");
            runManager.CombatStateSynchronizer.StartSync();
            runManager.ClearScreens();
            await runManager.CombatStateSynchronizer.WaitForSync();
            Log.Warn("[SpirePvp] duel: state sync complete");

            if (runManager.CombatReplayWriter.IsEnabled)
            {
                runManager.CombatReplayWriter.RecordInitialState(runManager.ToSave(null));
            }

            runManager.ActionExecutor.Pause();
            await runManager.EnterRoom(room);

            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null)
            {
                Log.Warn("[SpirePvp] duel arena entered but no combat state — aborting duel");
                DuelSession.Reset();
                await runManager.FadeIn();
                return;
            }

            // Now that both players exist in the combat, record the opponent and fix the layout.
            Player? me = LocalContext.GetMe(state);
            foreach (Creature creature in state.PlayerCreatures)
            {
                Player? owner = creature.Player;
                if (owner != null && me != null && owner.NetId != me.NetId)
                {
                    DuelSession.ActivateDuel(owner.NetId);
                    break;
                }
            }

            // Before the fade-in, not after: this moves the opponent across the screen, and
            // doing it on a visible arena reads as the duelists snapping into place.
            DuelLayout.MoveOpponentToEnemySide(state);
            DuelResult.Arm();

            // The clocks are NOT started here. They are run-scoped (DESIGN §9) and were started
            // at run creation by DuelMatch, having already ticked down through the race —
            // restarting would hand both players a fresh bank at the very moment the race is
            // meant to have cost them something. Only the flag arms here, because losing on time
            // is a duel rule.
            if (me != null)
            {
                DuelFlag.Arm();
            }

            Log.Warn($"[SpirePvp] duel arena ready — {state.PlayerCreatures.Count} duelists, {state.Enemies.Count} enemies");

            await runManager.FadeIn();
        }
    }
}
