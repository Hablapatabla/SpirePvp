using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;

namespace SpirePvp.Duel;

/// <summary>
/// Deciding a match whose opponent has gone away.
///
/// **There are two entirely different ways an opponent can vanish, and only one of them is
/// announced.** `DuelDisconnectPatch` handles the announced kind — a polite quit, which sends an
/// application-level `Disconnection` packet, and a Steam-transport drop, which `SteamHost`
/// reports properly. This class handles the other kind, which is the common one and was
/// completely silent:
///
/// **ENet never reports a hard drop.** `ENetHost.Update` handles the transport's own `Disconnect`
/// event with a bare `continue`, so a killed process, a closed window or a pulled cable produce
/// no event at all. Measured 2026-08-12 on two local clients: the client was closed mid-race and
/// the host played on for another eight hundred log lines, logging `Peer not connected` on every
/// send while `NetQualityTracker` sat there reporting `Packet Loss: 0.99999934` for a peer it
/// still considered present. Everything needed to notice was already being measured; nothing was
/// asking.
///
/// **So this asks.** `ConnectionStats.LastReceivedTime` is the same signal vanilla's own
/// `NMultiplayerTimeoutOverlay` watches to decide a peer has stopped responding, so this is
/// reading the engine's answer rather than inventing one — and heartbeats carry the peer's
/// loading flag, so a peer stuck on a loading screen is still *talking* and never looks silent.
///
/// **This is also the timer the reconnect window will need**, which is the main reason to build
/// it now: a window has to end somehow, and "the opponent has been silent for N seconds" is that
/// ending. When the window lands, this becomes its expiry rather than being replaced.
/// </summary>
public static class DuelDisconnect
{
    /// <summary>
    /// How long the notice sits on screen before the match is awarded.
    ///
    /// **Five seconds, because there is nothing to wait for.** This was thirty, with a "wait one
    /// more minute" button, on the assumption that a window let a dropped player come back. It
    /// does not: rejoining is not implemented in the game (see `docs/PLAYTEST_LIST.md`), so the
    /// window could only ever delay a result that was already decided. A countdown long enough to
    /// hope on is worse than a short one — it implies a return that cannot happen.
    ///
    /// What is left is a moment to read what happened before the screen changes.
    /// </summary>
    public const ulong ForfeitWindowMs = 5_000;

    /// <summary>
    /// How long a peer may be quiet before any of this starts.
    ///
    /// Only applies to heartbeat silence, where the peer may simply be hitching — vanilla treats
    /// three seconds as "unresponsive" and merely draws a curtain, so reacting sooner than that
    /// would end matches over nothing. A dead link needs no such grace: it is already certain.
    /// </summary>
    private const ulong SilenceBeforeNoticeMs = 5_000;

    /// <summary>Set once we have declared, so a silent peer is not re-declared every frame.</summary>
    private static bool _declared;

    /// <summary>Whether we currently have the timeout curtain up, so it is lowered exactly once.</summary>
    private static bool _showingNotice;

    /// <summary>When the match forfeits, in `Time.GetTicksMsec()`. Null while nobody is missing.</summary>
    private static ulong? _forfeitAtMs;

    /// <summary>
    /// When our own connection died, if it did — the client's route into the same wait.
    ///
    /// Needed because the two sides learn of a disappearance through different channels and only
    /// one of them leaves anything to measure. A host watching a client reads heartbeat silence;
    /// a client whose host vanishes gets a single `LocalPlayerDisconnected`, and the peer's stats
    /// are torn down with the connection, so there is no silence left to time. This timestamp is
    /// what gives that side a clock of its own.
    /// </summary>
    private static ulong? _connectionLostAtMs;

