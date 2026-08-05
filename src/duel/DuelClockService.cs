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
        if ((now - _lastSync).TotalMilliseconds < SyncIntervalMs)
        {
            return;
        }

        bool isHost = RunManager.Instance?.NetService?.Type == NetGameType.Host;

        // Authority moves with the phase, because knowledge does.
        //
        // In the duel there is one shared combat, so the host can see both players' end-turn
        // state and owns both clocks — the usual "host decides, clients display" rule.
        //
        // In the race the players are in separate combats and their action traffic is
        // deliberately dropped, so the host has no idea when the other player ended a turn.
        // A host-owned clock would simply be wrong. Each client therefore owns its own clock
        // during the race and reports it, and each side displays the other's last report.
        //
        // Self-reported time is trivially spoofable by a modified client. That is acceptable
        // here — this is a mod played between friends, both of whom must already be running
        // identical builds to connect at all — and the duel, where the match is actually
        // decided, is host-authoritative regardless.
        if (!isHost && !DuelSession.IsRaceActive)
        {
            return;
        }

        _lastSync = now;

        if (DuelSession.IsRaceActive)
        {
            // Report only our own clock; the opponent slot is left empty so the receiver
            // cannot mistake our guess about them for fact.
            RunManager.Instance!.NetService.SendMessage(new ClockSyncMessage
            {
                playerA = _local!.PlayerId,
                playerARemainingMs = (int)_local.RemainingMs,
                playerB = 0,
                playerBRemainingMs = 0
            });
            return;
        }

        RunManager.Instance!.NetService.SendMessage(new ClockSyncMessage
        {
            playerA = _local!.PlayerId,
            playerARemainingMs = (int)_local.RemainingMs,
            playerB = _opponent!.PlayerId,
            playerBRemainingMs = (int)_opponent.RemainingMs
        });
    }

    /// <summary>
    /// Apply an incoming clock report.
    ///
    /// During the duel this is the host's authoritative pair and a client snaps both clocks to
    /// it. During the race it is a peer reporting only itself, so we take their value and never
    /// let it touch our own — our clock is ours to own while the runs are decoupled.
    /// </summary>
    public static void ApplySync(ClockSyncMessage message)
    {
        if (DuelSession.IsRaceActive)
        {
            if (_opponent != null && message.playerA == _opponent.PlayerId)
            {
                _opponent.CorrectTo(message.playerARemainingMs);
            }
            return;
        }

        if (RunManager.Instance?.NetService?.Type == NetGameType.Host)
        {
            return;
        }

        Correct(message.playerA, message.playerARemainingMs);
        Correct(message.playerB, message.playerBRemainingMs);
    }

    private static void Correct(ulong playerId, int remainingMs)
    {
        if (_local != null && _local.PlayerId == playerId)
        {
            _local.CorrectTo(remainingMs);
        }
        else if (_opponent != null && _opponent.PlayerId == playerId)
        {
            _opponent.CorrectTo(remainingMs);
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

        CombatState? state = CombatManager.Instance.DebugOnlyGetState();

        // Outside combat — map, shop, event, rest site — every clock runs. Deliberate: a
        // competitive run should not stop the clock while you read an event or browse a shop.
        if (state == null)
        {
            _local.Start();
            _opponent.Start();
            return;
        }

        foreach (Player player in state.Players)
        {
            bool isMe = LocalContext.IsMe(player);
            DuelClock clock = isMe ? _local : _opponent;

            // Chess-clock rule, and it applies during the race as much as the duel: ending
            // your turn stops YOUR clock while your opponent's keeps running. That is the
            // pressure — finish the turn fast, and trade a little accuracy for time.
            if (CombatManager.Instance.IsPlayerReadyToEndTurn(player))
            {
                clock.Pause();
            }
            else
            {
                clock.Start();
            }

            // During the race the opponent is in their own combat, which this client cannot
            // see, so never infer their state from ours — their value arrives by message.
            if (!isMe && DuelSession.IsRaceActive)
            {
                _opponent.Start();
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
