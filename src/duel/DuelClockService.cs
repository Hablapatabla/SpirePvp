using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// Owns the chess clocks for a match (DESIGN §3.2).
///
/// Run-scoped, not duel-scoped: the bank covers the whole run, so the clock starts at run
/// start and survives room transitions. M5's race phase needs no retrofit — it is already
/// running by then.
///
/// Two phases, two meanings — settled 2026-08-05 by playing both:
///   race — a global countdown. Reach the arena before the bank empties. Both clocks run
///          continuously and never pause, so they stay identical. Stopping one while the
///          other ran would measure nothing: the players are in separate combats and never
///          wait on each other. Time spent racing is time you will not have in the duel.
///   duel — a real chess clock. Here you *do* wait on each other, so ending your turn stops
///          your clock while your opponent's keeps running.
///
/// Host-authoritative in both phases: nothing pauses during the race, and in the duel the
/// host sees both players' end-turn state directly. Sync carries each clock's paused flag so
/// a client's prediction stops when the owner's does, rather than counting a stopped clock
/// down and snapping it back twice a second.
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

    private const double SyncIntervalMs = 500;

    private static DuelClock? _local;
    private static DuelClock? _opponent;
    private static DateTime _lastTick;
    private static DateTime _lastSync;
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

        BroadcastSyncIfHost(now);
    }

    /// <summary>
    /// Host → all, roughly twice a second. Clients keep ticking locally between syncs so the
    /// display stays smooth, and snap to these values when they arrive.
    ///
    /// This corrects a start-time offset as much as any drift: each client starts its clock
    /// when its own duel entry completes, and those moments differ by the round-trip plus
    /// room-entry timing. Without correction that offset persists for the whole match —
    /// about a second over a minute in practice.
    /// </summary>
    private static void BroadcastSyncIfHost(DateTime now)
    {
        if (RunManager.Instance?.NetService?.Type != NetGameType.Host)
        {
            return;
        }

        if ((now - _lastSync).TotalMilliseconds < SyncIntervalMs)
        {
            return;
        }

        _lastSync = now;

        RunManager.Instance.NetService.SendMessage(new ClockSyncMessage
        {
            playerA = _local!.PlayerId,
            playerARemainingMs = (int)_local.RemainingMs,
            playerAPaused = !_local.IsRunning,
            playerB = _opponent!.PlayerId,
            playerBRemainingMs = (int)_opponent.RemainingMs,
            playerBPaused = !_opponent.IsRunning
        });
    }

    /// <summary>
    /// Apply an incoming clock report.
    ///
    /// The host's authoritative pair; clients snap both clocks to it. Host-owned in both
    /// phases — nothing pauses during the race, and in the duel the host sees both players'
    /// end-turn state directly.
    /// </summary>
    public static void ApplySync(ClockSyncMessage message)
    {
        if (RunManager.Instance?.NetService?.Type == NetGameType.Host)
        {
            return;
        }

        Correct(message.playerA, message.playerARemainingMs, message.playerAPaused);
        Correct(message.playerB, message.playerBRemainingMs, message.playerBPaused);
    }

    /// <summary>
    /// Mirror the owner's running state so local prediction moves the same way theirs does.
    /// Without this a paused clock keeps counting down here and jumps back on the next sync.
    /// </summary>
    private static void ApplyReportedPause(DuelClock clock, bool paused)
    {
        if (paused)
        {
            clock.Pause();
        }
        else
        {
            clock.Start();
        }
    }

    private static void Correct(ulong playerId, int remainingMs, bool paused)
    {
        if (_local != null && _local.PlayerId == playerId)
        {
            _local.CorrectTo(remainingMs);
            ApplyReportedPause(_local, paused);
        }
        else if (_opponent != null && _opponent.PlayerId == playerId)
        {
            _opponent.CorrectTo(remainingMs);
            ApplyReportedPause(_opponent, paused);
        }
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

        // RACE — a global countdown, not a chess clock. The players are in separate combats
        // and never wait on each other, so stopping one clock while the other runs would
        // measure nothing. Both simply count down: reach the arena before the bank empties.
        // Starting together and never pausing is what keeps the two values identical, which
        // is what "global timer" means here. Time spent racing is time you lack in the duel.
        if (!DuelSession.IsDuelActive)
        {
            _local.Start();
            _opponent.Start();
            return;
        }

        // DUEL — now a real chess clock, because now you *do* wait on each other. Ending your
        // turn stops your clock while your opponent's keeps running.
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

        // Always m:ss. An earlier version switched to tenths under ten seconds, which made
        // the label change shape exactly when you are most likely to be staring at it.
        TimeSpan span = TimeSpan.FromMilliseconds(ms);
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }
}
