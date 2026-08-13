using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace SpirePvp.Net;

// All INetMessage subtypes in a mod assembly are auto-registered by
// MessageTypes.Initialize (it scans mods via ReflectionHelper.GetSubtypesInMods).
// Message ids are positional — both clients MUST run the same mod version, which is
// why DuelReadyMessage carries the mod version for a handshake check.

/// <summary>Broadcast periodically during the race so the opponent's progress HUD updates.</summary>
public record struct RaceProgressMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.VeryDebug;

    public bool ShouldBuffer => true;

    public int mapRow;

    public int mapCol;

    public int currentHp;

    public int maxHp;

    public int deckSize;

    public bool actBossDefeated;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(mapRow);
        writer.WriteInt(mapCol);
        writer.WriteInt(currentHp);
        writer.WriteInt(maxHp);
        writer.WriteInt(deckSize);
        writer.WriteBool(actBossDefeated);
    }

    public void Deserialize(PacketReader reader)
    {
        mapRow = reader.ReadInt();
        mapCol = reader.ReadInt();
        currentHp = reader.ReadInt();
        maxHp = reader.ReadInt();
        deckSize = reader.ReadInt();
        actBossDefeated = reader.ReadBool();
    }
}

/// <summary>Sent to the host when a player has finished Act 1 + rewards and is ready to duel.</summary>
public record struct DuelReadyMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public string modVersion;

    /// <summary>
    /// False when the player revokes. Confirming is revocable right up until the opponent
    /// confirms too, so the screen is a negotiation rather than a commitment.
    /// </summary>
    public bool isReady;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(modVersion);
        writer.WriteBool(isReady);
    }

    public void Deserialize(PacketReader reader)
    {
        modVersion = reader.ReadString();
        isReady = reader.ReadBool();
    }
}

/// <summary>
/// Host → all: both players are ready, enter the duel.
///
/// Carries nothing. It once held `clockMs` and `suddenDeath`, from a design where the host
/// chose the duel's parameters and announced them here. §5b replaced that: the clocks and the
/// turn model are modifiers on the run, so both clients already hold identical values before
/// the duel starts and reading them off the run is one source of truth instead of two. The
/// fields were still being written and never read — a reader would reasonably conclude the
/// clock was negotiated at duel start, which has not been true since the modifiers landed.
///
/// The message itself is still needed, and is the point: two clients independently deciding to
/// enter a room is a race, so exactly one of them decides. M8 may add the turn model here when
/// something actually reads it; a field nobody reads is worse than no field.
/// </summary>
public record struct DuelStartMessage : INetMessage
{
    /// <summary>
    /// Who strikes first in the duel's opening turn, and therefore who strikes first in every
    /// odd-numbered one — the turn model alternates from here (M9).
    ///
    /// **It has to be on the wire because arrival order is not a local fact.** Whoever reached the
    /// arena first is the rule, and each client only knows when its *own* arrival happened and when
    /// the other's message reached it: on a slow link both can honestly believe they were first.
    /// The host sees both in one order, so the host decides — the same reasoning every other duel
    /// parameter follows, and the reason this message exists at all.
    ///
    /// This is the field `DuelStartMessage` was left empty waiting for: "a field nobody reads is
    /// worse than no field", and now `LockInTurnModel.StartsTheRound` reads it.
    /// </summary>
    public ulong firstInitiative;

    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(firstInitiative);
    }

    public void Deserialize(PacketReader reader)
    {
        firstInitiative = reader.ReadULong();
    }
}

/// <summary>Host → all, unreliable, ~2/sec: authoritative clock values for HUD smoothing.</summary>
public record struct ClockSyncMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Unreliable;

    public LogLevel LogLevel => LogLevel.VeryDebug;

    public bool ShouldBuffer => false;

    public ulong playerA;

    public int playerARemainingMs;

    /// <summary>
    /// Whether this clock is currently stopped. Without it the receiver keeps predicting a
    /// paused clock downward and then snaps back on every sync — visible rubber-banding at
    /// the sync interval. With it, local prediction matches the owner and corrections are
    /// sub-frame.
    /// </summary>
    public bool playerAPaused;

    public ulong playerB;

    public int playerBRemainingMs;

    public bool playerBPaused;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(playerA);
        writer.WriteInt(playerARemainingMs);
        writer.WriteBool(playerAPaused);
        writer.WriteULong(playerB);
        writer.WriteInt(playerBRemainingMs);
        writer.WriteBool(playerBPaused);
    }

    public void Deserialize(PacketReader reader)
    {
        playerA = reader.ReadULong();
        playerARemainingMs = reader.ReadInt();
        playerAPaused = reader.ReadBool();
        playerB = reader.ReadULong();
        playerBRemainingMs = reader.ReadInt();
        playerBPaused = reader.ReadBool();
    }
}

