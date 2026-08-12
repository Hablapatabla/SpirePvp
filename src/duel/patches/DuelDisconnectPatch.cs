using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// A dropped connection ends the match with a result, instead of ending it with nothing.
///
/// **The hole this closes.** Before this, a player who left mid-match simply vanished: the other
/// side was told "disconnected" and carried on playing a run whose opponent no longer existed,
/// and the match ended with no result recorded for anybody. For a competitive mode that is worse
/// than either player losing — a resignation at least says who won.
///
/// **This is the first slice of the disconnect handling proposed in `docs/PLAYTEST_LIST.md`, and
/// deliberately the endgame half of it.** The full proposal is freeze-both-players plus a
/// reconnect window, with disconnect-as-loss only for someone who never returns. That endgame is
/// needed either way — a window has to end somehow — so it is worth having on its own, and it is
/// the half that stops a match evaporating. The window is the second slice and needs
/// `JoinFlow.AttemptRejoin`, which is a different piece of work.
///
/// **Two routes in, and they are not symmetric**, because a disconnect is the one event that can
/// remove the arbiter:
///
/// - **The peer dropped** (`RemotePlayerDisconnected`). Fires on the host when a client goes, and
///   on a client when the host tells it a peer left. Whoever is still here wins.
/// - **We lost the connection** (`LocalPlayerDisconnected`). This is how a *client* learns the
///   host is gone, and there is nobody left to arbitrate it. So the client decides locally, which
///   breaks host authority knowingly: with one player left there is exactly one possible answer,
///   and refusing to answer it is what produced the original bug.
///
/// **The guards are vanilla's own, and are the whole correctness argument.**
/// `RunManager.LocalPlayerDisconnected` already distinguishes a genuine drop from the ordinary
/// ways a connection ends:
///
///     if (info.GetReason() != NetError.QuitGameOver &amp;&amp; !IsAbandoned &amp;&amp; !State.IsGameOver)
///
/// Those three exclusions are exactly the ones this needs, and for the same reasons. Leaving the
/// result screen disconnects (`QuitGameOver`) — that is a finished match, not a forfeit. A
/// vanilla abandon sets `IsAbandoned`. A finished run sets `IsGameOver`. Asking the same
/// questions vanilla asks means this cannot invent a result for a match that already has one.
///
/// `DuelResult.Declare` is idempotent once the phase is `Complete`, which is the backstop: a
/// resignation declares first and *then* the connection closes, so the close arrives to find the
/// match already decided and does nothing. That ordering is why resigning still reports as a
/// resignation rather than as a disconnect.
/// </summary>
[HarmonyPatch]
public static class DuelDisconnectPatch
{
    /// <summary>
    /// Captures why the last client dropped, on its way past.
    ///
    /// **The reason is known here and thrown away one line later.**
    /// `RunLobby.OnDisconnectedFromClientAsHost` is handed a `NetErrorInfo`, logs it, and then
    /// raises `RemotePlayerDisconnected` carrying nothing but the player id — so the host's own
    /// route below cannot tell a quit from a divergence kick it issued itself. That is how both
    /// players ended a match on a VICTORY banner (see <see cref="DuelEndReason.Desync"/>): the
    /// host read its own ejection of the client as the client walking away.
    ///
    /// A prefix because the event fires inside the method, so a postfix would run after the thing
    /// that needs the answer. The value is held on <see cref="DuelDisconnect"/> rather than here,
    /// because it is run-scoped state and that is where run-scoped state gets released.
    /// </summary>
    [HarmonyPatch(typeof(RunLobby), nameof(RunLobby.OnDisconnectedFromClientAsHost))]
    [HarmonyPrefix]
    public static void BeforeDisconnectedFromClientAsHost(NetErrorInfo info)
    {
        DuelDisconnect.NoteClientDropReason(info.GetReason());
    }

