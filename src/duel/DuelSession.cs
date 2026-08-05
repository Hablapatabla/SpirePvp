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

    /// <summary>
    /// M1 spike entry point (DESIGN §5: "for M1 the entry is just a dev-console command").
    /// Driven by <c>duel on</c>; because that command is networked, every client runs it,
    /// so the phase flips in lockstep without any custom message plumbing yet.
    /// </summary>
    public static void ActivateDuel(ulong opponentId)
    {
        Phase = DuelPhase.DuelActive;
        OpponentId = opponentId;
    }

    /// <summary>
    /// M5 spike entry point. Driven by the networked <c>race on</c> command, so both clients
    /// enter race mode in the same action stream — which matters, because a race is only
    /// coherent if both sides agree to stop synchronizing at the same moment.
    /// </summary>
    public static void ActivateRace()
    {
        Phase = DuelPhase.RaceActive;
    }

    /// <summary>True when the local player won the duel that just finished.</summary>
    public static bool LocalPlayerWon { get; private set; }

    public static void CompleteDuel(bool localPlayerWon)
    {
        Phase = DuelPhase.Complete;
        LocalPlayerWon = localPlayerWon;
    }

    // TODO(M6): real phase transitions over DuelMessages (INetGameService.RegisterMessageHandler
    // for DuelStartMessage etc.), replacing the console command. Registration must happen when a
    // net service exists — find the lobby/run start hook (Core/Multiplayer/Game/Lobby).
}
