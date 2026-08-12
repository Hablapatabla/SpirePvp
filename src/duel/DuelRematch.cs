using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Unlocks;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// Playing the same match again, from the result screen, without touching the main menu.
///
/// **The old scoping of this was wrong on its central premise, and the correction is what makes it
/// buildable.** HANDOFF used to say the run was already torn down by result-screen time —
/// `RunManager.CleanUp` fired, handlers released, transport gone — and concluded that a rematch
/// needed teardown ordering that kept the connection alive across a run boundary. `CleanUp` is not
/// called when a run *ends*; it is called from `NGame` and `NMainMenu` on the way back to the
/// menu. A run ending is `OnEnded`, which sets `IsGameOver` and nothing else. Measured on a real
/// match: the whole result screen renders, and only then does
/// `Disconnecting client 1001, reason: QuitGameOver` appear — issued explicitly by
/// `NGameOverScreen.OnMainMenuButtonPressed`.
///
/// So at the moment the button is pressed the connection is up, every handler is still armed, and
/// the finished run is still readable. That is the whole opportunity, and it is also why the
/// button has to live on this screen: leaving it is what disconnects.
///
/// **The real obstacle is one line in `RunManager`:** `SetUpNewMultiplayer` opens with
/// `if (State != null) throw new InvalidOperationException("State is already set.")`, and the only
/// thing that nulls `State` is `CleanUp` — whose second-to-last act is
/// `NetService.Disconnect(NetError.Quit)`. A new run therefore cannot start until the old one is
/// cleaned up, and cleaning up is what kills the connection the new run needs.
///
/// The answer is to run vanilla's teardown **whole** and suppress only the disconnect, which is
/// what `DuelRematchPatch` does for the duration of <see cref="Relaunching"/>. Reproducing
/// `CleanUp` step by step instead was rejected on the project's own history: `DuelArena` mirrors
/// `EnterMapPointInternal` that way and has cost six separate omissions, each failing differently
/// and none loudly. Suppressing one call is a smaller thing to get wrong than re-deriving
/// twenty-five.
/// </summary>
public static class DuelRematch
{
    private static bool _armed;

    /// <summary>
    /// True while a rematch is tearing the old run down, and read by `DuelRematchPatch` to hold
    /// the transport open through `CleanUp`.
    ///
    /// Deliberately *not* a phase on `DuelSession`: it spans the boundary between two runs, and
    /// `DuelSession` is reset by the teardown this flag exists to survive.
    /// </summary>
    public static bool Relaunching { get; private set; }

    /// <summary>We have asked and are waiting for an answer.</summary>
    public static bool OfferPending { get; private set; }

    /// <summary>They have asked and we have not answered.</summary>
    public static bool IncomingOfferPending { get; private set; }

    /// <summary>
    /// The opponent has left the result screen, so nothing on it can involve them any more.
    ///
    /// Tracked separately from `IsConnected` because the two do not fall at the same moment: a
    /// client pressing Main Menu tears its own run down and the host learns of it through
    /// `RemotePlayerDisconnected`, while the host pressing it disconnects outright. This is the
    /// one answer both routes agree on.
    /// </summary>
    public static bool PeerGone { get; private set; }

    /// <summary>
    /// Raised whenever anything a result-screen control would draw has changed — an offer
    /// arriving, an offer being answered, or the opponent leaving.
    ///
    /// An event rather than the buttons polling, because the interesting moments all arrive as
    /// messages on another thread of control and there is no frame hook on that screen worth
    /// borrowing. Subscribers must unsubscribe on `TreeExiting`; the screen outlives none of this.
    /// </summary>
    public static event Action? StateChanged;

