using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpirePvp.Duel;

/// <summary>
/// Coming back to a duel after the link dropped.
///
/// # The netcode is not the work, and that is the whole reason this is affordable
///
/// **Vanilla's rejoin is half-built and the missing half is on the client.** The handshake runs end
/// to end already: a client that connects to a host whose session is `RunSessionState.Running` sends
/// `ClientRejoinRequestMessage` and awaits the answer (`JoinFlow.AttemptRejoin`), and the host
/// answers any peer it still recognises — the gate is `_playerCollection.GetPlayer(senderId)`, which
/// is *not* pruned on disconnect — by shipping `GetRejoinMessage()`: the entire run **and** a full
/// combat snapshot. It then adds the returning player back to `Players` and broadcasts
/// `PlayerRejoinedMessage`. All of that is engine code and all of it works.
///
/// Then `NJoinFriendScreen` throws the answer away:
///
///     else if (joinResult.sessionState == RunSessionState.Running)
///     {
///         NErrorPopup.Create(new NetErrorInfo(NetError.RunInProgress, selfInitiated: false));
///         _currentJoinFlow.NetService.Disconnect(NetError.RunInProgress);
///     }
///
/// Mega Crit say why in their own enum docs — *"the run is already in progress, and rejoining is not
/// implemented"*. So this class is the consumer that screen never grew.
///
/// # Why it drives `JoinFlow` itself rather than patching the screen
///
/// The discarding branch lives inside `NJoinFriendScreen.JoinGameAsync`, an `async Task`, and this
/// project has paid twice for patching those: a prefix that skips one must assign `__result` or the
/// caller NREs on `await null`, and a postfix runs when the state machine is *created*, not when it
/// completes. Neither is needed here, because `JoinFlow` is public, constructible, and returns the
/// rejoin response as data — so we run the same flow the screen runs and keep the answer.
///
/// The one thing we do take from the screen is the *address*, captured by a non-skipping prefix in
/// `DuelRejoinCapturePatch`. A prefix that returns void and never returns `false` is safe on an
/// async method: it runs at call time and only reads its argument.
///
/// # Scope, decided 2026-08-20 (Lucas)
///
/// **A dropped link with the game still running, and only once the duel has started.** Both halves
/// are deliberate:
///
/// - **Duel-phase only.** In the arena the two simulations are coupled and checksummed, so the
///   host's copy of the returning player is correct *by construction* — that is exactly what a
///   checksum asserts. In the race they are decoupled, so the host's copy of you stopped updating
///   when the race began and a restore from it would hand you back roughly your Neow state. The
///   race case needs the client's own state broadcast to the host to be recoverable, and that puts
///   hidden deck information on the opponent's machine before the deck review — which is the thing
///   the review exists to control. It is a separate decision, not a smaller version of this one.
/// - **Manual, from the menu.** Asked for. It has a consequence worth stating: leaving to the main
///   menu runs `RunManager.CleanUp`, so the returning client's own run state is gone by the time it
///   reconnects. Every manual rejoin is therefore a rebuild from the host's copy — which is safe
///   here precisely because of the coupling above. The uncapped **Wait longer…** button on the
///   survivor's disconnect curtain is what makes a menu round-trip survivable.
///
/// # Re-arming, which was scoped as the hard part and is not
///
/// `docs/PLAYTEST_LIST.md` warned that `AfterRunCreated` fires only from `RunState.CreateForNewRun`,
/// so a restored run would come back with `IsPvpRun` true but no race patches, no armed handlers and
/// no clocks. That is true of a hand-rolled restore and false of this one: `InitializeSavedRun` ends
/// by calling `modifier.OnRunLoaded(State)` on every modifier, `ModifierModel.OnRunLoaded` calls
/// `AfterRunLoaded`, and every duel modifier in `DuelModifiers` already overrides that to
/// `DuelMatch.OnRunCreated`. Restoring **through the engine's saved-run path** therefore re-arms the
/// mod for free, and hand-rolling the three initialise calls in a different order would silently
/// lose it. That is the reason this follows `SetUpReplay` step for step rather than inventing a path
/// — the same discipline `DuelArena` follows against `EnterMapPointInternal`.
/// </summary>
public static class DuelRejoin
{
    /// <summary>
    /// How we reached the host last time, kept so the same address can be dialled again.
    ///
    /// **An initializer rather than an address**, because the two transports do not share one: ENet
    /// carries ip/port/netId and Steam carries a lobby id, and `IClientConnectionInitializer` is the
    /// seam vanilla already put between them. Storing what we were handed keeps this working on
    /// Steam without a second code path.
    /// </summary>
    private static IClientConnectionInitializer? _lastJoin;

