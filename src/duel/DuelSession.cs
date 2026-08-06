namespace SpirePvp.Duel;

/// <summary>
/// How a match ended. `Draw` exists because the race clock cannot produce anything else:
/// both race banks start together and never pause, so they are equal by construction and hit
/// zero in the same tick. Declaring a winner there only reported which clock the service
/// happened to tick first — the local one, so the host always "lost" its own race. Nobody
/// reached the arena; nobody won.
/// </summary>
public enum DuelOutcome
{
    Won,
    Lost,
    Draw
}

/// <summary>
/// The phases a match actually has.
///
/// DESIGN §5 sketched `LocalReady` and `DuelPending` between the race and the duel. Neither was
/// ever set: readiness turned out to belong to the two handshakes that own it — `DuelRendezvous`
/// for "I am at the arena" and `DuelEntry` for "I have seen your deck" — each of which needs
/// *both* players' state, which a single local enum cannot hold. They are removed rather than
/// left unset, because several checks read "not `DuelActive`" as "still racing"
/// (`DuelFlag.OnClockFlagged` decides a draw on it), and an unused phase sitting in the enum is
/// an invitation to add a state that silently reroutes those.
/// </summary>
public enum DuelPhase
{
    Inactive,
    RaceActive,
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
        Outcome = DuelOutcome.Lost;
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

    /// <summary>How the match ended, from this client's point of view.</summary>
    public static DuelOutcome Outcome { get; private set; }

    /// <summary>True when the local player won the duel that just finished.</summary>
    public static bool LocalPlayerWon => Outcome == DuelOutcome.Won;

    public static void CompleteDuel(DuelOutcome outcome)
    {
        Phase = DuelPhase.Complete;
        Outcome = outcome;
    }

    // TODO(M6): real phase transitions over DuelMessages (INetGameService.RegisterMessageHandler
    // for DuelStartMessage etc.), replacing the console command. Registration must happen when a
    // net service exists — find the lobby/run start hook (Core/Multiplayer/Game/Lobby).
}
