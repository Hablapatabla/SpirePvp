using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// Decides losses on time, host-authoritatively (DESIGN §9: sudden death).
///
/// Both clients tick their own clocks from wall-clock time, which is fine for *display* —
/// they agree to within a few milliseconds. It is not fine for deciding a match. If each
/// client concluded "flagged" independently, a few ms of skew near zero could have them
/// disagree about who won, which is the one way a clock could break the determinism
/// everything else in this mod is built on.
///
/// So only the host declares it. The host watches both clocks, and on a flag broadcasts
/// DuelResultMessage{winnerId, reason = flag}. Every client — host included — reacts to that
/// message and nothing else, so there is exactly one decision-maker and no path to
/// disagreement. Clients' local clocks stay display-only.
///
/// The result then routes through DuelResult.Declare, the same code a death uses, so the
/// victory/defeat screens come free.
/// </summary>
public static class DuelFlag
{
    private const int ReasonFlag = 1;

    private static bool _armed;

    public static void Arm()
    {
        if (_armed)
        {
            return;
        }

        INetGameService? net = RunManager.Instance?.NetService;
        if (net == null)
        {
            return;
        }

        net.RegisterMessageHandler<DuelResultMessage>(OnDuelResult);
        net.RegisterMessageHandler<ClockSyncMessage>(OnClockSync);

        if (DuelClockService.Local != null)
        {
            DuelClockService.Local.Flagged += OnClockFlagged;
        }

        if (DuelClockService.Opponent != null)
        {
            DuelClockService.Opponent.Flagged += OnClockFlagged;
        }

        _armed = true;
    }

    public static void Disarm()
    {
        // Unregister as well as unsubscribe: a run ending drops the net service these were
        // bound to, and leaving _armed set would stop the next run re-registering on the new
        // one. See DuelMatch.OnRunEnded.
        INetGameService? net = RunManager.Instance?.NetService;
        net?.UnregisterMessageHandler<DuelResultMessage>(OnDuelResult);
        net?.UnregisterMessageHandler<ClockSyncMessage>(OnClockSync);

        if (DuelClockService.Local != null)
        {
            DuelClockService.Local.Flagged -= OnClockFlagged;
        }

        if (DuelClockService.Opponent != null)
        {
            DuelClockService.Opponent.Flagged -= OnClockFlagged;
        }

        _armed = false;
    }

    private static bool IsHost =>
        RunManager.Instance?.NetService?.Type == NetGameType.Host;

    /// <summary>
    /// A clock hit zero locally. Only the host acts on it; everyone else waits to be told,
    /// so the two clients cannot reach different conclusions.
    /// </summary>
    private static void OnClockFlagged(DuelClock flagged)
    {
        if (!IsHost || DuelSession.Phase == DuelPhase.Complete)
        {
            return;
        }

        ulong winner = flagged.PlayerId == DuelClockService.Local?.PlayerId
            ? DuelClockService.Opponent?.PlayerId ?? 0
            : DuelClockService.Local?.PlayerId ?? 0;

        Log.Warn($"[SpirePvp] flag fell for {flagged.PlayerId}; {winner} wins on time");

        DuelResultMessage message = new DuelResultMessage
        {
            winnerId = winner,
            reason = ReasonFlag
        };

        RunManager.Instance.NetService.SendMessage(message);

        // The host does not receive its own broadcast, so apply it here too.
        Declare(winner);
    }

    private static void OnDuelResult(DuelResultMessage message, ulong senderId)
    {
        Declare(message.winnerId);
    }

    private static void OnClockSync(ClockSyncMessage message, ulong senderId)
    {
        DuelClockService.ApplySync(message);
    }

    private static void Declare(ulong winnerId)
    {
        Disarm();
        DuelClockService.Stop();

        bool localWon = DuelClockService.Local?.PlayerId == winnerId;
        DuelResult.DeclareWinner(localWon);
    }
}