    /// <summary>
    /// Why the last client dropped, stashed by `DuelDisconnectPatch` on its way past.
    ///
    /// **`RunLobby` knows the reason and the event it raises does not carry it.**
    /// `OnDisconnectedFromClientAsHost` is handed a `NetErrorInfo`, logs it, and then raises
    /// `RemotePlayerDisconnected` with nothing but the player id — so the host's own route cannot
    /// tell a quit from a divergence kick it issued itself.
    ///
    /// It lives here rather than in the patch because of what it is: static state belonging to a
    /// run, which has to be released with one. The patch is where it is *set*; this is where
    /// everything else that outlives a match already gets cleared.
    /// </summary>
    private static NetError? _lastClientDropReason;

    /// <summary>Records the reason. Called from the prefix that runs before the event fires.</summary>
    public static void NoteClientDropReason(NetError reason) => _lastClientDropReason = reason;

    /// <summary>
    /// Reads the reason and forgets it, so a later disconnect cannot inherit this one.
    ///
    /// **Consuming is not sufficient on its own**, which is why `Reset` clears it too:
    /// `RunLobby` raises `RemotePlayerDisconnected` only `if (num >= 0)` — only when the player
    /// was still in its list — and it logs `Is in connected players: False` for the case where it
    /// is not. On that path the reason is stashed and never read, and without the teardown clear
    /// it would survive into the next match and decide *its* first disconnect: a polite quit
    /// reading a stale `StateDivergence` and being scored a void draw instead of a win.
    /// </summary>
    public static NetError? TakeClientDropReason()
    {
        NetError? reason = _lastClientDropReason;
        _lastClientDropReason = null;
        return reason;
    }

    /// <summary>Released with the run, like every other piece of static match state.</summary>
    public static void Reset()
    {
        _declared = false;
        _connectionLostAtMs = null;
        _lastClientDropReason = null;
        ClearWait("the run ended");
    }

    /// <summary>
    /// Our connection to the match died. Opens the same wait the other side gets, unless the
    /// opponent left on purpose.
    ///
    /// **A deliberate departure is not something to wait out.** Quitting, abandoning and kicking
    /// are decisions, and offering to wait a minute for someone who chose to leave would be
    /// pretending the match might resume. Everything else — a timeout, no internet, an unknown
    /// network error — is exactly the case the window exists for.
    /// </summary>
    public static void NoteConnectionLost(NetErrorInfo info)
    {
        NetError reason = info.GetReason();

        if (IsDesync(reason))
        {
            DeclareVoid(reason);
            return;
        }

        if (reason is NetError.Quit or NetError.HostAbandoned or NetError.Kicked)
        {
            Declare($"the opponent left deliberately ({reason})");
            return;
        }

        _connectionLostAtMs ??= Time.GetTicksMsec();
        Log.Warn($"[SpirePvp] lost the connection mid-match ({reason}) — opening the wait window");
    }

    /// <summary>
    /// Whether a wait is currently running, which is the window in which vanilla must *not* pull
    /// the player out to the main menu. See `DuelDisconnectPatch`.
    /// </summary>
    public static bool IsWaiting => _connectionLostAtMs != null || _forfeitAtMs != null;

    /// <summary>
    /// Whether a vanished opponent right now should decide the match.
    ///
    /// Asks whether there *is* a live match rather than testing a single phase: a drop during the
    /// race counts exactly as much as one during the duel, and a match already `Complete` must
    /// not be decided twice.
    ///
    /// The three exclusions are vanilla's own, lifted from `RunManager.LocalPlayerDisconnected`,
    /// which separates a genuine drop from the ordinary ways a connection ends. Leaving the
    /// result screen disconnects — that is a finished match, not a forfeit.
    /// </summary>
    public static bool ShouldDecide(RunManager? runManager)
    {
        if (runManager == null || runManager.IsAbandoned || runManager.State?.IsGameOver != false)
        {
            return false;
        }

        if (!DuelMatch.IsPvpRun(runManager.State))
        {
            return false;
        }

        return DuelSession.Phase is DuelPhase.RaceActive or DuelPhase.DuelActive;
    }