    /// <summary>The peer went away while we are still playing. We win — unless the sim came apart.</summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.RemotePlayerDisconnected))]
    [HarmonyPostfix]
    public static void AfterRemotePlayerDisconnected(RunManager __instance, ulong playerId)
    {
        NetError? reason = DuelDisconnect.TakeClientDropReason();

        // **Before the ShouldDecide guard, because that guard is about deciding a *match* and
        // this is not.** A match already `Complete` returns false there, which is right — a
        // finished match must not be re-decided — but the result screen still has live controls
        // that involve the opponent, and they have to learn the opponent has gone.
        DuelRematch.NotePeerGone();

        if (!DuelDisconnect.ShouldDecide(__instance))
        {
            return;
        }

        // A divergence is not a departure, and the host is the side that has to notice: it is the
        // one that issued the kick, so "the client disconnected" is true and entirely misleading.
        if (reason != null && DuelDisconnect.IsDesync(reason.Value))
        {
            DuelDisconnect.DeclareVoid(reason.Value);
            return;
        }

        DuelDisconnect.Declare($"opponent {playerId} announced a disconnect mid-match");
    }

    /// <summary>
    /// We lost the connection while still playing — which for a client is how the host vanishing
    /// arrives.
    ///
    /// **A prefix, and that is the whole point.** `LocalPlayerDisconnected` kicks off
    /// `ReturnToMainMenuWithError` *inside itself*, so a postfix here runs too late to matter: the
    /// suppression prefix below had already asked whether a wait was running, been told no, and
    /// let vanilla start tearing the run down. The client duly opened a wait window and then went
    /// to the main menu anyway — the log said `opening the wait window` and, four lines later,
    /// `Time to main menu`. Marking the wait *before* vanilla decides is what makes the
    /// suppression see it.
    ///
    /// Returns true throughout: vanilla's own bookkeeping here (telling the input synchronizer
    /// about the departed player) is still wanted, and the only thing being changed is what it
    /// concludes at the end.
    /// </summary>
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.LocalPlayerDisconnected))]
    [HarmonyPrefix]
    public static bool BeforeLocalPlayerDisconnected(RunManager __instance, NetErrorInfo info)
    {
        // Same reasoning as the remote route: a `QuitGameOver` or an already-finished match is
        // not a forfeit, but it does mean nothing on the result screen can involve the peer again.
        DuelRematch.NotePeerGone();

        // The same test vanilla makes one line above, for the same reason: this is only a forfeit
        // when it is not one of the ordinary ways a connection ends.
        if (info.GetReason() == NetError.QuitGameOver || !DuelDisconnect.ShouldDecide(__instance))
        {
            return true;
        }

        DuelDisconnect.NoteConnectionLost(info);

        // A deliberate departure was declared outright, so the run is over and vanilla can finish
        // normally — its own `!State.IsGameOver` guard now stops it going to the menu.
        if (!DuelDisconnect.IsWaiting)
        {
            return true;
        }

        // Vanilla's own bookkeeping, kept: the input synchronizer has to be told, or the departed
        // player's cursor and focus state linger.
        IRunState? state = __instance.State;
        if (state != null)
        {
            foreach (Player player in state.Players)
            {
                if (!LocalContext.IsMe(player))
                {
                    __instance.InputSynchronizer.OnPlayerDisconnected(player.NetId);
                }
            }
        }

        Log.Warn("[SpirePvp] holding the run open — a disconnect wait is running");
        return false;
    }

    // The obvious seam — a prefix on `RunManager.ReturnToMainMenuWithError` — was tried and does
    // not work, which is worth recording because everything about it looks fine.
    //
    // **A small `async Task` method can be inlined into its caller, and Harmony then registers a
    // patch that never executes.** A load-time probe listed `patched
    // RunManager.ReturnToMainMenuWithError`, the prefix body never ran, and the stack trace showed
    // why: every patched frame carries a `_Patch1` suffix, and that one did not —
    //
    //     at NGame.ReturnToMainMenu_Patch1(NGame this)
    //     at NGame.ReturnToMainMenuAfterRun()
    //     at RunManager.ReturnToMainMenuWithError(NetErrorInfo info)   <- no _Patch1
    //     at AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
    //     at RunManager.LocalPlayerDisconnected_Patch1(RunManager this, NetErrorInfo info)
    //
    // — with the stub's own frame absent, because patching the caller re-JITted it and the JIT
    // inlined the tiny stub in the process. This is the project's "a missing frame means check the
    // patch, not the callee" rule aimed at the patch itself. The fix is to not patch a method
    // small enough to vanish: suppress at the caller, above, which is a `void` and stays real.
}
