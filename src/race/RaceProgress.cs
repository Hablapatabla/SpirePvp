using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Net;

namespace SpirePvp.Race;

/// <summary>
/// Shows the opponent's position on your map, so a race feels like a race.
///
/// Vanilla already draws a player's portrait on a map node — it is how co-op shows where a
/// teammate has voted to go — so this reuses that rather than building a second indicator. The
/// race suppresses voting entirely, which leaves those portraits free for us to mean something
/// else: not "where they want to go" but "where they are".
///
/// Position is broadcast on arrival at a node rather than continuously. There is nothing to
/// interpolate — movement between nodes is discrete — so one reliable message per room entry
/// is both cheaper and more accurate than polling.
///
/// This is also the fix for a cosmetic bug that predates it: with the party decoupled, the
/// vanilla marker kept drawing *both* players wherever the local one stood, which made it look
/// like the opponent was shadowing you.
/// </summary>
public static class RaceProgress
{
    private static bool _armed;
    private static MapCoord? _opponentCoord;

    /// <summary>The opponent's last known node, or null before they have moved.</summary>
    public static MapCoord? OpponentCoord => _opponentCoord;

    /// <summary>
    /// The opponent's last reported HP and deck size.
    ///
    /// These arrive on every `RaceProgressMessage` and were previously read straight into a log
    /// line and dropped. Retaining them is the whole of what `RaceProgressHud` needed from the
    /// network — the message has carried them since M5, so the HUD costs no new traffic and no
    /// change to a wire format whose ids are positional.
    /// </summary>
    public static int OpponentCurrentHp { get; private set; }

    public static int OpponentMaxHp { get; private set; }

    public static int OpponentDeckSize { get; private set; }

    /// <summary>False until the opponent's first move, when there is nothing to report yet.</summary>
    public static bool HasOpponentReport => OpponentMaxHp > 0;

    public static void Reset()
    {
        _opponentCoord = null;
        OpponentCurrentHp = 0;
        OpponentMaxHp = 0;
        OpponentDeckSize = 0;
    }

    /// <summary>
    /// Register the handler at run start. Arming on first use would lose whatever the opponent
    /// announced before we happened to move — the same lifecycle mistake that cost the duel
    /// handshake a debugging round.
    /// </summary>
    public static void Arm()
    {
        if (_armed)
        {
            return;
        }

        RunManager.Instance.NetService.RegisterMessageHandler<RaceProgressMessage>(OnProgress);
        _armed = true;
    }

    /// <summary>Releases the handler and the armed flag so the next run re-arms. See DuelMatch.OnRunEnded.</summary>
    public static void Disarm()
    {
        RunManager.Instance?.NetService?.UnregisterMessageHandler<RaceProgressMessage>(OnProgress);
        _opponentCoord = null;
        _armed = false;
    }

    /// <summary>Announce that we have moved to <paramref name="coord"/>.</summary>
    public static void ReportLocalMove(MapCoord coord)
    {
        RunManager? run = RunManager.Instance;
        RunState? state = run?.State;
        Player? me = state != null ? LocalContext.GetMe(state.Players) : null;
        if (run == null || state == null || me == null)
        {
            return;
        }

        run.NetService.SendMessage(new RaceProgressMessage
        {
            mapRow = coord.row,
            mapCol = coord.col,
            currentHp = me.Creature.CurrentHp,
            maxHp = me.Creature.MaxHp,
            deckSize = me.Deck.Cards.Count,
            actBossDefeated = false
        });
    }

    private static void OnProgress(RaceProgressMessage message, ulong senderId)
    {
        if (LocalContext.NetId == senderId)
        {
            return;
        }

        RunState? state = RunManager.Instance?.State;
        Player? opponent = state?.GetPlayer(senderId);
        if (state == null || opponent == null)
        {
            return;
        }

        MapCoord previous = _opponentCoord ?? new MapCoord(-1, -1);
        MapCoord now = new MapCoord(message.mapCol, message.mapRow);
        _opponentCoord = now;

        OpponentCurrentHp = message.currentHp;
        OpponentMaxHp = message.maxHp;
        OpponentDeckSize = message.deckSize;

        // Passing the previous coord is what clears the old portrait; without it they would
        // accumulate a trail of markers down the map.
        NMapScreen.Instance?.OnPlayerVoteChangedInternal(
            opponent,
            previous.col >= 0 ? previous : null,
            now);

        Log.Info($"[SpirePvp] race: opponent {senderId} now at {now} " +
                 $"({message.currentHp}/{message.maxHp} hp, {message.deckSize} cards)");

        RaceProgressHud.Refresh();
    }
}
