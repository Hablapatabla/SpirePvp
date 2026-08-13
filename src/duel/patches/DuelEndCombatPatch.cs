using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Ends a duel without running vanilla's end-of-combat run progression.
///
/// CombatManager.EndCombatInternal assumes it is finishing a real room on the map. It reads
/// localPlayer.PlayerCombatState.TurnNumber unguarded, walks
/// runState.CurrentMapPointHistoryEntry.Rooms.Last(), inspects runState.Map's boss map
/// points, then calls SaveRun, UpdateProgressAfterCombatWon and the "defeated all enemies"
/// achievement check. The duel arena is a synthetic CombatRoom entered through EnterRoom with
/// no map point behind it, so that path threw a NullReferenceException — and because it runs
/// inside the turn loop (StartTurn -> CheckWinCondition -> EndCombatInternal) the exception
/// killed the loop. That is the freeze: not a deadlock, a crashed turn loop, which is why the
/// duel sat there forever and poison stopped ticking (AfterSideTurnStart never fired again).
///
/// None of that progression is meaningful for a duel: there is no map point to record, no
/// room reward, no run to advance — the duel pays out a winner and the run is over. So this
/// replaces the whole method with the minimum teardown needed to leave combat cleanly, then
/// shows the result screen.
///
/// # The step-by-step audit (written 2026-08-13, against v0.110.1)
///
/// This patch skipped `EndCombatInternal` **wholesale**, which is the `RunManager.EnterRoom` trap at
/// the other end of the combat: a vanilla teardown replaced in one line, inheriting every omission
/// silently. `DuelArena` had to mirror `EnterMapPointInternal` step for step and six quiet omissions
/// were found one at a time. So here is the same table for this method, in vanilla's order. **It is
/// an audit, not a changelog: most rows are still skipped, and the two that are not are marked.**
///
/// | # | Vanilla step | Here |
/// |---|---|---|
/// | 1 | `turnState.IsInProgress = false` | **done** |
/// | 2 | `PlayersTakingExtraTurn.Clear()` under `ReadyLock` | **added 2026-08-13.** Pure state, no presentation. Left set, an extra turn owed in a duel outlives the match — and `DuelRematch` keeps the process alive across runs, which is what turns "harmless at teardown" into a bug in the *next* match |
/// | 3 | `SetPhaseForAllPlayers(None)` | **done** |
/// | 4 | `PlayerActionsDisabled = false` | **done** |
/// | 5 | `player.ReviveBeforeCombatEnd()` | skipped — `DuelNoRevivePatch` suppresses it; the loser stays down |
/// | 6 | `Hook.AfterCombatEnd` | **skipped, and this is the largest known omission.** It is how relics and powers reset themselves (`LetterOpener` clears its counter, `Metronome` its state). It is `async`, so including it means this prefix returning a real task rather than `Task.CompletedTask` — a change to what the caller awaits — and its listeners are the models least likely to survive an arena with no map point behind it. Wants its own pass, with a playtest |
/// | 7 | `History.Clear()` | skipped — the combat history feeds the replay writer, which is *also* not stopped here (row 9); clearing one half of a pair is worse than clearing neither |
/// | 8 | `room.OnCombatEnded()` | skipped — it is one line, `GoldProportion = Encounter.CalculateGoldProportion(...)`, and a duel pays no gold |
/// | 9 | `WriteReplay(stopRecording: true)` | skipped, deliberately and nervously. `DuelArena.EnterRoom` calls `RecordInitialState` on the way *in*, and a writer left recording is exactly the state that made a second `RecordInitialState` throw `must be called first`. Nothing has hit it because the run ends here — but rematch replays a run in the same process |
/// | 10 | `player.AfterCombatEnd()` | **skipped, and it must stay skipped in this position.** It clears every card pile, removes all powers and drops block. The result screen goes up immediately afterwards and `RunManager.OnEnded` writes the run history it reads; wiping the piles first is how you get a result screen describing an empty deck |
/// | 11 | `Hook.AfterCombatVictory` | skipped — nobody won a *room*; the duel pays out its own result |
/// | 12 | `NHoverTipSet.Clear()` | **added 2026-08-13.** Inert, and it fixes a small real thing: a tip left hanging over the result screen, which nothing else takes down |
/// | 13 | `CurrentMapPointHistoryEntry.Rooms.Last().TurnsTaken` | skipped. Note this is no longer *unsafe* — `DuelArena.RecordArenaInMapPointHistory` gives the arena a real entry now — it is simply meaningless: nothing reads a duel's turn count off the map history |
/// | 14 | `WinTime` | skipped — that is the act boss, not us |
/// | 15 | `room.MarkPreFinished()` | skipped — it marks a room the run will re-enter as already finished, and a duel's room is never re-entered |
/// | 16–18 | `SaveRun`, `UpdateProgressAfterCombatWon`, achievements | skipped — duel arenas are not run progression, and the achievement check would credit "defeated all enemies" for beating a person |
/// | 19 | `MultiplayerScalingModel?.OnCombatFinished()` | skipped — co-op scaling, and a duel is 1v1 |
/// | 20 | `CombatWon?.Invoke(room)` | skipped — a duel has no room victory |
/// | 21 | `ActionExecutor.Unpause()` | **done** |
/// | 22 | `SetCombatState(NotInCombat)` | **done** |
/// | 23 | `NRunMusicController.UpdateTrack()` | skipped — cosmetic, and the result screen changes the music anyway |
/// | 24 | `CombatEnded?.Invoke(room)` | **skipped, and it is the prime suspect for "the killing blow hangs in mid-air".** `NPlayerHand`, `NCombatUi`, `NCreature`, `NTargetManager` and `NCombatRoom` all subscribe, and that is the engine's own signal to wind the combat presentation down. **It was not added tonight on purpose:** `NCardPlayQueue.AnimOut` hands locally-owned queued cards back to the hand, and three `NCard` double-frees per match are already on the books from that path — so raising an event that plausibly runs it a second time, with no way to play the result, would risk making a known bug worse to fix a cosmetic one. Whoever picks this up should find the *first* free before adding this line |
///
/// **The through-line, and it is the one `DuelArena` learned:** the reason to skip a step has to be
/// a property of the duel, not "it did not seem to matter". Rows 6, 9 and 24 are the ones where
/// that is still a guess.
/// </summary>
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndCombatInternal), typeof(CombatTurnState))]
public static class DuelEndCombatPatch
{
    public static bool Prefix(CombatManager __instance, CombatTurnState turnState, ref Task __result)
    {
        // Complete counts as well as DuelActive, and leaving it out cost exactly the NRE this
        // patch exists to prevent. Ending the duel runs DuelResult.DeclareWinner, which moves
        // the phase to Complete — so the *second* time anything asks, this guard said "not a
        // duel" and handed vanilla the arena it cannot cope with. Something always asks again:
        // the room is not exited while the result screen is up, so a trailing action in
        // ActionExecutor re-runs CheckWinCondition, IsCombatEnding is still true with the loser
        // dead, and EndCombatInternal fires a second time.
        //
        // "The duel is over" is not the same question as "this is not a duel".
        if (DuelSession.Phase is not (DuelPhase.DuelActive or DuelPhase.Complete))
        {
            return true;
        }

        Log.Warn("[SpirePvp] duel combat ending — skipping vanilla run progression");

        turnState.IsInProgress = false;

        // Row 2 of the audit. Vanilla holds the same lock for the same clear; an extra turn owed
        // when the match ended would otherwise still be owed in the next one, because rematch keeps
        // this process alive across runs.
        using (turnState.ReadyLock.EnterScope())
        {
            turnState.PlayersTakingExtraTurn.Clear();
        }

        __instance.PlayerActionsDisabled = false;
        CombatManager.SetPhaseForAllPlayers(turnState.State, PlayerTurnPhase.None);

        RunManager.Instance.ActionExecutor.Unpause();
        RunManager.Instance.ActionQueueSynchronizer.SetCombatState(ActionSynchronizerCombatState.NotInCombat);

        // Row 12. Nothing else takes a hover tip down on the way to the result screen.
        NHoverTipSet.Clear();

        DuelResult.ShowFor(turnState.State);

        // **Required, and its absence was the `duel over` NullReferenceException.**
        // EndCombatInternal is `async Task`. Skipping it without assigning __result leaves the
        // caller holding null and doing `await null`, which throws — inside
        // `CheckWinCondition`, with no EndCombatInternal frame on the stack, because the method
        // never ran. That is why the trace looked like inlining had eaten the frames and why
        // two sessions of reading CheckWinCondition found nothing wrong with it.
        //
        // It only ever appeared on duels decided by HP: a duel decided on the clock ends
        // through DuelFlag -> DuelResult.DeclareWinner without IsCombatEnding ever going true,
        // so EndCombatInternal is not called at all and there is nothing to skip.
        //
        // Harmless in practice — everything above already ran, so the result screen was up and
        // the winner correct — but it threw once per duel on both clients and made every log
        // read start by discounting a real exception.
        __result = Task.CompletedTask;

        // Skip the original entirely.
        return false;
    }
}