/// <summary>Host → all: a player's flag fell; force-end their turn (see DESIGN §3.2, I5).</summary>
public record struct ForcedEndTurnMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public ulong flaggedPlayerId;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(flaggedPlayerId);
    }

    public void Deserialize(PacketReader reader)
    {
        flaggedPlayerId = reader.ReadULong();
    }
}

/// <summary>Host → all: duel is over.</summary>
public record struct DuelResultMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public ulong winnerId;

    /// <summary>
    /// How the match ended. Values are <see cref="Duel.DuelEndReason"/> — do not restate them
    /// here; this comment previously listed a set that had drifted out of step with the code.
    /// </summary>
    public int reason;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(winnerId);
        writer.WriteInt(reason);
    }

    public void Deserialize(PacketReader reader)
    {
        winnerId = reader.ReadULong();
        reason = reader.ReadInt();
    }
}

/// <summary>
/// A draw offer, and the answer to one — both directions on one type.
///
/// Appended at the end of this file deliberately. Message ids are positional, so inserting a
/// type above an existing one renumbers everything after it; adding at the bottom leaves the
/// established ids alone. (Both players must still run the same build, as ever.)
/// </summary>
public record struct DuelDrawOfferMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    /// <summary>False = "I offer a draw"; true = "here is my answer to yours".</summary>
    public bool isResponse;

    /// <summary>Meaningful only when <see cref="isResponse"/> is true.</summary>
    public bool accepted;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(isResponse);
        writer.WriteBool(accepted);
    }

    public void Deserialize(PacketReader reader)
    {
        isResponse = reader.ReadBool();
        accepted = reader.ReadBool();
    }
}

/// <summary>
/// A player's race and duel numbers, broadcast as the match is decided so the result screen can
/// compare the two players rather than score a run neither of them played.
///
/// Sent rather than read locally because the race deliberately decouples the clients: each
/// one's `MapPointHistory` records only its own moves, so the opponent's gold and elites are not
/// in local state to be looked up. Same reason `RaceProgressMessage` exists.
///
/// Appended at the end of the file, like `DuelDrawOfferMessage` — message ids are positional, so
/// new types go at the bottom and leave the established ids alone.
/// </summary>
public record struct DuelStatsMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public int cardsPlayed;

    public int damageDealt;

    public int currentHp;

    public int maxHp;

    public int deckSize;

    public int floorsClimbed;

    public int goldGained;

    public int elitesKilled;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(cardsPlayed);
        writer.WriteInt(damageDealt);
        writer.WriteInt(currentHp);
        writer.WriteInt(maxHp);
        writer.WriteInt(deckSize);
        writer.WriteInt(floorsClimbed);
        writer.WriteInt(goldGained);
        writer.WriteInt(elitesKilled);
    }

    public void Deserialize(PacketReader reader)
    {
        cardsPlayed = reader.ReadInt();
        damageDealt = reader.ReadInt();
        currentHp = reader.ReadInt();
        maxHp = reader.ReadInt();
        deckSize = reader.ReadInt();
        floorsClimbed = reader.ReadInt();
        goldGained = reader.ReadInt();
        elitesKilled = reader.ReadInt();
    }
}

