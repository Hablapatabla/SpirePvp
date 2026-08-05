using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

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

    private static async Task EnterRoom(RunManager runManager, CombatRoom room)
    {
        await runManager.EnterRoom(room);

        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            Log.Warn("[SpirePvp] duel arena entered but no combat state — aborting duel");
            DuelSession.Reset();
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

        DuelLayout.MoveOpponentToEnemySide(state);
        DuelResult.Arm();

        // The clocks are NOT started here. They are run-scoped (DESIGN §9) and were started at
        // run creation by DuelMatch, having already ticked down through the race — restarting
        // would hand both players a fresh bank at the very moment the race is meant to have
        // cost them something. Only the flag arms here, because losing on time is a duel rule.
        if (me != null)
        {
            DuelFlag.Arm();
        }

        Log.Warn($"[SpirePvp] duel arena ready — {state.PlayerCreatures.Count} duelists, {state.Enemies.Count} enemies");
    }
}
