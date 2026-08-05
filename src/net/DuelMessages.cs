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

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(modVersion);
    }

    public void Deserialize(PacketReader reader)
    {
        modVersion = reader.ReadString();
    }
}

/// <summary>Host → all: both players are ready, enter the duel with these parameters.</summary>
public record struct DuelStartMessage : INetMessage
{
    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    /// <summary>Time bank per player, milliseconds.</summary>
    public int clockMs;

    /// <summary>True = flag means instant loss; false = flag means auto-pass each round.</summary>
    public bool suddenDeath;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(clockMs);
        writer.WriteBool(suddenDeath);
    }

    public void Deserialize(PacketReader reader)
    {
        clockMs = reader.ReadInt();
        suddenDeath = reader.ReadBool();
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

    public ulong playerB;

    public int playerBRemainingMs;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(playerA);
        writer.WriteInt(playerARemainingMs);
        writer.WriteULong(playerB);
        writer.WriteInt(playerBRemainingMs);
    }

    public void Deserialize(PacketReader reader)
    {
        playerA = reader.ReadULong();
        playerARemainingMs = reader.ReadInt();
        playerB = reader.ReadULong();
        playerBRemainingMs = reader.ReadInt();
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

    /// <summary>How the duel ended: 0 = HP, 1 = flag (sudden death), 2 = concede, 3 = disconnect.</summary>
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
