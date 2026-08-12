using MegaCrit.Sts2.Core.Context;
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

        // Arming before the clocks exist subscribes to nothing and then sets `_armed`, so
        // nobody ever loses on time and nothing says why. Callers must Start the clocks first.
        if (DuelClockService.Enabled && DuelClockService.Local == null)
        {
            Log.Error("[SpirePvp] DuelFlag armed before the clocks were started — nothing is " +
                      "watching for a flag. See DuelMatch.OnRunLaunched for the required order.");
        }

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

        // A race clock running out is a draw, not a loss. Both race banks start together and
        // never pause (DESIGN §9: the race is a global countdown, not a chess clock), so they
        // are equal by construction and empty in the same tick. Whichever one this happens to
        // be, the other is at zero too — so "the opponent wins" was really "the service ticks
        // the local clock first", and the host lost its own race every time.
        //
        // Asked of the bank, not the phase. Which clock just expired is a question about which
        // bank was running, and those are not the same question — `DuelClockService` keys its
        // own display on the grant for this reason, after a phase test reported two duel clocks
        // for a match that ended during the race. Same test, same trap, and this branch decides
        // a result rather than a label.
        if (!DuelClockService.DuelBankGranted)
        {
            Log.Warn("[SpirePvp] race clock expired for both players — draw, nobody reached the arena");

            RunManager.Instance.NetService.SendMessage(new DuelResultMessage
            {
                winnerId = 0,
                reason = DuelEndReason.RaceExpired
            });

            DuelResult.DeclareDraw(DuelEndReason.RaceExpired);
            return;
        }

        ulong winner = flagged.PlayerId == DuelClockService.Local?.PlayerId
            ? DuelClockService.Opponent?.PlayerId ?? 0
            : DuelClockService.Local?.PlayerId ?? 0;

        Log.Warn($"[SpirePvp] flag fell for {flagged.PlayerId}; {winner} wins on time");

        DuelResultMessage message = new DuelResultMessage
        {
            winnerId = winner,
            reason = DuelEndReason.Flag
        };

        RunManager.Instance.NetService.SendMessage(message);

        // The host does not receive its own broadcast, so apply it here too.
        Declare(winner, DuelEndReason.Flag);
    }

    /// <summary>
    /// Every route out of a match arrives here on the receiving side — flag, resign, agreed
    /// draw, race expiry. Switching on the reason rather than on `winnerId != 0` keeps the two
    /// drawn outcomes distinguishable from a win, since a draw legitimately carries no winner.
    /// </summary>
    private static void OnDuelResult(DuelResultMessage message, ulong senderId)
    {
        if (message.reason == DuelEndReason.RaceExpired ||
            message.reason == DuelEndReason.AgreedDraw)
        {
            Disarm();
            DuelClockService.Stop();

            // The peer's reason, not a guess. Both codes land here and they read very
            // differently on the result screen.
            DuelResult.DeclareDraw(message.reason);
            return;
        }

        Declare(message.winnerId, message.reason);
    }

    private static void OnClockSync(ClockSyncMessage message, ulong senderId)
    {
        DuelClockService.ApplySync(message);
    }

    private static void Declare(ulong winnerId, int reason)
    {
        Disarm();
        DuelClockService.Stop();

        // `LocalContext.NetId`, not the local clock's PlayerId. They are the same number
        // whenever a clock exists, and the clock does not exist when both banks are `Off` — at
        // which point `DuelClockService.Local?.PlayerId` is null, never equals a winner id, and
        // every client concludes it lost. That could not happen while the only route here was a
        // flag (a flag requires a clock), so it sat harmless. Resigning is reachable in an
        // untimed match and would have made both players see DEFEATED.
        bool localWon = LocalContext.NetId == winnerId;
        DuelResult.DeclareWinner(localWon, reason);
    }
}
