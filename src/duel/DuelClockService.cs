using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;

namespace SpirePvp.Duel;

/// <summary>
/// Owns the chess clocks for a match (DESIGN §3.2).
///
/// Run-scoped, not duel-scoped: the bank covers the whole run, so the clock starts at run
/// start and survives room transitions. M5's race phase needs no retrofit — it is already
/// running by then.
///
/// Tick semantics differ by phase, per the design:
///   race  — both players act continuously and simultaneously, so both clocks just run down.
///   duel  — a true chess clock: yours runs while you have not ended turn.
///
/// Duration 0 disables the clock entirely; nobody can lose on time. That is the default, so
/// the mod stays inert for anyone who has not opted in.
///
/// Time is measured from wall-clock deltas rather than accumulated ticks, so display
/// granularity and flag accuracy do not depend on how often the UI refreshes, and slow
/// frames cannot make the clock drift.
/// </summary>
public static class DuelClockService
{
    /// <summary>Bank per player. Zero means no clock.</summary>
    public static double ConfiguredMs { get; private set; }

    public static bool Enabled => ConfiguredMs > 0;

    private static DuelClock? _local;
    private static DuelClock? _opponent;
    private static DateTime _lastTick;
    private static bool _running;

    public static DuelClock? Local => _local;

    public static DuelClock? Opponent => _opponent;

    /// <summary>Set the bank, in minutes. 0 turns the clock off. Resets any running clocks.</summary>
    public static void Configure(double minutes)
    {
        ConfiguredMs = Math.Max(0, minutes) * 60_000;
        _local = null;
        _opponent = null;
        _running = false;
    }

    /// <summary>
    /// Begin the match clocks. Safe to call more than once; only the first call after a
    /// Configure creates the banks.
    /// </summary>
    public static void Start(ulong localId, ulong opponentId)
    {
        if (!Enabled || _local != null)
        {
            return;
        }

        _local = new DuelClock(localId, ConfiguredMs);
        _opponent = new DuelClock(opponentId, ConfiguredMs);
        _lastTick = DateTime.UtcNow;
        _running = true;

        _local.Start();
        _opponent.Start();

        Log.Warn($"[SpirePvp] clocks started at {ConfiguredMs / 60000:0.##} min each");
    }

    public static void Stop()
    {
        _running = false;
        _local?.Pause();
        _opponent?.Pause();
    }

    public static void Reset()
    {
        _local = null;
        _opponent = null;
        _running = false;
    }

    /// <summary>
    /// Advance both clocks by real elapsed time and apply phase rules. Driven from the
    /// top-bar timer refresh, but independent of its cadence.
    /// </summary>
    public static void Tick()
    {
        if (!Enabled || !_running || _local == null || _opponent == null)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        double deltaMs = (now - _lastTick).TotalMilliseconds;
        _lastTick = now;

        if (deltaMs <= 0)
        {
            return;
        }

        ApplyPhaseRules();

        _local.Tick(deltaMs);
        _opponent.Tick(deltaMs);
    }

    /// <summary>
    /// During a duel a player's clock stops once they have declared end turn. Outside a duel
    /// (the race) both clocks simply run.
    /// </summary>
    private static void ApplyPhaseRules()
    {
        if (_local == null || _opponent == null)
        {
            return;
        }

        if (!DuelSession.IsDuelActive)
        {
            _local.Start();
            _opponent.Start();
            return;
        }

        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            return;
        }

        foreach (Player player in state.Players)
        {
            DuelClock clock = LocalContext.IsMe(player) ? _local : _opponent;
            if (CombatManager.Instance.IsPlayerReadyToEndTurn(player))
            {
                clock.Pause();
            }
            else
            {
                clock.Start();
            }
        }
    }

    /// <summary>Label text for the top bar: "YOU 2:31 · OPP 1:47".</summary>
    public static string? FormatForHud()
    {
        if (!Enabled || _local == null || _opponent == null)
        {
            return null;
        }

        return $"YOU {Format(_local.RemainingMs)} · OPP {Format(_opponent.RemainingMs)}";
    }

    private static string Format(double ms)
    {
        if (ms <= 0)
        {
            return "0:00.0";
        }

        TimeSpan span = TimeSpan.FromMilliseconds(ms);

        // Tenths under ten seconds, where the difference actually matters.
        if (ms < 10_000)
        {
            return $"{span.Seconds}.{span.Milliseconds / 100}";
        }

        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }
}