    /// <summary>
    /// The combat we are coming back to, held between the restore and the arena.
    ///
    /// **A field rather than an argument because the two ends are not connected by a call.**
    /// `LoadRun` sits between them: the run is rebuilt here, the engine takes over to load the
    /// scene, and the mod is re-armed from inside that by `AfterRunLoaded`. Nothing threads a
    /// parameter through that, so the snapshot waits here for the resume to collect it.
    ///
    /// Non-null is also the *only* honest test for "this run is being restored". Everything else
    /// available at that moment — the modifiers, the phase, the player list — reads identically on a
    /// fresh match, which is precisely why the first rejoin attempt cheerfully started a draft.
    /// </summary>
    private static NetFullCombatState? _resumeFrom;

    /// <summary>Whether a run is being restored right now, asked by the setup that must not re-run.</summary>
    public static bool IsRestoring => _resumeFrom != null;

    /// <summary>Dropped when the resume finishes or the run ends, so the next match starts clean.</summary>
    public static void ClearResume() => _resumeFrom = null;

    /// <summary>Remembered by <see cref="Patches.DuelRejoinCapturePatch"/> on every join attempt.</summary>
    public static void RememberJoin(IClientConnectionInitializer initializer)
    {
        _lastJoin = initializer;
        Log.Info($"[SpirePvp] rejoin: remembered {initializer} as the way back");
    }

    /// <summary>
    /// Dial an address given explicitly, for the client that has no memory of one.
    ///
    /// **The remembered address is static mod state, and static mod state dies with the process** —
    /// which is precisely the case a reconnect exists to serve. A client killed mid-duel comes back
    /// knowing nothing, so a rejoin that can only re-dial a remembered address cannot serve the
    /// most ordinary drop there is. Persisting it is the eventual answer; naming it is the one that
    /// makes the feature testable today, and it stays useful afterwards as the manual override for
    /// a host whose address moved.
    ///
    /// Defaults are the dev rig's: `127.0.0.1:33771` is what `NJoinFriendScreen.FastMpJoin` dials,
    /// and the netId comes from `--clientId` for the same reason vanilla reads it there — the ENet
    /// client sends its *own* id in the handshake, and the host's rejoin gate is
    /// `GetPlayer(senderId)`, so coming back as a different id is coming back as a stranger.
    /// </summary>
    public static void UseAddress(string ip, ushort port, ulong? netId = null)
    {
        ulong id = netId ?? 1000uL;
        if (netId == null && CommandLineHelper.TryGetValue("clientId", out string value)
            && ulong.TryParse(value, out ulong parsed))
        {
            id = parsed;
        }

        _lastJoin = new ENetClientConnectionInitializer(id, ip, port);
        Log.Info($"[SpirePvp] rejoin: dialling {ip}:{port} as net id {id}");
    }

    /// <summary>
    /// Whether offering a rejoin makes sense right now.
    ///
    /// **It asks whether a run is running, not whether one ended badly**, and that is the
    /// guard-on-the-condition rule this project keeps relearning: there are several ways to leave a
    /// duel and only some of them announce themselves. If `RunManager` has no state we are at the
    /// menu, and if we know an address we have something to dial.
    /// </summary>
    public static bool IsOffered => _lastJoin != null && RunManager.Instance?.State == null;

    /// <summary>
    /// Dial the host, and if it says a run is in progress, take the run it sends us.
    ///
    /// Returns a sentence fit to show the player; the detail goes to the log.
    /// </summary>
    public static async Task<string> Attempt()
    {
        if (_lastJoin == null)
        {
            return "No match to rejoin — this client has not joined a host yet.";
        }

        if (RunManager.Instance?.State != null)
        {
            return "Already in a run. Leave it before rejoining.";
        }

        Log.Info($"[SpirePvp] rejoin: dialling {_lastJoin}");
        JoinFlow flow = new JoinFlow(new NetClientGameService(PeerVersionInfo.LocalDefault()));

        JoinResult result;
        try
        {
            result = await flow.Begin(_lastJoin, NGame.Instance?.GetTree());
        }
        catch (System.Exception e)
        {
            Log.Warn($"[SpirePvp] rejoin: the join flow failed — {e.GetType().Name}: {e.Message}");
            return "Could not reach the host.";
        }

        if (result.sessionState != RunSessionState.Running)
        {
            // Not an error: the host may be back in the lobby, in which case vanilla's own join is
            // the right route and this one has nothing to add.
            Log.Info($"[SpirePvp] rejoin: host reports {result.sessionState}, not a run in progress");
            flow.NetService.Disconnect(NetError.InvalidJoin);
            return $"The host is not in a run ({result.sessionState}) — join normally instead.";
        }

        if (!result.rejoinResponse.HasValue)
        {
            // The host recognised us or it did not; if it did not, it has already disconnected us
            // with `RunInProgress` and there is no response to read.
            Log.Warn("[SpirePvp] rejoin: run in progress but no rejoin response — the host did not recognise this peer");
            return "The host did not recognise this client. Its run may have moved on.";
        }

        await Restore(flow.NetService, result.rejoinResponse.Value);
        return "Rejoined.";
    }

