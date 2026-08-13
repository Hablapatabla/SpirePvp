using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Saves.Runs;
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

    /// <summary>
    /// Who reached the arena first, as *this* client saw it. Only the host's answer is used.
    ///
    /// **Arrival order is not a local fact**, which is the whole reason this is host-decided and
    /// travels on `DuelStartMessage`: each client knows when its own arrival happened and when the
    /// other's message landed, so on a slow link both can honestly believe they were first. The
    /// host sees both in one order. Reading it anywhere but the host is a bug.
    /// </summary>
    private static ulong _firstToArrive;

    /// <summary>The netId that reached the arena first, or 0 if nobody has arrived yet.</summary>
    public static ulong FirstToArrive => _firstToArrive;
    private static bool _armed;

    public static bool LocalArrived => _localArrived;

    public static bool RemoteArrived => _remoteArrived;

    /// <summary>
    /// The opponent's deck as it stood when they arrived, rebuilt from their arrival message.
    /// Null until they arrive. Read by <see cref="DuelEntry"/>, which cannot use the local copy
    /// of their `Player` — see the note on <see cref="DuelArrivedMessage.deck"/>.
    /// </summary>
    public static IReadOnlyList<CardModel>? OpponentDeck { get; private set; }

    public static void Reset()
    {
        _localArrived = false;
        _remoteArrived = false;
        _firstToArrive = 0;
        OpponentDeck = null;
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
        RecordArrival(LocalContext.NetId ?? 0UL);
        Log.Warn("[SpirePvp] arena: arrived, waiting for opponent");

        // **The rest happens on arrival, before the announcement carries our state.** You can read
        // both players' HP on the deck review, so a heal applied later shows the review a number
        // that is about to be wrong. Healing here and *sending* the result is what makes the
        // reveal honest — see DuelArenaRest, and note the message field exists because the sync
        // does not carry your own state to the peer.
        DuelArenaRest.HealLocalDuelist(RunManager.Instance.State);

        Creature? mine = LocalContext.GetMe(RunManager.Instance.State)?.Creature;
        RunManager.Instance.NetService.SendMessage(new DuelArrivedMessage
        {
            modVersion = DuelEntry.ModVersion,
            hp = mine?.CurrentHp ?? 0,
            maxHp = mine?.MaxHp ?? 0,
            deck = LocalDeckSnapshot()
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

    /// <summary>Releases the handler and the armed flag so the next run re-arms. See DuelMatch.OnRunEnded.</summary>
    public static void Disarm()
    {
        RunManager.Instance?.NetService?.UnregisterMessageHandler<DuelArrivedMessage>(OnArrived);
        Reset();
        _armed = false;
    }

    /// <summary>
    /// Writes the opponent's post-rest HP into our copy of them, so the deck review reads true.
    ///
    /// **This is the peer's own fact, not a local calculation**, which is the whole reason it is on
    /// the wire. Guarded to zero so a message from an older build cannot blank a duelist's HP —
    /// message ids are positional and the version gate above already refuses a mismatch, but a
    /// field that silently means "dead" is not one to leave ungarded.
    ///
    /// The pre-combat sync still runs at arena entry and remains authoritative; this only has to be
    /// right for the screen in between.
    /// </summary>
    private static void ApplyOpponentHp(ulong senderId, int hp, int maxHp)
    {
        if (hp <= 0 || maxHp <= 0)
        {
            return;
        }

        foreach (Player player in RunManager.Instance.State?.Players ?? Array.Empty<Player>())
        {
            if (player.NetId != senderId)
            {
                continue;
            }

            player.Creature.MaxHp = maxHp;
            player.Creature.CurrentHp = hp;
            Log.Info($"[SpirePvp] arena: opponent {senderId} arrived at {hp}/{maxHp} after their rest");
            return;
        }
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
        RecordArrival(senderId);
        OpponentDeck = RebuildDeck(message.deck);
        ApplyOpponentHp(senderId, message.hp, message.maxHp);
        Log.Warn($"[SpirePvp] arena: opponent {senderId} arrived with {OpponentDeck?.Count ?? 0} cards");

        ShowWaitingPortrait();
        TryOpenDeckReview();
    }

    /// <summary>This client's deck, in the form the wire takes.</summary>
    private static List<SerializableCard> LocalDeckSnapshot()
    {
        List<SerializableCard> cards = new List<SerializableCard>();
        Player? me = LocalContext.GetMe(RunManager.Instance?.State?.Players
                                        ?? (IEnumerable<Player>)Array.Empty<Player>());
        if (me == null)
        {
            return cards;
        }

        foreach (CardModel card in me.Deck.Cards)
        {
            cards.Add(card.ToSerializable());
        }

        return cards;
    }

    /// <summary>
    /// Rebuild the opponent's cards for display.
    ///
    /// `CardModel.FromSerializable` warns that callers should eventually put the card into an
    /// `ICardScope`. These deliberately go into none: they exist to be *drawn* on the entry screen
    /// and nothing else, they belong to a player who is not us, and the real cards arrive with the
    /// pre-combat state sync a moment later. Adding them to a scope is what would make them real.
    /// </summary>
    private static List<CardModel> RebuildDeck(List<SerializableCard>? cards)
    {
        List<CardModel> rebuilt = new List<CardModel>();
        if (cards == null)
        {
            return rebuilt;
        }

        foreach (SerializableCard card in cards)
        {
            try
            {
                rebuilt.Add(CardModel.FromSerializable(card));
            }
            catch (Exception e)
            {
                // One unreadable card should cost that card, not the entry screen.
                Log.Warn($"[SpirePvp] arena: could not rebuild an opponent card ({e.Message})");
            }
        }

        return rebuilt;
    }

    /// <summary>
    /// The moment the second player clicks the arena, both sides leave the race behind.
    ///
    /// The duel phase begins here rather than at arena entry, because the deck review is
    /// already part of the duel: the clocks switch to chess-clock semantics and the top bar
    /// splits into YOU/OPP, so studying their deck visibly costs you time you will want in the
    /// fight. Waiting until the arena loaded would have left the race's shared countdown on
    /// screen through a decision that is no longer shared.
    ///
    /// The map is closed at the same time. The deck screen is an overlay, so without this it
    /// sits on top of a still-visible map — and the map is meaningless now that both players
    /// have arrived.
    ///
    /// `Close(animateOut: false)`, not `Visible = false`. Hiding the node leaves
    /// `NMapScreen.IsOpen` true, and `ActiveScreenContext.GetCurrentScreen` tests `IsOpen`
    /// *before* the combat room — so an invisible map goes on being the active screen and the
    /// arena's combat UI stays disabled. That is what froze the cards in the arena. Close does
    /// the hiding as well, so nothing is lost by going through it.
    ///
    /// The swap happens behind the run's own room transition (`RunManager.FadeOut`/`FadeIn`),
    /// the same fade every map → room move uses. Cutting straight from the map to a full-screen
    /// card grid read as a glitch rather than as arriving somewhere.
    /// </summary>
    private static void TryOpenDeckReview()
    {
        if (!_localArrived || !_remoteArrived)
        {
            return;
        }

        Log.Warn("[SpirePvp] arena: both players present — entering duel, opening deck review");

        // Phase first and synchronously: the clocks switch to chess-clock semantics here, so
        // the fade must be charged to the duel, not to the race.
        DuelSession.ActivateDuel(OpponentNetId());

        TaskHelper.RunSafely(OpenDeckReviewBehindFade());
    }

    private static async Task OpenDeckReviewBehindFade()
    {
        RunManager? run = RunManager.Instance;
        if (run == null)
        {
            return;
        }

        // The wipe sfx belongs to this fade — NMapScreen.TravelToMapCoord plays it against the
        // same FadeOut on every ordinary map → room move.
        SfxCmd.Play("event:/sfx/ui/wipe_map");

        await run.FadeOut();
        NMapScreen.Instance?.Close(animateOut: false);
        DuelEntry.Open();
        await run.FadeIn();
    }

    /// <summary>
    /// First arrival wins and later ones are ignored, so this is safe to call on every arrival.
    /// </summary>
    private static void RecordArrival(ulong netId)
    {
        if (_firstToArrive != 0 || netId == 0)
        {
            return;
        }

        _firstToArrive = netId;
        Log.Warn($"[SpirePvp] arena: {netId} reached the arena first — they take the opening initiative");
    }

    private static ulong OpponentNetId()
    {
        RunState? state = RunManager.Instance?.State;
        if (state == null)
        {
            return 0;
        }

        foreach (Player player in state.Players)
        {
            if (!LocalContext.IsMe(player))
            {
                return player.NetId;
            }
        }
        return 0;
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
