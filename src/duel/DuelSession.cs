namespace SpirePvp.Duel;

public enum DuelPhase
{
    Inactive,
    RaceActive,
    LocalReady,
    DuelPending,
    DuelActive,
    Complete
}

/// <summary>
/// Client-local state machine for a PvP match (see DESIGN §5). Every Harmony patch in this
/// mod is a no-op unless the relevant phase is active, so the mod is inert in normal play.
/// Phase transitions are driven by DuelMessages so all clients agree on the phase.
/// </summary>
public static class DuelSession
{
    public static DuelPhase Phase { get; private set; } = DuelPhase.Inactive;

    /// <summary>NetId of the local player's opponent. 0 when no match is running.</summary>
    public static ulong OpponentId { get; private set; }

    public static bool IsDuelActive => Phase == DuelPhase.DuelActive;

    public static bool IsRaceActive => Phase == DuelPhase.RaceActive;

    public static void Reset()
    {
        Phase = DuelPhase.Inactive;
        OpponentId = 0;
    }

    // TODO(M1): message handler registration (INetGameService.RegisterMessageHandler for
    // DuelStartMessage etc.) and phase transitions. Registration must happen when a net
    // service exists — find the lobby/run start hook (Core/Multiplayer/Game/Lobby).
}
