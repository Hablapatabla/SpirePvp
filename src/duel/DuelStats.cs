using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// What a match did, for both players, so the result screen can say something true about it.
///
/// Vanilla's game-over screen scores a *run* — floors, gold, elites, bosses, ascension — and a
/// duel is not a run. Those lines are suppressed (`DuelResultLinesPatch`) and these replace
/// them. The numbers are chosen to be *comparative*: a duel has an opponent, so "12 cards
/// played" is far less interesting than "12 to their 20".
///
/// Two halves, gathered differently:
///
/// - **Race stats** are reconstructed at the end from `MapPointHistory` using vanilla's own
///   `ScoreUtility`, so nothing has to be tracked while the race runs.
/// - **Duel stats** have to be counted as they happen — nothing in the engine accumulates
///   cards played or damage dealt across a combat in a form we can read afterwards.
///
/// **The opponent's numbers must be sent.** During a race the two clients are deliberately
/// decoupled, so each one's `MapPointHistory` records only its own moves; the opponent's entries
/// are simply not there to read. This is the same reason `RaceProgressMessage` exists.
/// </summary>
public static class DuelStats
{
    private static bool _armed;

    /// <summary>Cards the local player played during the duel.</summary>
    public static int CardsPlayed { get; private set; }

    /// <summary>Damage the local player dealt during the duel, blocked and unblocked.</summary>
    public static int DamageDealt { get; private set; }

    /// <summary>The opponent's snapshot, once it arrives. Null until then.</summary>
    public static DuelStatsMessage? Opponent { get; private set; }

    /// <summary>True when both sides' numbers are in hand and a comparison is honest.</summary>
    public static bool HasOpponent => Opponent != null;

    public static void Reset()
    {
        CardsPlayed = 0;
        DamageDealt = 0;
        Opponent = null;
    }

    /// <summary>
    /// Arm at run start, like every other handler in this mod. The opponent can finish and
    /// broadcast their stats before we have finished rendering anything, and a handler
    /// registered lazily would drop that message — the mistake this codebase has paid for four
    /// times now.
    /// </summary>
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

        net.RegisterMessageHandler<DuelStatsMessage>(OnOpponentStats);
        _armed = true;
    }

    public static void Disarm()
    {
        RunManager.Instance?.NetService?.UnregisterMessageHandler<DuelStatsMessage>(OnOpponentStats);
        _armed = false;
    }

    /// <summary>Counted from a prefix on Hook.AfterCardPlayed.</summary>
    public static void RecordCardPlayed() => CardsPlayed++;

    /// <summary>Counted from a prefix on Hook.AfterDamageGiven.</summary>
    public static void RecordDamageDealt(int amount)
    {
        if (amount > 0)
        {
            DamageDealt += amount;
        }
    }

    /// <summary>
    /// Broadcast this client's numbers.
    ///
    /// Called as the match is decided and **before** the result screen goes up, which is the
    /// only ordering that works: the run teardown that follows takes the transport with it, and
    /// a stats message sent afterwards would go into a disconnected service — the failure this
    /// project has already fixed twice, once for the clocks and once for the resignation.
    ///
    /// Delivery is not guaranteed to beat the screen. Both clients decide within a few
    /// milliseconds of each other over a local ENet link and the score lines are several frames
    /// later, so in practice it arrives first; if it does not, the screen shows this client's
    /// own numbers and says the comparison is unavailable rather than inventing one.
    /// </summary>
    public static void Broadcast()
    {
        INetGameService? net = RunManager.Instance?.NetService;
        if (net == null || !net.IsConnected)
        {
            return;
        }

        DuelStatsMessage message = BuildLocal();
        net.SendMessage(message);

        Log.Info($"[SpirePvp] stats sent: {message.cardsPlayed} cards, {message.damageDealt} dmg, " +
                 $"{message.goldGained} gold, {message.elitesKilled} elites, floor {message.floorsClimbed}");
    }

    /// <summary>This client's numbers, race half reconstructed from the run.</summary>
    public static DuelStatsMessage BuildLocal()
    {
        RunManager? run = RunManager.Instance;
        RunState? state = run?.State;
        Player? me = state != null ? LocalContext.GetMe(state.Players) : null;

        DuelStatsMessage message = new DuelStatsMessage
        {
            cardsPlayed = CardsPlayed,
            damageDealt = DamageDealt,
            currentHp = me?.Creature.CurrentHp ?? 0,
            maxHp = me?.Creature.MaxHp ?? 0,
            deckSize = me?.Deck.Cards.Count ?? 0,
            floorsClimbed = state?.TotalFloor ?? 0,
            goldGained = 0,
            elitesKilled = 0
        };

        // Vanilla's own accounting, rather than a second implementation that could disagree
        // with the score screen a normal run shows.
        try
        {
            if (state != null && me != null)
            {
                message.elitesKilled = ScoreUtility.GetElitesKilledCount(state.MapPointHistory);

                int gold = 0;
                foreach (IReadOnlyList<MapPointHistoryEntry> act in state.MapPointHistory)
                {
                    foreach (MapPointHistoryEntry entry in act)
                    {
                        gold += entry.GetEntry(me.NetId).GoldGained;
                    }
                }

                message.goldGained = gold;
            }
        }
        catch (Exception e)
        {
            // Losing the race half is worth far less than losing the screen.
            Log.Warn($"[SpirePvp] stats: could not read run history ({e.Message})");
        }

        return message;
    }

    private static void OnOpponentStats(DuelStatsMessage message, ulong senderId)
    {
        if (LocalContext.NetId == senderId)
        {
            return;
        }

        Opponent = message;
        Log.Info($"[SpirePvp] stats received from {senderId}: {message.cardsPlayed} cards, " +
                 $"{message.damageDealt} dmg, {message.goldGained} gold, {message.elitesKilled} elites");
    }
}
