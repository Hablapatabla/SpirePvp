namespace SpirePvp.Duel;

/// <summary>
/// Pure chess-clock logic for one player: a time bank that ticks while the round is live
/// and the player hasn't ended turn (DESIGN §3.2). No game or Godot dependencies — keep it
/// that way so it stays unit-testable. The host owns the authoritative pair of these;
/// clients hold predicted copies corrected by ClockSyncMessage.
/// </summary>
public sealed class DuelClock
{
    public ulong PlayerId { get; }

    public double RemainingMs { get; private set; }

    public bool IsRunning { get; private set; }

    public bool HasFlagged => RemainingMs <= 0;

    /// <summary>Raised once when the bank hits zero. Host reacts by sending ForcedEndTurnMessage.</summary>
    public event Action<DuelClock>? Flagged;

    public DuelClock(ulong playerId, double initialMs)
    {
        PlayerId = playerId;
        RemainingMs = initialMs;
    }

    /// <summary>Round's player phase opened (or player backed out of end turn).</summary>
    public void Start()
    {
        if (!HasFlagged)
        {
            IsRunning = true;
        }
    }

    /// <summary>Player declared end turn (or the round resolved).</summary>
    public void Pause() => IsRunning = false;

    /// <summary>Advance by frame/tick delta. Call from a per-frame hook on the host.</summary>
    public void Tick(double deltaMs)
    {
        if (!IsRunning || HasFlagged)
        {
            return;
        }
        RemainingMs -= deltaMs;
        if (RemainingMs <= 0)
        {
            RemainingMs = 0;
            IsRunning = false;
            this.Flagged?.Invoke(this);
        }
    }

    /// <summary>Client-side: snap to an authoritative value from ClockSyncMessage.</summary>
    public void CorrectTo(double authoritativeMs)
    {
        RemainingMs = authoritativeMs;
    }

    /// <summary>Fischer increment (design knob, DESIGN §9).</summary>
    public void AddIncrement(double ms)
    {
        RemainingMs += ms;
    }
}