/// <summary>
/// "I have reached the arena and am waiting." Sent when a player clicks the arena node, which
/// deliberately does not enter the room — the arena is the one rendezvous in an otherwise
/// independent race (DESIGN §5b). When both have arrived, the deck-review screen opens on both
/// clients and DuelEntry takes over from there.
/// </summary>
public record struct DuelArrivedMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public string modVersion;

    /// <summary>
    /// The sender's HP after their arena rest, and their max HP.
    ///
    /// **Same reason the deck travels, and it took a desync to see it.** The heal used to run on
    /// arena entry, after the pre-combat sync — safe, but too late to show on the deck review,
    /// where you can read both players' HP. Moving it earlier meant each client mutating its own
    /// duelist during the race, and the sync does *not* carry your own state to the peer: it fixes
    /// your copy of *them*. Measured 2026-08-13 — host healed 56 to 70, client healed 52 to 66, and
    /// the first checksum of the duel diverged, because the host was still holding 52 for the
    /// client.
    ///
    /// So the healed value is sent, exactly as the deck is, and for the identical reason: what the
    /// peer needs must be sent, not looked up. Riding on the arrival keeps the ordering free.
    /// </summary>
    public int hp;

    public int maxHp;

    /// <summary>
    /// The sender's deck, as it stands on arrival.
    ///
    /// **The decklist has to travel, because the reader's copy of it is a lie.** The race
    /// decouples the two runs — that is the whole of M5 — so the opponent's `Player` in your local
    /// run state is frozen at whatever it was when the race began. Their rewards, their upgrades,
    /// their removals all happened on their client and nowhere else. The pre-combat state sync
    /// does reconcile it, but that runs on *arena entry*, and the deck review opens before then:
    /// so the entry screen was showing a stale deck, missing every card the opponent had picked up
    /// — cards they then played in the duel, because by then the sync had happened. A decklist
    /// that is quietly wrong is worse than none at all; the reveal is a core information rule
    /// (DESIGN §1) and the whole point is that you can trust what it says.
    ///
    /// Carried on *arrival* rather than in a message of its own, which is what makes the ordering
    /// free: the review opens when both arrivals are in hand, so the deck is there by construction
    /// with no second handler to arm and no race to lose.
    /// </summary>
    public List<SerializableCard> deck;

    /// <summary>
    /// The sender's relics, for the same reason and by the same route as <see cref="deck"/>.
    ///
    /// Wanted after the 2026-08-12 session: *"the opponent's relics are not shown in the deck
    /// review."* They fall under the identical rule — your copy of their `Player` stopped updating
    /// when the race began, so every relic they picked up in the race is invisible to you, and a
    /// relic list that is quietly wrong is worse than none. **This has to be sent, not looked up.**
    ///
    /// Riding on the arrival message rather than in one of its own keeps the ordering free, exactly
    /// as the deck does: the review opens once both arrivals are in hand.
    ///
    /// **Appended after the deck, not inserted.** Field order is the wire format here — `Serialize`
    /// and `Deserialize` are hand-written and positional in the same way message *ids* are — so a
    /// new field goes last. Both clients must be on the same build regardless (the engine's own
    /// mod-match gate enforces it), which is what makes adding one safe at all.
    /// </summary>
    public List<SerializableRelic> relics;


    // **A field on the struct is not a field on the wire.** These are hand-written serializers, so
    // adding `hp`/`maxHp` above and populating them at the send site left them out of the packet
    // entirely: the receiver read the struct's default and every arrival announced `hp=0/0`, which
    // the guard in `DuelRendezvous.ApplyOpponentHp` correctly refused. Nothing failed to compile,
    // nothing threw, and the only symptom was a first-checksum divergence. When adding a field to
    // any message in this file, add it here in the same order on both sides.
    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(modVersion);
        writer.WriteInt(hp);
        writer.WriteInt(maxHp);
        writer.WriteList<SerializableCard>(deck ?? new List<SerializableCard>());
        writer.WriteList<SerializableRelic>(relics ?? new List<SerializableRelic>());
    }

    public void Deserialize(PacketReader reader)
    {
        modVersion = reader.ReadString();
        hp = reader.ReadInt();
        maxHp = reader.ReadInt();
        deck = reader.ReadList<SerializableCard>();
        relics = reader.ReadList<SerializableRelic>();
    }
}

/// <summary>
/// "Play it again" — the offer and its answer, exchanged on the result screen.
///
/// **Same shape as `DuelDrawOfferMessage`, and for the same reason:** a rematch needs both
/// players, offers that cross on the wire are agreement rather than a conflict, and the answer
/// has to be able to say no. Rather than share that message's type, this is its own — a draw ends
/// a match and a rematch starts one, and one field meaning two opposite things is exactly the
/// drift `DuelEndReason` exists to prevent.
///
/// **No seed rides on it, deliberately.** A rematch replays the same seed, and both clients
/// already hold it in the run they are looking at — identical by construction, since a shared seed
/// is the premise of the whole mode. Sending it would invite the two copies to disagree and give
/// the receiver a reason to trust the wire over its own state.
///
/// Appended at the bottom, like every message since `DuelDrawOfferMessage`: ids are positional, so
/// new types go last and leave the established ones alone.
/// </summary>
public record struct DuelRematchMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    /// <summary>False = "I want a rematch"; true = "here is my answer to yours".</summary>
    public bool isResponse;

    /// <summary>Meaningful only when <see cref="isResponse"/> is true.</summary>
    public bool accepted;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(isResponse);
        writer.WriteBool(accepted);
    }

    public void Deserialize(PacketReader reader)
    {
        isResponse = reader.ReadBool();
        accepted = reader.ReadBool();
    }
}