    /// <summary>The opponent has gone. Called from both disconnect routes.</summary>
    public static void NotePeerGone()
    {
        if (PeerGone)
        {
            return;
        }

        PeerGone = true;
        Log.Warn("[SpirePvp] rematch: opponent left — no rematch from here");
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Whether a rematch can be offered at all.
    ///
    /// Asks whether there is a finished PvP match with a live connection, rather than testing the
    /// phase alone — the standing rule about asking the condition you mean. A match that ended by
    /// disconnect is `Complete` like any other, and offering a rematch to a peer who has gone
    /// would put a prompt on screen that can never be answered.
    /// </summary>
    public static bool CanOffer
    {
        get
        {
            INetGameService? net = RunManager.Instance?.NetService;
            return DuelSession.Phase == DuelPhase.Complete
                   && net is { IsConnected: true }
                   && net.Type != NetGameType.Singleplayer
                   && !Relaunching
                   && !PeerGone;
        }
    }

    public static void Reset()
    {
        OfferPending = false;
        IncomingOfferPending = false;
        PeerGone = false;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Armed at run start with every other handler.
    ///
    /// **Not armed when the result screen opens**, which is the tempting place given that is the
    /// only screen this feature appears on. The peer can press Rematch before we have finished
    /// rendering ours — they decide the match a few milliseconds before we do on the losing side —
    /// and a handler registered on first local use would drop that offer silently. Five separate
    /// bugs in this project have been exactly that mistake.
    /// </summary>
    public static void Arm()
    {
        if (_armed)
        {
            return;
        }

        INetGameService? net = RunManager.Instance?.NetService;
        if (net == null)
        {
            return;
        }

        net.RegisterMessageHandler<DuelRematchMessage>(OnRematchMessage);
        _armed = true;
    }

    public static void Disarm()
    {
        RunManager.Instance?.NetService?.UnregisterMessageHandler<DuelRematchMessage>(OnRematchMessage);
        _armed = false;
        Reset();
    }

    /// <summary>
    /// Ask for a rematch — or accept one already on the table.
    ///
    /// Offers crossing on the wire count as agreement, exactly as they do for draws: we each said
    /// we would play again, and making one of us dismiss a prompt to answer a question we had just
    /// asked ourselves would be worse than noticing.
    /// </summary>
    public static void Offer()
    {
        if (!CanOffer || OfferPending)
        {
            return;
        }

        if (IncomingOfferPending)
        {
            Respond(accept: true);
            return;
        }

        OfferPending = true;
        Log.Warn("[SpirePvp] rematch offered");
        StateChanged?.Invoke();

        RunManager.Instance.NetService.SendMessage(new DuelRematchMessage
        {
            isResponse = false,
            accepted = false
        });
    }

    /// <summary>Answer an offer the opponent made.</summary>
    public static void Respond(bool accept)
    {
        if (!CanOffer)
        {
            return;
        }

        IncomingOfferPending = false;
        Log.Warn($"[SpirePvp] rematch offer {(accept ? "accepted" : "declined")}");

        RunManager.Instance.NetService.SendMessage(new DuelRematchMessage
        {
            isResponse = true,
            accepted = accept
        });

        if (accept)
        {
            Relaunch("we accepted their offer");
        }
    }

    private static void OnRematchMessage(DuelRematchMessage message, ulong senderId)
    {
        if (LocalContext.NetId == senderId)
        {
            return;
        }

        if (!message.isResponse)
        {
            Log.Warn($"[SpirePvp] opponent {senderId} wants a rematch");

            // Crossing offers are agreement. Whoever's message arrives second finds the other
            // already pending and simply goes, so both sides relaunch without a prompt.
            if (OfferPending)
            {
                Log.Warn("[SpirePvp] rematch offers crossed — treating as agreement");
                Relaunch("offers crossed");
                return;
            }

            IncomingOfferPending = true;
            StateChanged?.Invoke();
            return;
        }

        OfferPending = false;
        StateChanged?.Invoke();

        if (message.accepted)
        {
            Log.Warn($"[SpirePvp] opponent {senderId} accepted the rematch");
            Relaunch("they accepted our offer");
        }
        else
        {
            Log.Warn($"[SpirePvp] opponent {senderId} declined the rematch");
        }
    }

    /// <summary>
    /// Tears the finished run down and starts an identical one, on this client.
    ///
    /// **Both clients run this independently, and that is safe here for a specific reason.** The
    /// standing rule is that anything deciding an outcome is host-authoritative, because two
    /// clients concluding the same thing separately is how a sim desyncs. A rematch decides no
    /// outcome: it re-derives a run from inputs both sides already hold identically — the same
    /// seed, the same modifiers, the same players — and the new run then establishes determinism
    /// from scratch the way every run does, with `CombatStateSynchronizer` reconciling at the first
    /// combat. What must not happen is the two sides launching *different* runs, and every input
    /// below is read from state the two already agree on rather than chosen locally.
    /// </summary>
    private static void Relaunch(string why)
    {
        if (Relaunching)
        {
            return;
        }

        RunManager? run = RunManager.Instance;
        RunState? old = run?.State;
        if (run == null || old == null)
        {
            Log.Error("[SpirePvp] rematch: no run to rematch from — the screen outlived its run");
            return;
        }

        // Everything the new run needs, read before the teardown frees it. The seed above all:
        // it is the one value that makes this a *rematch* rather than a new match, and it lives
        // on the run being ended (DESIGN §5b settled same-seed — both players have seen the map,
        // so the second run is pure decision-making, and it is strictly less work than rolling).
        string seed = old.Rng.StringSeed;
        GameMode gameMode = old.GameMode;
        int ascension = old.AscensionLevel;
        List<ModifierModel> modifiers = new List<ModifierModel>(old.Modifiers);
        List<RunLobbyPlayer> lobbyPlayers = new List<RunLobbyPlayer>(run.RunLobby?.Players
                                                                    ?? new List<RunLobbyPlayer>());

        // Rebuilt from the *old players* rather than from a lobby, because the StartRunLobby is
        // long gone — `NCustomRunScreen` disposes it as soon as the run starts. Character, unlock
        // state and net id are the whole of what `Player.CreateForNewRun` wants, and all three are
        // still on the finished run.
        List<(CharacterModel Character, UnlockState Unlocks, ulong NetId)> seats =
            old.Players
               .Select(p => (p.Character, p.UnlockState, p.NetId))
               .ToList();

        Log.Warn($"[SpirePvp] rematch: relaunching ({why}) — seed {seed}, {seats.Count} player(s), "
                 + $"{modifiers.Count} modifier(s), ascension {ascension}, "
                 + $"lobby players {lobbyPlayers.Count}");

        Relaunching = true;
        TaskHelper.RunSafely(RelaunchAsync(run, seed, gameMode, ascension, modifiers, seats, lobbyPlayers));
    }

    private static async Task RelaunchAsync(
        RunManager run,
        string seed,
        GameMode gameMode,
        int ascension,
        List<ModifierModel> modifiers,
        List<(CharacterModel Character, UnlockState Unlocks, ulong NetId)> seats,
        List<RunLobbyPlayer> lobbyPlayers)
    {
        try
        {
            // Vanilla's own teardown, whole, with only the disconnect held back
            // (`DuelRematchPatch`). This is what releases the synchronizers, the replay writer and
            // the combat state, and what nulls `State` so `SetUpNewMultiplayer` will accept a new
            // one. `DuelRunCleanupPatch` rides it and releases the mod's handlers, which is why
            // `Arm` runs again below by way of the new run's own launch.
            run.CleanUp();

            // The acts are *recomputed from the seed* rather than copied off the old run, which is
            // what `StartRunLobby.BeginRunLocally` does and therefore what both clients will agree
            // on. The old run's own act models have been mutated all match — rooms generated,
            // coords visited — so reusing them would hand a fresh run a used map.
            //
            // Known gap, recorded rather than guessed at: `BeginRunLocally` also applies the
            // lobby's `Act1` override before this list is used, and that override does not survive
            // on the run. A duel lobby does not set it today, so the recomputation matches; if a
            // duel ever offers an act choice, this is the line that has to carry it.
            Rng rng = new Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection");
            List<ActModel> acts =
                ActModel.GetRandomList(rng, seats[0].Unlocks, isMultiplayer: true).ToList();

            RunState fresh = RunState.CreateForNewRun(
                seats.Select(s => Player.CreateForNewRun(s.Character, s.Unlocks, s.NetId)).ToList(),
                acts.Select(a => a.ToMutable()).ToList(),
                modifiers,
                gameMode,
                ascension,
                seed);

            // `SetUpNewMultiplayer` takes a `StartRunLobby` we no longer have, so its four steps
            // are mirrored here instead. Listed in its own order, because that ordering is
            // load-bearing and the same class of mistake `DuelArena` documents against
            // `EnterMapPointInternal`:
            //
            //   State = state
            //   InitializeShared(netService, inputSynchronizer, shouldSave, dailyTime, now, 0, 0, 0)
            //   InitializeRunLobby(netService, state, players)
            //   InitializeNewRun()
            //   GenerateRooms()
            run.State = fresh;
            run.InitializeShared(
                run.NetService,
                run.InputSynchronizer,
                shouldSave: true,
                dailyTime: null,
                startTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                runTime: 0L,
                winTime: 0L,
                numReloads: 0);
            run.InitializeRunLobby(run.NetService, fresh, lobbyPlayers);
            run.InitializeNewRun();
            run.GenerateRooms();

            Log.Warn($"[SpirePvp] rematch: new run created on seed {seed} — starting");

            // Vanilla's own `NGame.StartRun`, called rather than re-derived. It is private and
            // reachable only through the publicizer, which is worth the reach: it loads the run
            // and act assets, finalizes starting relics, launches, swaps the scene and enters act
            // 0, all in an order this has no business re-deciding. The four `RunManager` calls
            // above are already one re-derivation more than is comfortable.
            await NGame.Instance.StartRun(fresh);

            Log.Warn("[SpirePvp] rematch: under way");
        }
        catch (Exception e)
        {
            // A failed rematch must not strand both players on a dead screen with a live
            // connection and no run. Say so loudly; the menu is still reachable.
            Log.Error($"[SpirePvp] rematch failed: {e}");
        }
        finally
        {
            Relaunching = false;
            Reset();
        }
    }
}
