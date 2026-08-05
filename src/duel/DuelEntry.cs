using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// The duel's entry flow: look at your opponent's deck, confirm, fight.
///
/// Rather than a deck *panel* shown once the duel starts, the decklist IS the entry screen —
/// the vanilla deck view, pointed at the opponent's player, with its button relabelled
/// START DUEL. That makes the information rule (DESIGN §1: opponent's decklist is revealed)
/// self-enforcing, since there is no way into the arena that does not go through it, and the
/// confirm doubles as the ready handshake.
///
/// Confirming is revocable until the opponent confirms as well, so this is a negotiation and
/// not a commitment. The clocks keep running throughout: studying their deck costs you time
/// you would rather spend in the duel, which is the intended pressure.
///
/// Entering is host-decided, matching how the flag is resolved: the host notices both sides
/// are ready and broadcasts DuelStartMessage; every client enters on that message and nothing
/// else. Two clients independently deciding to enter a room is exactly the kind of race that
/// would desync the sim.
/// </summary>
public static class DuelEntry
{
    private const string ModVersion = "0.1.0";

    private static bool _armed;
    private static bool _localReady;
    private static bool _opponentReady;
    private static Player? _opponent;

    public static bool IsChoosing { get; private set; }

    public static bool LocalReady => _localReady;

    /// <summary>Opens the opponent's deck as the duel's entry screen.</summary>
    public static bool Open()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        Player? me = state != null ? LocalContext.GetMe(state) : null;
        _opponent = FindOpponent(state, me);

        if (_opponent == null)
        {
            return false;
        }

        Arm();
        _localReady = false;
        _opponentReady = false;
        IsChoosing = true;

        NDeckViewScreen.ShowScreen(_opponent);
        Log.Warn("[SpirePvp] duel entry — showing opponent deck");
        return true;
    }

    /// <summary>The START DUEL button was pressed. Toggles, because confirming is revocable.</summary>
    public static void ToggleReady()
    {
        if (!IsChoosing)
        {
            return;
        }

        _localReady = !_localReady;
        Log.Warn($"[SpirePvp] duel entry — local ready = {_localReady}");

        RunManager.Instance.NetService.SendMessage(new DuelReadyMessage
        {
            modVersion = ModVersion,
            isReady = _localReady
        });

        TryStart();
    }

    private static void Arm()
    {
        if (_armed)
        {
            return;
        }

        INetGameService net = RunManager.Instance.NetService;
        net.RegisterMessageHandler<DuelReadyMessage>(OnReady);
        net.RegisterMessageHandler<DuelStartMessage>(OnStart);
        _armed = true;
    }

    private static void OnReady(DuelReadyMessage message, ulong senderId)
    {
        if (message.modVersion != ModVersion)
        {
            Log.Warn($"[SpirePvp] opponent runs mod version {message.modVersion}, we run {ModVersion} — " +
                     "message ids are positional, so a duel between mismatched versions is unsafe.");
            return;
        }

        _opponentReady = message.isReady;
        TryStart();
    }

    /// <summary>Host only: both sides ready, so tell everyone to enter.</summary>
    private static void TryStart()
    {
        if (!_localReady || !_opponentReady)
        {
            return;
        }

        if (RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }

        RunManager.Instance.NetService.SendMessage(new DuelStartMessage
        {
            clockMs = (int)DuelClockService.ConfiguredMs,
            suddenDeath = true
        });

        Begin();
    }

    private static void OnStart(DuelStartMessage message, ulong senderId)
    {
        Begin();
    }

    private static void Begin()
    {
        if (!IsChoosing)
        {
            return;
        }

        IsChoosing = false;
        NCapstoneContainer.Instance?.Close();
        DuelArena.Enter();
    }

    private static Player? FindOpponent(ICombatState? state, Player? me)
    {
        if (state == null || me == null)
        {
            return null;
        }

        foreach (Creature creature in state.PlayerCreatures)
        {
            Player? owner = creature.Player;
            if (owner != null && owner.NetId != me.NetId)
            {
                return owner;
            }
        }

        return null;
    }
}