    /// <summary>
    /// Ends the match in our favour because the opponent is gone.
    ///
    /// **Both sides may reach this on a true network partition, and both would claim the win.**
    /// That is a known limitation rather than an oversight: with the connection cut there is no
    /// shared authority left to arbitrate, and neither client can tell "they crashed" from "my
    /// own link died". It is also not a case anyone shares a screen for. The freeze-and-arbitrate
    /// design in `docs/PLAYTEST_LIST.md` is what actually resolves it; until then, deciding is
    /// still strictly better than the original behaviour, which was to record no result at all.
    /// </summary>
    public static void Declare(string why)
    {
        _declared = true;
        Log.Warn($"[SpirePvp] {why} — declaring a win by disconnect");
        DuelResult.DeclareWinner(true, DuelEndReason.Disconnect);
    }

    /// <summary>
    /// Whether a lost connection is the sim having come apart rather than the peer having gone.
    ///
    /// Asked as its own question, in one place, because the two sides learn of it through
    /// different channels — the host issues the kick, the client is told about it — and the whole
    /// point is that they must reach the *same* answer without talking.
    /// </summary>
    public static bool IsDesync(NetError reason) => reason == NetError.StateDivergence;

    /// <summary>
    /// Ends the match as a void draw, because the two simulations stopped agreeing.
    ///
    /// **This exists because the alternative put a VICTORY banner on both screens.** See
    /// <see cref="DuelEndReason.Desync"/> for the measurement and the reasoning; the short version
    /// is that a desync destroys the evidence a winner would be read from, so there is no winner
    /// to name and pretending otherwise is worse than saying so.
    ///
    /// Declared locally on both sides rather than announced, like every other disconnect route —
    /// there is no connection left to announce over. It agrees anyway because both sides switch on
    /// the same reason code.
    /// </summary>
    public static void DeclareVoid(NetError reason)
    {
        _declared = true;
        ClearWait("the match is void");
        Log.Warn($"[SpirePvp] the simulations diverged ({reason}) — voiding the match as a draw; " +
                 "neither side's state is evidence of who was ahead");
        DuelResult.DeclareDraw(DuelEndReason.Desync);
    }

    /// <summary>
    /// Checks whether the opponent has stopped talking to us. Driven from the same per-frame hook
    /// as the clocks, and measured in wall-clock milliseconds, so the cadence does not matter.
    /// </summary>
    public static void Tick()
    {
        RunManager? runManager = RunManager.Instance;
        if (_declared || !ShouldDecide(runManager))
        {
            return;
        }

        IRunState? state = runManager!.State;
        Player? opponent = state?.Players.FirstOrDefault(p => !LocalContext.IsMe(p));
        if (opponent == null)
        {
            return;
        }

        ulong now = Time.GetTicksMsec();
        ulong silentFor;

        // Whether our own link is gone, as opposed to the peer merely having gone quiet. The two
        // are measured differently and, crucially, only one of them can recover.
        ulong? connectionLostAt = _connectionLostAtMs;
        bool connectionGone = connectionLostAt != null;

        if (connectionGone)
        {
            // Our own connection is gone, so there are no heartbeats to miss and no stats to read
            // — the peer's tracking was disposed along with the link. Time it from the moment we
            // were told instead. Checked first for that reason: the stats lookup below would
            // return null here and bail out before the window ever opened.
            silentFor = now - connectionLostAt!.Value;
        }
        else
        {
            // Null stats mean the peer is not being tracked — the normal state in a singleplayer
            // or replay service, and not evidence of anybody leaving. Silence about silence is
            // not silence.
            ConnectionStats? stats = runManager.NetService?.GetStatsForPeer(opponent.NetId);
            if (stats?.LastReceivedTime == null)
            {
                return;
            }

            silentFor = now - stats.LastReceivedTime.Value;
        }

        if (!connectionGone && silentFor < SilenceBeforeNoticeMs)
        {
            // **This is the reconnect half, and it needs no handshake.** A peer that starts
            // talking again refreshes LastReceivedTime, the measured silence collapses, and the
            // match simply carries on. Because nothing latches a "disconnected" state, there is
            // nothing to un-latch — a stall that recovers costs the match nothing at all.
            //
            // **Only for heartbeat silence, which is the whole reason `connectionGone` exists.**
            // A dead link cannot start talking again, and it reports zero elapsed silence on the
            // tick it is discovered — so this branch fired immediately on the client, called
            // ClearWait, and wiped the very timestamp that side was counting from. The wait then
            // evaporated in silence, leaving a run held open with nothing on screen and no
            // countdown: the log read `holding the run open` and then nothing at all, which is a
            // worse failure than the one it replaced.
            ClearWait("the opponent is talking again");
            return;
        }

        // Set on the first tick past the notice threshold, which is the tick that puts the
        // countdown on screen. Everything downstream reads the deadline rather than the silence,
        // so extending the wait is just moving this number.
        _forfeitAtMs ??= now + ForfeitWindowMs;

        ulong remainingMs = _forfeitAtMs.Value > now ? _forfeitAtMs.Value - now : 0;
        ShowNotice(remainingMs);

        if (remainingMs > 0)
        {
            return;
        }

        ulong waited = ForfeitWindowMs;
        ClearWait("the match is decided");
        Declare($"opponent {opponent.NetId} never returned "
                + $"(silent for {silentFor / 1000}s, window {waited / 1000}s)");
    }

