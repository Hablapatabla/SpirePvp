using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;

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

        if (mode != "on")
        {
            return new CmdResult(success: false, "Invalid argument '" + args[0] + "'. Use 'on' or 'off'.");
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

        List<Creature> enemies = new List<Creature>(state.Enemies);
        TaskHelper.RunSafely(ClearEnemySide(enemies));

        Log.Warn($"[SpirePvp] duel mode ON — {duelists.Count} duelists, clearing {enemies.Count} enemies");
        return new CmdResult(success: true,
            $"Duel on: {duelists.Count} duelists, {enemies.Count} enemies cleared.");
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
