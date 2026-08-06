using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// The arena is the one node in the race where the players wait for each other.
///
/// Every other node is raced independently — that is the whole point of M5 — but the duel
/// needs both of them present, so clicking the arena deliberately does *not* enter the room.
/// It announces arrival and waits. Once both have arrived, the deck-review screen opens on both
/// clients and `DuelEntry` runs the rest: view the opponent's deck, both confirm, enter.
///
/// So the race's finish line is "arrive and wait", not "arrive and fight".
///
/// Arrival is a separate message from `DuelReadyMessage` rather than a reuse of it. They mean
/// different things — "I am here" versus "I have seen your deck and accept" — and collapsing
/// them would make it impossible to distinguish a player who walked in from one who confirmed.
/// </summary>
public static class DuelRendezvous
{
    private static bool _localArrived;
    private static bool _remoteArrived;
    private static bool _armed;

    public static bool LocalArrived => _localArrived;

    public static bool RemoteArrived => _remoteArrived;

    public static void Reset()
    {
        _localArrived = false;
        _remoteArrived = false;
    }

    /// <summary>True when <paramref name="coord"/> is this run's arena node.</summary>
    public static bool IsArenaCoord(MapCoord coord)
    {
        MapPoint? arena = RunManager.Instance?.State?.Map?.SecondBossMapPoint;
        return arena != null && arena.coord.col == coord.col && arena.coord.row == coord.row;
    }

    /// <summary>
    /// The local player clicked the arena. Announce it and wait; do not enter the room.
    /// </summary>
    public static void ArriveLocal()
    {
        Arm();

        if (_localArrived)
        {
            return;
        }

        _localArrived = true;
        Log.Warn("[SpirePvp] arena: arrived, waiting for opponent");

        RunManager.Instance.NetService.SendMessage(new DuelArrivedMessage
        {
            modVersion = DuelEntry.ModVersion
        });

        ShowWaitingPortrait();
        TryOpenDeckReview();
    }

    /// <summary>
    /// Register the arrival handler.
    ///
    /// Must happen at run start, not on local arrival. Arming lazily meant a client that had
    /// not clicked the arena yet had no handler registered when the host's arrival message
    /// came in, so it dropped it: the host's portrait never appeared on the client's map, and
    /// the client's own arrival then found `_remoteArrived` still false and waited forever for
    /// something that had already happened.
    /// </summary>
    public static void Arm()
    {
        if (_armed)
        {
            return;
        }

        RunManager.Instance.NetService.RegisterMessageHandler<DuelArrivedMessage>(OnArrived);
        _armed = true;
    }

    private static void OnArrived(DuelArrivedMessage message, ulong senderId)
    {
        if (message.modVersion != DuelEntry.ModVersion)
        {
            Log.Warn($"[SpirePvp] arena: opponent runs mod version {message.modVersion}, we run " +
                     $"{DuelEntry.ModVersion} — message ids are positional, so this match is unsafe.");
            return;
        }

        if (LocalContext.NetId == senderId)
        {
            return;
        }

        _remoteArrived = true;
        Log.Warn($"[SpirePvp] arena: opponent {senderId} arrived");

        ShowWaitingPortrait();
        TryOpenDeckReview();
    }

    private static void TryOpenDeckReview()
    {
        if (!_localArrived || !_remoteArrived)
        {
            return;
        }

        Log.Warn("[SpirePvp] arena: both players present — opening deck review");
        DuelEntry.Open();
    }

    /// <summary>
    /// Marks who is waiting, using the map's own per-player node portraits.
    ///
    /// Vanilla already draws a player's portrait on the node they voted for, which is exactly
    /// the affordance wanted here — "I am standing at the arena" — so this reuses it rather
    /// than inventing a second waiting indicator. The race suppresses voting itself, so nothing
    /// else is writing to these.
    /// </summary>
    private static void ShowWaitingPortrait()
    {
        MapPoint? arena = RunManager.Instance?.State?.Map?.SecondBossMapPoint;
        NMapScreen? screen = NMapScreen.Instance;
        RunState? state = RunManager.Instance?.State;
        if (arena == null || screen == null || state == null)
        {
            return;
        }

        foreach (Player player in state.Players)
        {
            bool arrived = LocalContext.IsMe(player) ? _localArrived : _remoteArrived;
            if (arrived)
            {
                screen.OnPlayerVoteChangedInternal(player, null, arena.coord);
            }
        }
    }
}