/// <summary>
/// "I have locked in" — one player's half of a turn-based round (DESIGN §3.1b).
///
/// **Carries no actions, and that is the point.** The plays themselves travel by the engine's own
/// `RequestEnqueueActionMessage`, exactly as they do in blitz; the only difference is that the
/// client sends them all at lock-in rather than as it clicks, and the host holds them instead of
/// enqueuing on arrival. So the lock-in model needs no wire format of its own for actions, and the
/// action path stays the one the engine already debugs and the mod already trusts.
///
/// **Ordering is what makes that safe.** The transport is reliable and ordered, and the client
/// sends its plays *before* this message — so a host holding this in its hand knows the client's
/// buffer is complete. There is no "have I got them all yet" question to answer, which is the kind
/// of question this project keeps getting wrong.
/// </summary>
public record struct DuelLockInMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    /// <summary>How many plays the sender locked in. Host-side sanity check, and a log line worth having.</summary>
    public int playCount;

    public void Serialize(PacketWriter writer) => writer.WriteInt(playCount);

    public void Deserialize(PacketReader reader) => playCount = reader.ReadInt();
}

/// <summary>
/// One play the host has booked but not yet released — an element of
/// <see cref="DuelPendingPlaysMessage"/>.
///
/// **The card travels as its display name rather than as a model id**, and that is a deliberate
/// narrowing. Nothing on the receiving side looks this up, constructs a card from it, or lets it
/// touch the simulation: it is drawn as text over the opponent and thrown away on the next update.
/// A model id would invite exactly the lookup that turns a presentation message into a second,
/// quietly divergent source of truth about what is in play.
/// </summary>
public struct SerializablePendingPlay : IPacketSerializable
{
    public ulong owner;

    public string cardName;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(owner);
        writer.WriteString(cardName ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        owner = reader.ReadULong();
        cardName = reader.ReadString();
    }
}

/// <summary>
/// Host → all: everything currently waiting in `DuelPlayScheduler`, so each player can see what the
/// other has committed but not yet had resolved (M8.5 slice 3).
///
/// **This is the piece that makes paced real-time worth pacing.** Without it you only ever see a
/// play once it has been *released*, which is at most the length of one beat of warning — not enough
/// to read, and answering is the entire point of the mode. The plays exist, ordered, in the host's
/// pool for as long as a burst takes to drain; this puts that pool on the wire.
///
/// **A deliberate change to the information rules** (DESIGN §1), decided as such rather than
/// arrived at: you may now see that a card is coming before it lands. It reveals only what has been
/// irrevocably committed — a play in the pool has been clicked and cannot be taken back — so it
/// exposes no intention the opponent could still change their mind about.
///
/// **Full state on every change, never a delta.** The pool is small and the rule this project keeps
/// paying for is that a message which only fires on *change* cannot carry initial state. Sending the
/// whole pool means a receiver that misses nothing needs no catch-up path and no arrival hook, and a
/// late or reordered update simply overwrites with the truth.
///
/// `ShouldBuffer` is false for the same reason `ClockSyncMessage` sets it: this is a snapshot of a
/// live thing, worthless once the moment has passed, and buffering it past a run teardown is how the
/// "no message handlers are registered" residue gets made.
///
/// Appended last, like every message since `DuelDrawOfferMessage`: ids are positional.
/// </summary>
public record struct DuelPendingPlaysMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.VeryDebug;

    public bool ShouldBuffer => false;

    public List<SerializablePendingPlay> plays;

    public void Serialize(PacketWriter writer) =>
        writer.WriteList<SerializablePendingPlay>(plays ?? new List<SerializablePendingPlay>());

    public void Deserialize(PacketReader reader) => plays = reader.ReadList<SerializablePendingPlay>();
}