    /// <summary>
    /// Says what happened, and counts down to the result.
    ///
    /// **No buttons, so vanilla's timeout overlay is the right surface again.** It was abandoned
    /// earlier for covering our popup — `NMultiplayerTimeoutOverlay` and `NModalContainer` are
    /// both plain `Control`s and the overlay draws on top — but with nothing to click, being
    /// non-interactive and full-screen is exactly what is wanted. It is also the notice players
    /// already recognise from a stalled host, and it is a permanent node on `NGame`, so there is
    /// nothing to build or free.
    ///
    /// **Vanilla drives this overlay too, on a client, from its own three-second test** — so the
    /// text is rewritten every tick rather than once, or its wording would reappear underneath
    /// our countdown.
    /// </summary>
    private static void ShowNotice(ulong remainingMs)
    {
        NMultiplayerTimeoutOverlay? overlay = NGame.Instance?.TimeoutOverlay;
        if (!overlay.IsValid())
        {
            return;
        }

        LocString description = new LocString("main_menu_ui", "SPIREPVP_TIMEOUT.description");
        description.Add("Seconds", ((remainingMs + 999) / 1000).ToString());

        overlay!.GetNodeOrNull<MegaLabel>("%Title")
                ?.SetTextAutoSize(new LocString("main_menu_ui", "SPIREPVP_TIMEOUT.title")
                                  .GetFormattedText());
        overlay.GetNodeOrNull<MegaRichTextLabel>("%Description")
               ?.SetTextAutoSize(description.GetFormattedText());
        overlay.Visible = true;

        if (!_showingNotice)
        {
            _showingNotice = true;
            Log.Warn($"[SpirePvp] opponent gone — awarding the match in {ForfeitWindowMs / 1000}s");
        }
    }

    /// <summary>
    /// Takes the curtain and the prompt down and forgets the deadline — on a reconnect, on a
    /// decision, or with the run.
    ///
    /// The caller says why, rather than the message listing the possibilities: the first version
    /// logged "opponent is talking again (or the match is decided)" for both, which reads as a
    /// reconnect at exactly the moment it is announcing a forfeit.
    /// </summary>
    private static void ClearWait(string why)
    {
        _forfeitAtMs = null;
        _connectionLostAtMs = null;

        if (!_showingNotice)
        {
            return;
        }

        _showingNotice = false;

        NMultiplayerTimeoutOverlay? overlay = NGame.Instance?.TimeoutOverlay;
        if (overlay.IsValid())
        {
            overlay!.Visible = false;
        }

        Log.Warn($"[SpirePvp] disconnect notice cleared — {why}");
    }
}
