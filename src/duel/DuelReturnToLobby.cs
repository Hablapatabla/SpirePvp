using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// Going back to the Duel lobby together, with the connection intact.
///
/// **Rematch replays the same match; this changes it.** Rematch exists because the transport does
/// not survive leaving the result screen, so "play again" had to be a button *on* that screen. The
/// same constraint applies to anything else two players want to do next — and the thing they most
/// want after a match is not the identical match again, it is the same opponent with a different
/// seed, format or clock. That is the lobby, and getting back to it is the same problem wearing a
/// different hat.
///
/// # The one hard constraint, and it reframes the work
///
/// **The lobby screen lives in the main menu scene.** `NMainMenu.SubmenuStack` is a node there; it
/// does not exist during a run. So this is not "open a lobby over the run" — it is *get both peers
/// back to the main menu without dropping the connection, then open a lobby on each*. The screen
/// half is three lines. The ordering half is everything, which is the opposite of how the item read
/// before anyone looked.
///
/// # The order, and why each step is where it is
///
/// 1. **Both sides agree first** (<see cref="DuelReturnToLobbyMessage"/>). One peer tearing its run
///    down alone leaves the other on a result screen whose buttons all refer to a match that no
///    longer exists on the wire.
/// 2. **Both tear down**, holding the transport open. `RunManager.CleanUp` ends with
///    `NetService.Disconnect`, so <see cref="Holding"/> suppresses it exactly the way
///    `DuelRematch.Relaunching` does — and, like it, is deliberately *not* cleared by
///    <see cref="Reset"/>, because `Reset` runs from `OnRunEnded` in the middle of the teardown it
///    is protecting.
/// 3. **Both return to the main menu**, which is `NGame.ReturnToMainMenuAfterRun()` — the same call
///    the vanilla Main Menu button makes, minus the disconnect that precedes it.
/// 4. **The host opens its lobby**, which is what creates a `StartRunLobby` and arms its
///    `ClientLobbyJoinRequestMessage` handler.
/// 5. **The host says so**, and only then does the client ask to join. This is the step that cannot
///    be reordered: a client needs a `ClientLobbyJoinResponseMessage` to open the lobby at all, only
///    a live `StartRunLobby` answers a request with one, and `NetMessageBus` *drops* a message with
///    no registered handler rather than buffering it. A request sent one frame early is not late,
///    it is gone.
///
/// # What this deliberately does not do
///
/// **It does not rejoin.** The connection is never dropped, so there is nothing to reconnect — this
/// is the same socket the match ran on, handed to a new lobby. Actual rejoining after a *drop* is a
/// separate milestone and is not built.
/// </summary>
public static class DuelReturnToLobby
{
    /// <summary>
    /// How long the client waits for the host's lobby before giving up and going to the menu alone.
    ///
    /// Generous on purpose: the host is loading the main menu scene, and the failure this guards is
    /// a client sitting on a blank screen forever, not a slow host. Landing on the main menu with
    /// the connection dropped is a bad outcome; landing there having waited eight seconds first is
    /// the same bad outcome, arrived at honestly.
    /// </summary>
    private const double HostLobbyTimeoutSeconds = 8.0;

    private static bool _armed;

    /// <summary>
    /// True from the moment a teardown starts until the lobby is open, suppressing the disconnect.
    ///
    /// Read by `DuelRematchPatch`'s `Disconnect` prefixes, which already hold the transport for a
    /// rematch — one flag more on the same guard rather than a second pair of patches.
    /// </summary>
    public static bool Holding { get; private set; }

    /// <summary>We asked, and are waiting to hear back.</summary>
    public static bool OfferPending { get; private set; }

    /// <summary>They asked, and we have not answered.</summary>
    public static bool IncomingOfferPending { get; private set; }

    /// <summary>Raised whenever the button's caption or availability should change.</summary>
    public static event Action? StateChanged;

    /// <summary>
    /// Whether offering makes sense right now.
    ///
    /// Leans on `DuelRematch.PeerGone` rather than tracking its own: a peer that vanished is gone
    /// for both features, and the fact is recorded once at the point vanilla reports it. Offering
    /// to go back to a lobby with somebody who has already left is the same nonsense as offering
    /// them a rematch.
    /// </summary>
    public static bool CanOffer => !DuelRematch.PeerGone
                                   && !Holding
                                   && RunManager.Instance?.NetService != null
                                   && RunManager.Instance.NetService.Type != NetGameType.Singleplayer;

