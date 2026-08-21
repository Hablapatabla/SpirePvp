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
/// **IsNetworked is false, and that reversed an earlier decision for a measured reason.** It was
/// true so that one player typing <c>duel on</c> flipped both clients through the same
/// deterministic action stream. What that actually produced was a desync, twice on 2026-08-12, and
/// the mechanism is worth keeping because it is not obvious:
///
/// `ActionEnqueuedMessage` carries no action id — **each side assigns ids from its own counter as
/// it enqueues**, so the two agree only while both enqueue the same actions in the same order.
/// During a race `RaceIgnoreRemoteActionsPatch` drops peer action traffic, which is symmetric and
/// fine. But a client typing <c>duel now</c> *ends the race locally*, which un-gags that patch — so
/// the host's copy, arriving a moment later, is no longer dropped and executes too. The client
/// enqueues two console actions, the host one, and every id after that is off by one. Both state
/// dumps were otherwise identical: `Last executed action ID: 0` against `1`.
///
/// Local-only removes the whole family: nothing this command does reaches the shared queue, so it
/// cannot renumber it. Both players type it, which is what was happening anyway — <c>duel now</c>
/// never moved the client reliably, and that is the same fact seen from the other side.
/// </summary>
public class DuelConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "duel";

    public override string Args => "'start'|'now'|'on'|'off'|'hud' ['off']";

    public override string Description =>
        "SpirePvp: turn the current combat into a 1v1 duel — clears the enemy side and makes " +
        "attack cards target the other player. 'off' reverts to normal combat rules.";

    public override bool IsNetworked => false;

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

        // `duel clock <minutes>` was removed deliberately. The clock is part of the match
        // agreement, chosen in the lobby (DESIGN §5b) and running from run creation, so a
        // command that reconfigured it mid-run could only do two things: hand someone a bank
        // they never agreed to, or reset one already spent. Both silently invalidate a match.
        if (mode == "clock")
        {
            return new CmdResult(success: false,
                "The clocks are set in the lobby: host a Custom run and pick a 'Race Clock' and a " +
                "'Duel Clock' modifier. The race bank starts when the run does, the duel gets a fresh " +
                "bank when it begins, and neither can be changed mid-match.");
        }

        // Debug only, and deliberately so: a live readout of the opponent's HP and deck was
        // built as a feature and rejected on sight — it is clutter, and it changes the game by
        // handing both players information a match should make them infer. The data is still
        // tracked for the result screen; this just shows it while diagnosing the race.
        if (mode == "hud")
        {
            bool on = args.Length < 2 || args[1].ToLowerInvariant() != "off";
            SpirePvp.Race.RaceProgressHud.Toggle(on);
            return new CmdResult(success: true,
                $"Race progress debug HUD {(on ? "on" : "off")}.");
        }

        // **A dev shortcut through the draft, and it is local-only like every other mod command.**
        // A full draft is 24 clicks across two rounds before a duel can even start, which is a long
        // way to walk to reach the thing being tested. This takes the local player's remaining picks
        // for the running round, in pool order, and submits them one per tick.
        //
        // It picks *for one player only*, so the opponent still drafts normally and the alternation
        // is untouched — both sides typing it is simply a fast draft, not a desync. The host still
        // arbitrates every pick, which is the property that makes automating clicks safe here where
        // automating anything in the duel would not be.
        if (mode == "draft")
        {
            bool on = args.Length < 2 || args[1].ToLowerInvariant() != "off";
            DuelDraft.AutoPick = on;
            return new CmdResult(success: true,
                on
                    ? "Auto-drafting your picks in pool order. `duel draft off` to stop."
                    : "Auto-draft off.");
        }

        // **A rejoin from the console before it is a rejoin from a menu**, because the interesting
        // half is the restore and the menu is a button on top of it. `duel rejoin` dials the host
        // this client last joined and takes the run it sends back.
        //
        // Fire-and-forget rather than awaited: a console command answers synchronously and the
        // rejoin is a whole network round trip plus a scene load. The result lands in the log, which
        // is where the diagnosis for this lives anyway.
        if (mode == "rejoin")
        {
            if (RunManager.Instance?.State != null)
            {
                return new CmdResult(success: false,
                    "Already in a run — leave to the main menu first, then rejoin.");
            }

            // **An address can be given, and on a restarted client it has to be.** The remembered
            // one is static mod state and dies with the process, which is the very case a reconnect
            // serves. With no argument this falls back to the dev rig's host, which is what a local
            // two-client test wants and what a killed client comes back knowing nothing about.
            string ip = args.Length > 1 ? args[1] : "127.0.0.1";
            ushort port = 33771;
            if (args.Length > 2 && !ushort.TryParse(args[2], out port))
            {
                return new CmdResult(success: false, $"'{args[2]}' is not a port.");
            }

            if (args.Length > 1 || !DuelRejoin.IsOffered)
            {
                DuelRejoin.UseAddress(ip, port);
            }

            TaskHelper.RunSafely(DuelRejoin.Attempt());
            return new CmdResult(success: true, $"Rejoining {ip}:{port} — watch the log.");
        }

        if (mode == "start")
        {
            return StartDuelRoom();
        }

        if (mode == "now")
        {
            return StartDuelRoomImmediately();
        }

        if (mode != "on")
        {
            return new CmdResult(success: false,
                "Invalid argument '" + args[0] + "'. Use 'start', 'now', 'hud', 'rejoin', 'on' or 'off'.");
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
    /// <summary>
    /// Announces arrival at the arena, which opens the entry screen — the opponent's decklist,
    /// with START DUEL on it — once both players are there. Both confirm before the arena loads.
    ///
    /// **It goes through the rendezvous rather than opening the screen locally**, for the reason
    /// written up on <see cref="StartDuelRoomImmediately"/>: a phase flip taken at each client's own
    /// moment lets race traffic cross it, and that desynced a match. Opening the screen directly was
    /// the M1-era path, from before there was a rendezvous to go through; it needed a live combat
    /// to open in, which is itself a sign it predated the map node.
    /// </summary>
    private static CmdResult StartDuelRoom()
    {
        if (RunManager.Instance?.State == null)
        {
            return new CmdResult(success: false, "Not in a run — start a multiplayer run first.");
        }

        if (DuelArena.HasEntered)
        {
            return new CmdResult(success: true, "Already in the duel arena — nothing to do.");
        }

        DuelRendezvous.ArriveLocal();
        return new CmdResult(success: true, DuelRendezvous.RemoteArrived
            ? "Both at the arena — opening the deck review."
            : "Waiting at the arena for your opponent…");
    }

    /// <summary>
    /// Skips the entry *screen* and goes to the arena — but not the rendezvous, which is the part
    /// that makes the phase flip safe.
    ///
    /// **This used to call `DuelArena.Enter` directly, and that desynced a match on 2026-08-12.**
    /// Entering locally flips the phase wherever each player happens to be in the message stream,
    /// and `RaceCoordinator` resets the choice / reward / action / hook counters at that flip. The
    /// host typed `duel now` while the client was still taking its Act 1 card rewards; the client's
    /// reward traffic then arrived *after* the host had reset, so the host consumed choice ids 0
    /// and 1 and reward id 0 for race work the client had already accounted for — and generated the
    /// reward set from an RNG the reset had moved, handing player 1001 Predator and Capacitor where
    /// the client held Shrug It Off and Acrobatics. The dumps differed in exactly three lines:
    /// `Choice IDs 0,2` against `0,0`, `Reward IDs 0,1` against `0,0`, and `RNG counter Niche: 7`
    /// against `1`. Everything else — every card, pile, HP and creature — matched, because the
    /// pre-combat state sync covers those and covers none of these.
    ///
    /// **The rendezvous is immune to that, and not by accident.** Both players announce arrival and
    /// the flip happens once both announcements are in hand. The transport is reliable and ordered
    /// per direction, so anything a player sent during the race — every reward, every choice —
    /// necessarily arrives before their arrival message does. Waiting for that message is therefore
    /// waiting for their race to be fully applied. That is why the real flow has survived every
    /// playtest while the shortcut around it has now desynced twice.
    ///
    /// So this arrives properly and only skips the reading: the review still opens, and confirms
    /// itself (`DuelEntry.AutoConfirm`). HANDOFF predicted this exact failure and said the hazard
    /// "is not closed"; closing it means the shortcut stops being a second path into the duel.
    ///
    /// **Typing it twice used to brick the run**, which is a debug command doing far more damage
    /// than a debug command should: the second entry built a second `CombatRoom` while the first
    /// was still starting, and the combat froze with a stack trace naming the music controller.
    /// `DuelArena.Enter` refuses the second entry now — the guard is there rather than here
    /// because the rendezvous reaches the same call without anyone typing — and this reports it
    /// as the no-op it is.
    /// </summary>
    private static CmdResult StartDuelRoomImmediately()
    {
        if (DuelArena.HasEntered)
        {
            return new CmdResult(success: true, "Already in the duel arena — nothing to do.");
        }

        if (RunManager.Instance?.State == null)
        {
            return new CmdResult(success: false, "Not in a run — start a multiplayer run first.");
        }

        DuelEntry.AutoConfirm = true;
        DuelRendezvous.ArriveLocal();
        return new CmdResult(success: true, DuelRendezvous.RemoteArrived
            ? "Both at the arena — entering."
            : "Waiting at the arena for your opponent…");
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
            return CompleteArgument(new List<string> { "start", "now", "hud", "on", "off" }, Array.Empty<string>(),
                args.FirstOrDefault() ?? "");
        }

        return new CompletionResult
        {
            Type = CompletionType.Argument,
            ArgumentContext = CmdName
        };
    }
}
