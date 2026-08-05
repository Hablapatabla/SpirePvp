using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel;

/// <summary>
/// M1 duel entry (DESIGN §5, I1). The dev console discovers this automatically:
/// <c>DevConsole</c>'s constructor enumerates
/// <c>ReflectionHelper.GetSubtypesInMods&lt;AbstractConsoleCmd&gt;()</c>, so a mod command
/// needs no registration call and no Harmony patch.
///
/// Why this instead of building a synthetic empty-encounter CombatRoom: a custom
/// EncounterModel is an abstract subclass with a ModelId, which drags in model registration
/// (BaseLib) for something M1 does not actually need. Turning the combat you are already in
/// into a duel exercises every M1 mechanic — retargeting, the win-condition veto, round
/// rollover — without any room-entry work. Real duel entry belongs with the automatic flow
/// in M6.
///
/// IsNetworked = true matters: the console routes networked commands through
/// NetConsoleCmdGameAction, so one player typing <c>duel on</c> flips both clients in the
/// same deterministic action stream. That is what keeps the two sims in agreement.
/// </summary>
public class DuelConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "duel";

    public override string Args => "'on'|'off'";

    public override string Description =>
        "SpirePvp: turn the current combat into a 1v1 duel — clears the enemy side and makes " +
        "attack cards target the other player. 'off' reverts to normal combat rules.";

    public override bool IsNetworked => true;

    // Vanilla commands default to DebugOnly, which hides them unless debug commands are
    // allowed. The duel spike has to be usable in a normal build, so opt out.
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "on";

        if (mode == "off")
        {
            CombatState? current = CombatManager.Instance.DebugOnlyGetState();
            if (current != null)
            {
                DuelLayout.RestoreAllySide(current);
            }

            DuelSession.Reset();
            Log.Warn("[SpirePvp] duel mode OFF");
            return new CmdResult(success: true, "Duel mode off.");
        }

        if (mode == "clock")
        {
            if (args.Length < 2 || !double.TryParse(args[1], out double minutes) || minutes < 0)
            {
                return new CmdResult(success: false, "Usage: duel clock <minutes>. 0 disables the clock.");
            }

            DuelClockService.Configure(minutes);
            return new CmdResult(success: true, minutes <= 0
                ? "Clock off — nobody can lose on time."
                : $"Clock set to {minutes:0.##} min per player. Takes effect at the next duel start.");
        }

        if (mode == "start")
        {
            return StartDuelRoom();
        }

        if (mode != "on")
        {
            return new CmdResult(success: false,
                "Invalid argument '" + args[0] + "'. Use 'start', 'on' or 'off'.");
        }

        if (!CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Not in a combat — start one first, then run 'duel on'.");
        }

        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            return new CmdResult(success: false, "No combat state available.");
        }

        List<Creature> duelists = new List<Creature>();
        foreach (Creature creature in state.PlayerCreatures)
        {
            if (creature.IsAlive)
            {
                duelists.Add(creature);
            }
        }

        if (duelists.Count < 2)
        {
            return new CmdResult(success: false,
                $"A duel needs two live players; this combat has {duelists.Count}. Run it from a 2-player session.");
        }

        // Opponent, from this client's point of view. The targeting patch derives the target
        // from the acting card's Owner rather than this field, so it is informational for now
        // (the clock and result screen in M3/M6 will want it).
        ulong opponentId = 0;
        if (issuingPlayer != null)
        {
            foreach (Creature creature in duelists)
            {
                Player? owner = creature.Player;
                if (owner != null && owner.NetId != issuingPlayer.NetId)
                {
                    opponentId = owner.NetId;
                    break;
                }
            }
        }

        DuelSession.ActivateDuel(opponentId);
        DuelLayout.MoveOpponentToEnemySide(state);
        DuelResult.Arm();

        List<Creature> enemies = new List<Creature>(state.Enemies);
        TaskHelper.RunSafely(ClearEnemySide(enemies));

        Log.Warn($"[SpirePvp] duel mode ON — {duelists.Count} duelists, clearing {enemies.Count} enemies");
        return new CmdResult(success: true,
            $"Duel on: {duelists.Count} duelists, {enemies.Count} enemies cleared.");
    }

    /// <summary>
    /// Enters a fresh <see cref="DuelEncounter"/> room: a real combat with an empty enemy
    /// side, so the duel starts from clean state rather than mid-fight.
    ///
    /// Ordering is load-bearing. DuelSession must be active *before* the room is entered:
    /// combat setup evaluates the win condition against zero enemies, and without the veto
    /// from DuelWinConditionPatch already in place the duel would end the instant it began.
    /// </summary>
    private static CmdResult StartDuelRoom()
    {
        RunManager? runManager = RunManager.Instance;
        RunState? runState = runManager?.DebugOnlyGetState();
        if (runManager == null || runState == null)
        {
            return new CmdResult(success: false, "Not in a run — start a multiplayer run first.");
        }

        EncounterModel encounter = (EncounterModel)ModelDb.Encounter<DuelEncounter>().ToMutable();
        CombatRoom room = new CombatRoom(encounter, runState);

        // Veto first, then enter. See the ordering note above.
        DuelSession.ActivateDuel(0);
        TaskHelper.RunSafely(EnterDuelRoom(runManager, room));

        Log.Warn("[SpirePvp] entering duel arena");
        return new CmdResult(success: true, "Entering the duel arena…");
    }

    private static async Task EnterDuelRoom(RunManager runManager, CombatRoom room)
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

        // Run-scoped in principle (DESIGN §9: the bank covers the whole run). Started here
        // only because the race phase does not exist yet — M5 moves this to run start.
        if (me != null)
        {
            DuelClockService.Start(me.NetId, DuelSession.OpponentId);
        }

        Log.Warn($"[SpirePvp] duel arena ready — {state.PlayerCreatures.Count} duelists, {state.Enemies.Count} enemies");
    }

    /// <summary>
    /// Empties the enemy side. Safe to do while the duel is active: DuelWinConditionPatch is
    /// already vetoing the "no enemies left ⇒ victory" conclusion, so this does not end combat.
    /// </summary>
    private static async Task ClearEnemySide(List<Creature> enemies)
    {
        foreach (Creature enemy in enemies)
        {
            await CreatureCmd.Kill(enemy);
        }

        await CombatManager.Instance.CheckWinCondition();
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1)
        {
            return CompleteArgument(new List<string> { "on", "off" }, Array.Empty<string>(),
                args.FirstOrDefault() ?? "");
        }

        return new CompletionResult
        {
            Type = CompletionType.Argument,
            ArgumentContext = CmdName
        };
    }
}