    /// <summary>
    /// Armed at run start with everything else, never on first local use.
    ///
    /// The peer can offer before you have looked at the screen, and a dropped offer is a button
    /// that does nothing for one side and a permanent "waiting" for the other.
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

        net.RegisterMessageHandler<DuelReturnToLobbyMessage>(OnMessage);
        _armed = true;
    }

    /// <summary>Releases it. See `DuelMatch.OnRunEnded`.</summary>
    public static void Disarm()
    {
        RunManager.Instance?.NetService?.UnregisterMessageHandler<DuelReturnToLobbyMessage>(OnMessage);
        _armed = false;
    }

    /// <summary>
    /// Clears the offer state with the run.
    ///
    /// **<see cref="Holding"/> is deliberately absent**, exactly as `DuelRematch.Relaunching` is
    /// absent from its own `Reset`. This runs from `OnRunEnded`, which fires *inside* the teardown
    /// the flag exists to protect — clearing it here would let the disconnect through at the one
    /// moment it must not.
    /// </summary>
    public static void Reset()
    {
        OfferPending = false;
        IncomingOfferPending = false;
        StateChanged?.Invoke();
    }

    /// <summary>Ask the opponent to go back to the lobby.</summary>
    public static void Offer()
    {
        if (OfferPending || Holding)
        {
            return;
        }

        // Their offer crossing ours is agreement, not a conflict — the same reading a crossed draw
        // offer already gets. Both sides wanted the same thing and said so at the same time.
        if (IncomingOfferPending)
        {
            Respond(accept: true);
            return;
        }

        OfferPending = true;
        StateChanged?.Invoke();
        Log.Warn("[SpirePvp] return to lobby: offered");
        RunManager.Instance?.NetService?.SendMessage(new DuelReturnToLobbyMessage
        {
            kind = DuelReturnToLobbyMessage.Offer
        });
    }

    /// <summary>Answer theirs.</summary>
    public static void Respond(bool accept)
    {
        if (!IncomingOfferPending)
        {
            return;
        }

        IncomingOfferPending = false;
        OfferPending = false;
        StateChanged?.Invoke();
        Log.Warn($"[SpirePvp] return to lobby: {(accept ? "accepted" : "declined")}");

        RunManager.Instance?.NetService?.SendMessage(new DuelReturnToLobbyMessage
        {
            kind = DuelReturnToLobbyMessage.Answer,
            accepted = accept
        });

        if (accept)
        {
            Begin();
        }
    }

    private static void OnMessage(DuelReturnToLobbyMessage message, ulong senderId)
    {
        switch (message.kind)
        {
            case DuelReturnToLobbyMessage.Offer:
                // Ours crossed theirs. Both wanted it; stop asking and go.
                if (OfferPending)
                {
                    OfferPending = false;
                    StateChanged?.Invoke();
                    Log.Warn("[SpirePvp] return to lobby: offers crossed — treating it as agreement");
                    RunManager.Instance?.NetService?.SendMessage(new DuelReturnToLobbyMessage
                    {
                        kind = DuelReturnToLobbyMessage.Answer,
                        accepted = true
                    });
                    Begin();
                    return;
                }

                IncomingOfferPending = true;
                StateChanged?.Invoke();
                Log.Warn($"[SpirePvp] return to lobby: {senderId} asked to go back to the lobby");
                return;

            case DuelReturnToLobbyMessage.Answer:
                OfferPending = false;
                StateChanged?.Invoke();
                Log.Warn($"[SpirePvp] return to lobby: {senderId} "
                         + (message.accepted ? "accepted" : "declined"));
                if (message.accepted)
                {
                    Begin();
                }

                return;

            case DuelReturnToLobbyMessage.HostLobbyReady:
                Log.Warn("[SpirePvp] return to lobby: the host's lobby is open — asking to join it");
                _hostLobbyReady = true;
                return;
        }
    }

    /// <summary>Client only: set when the host says its lobby is up. See the message's third state.</summary>
    private static bool _hostLobbyReady;

    /// <summary>
    /// The finished match's modifiers, carried across the teardown so the new lobby opens on them.
    /// </summary>
    private static List<ModifierModel> _carriedModifiers = new();

    /// <summary>
    /// Puts the last match's settings back on the lobby's tickboxes.
    ///
    /// Routed through `SetTickedModifiers` rather than written onto the lobby directly, for the
    /// reason `DuelHostLobbyPatch` already documents: the list owns the instances vanilla built for
    /// it, and it emits `ModifiersChanged`, which is what syncs a connected client.
    /// </summary>
    private static void ReapplyModifiers(NCustomRunScreen screen)
    {
        if (_carriedModifiers.Count == 0)
        {
            Log.Warn("[SpirePvp] return to lobby: no modifiers carried over — the lobby opens plain");
            return;
        }

        try
        {
            screen._modifiersList.SetTickedModifiers(_carriedModifiers);
            Log.Warn($"[SpirePvp] return to lobby: re-ticked {_carriedModifiers.Count} modifier(s) "
                     + "from the finished match");
        }
        catch (Exception e)
        {
            Log.Error($"[SpirePvp] return to lobby: could not re-tick the match's modifiers, so the "
                      + $"lobby opens plain rather than not at all: {e}");
        }
    }

    /// <summary>
    /// Tears the run down and gets this side to its lobby. Both peers run it; the halves differ.
    /// </summary>
    private static void Begin()
    {
        if (Holding)
        {
            return;
        }

        RunManager? run = RunManager.Instance;
        if (run == null)
        {
            Log.Warn("[SpirePvp] return to lobby: no run to leave");
            return;
        }

        bool isHost = run.NetService.Type == NetGameType.Host;

        // **Read before the teardown frees it.** Same reason `DuelRematch` copies the seed and the
        // seats off the old run first: `CleanUp` nulls `State`, and by the time the lobby exists
        // there is nothing left to ask what the last match was configured as.
        _carriedModifiers = run.State != null
            ? new List<ModifierModel>(run.State.Modifiers)
            : new List<ModifierModel>();

        Holding = true;
        _hostLobbyReady = false;
        StateChanged?.Invoke();

        Log.Warn($"[SpirePvp] return to lobby: tearing the run down as {(isHost ? "host" : "client")}, "
                 + "transport held open");

        TaskHelper.RunSafely(BeginAsync(run, isHost));
    }

    private static async Task BeginAsync(RunManager run, bool isHost)
    {
        try
        {
            // Vanilla's own teardown, whole, with only the disconnect held back — the same shape
            // `DuelRematch` uses, and the reason `Holding` exists. `DuelRunCleanupPatch` rides this
            // and releases the mod's handlers, including ours; `Arm` runs again with the next run.
            run.CleanUp();

            // **Re-armed immediately, because the teardown we just ran disarmed us.**
            // `DuelRunCleanupPatch` prefixes `CleanUp` and calls `DuelMatch.OnRunEnded`, which
            // releases every handler this mod owns — including this one, in the middle of the
            // exchange it is coordinating. Measured 2026-08-18: the host reached its lobby and sent
            // `HostLobbyReady` to a client that had no handler left for it, and the client sat out
            // its whole eight seconds and gave up.
            //
            // Re-arming here rather than exempting this handler from `OnRunEnded` keeps that method
            // honest — it still releases everything — and puts the one exception at the one place
            // that knows it is an exception. The service is the same object either side of
            // `CleanUp`; only `State` is nulled.
            Arm();

            // The same call the vanilla Main Menu button makes. It is public, so there is nothing
            // to reflect at — the only thing that button does which this does not is the
            // `Disconnect` immediately before it, which is exactly the difference we want.
            await NGame.Instance.ReturnToMainMenuAfterRun();

            if (isHost)
            {
                await OpenHostLobby();
            }
            else
            {
                await OpenClientLobby();
            }
        }
        catch (Exception e)
        {
            Log.Error($"[SpirePvp] return to lobby: failed, and the transport is being released "
                      + $"rather than left half-held: {e}");
        }
        finally
        {
            // **Cleared last and always.** While this is true the game cannot be disconnected from,
            // which is a far worse state to leave behind than a failed lobby open.
            Holding = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Host: open the Duel lobby, then tell the client it may ask to join.
    ///
    /// The order is the whole point — see <see cref="DuelReturnToLobbyMessage.HostLobbyReady"/>.
    /// `InitializeMultiplayerAsHost` is what builds the `StartRunLobby` that answers join requests,
    /// so the announcement has to come after it and not before.
    /// </summary>
    private static async Task OpenHostLobby()
    {
        NCustomRunScreen? screen = GetLobbyScreen();
        if (screen == null)
        {
            return;
        }

        INetGameService? net = RunManager.Instance?.NetService;
        if (net == null)
        {
            Log.Error("[SpirePvp] return to lobby: the transport went away during teardown");
            return;
        }

        // Four, matching vanilla's own host path (`NMultiplayerHostSubmenu`). A duel uses two of
        // them; the lobby's own rules are what keep a third out, not this number.
        screen.InitializeMultiplayerAsHost(net, 4);
        PushLobbyScreen(screen);

        // **The match's own modifiers, not `DuelHostFlow.Requested`.** Setting that flag is how the
        // Duel *menu entry* opens a lobby, and `DuelHostLobbyPatch` consumes it by applying the
        // default preset — which is exactly wrong here. Someone returning to the lobby is going
        // back to change *one* thing about the match they just played; handing them real-time and
        // no clocks instead would throw away the format, the turn model and both clocks every time.
        //
        // Re-ticking is also what dresses the lobby as a Duel lobby at all: `DuelLobbyPanelPatch`
        // keys on the modifiers rather than on the flag, which is the same reason it works on a
        // client. `SetTickedModifiers` emits `ModifiersChanged`, so the client is synced through
        // vanilla's own handler and needs to be told nothing extra.
        // After the push, deliberately: `OnSubmenuOpened` resets parts of the screen, so a tick
        // applied before it would be reset along with them.
        ReapplyModifiers(screen);

        Log.Warn("[SpirePvp] return to lobby: host lobby open — telling the client to join");
        net.SendMessage(new DuelReturnToLobbyMessage { kind = DuelReturnToLobbyMessage.HostLobbyReady });

        await Task.CompletedTask;
    }

    /// <summary>
    /// Client: wait for the host's lobby, ask to join it, and open the lobby on the answer.
    ///
    /// **The request is re-sent over the live connection**, which works because the host's fresh
    /// `StartRunLobby` cannot tell it from a first join: `HandleClientLobbyJoinRequestMessage`
    /// answers whoever asks, and this peer is already connected and already in its player list.
    /// </summary>
    private static async Task OpenClientLobby()
    {
        INetGameService? net = RunManager.Instance?.NetService;
        if (net == null)
        {
            Log.Error("[SpirePvp] return to lobby: the transport went away before the join");
            return;
        }

        if (!await PumpUntil(net, () => _hostLobbyReady))
        {
            Log.Error($"[SpirePvp] return to lobby: the host's lobby never opened within "
                      + $"{HostLobbyTimeoutSeconds}s — staying on the main menu");
            return;
        }

        ClientLobbyJoinResponseMessage response;
        try
        {
            response = await RequestJoin(net);
        }
        catch (Exception e)
        {
            Log.Error($"[SpirePvp] return to lobby: the host never answered the join request: {e}");
            return;
        }

        NCustomRunScreen? screen = GetLobbyScreen();
        if (screen == null)
        {
            return;
        }

        // **Nothing is ticked here on purpose.** The host owns the match configuration and the
        // client receives it over `LobbyModifiersChangedMessage`; a client that pre-ticked its own
        // boxes would be inventing settings the run is not going to have. Same rule
        // `DuelHostLobbyPatch` states for a fresh client lobby.
        screen.InitializeMultiplayerAsClient(net, response);
        PushLobbyScreen(screen);
        Log.Warn("[SpirePvp] return to lobby: client lobby open");
    }

    /// <summary>
    /// Sends a join request and waits for the answer, the way `JoinFlow.AttemptJoin` does.
    ///
    /// Reimplemented rather than called because `JoinFlow` owns a whole connection lifecycle —
    /// creating a transport, handshaking versions, comparing mod lists — and all of that has
    /// already happened on this socket. What is wanted is the last exchange of it, alone.
    /// </summary>
    private static async Task<ClientLobbyJoinResponseMessage> RequestJoin(INetGameService net)
    {
        TaskCompletionSource<ClientLobbyJoinResponseMessage> completion = new();

        void OnResponse(ClientLobbyJoinResponseMessage message, ulong senderId) =>
            completion.TrySetResult(message);

        net.RegisterMessageHandler<ClientLobbyJoinResponseMessage>(OnResponse);
        try
        {
            UnlockState unlocks = SaveManager.Instance.GenerateUnlockStateFromProgress();
            net.SendMessage(new ClientLobbyJoinRequestMessage
            {
                maxAscensionUnlocked = SaveManager.Instance.Progress.MaxMultiplayerAscension,
                unlockState = unlocks.ToSerializable()
            });

            if (!await PumpUntil(net, () => completion.Task.IsCompleted))
            {
                throw new TimeoutException(
                    $"no ClientLobbyJoinResponseMessage within {HostLobbyTimeoutSeconds}s");
            }

            return await completion.Task;
        }
        finally
        {
            net.UnregisterMessageHandler<ClientLobbyJoinResponseMessage>(OnResponse);
        }
    }

    /// <summary>
    /// Waits for something to arrive, **pumping the transport while it does**.
    ///
    /// # This is the whole reason the client half was hard
    ///
    /// **Nothing polls the socket between leaving a run and opening a lobby.** There are exactly
    /// two callers of `INetGameService.Update()` in the game:
    ///
    ///     NRun.cs:201                RunManager.Instance.NetService.Update();   // during a run
    ///     NCustomRunScreen.cs:568    _lobby.NetService.Update();                // in the lobby
    ///
    /// A run node pumps it while a run exists, and the lobby screen pumps it once a lobby exists.
    /// Return to lobby lives in the gap between those two, where the connection is open, held, and
    /// **completely unread**.
    ///
    /// Measured 2026-08-18, and the asymmetry is the tell: the *host* worked. It opens its lobby
    /// before it sends anything, so `NCustomRunScreen` is already pumping by then. The client sat
    /// on the main menu waiting for `HostLobbyReady` with no lobby screen and therefore no pump —
    /// so the packet arrived at a socket nobody was reading, and the log recorded neither the
    /// message nor a missing-handler error, because the bus was never asked. It could have waited
    /// forever; eight seconds only made it fail faster.
    ///
    /// So this stands in for the frame pump for exactly that window. It is the same call both
    /// vanilla pumps make, on the same service, at roughly a frame's cadence.
    /// </summary>
    /// <returns>True if the condition came true, false if the wait timed out.</returns>
    private static async Task<bool> PumpUntil(INetGameService net, Func<bool> until)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(HostLobbyTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (until())
            {
                return true;
            }

            try
            {
                net.Update();
            }
            catch (Exception e)
            {
                Log.Error($"[SpirePvp] return to lobby: the transport threw while being pumped: {e}");
                return false;
            }

            await Task.Delay(16);
        }

        return until();
    }

    /// <summary>
    /// Gets the lobby screen onto the main menu's stack.
    ///
    /// `GetSubmenuType` builds it on first use and reuses it after — `NMainMenuSubmenuStack` caches
    /// `_customRunScreen` — which is the same reuse `DuelLobbyPanel.Apply` is idempotent for. It is
    /// fetched, initialized by the caller, and pushed, in that order, because that is the order
    /// both of vanilla's own callers use.
    /// </summary>
    private static NCustomRunScreen? GetLobbyScreen()
    {
        // `NGame.MainMenu` is `RootSceneContainer.CurrentScene as NMainMenu` — null unless the main
        // menu is genuinely the scene on screen, which is precisely the precondition this needs.
        NMainMenu? menu = NGame.Instance?.MainMenu;
        if (menu == null)
        {
            Log.Error("[SpirePvp] return to lobby: reached the main menu and found no NMainMenu");
            return null;
        }

        return menu.SubmenuStack.GetSubmenuType<NCustomRunScreen>();
    }

    /// <summary>
    /// Pushes the lobby screen, which must happen **after** it has been initialized.
    ///
    /// **The order is not a style choice, and getting it wrong is a crash.** `Push` raises
    /// `NCustomRunScreen.OnSubmenuOpened`, which resets the character-select buttons, which
    /// refreshes their player icons — and those read the multiplayer state that
    /// `InitializeMultiplayerAs{Host,Client}` is what installs. Pushing first, measured
    /// 2026-08-18:
    ///
    ///     at NCharacterSelectButton.RefreshPlayerIcons()
    ///     at NCharacterSelectButton.Reset()
    ///     at NCustomRunScreen.OnSubmenuOpened()
    ///     at NSubmenuStack.Push(NSubmenu screen)
    ///
    /// Both of vanilla's own callers — `NMultiplayerHostSubmenu` and `NJoinFriendScreen` — get,
    /// initialize, then push, in that order. That they agree is the tell that the order is
    /// load-bearing, and this code had it written down in a comment while doing the opposite.
    /// </summary>
    private static void PushLobbyScreen(NCustomRunScreen screen)
    {
        NGame.Instance?.MainMenu?.SubmenuStack.Push(screen);
    }
}
