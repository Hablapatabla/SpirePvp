using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
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
    /// <summary>
    /// Whether the arena has already been entered this run.
    ///
    /// **Entering twice breaks the run**, and it does so a long way from the cause: the second
    /// call builds a second `CombatRoom` and enters it while the first is still starting, so the
    /// turn loop dies mid-setup — measured as `NRunMusicController.UpdateTrack` throwing a
    /// `NullReferenceException` inside `StartCombatInternal`, then
    /// `Combat #1 turn loop died while its combat is in progress; the combat is stuck until the
    /// room is restarted`. Nothing in that trace mentions the arena, and the run simply stops
    /// responding.
    ///
    /// **The guard belongs here rather than in the console command**, because `duel now` is only
    /// the route that found it. `DuelRendezvous` enters the arena off `DuelStartMessage`, and a
    /// message arriving twice — or a player clicking through as one lands — is the same call
    /// again with nobody typing anything. Guard on the condition, not on each route in.
    ///
    /// Reset in <see cref="DuelMatch.OnRunEnded"/>, without which the *second* match in one
    /// process could never enter the arena at all: mod state is static, the run it belongs to is
    /// not.
    /// </summary>
    private static bool _entered;

    /// <summary>
    /// Whether this run has already entered the arena.
    ///
    /// Exposed so callers can tell the two reasons <see cref="Enter"/> returns false apart. They
    /// are not interchangeable: "no run in progress" and "already duelling" are opposite states,
    /// and a command that reports the first when the second is true sends the reader off looking
    /// for a run that is right in front of them.
    /// </summary>
    public static bool HasEntered => _entered;

    /// <summary>Forgets the arena, so the next match in this process can enter its own.</summary>
    public static void Reset()
    {
        _entered = false;
    }

    public static bool Enter()
    {
        RunManager? runManager = RunManager.Instance;
        RunState? runState = runManager?.DebugOnlyGetState();
        if (runManager == null || runState == null)
        {
            Log.Warn("[SpirePvp] cannot enter duel arena — no run in progress");
            return false;
        }

        if (_entered)
        {
            Log.Warn("[SpirePvp] already in the duel arena — ignoring a second entry");
            return false;
        }

        _entered = true;

        // Before the room is constructed. See the note on MoveRunToArenaCoord: the room takes its
        // Id from NextRoomId in its constructor, and moving to the coord is what resets that
        // counter, so doing this afterwards would fix half of RunLocation and leave the other
        // half divergent.
        MoveRunToArenaCoord(runState);

        EncounterModel encounter = (EncounterModel)ModelDb.Encounter<DuelEncounter>().ToMutable();
        CombatRoom room = new CombatRoom(encounter, runState);

        // Veto first, then enter. See the ordering note above.
        DuelSession.ActivateDuel(0);
        TaskHelper.RunSafely(EnterRoom(runManager, room));

        Log.Warn("[SpirePvp] entering duel arena");
        return true;
    }

    /// <summary>
    /// Moves the run onto the arena's own map coord, so both clients duel at the same
    /// <c>RunLocation</c>.
    ///
    /// **This is not bookkeeping — it decides whether the client can play at all.** Every
    /// `ActionEnqueuedMessage` is an `IRunLocationTargetedMessage`, tagged with the sender's
    /// location, and `RunLocationTargetedMessageBuffer` holds any message addressed to a location
    /// the receiver has never visited. The duel is host-arbitrated: the client requests a play,
    /// the host orders it and broadcasts the enqueue. If the two runs sit at different locations,
    /// every one of those broadcasts is buffered instead of delivered, so the client's cards hang
    /// in mid-air and its end turn does nothing — while the host, which arbitrates for itself,
    /// plays on perfectly normally. Measured 2026-08-06: 28 messages stuck in the buffer, host at
    /// `coord (1, 12) room 1` and client at `coord (3, 0) room 1`.
    ///
    /// The arena is a real map node — the act's second boss — but nothing was ever travelling to
    /// it. `DuelRendezvous` deliberately does not enter the node on click: it announces arrival
    /// and waits, which is what makes the arena a rendezvous rather than a room. So each client
    /// entered the arena room while still standing wherever it happened to be.
    ///
    /// That went unnoticed through every previous playtest because both players had *walked* to
    /// the Act 1 boss, and all paths converge there — so both were at the boss's coord and the
    /// locations matched by accident. It reproduces the moment they do not, which `travel` makes
    /// trivial. **"Both players walked to the boss" is a correlate; the condition is "both clients
    /// are at the same RunLocation."** Moving both to the arena coord — the one coord they agree
    /// on by definition — establishes that condition outright, wherever either was standing.
    ///
    /// `RunLocation` is (map coord, room id) and both halves are settled here.
    /// `AddVisitedMapCoord` makes the arena the current coord (`CurrentMapCoord` is simply the
    /// last visited) *and* resets `NextRoomId` to 0, which is what makes the room ids agree too —
    /// hence the call site above, before the `CombatRoom` is constructed and takes its Id.
    /// Adding to the visited set also releases anything the buffer was holding for this location.
    /// </summary>
    private static void MoveRunToArenaCoord(RunState runState)
    {
        MapPoint? arena = runState.Map?.SecondBossMapPoint;
        if (arena == null)
        {
            // The legacy `duel on` / `duel start` path can run in a plain co-op run that never
            // had an arena node installed. There the party is co-located anyway, which is the
            // property this method exists to guarantee.
            Log.Info("[SpirePvp] duel: no arena node on this map — leaving the run where it is");
            return;
        }

        if (!runState.AddVisitedMapCoord(arena.coord))
        {
            // Already visited, so NextRoomId was not reset and the room ids may not line up.
            // Worth a line rather than silence: this is exactly the shape of the bug above.
            Log.Warn($"[SpirePvp] duel: arena coord {arena.coord} was already visited — room ids " +
                     "may differ between clients");
            return;
        }

        Log.Warn($"[SpirePvp] duel: run moved to arena coord {arena.coord} " +
                 "(both clients converge here, so host arbitration reaches the client)");
    }

    /// <summary>
    /// Records the arena as a room the player actually entered.
    ///
    /// `EnterMapPointInternal` calls `AppendToMapPointHistory` between pausing the executor and
    /// entering the room; the arena never did, so as far as the run was concerned the last place
    /// anybody went was the final room of the *race*. Three things read that history and all three
    /// were wrong:
    ///
    /// - **Elites defeated showed 0 after beating an elite.** `ScoreUtility.GetElitesKilledCount`
    ///   counts elite rooms and then subtracts one if the *last* room in the history is an elite —
    ///   vanilla's way of saying "you died in that one, so you did not kill it". Fight an elite on
    ///   the way to the arena and it was always the last room recorded, so the kill cancelled
    ///   itself out.
    /// - **The run-history breakdown named the elite as the cause of death**, for the same reason:
    ///   the last room recorded is where the run looks like it ended.
    /// - The duel never appeared in run history at all, so a match left no trace of the room it
    ///   was actually decided in.
    ///
    /// This is the same omission as the map coord above and it comes from the same place: the
    /// arena is the one room this mod enters that was not reached through a map point, so every
    /// step the map path performs has to be spelled out.
    /// </summary>
    private static void RecordArenaInMapPointHistory(RunState? runState, CombatRoom room)
    {
        if (runState == null)
        {
            return;
        }

        // The arena node's own type when there is one; Boss is what DuelEncounter.RoomType has to
        // be anyway, since SetSecondBossEncounter rejects anything else.
        MapPointType pointType = runState.Map?.SecondBossMapPoint?.PointType ?? MapPointType.Boss;
        runState.AppendToMapPointHistory(pointType, room.RoomType, room.ModelId);
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
    /// - **the map coord** — see <see cref="MoveRunToArenaCoord"/>, which now runs before the room
    ///   is even constructed. Vanilla's `EnterMapCoord` calls `AddVisitedMapCoord` *before*
    ///   `EnterMapPointInternal`; skipping it left the two clients duelling at different
    ///   `RunLocation`s, which silently buffered every host arbitration message bound for the
    ///   client and froze it mid-turn.
    /// - **the map point history entry** — see <see cref="RecordArenaInMapPointHistory"/>. Without
    ///   it the run's last recorded room was the last room of the race, which made an elite killed
    ///   on the way to the arena count as zero and made that elite the reported cause of death.
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

            // Vanilla appends here, between the pause and the entry, and skipping it had three
            // visible consequences — see RecordArenaInMapPointHistory.
            RecordArenaInMapPointHistory(runManager.State, room);

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

            // **The arrow goes up with the scene, not with the hand.** `CombatManager.TurnStarted`
            // is the alternation's event and it is the *last* line of turn setup — it fires after
            // `RunAutoPrePlayPhase`, i.e. after the draw and its animation — so hanging the opening
            // turn's arrow on it alone meant it appeared once the cards had landed. Reported
            // 2026-08-12: it should render as soon as the scene does.
            //
            // Same family as every other "hook the arrival too" in this project: `TurnStarted` fires
            // on a *change* of turn and cannot carry the state the first turn opens in. The creature
            // nodes exist by now — `MoveOpponentToEnemySide` above has just been through them — so
            // this is the earliest point the arrow has something to hang off.
            if (Turns.DuelTurnModel.Current is Turns.IPlanningTurnModel model)
            {
                Turns.LockInPlanView.ShowInitiative(model.CurrentLeader);
            }

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
