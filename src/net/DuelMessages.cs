using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

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
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
    }

    public void Deserialize(PacketReader reader)
    {
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

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(modVersion);
    }

    public void Deserialize(PacketReader reader)
    {
        modVersion = reader.ReadString();
    }
}