    /// <summary>
    /// Walk straight into the arena, carrying the snapshot.
    ///
    /// **`DuelArena.Enter` rather than a second copy of it.** That method mirrors
    /// `EnterMapPointInternal` step for step and has already lost six steps to omission, each one
    /// failing differently and none loudly — the map coord and the map-point-history entry most
    /// recently. A resume that re-implemented the sequence would inherit that debt on day one, so
    /// the snapshot is threaded *through* the existing path and the only thing it changes is the
    /// pre-combat sync, which a rejoin has no partner for.
    /// </summary>
    private static void Resume()
    {
        if (_resumeFrom == null)
        {
            Log.Warn("[SpirePvp] rejoin: no combat snapshot — the run is restored but the duel cannot be resumed");
            return;
        }

        NetFullCombatState snapshot = _resumeFrom;
        _resumeFrom = null;

        DuelArena.Reset();
        if (!DuelArena.Enter(snapshot))
        {
            Log.Error("[SpirePvp] rejoin: the arena refused to open — the duel cannot be resumed");
        }
    }

    /// <summary>
    /// Rebuild the run from the host's copy and enter it.
    ///
    /// **This mirrors `RunManager.SetUpReplay` rather than `SetUpSavedMultiplayer`**, and the choice
    /// is not cosmetic. `SetUpSavedMultiplayer` is built around a `LoadRunLobby` — the object a
    /// *load-game* lobby produces — which a rejoin does not have and cannot fake. `SetUpReplay` does
    /// the same three initialise calls while taking its player list from `state.Players`, which is
    /// exactly what we have. So the shape is borrowed from the one vanilla path whose inputs match.
    /// </summary>
    private static async Task Restore(INetClientGameService netService, ClientRejoinResponseMessage response)
    {
        SerializableRun save = response.serializableRun;
        RunState state = RunState.FromSerializable(save);
        RunManager runManager = RunManager.Instance;

        // **Set before a single line of setup runs, not after.** `InitializeSavedRun` below is what
        // re-arms the mod, via `OnRunLoaded` → `AfterRunLoaded` → `DuelMatch.OnRunCreated`, and
        // everything that must behave differently on a restore asks `IsRestoring` from inside that
        // call. Assigning the snapshot afterwards would leave the flag false for the whole of setup
        // and re-run exactly the new-match work this exists to suppress — which is the first rejoin
        // attempt's empty arena, reproduced by an ordering mistake instead of a missing one.
        _resumeFrom = response.combatState;

        Log.Info($"[SpirePvp] rejoin: restoring run — {state.Players.Count} player(s), act {state.CurrentActIndex}, combat state {(response.combatState == null ? "absent" : "present")}");

        runManager.State = state;
        runManager.InitializeShared(
            netService,
            new PeerInputSynchronizer(netService),
            shouldSave: true,
            save.DailyTime,
            save.StartTime,
            save.RunTime,
            save.WinTime,
            save.NumReloads);

        runManager.InitializeRunLobby(
            netService,
            state,
            state.Players.Select(p => new RunLobbyPlayer
            {
                id = p.NetId,
                isModded = netService.LocalVersion.IsModded(),
            }));

        // Re-arms the mod as a side effect — see the class note. Anything that reorders this or
        // replaces it with the individual steps has to keep `OnRunLoaded` firing.
        runManager.InitializeSavedRun(save);

        await NGame.Instance.LoadRun(state, save.PreFinishedRoom);

        Log.Warn($"[SpirePvp] rejoin: run restored and entered — pvp={DuelMatch.IsPvpRun(state)}, "
                 + $"resuming={(_resumeFrom != null ? "a live combat" : "nothing — no snapshot was sent")}");

        Resume();
    }
}
