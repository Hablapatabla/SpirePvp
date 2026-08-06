using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Net;

namespace SpirePvp.Duel;

/// <summary>
/// Resigning, and agreeing a draw — the two ways a match ends by consent rather than by play.
///
/// **Abandoning a PvP run is a resignation, not a disconnect.** Decided 2026-08-06: tipping
/// your king over is a loss for you and a win for your opponent, exactly as in chess. Before
/// this, abandoning tore the run down and told the other player "the host abandoned the game" —
/// which is not a result, leaves no record of who won, and was the only exit from a finished
/// duel because there was no rematch button.
///
/// **Why this replaces vanilla's abandon rather than running alongside it.**
/// `RunManager.Abandon` sends `RunAbandonedMessage` and then *disconnects*
/// (`RunLobby.AbandonRun` → `NetService.Disconnect`). Declaring the result before that runs
/// would put a result screen up and then have vanilla tear it down; declaring it after would
/// be a send into a dead transport, which is the bug this project just spent a session
/// removing. So a PvP resignation skips vanilla's path entirely: broadcast, declare, and leave
/// the connection *up*. The resigning player still gets out — the result screen is the exit —
/// and both players keep a live connection, which is what a rematch button will need.
///
/// **It is also the only resign path a client has.** `RunLobby.AbandonRun` throws for anyone
/// who is not the host, and the pause menu hides Give Up from clients for that reason. Skipping
/// vanilla is what lets the same button mean the same thing on both sides; see
/// `DuelPauseMenuPatch`, which reveals and relabels it.
///
/// Resigning is legal during the race as well as the duel. Conceding a race you cannot win is
/// a real decision, and the alternative — forcing someone to walk to the arena to lose — is
/// worse. The guard is therefore "is a PvP run in progress", not "is a duel active".
/// </summary>
public static class DuelResign
{
    private static bool _armed;

    /// <summary>True when there is a live PvP match that could still be resigned or drawn.</summary>
    public static bool CanResign =>
        RunManager.Instance?.IsInProgress == true &&
        DuelMatch.IsPvpRun(RunManager.Instance?.State) &&
        DuelSession.Phase != DuelPhase.Complete;

    /// <summary>
    /// True once this client has offered a draw and is waiting for an answer. Used to stop the
    /// button spamming offers, and to tell an incoming offer apart from an incoming acceptance.
    /// </summary>
    public static bool DrawOfferPending { get; private set; }

    /// <summary>
    /// True when the opponent has offered a draw and we have not answered. Pressing Offer Draw
    /// in that state means "yes", not "here is a competing offer" — see <see cref="OfferDraw"/>.
    /// </summary>
    public static bool IncomingOfferPending { get; private set; }

    /// <summary>
    /// Register the draw-offer handler at run start.
    ///
    /// At run start, not when the local player first opens the pause menu — the opponent can
    /// offer a draw before you have touched the menu, and an unregistered handler drops the
    /// message silently. That mistake has now cost this project four separate debugging rounds
    /// (the duel handshake, the clock sync, the arena rendezvous, and `DuelFlag`'s clocks), so
    /// it is the default here rather than a lesson to relearn.
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

        net.RegisterMessageHandler<DuelDrawOfferMessage>(OnDrawOffer);
        _armed = true;
    }

    /// <summary>Releases the handler so the next run re-arms. See DuelMatch.OnRunEnded.</summary>
    public static void Disarm()
    {
        RunManager.Instance?.NetService?.UnregisterMessageHandler<DuelDrawOfferMessage>(OnDrawOffer);
        DrawOfferPending = false;
        _armed = false;
    }

    public static void Reset()
    {
        DrawOfferPending = false;
        IncomingOfferPending = false;
    }

    /// <summary>
    /// Resign the match. The opponent wins.
    ///
    /// Returns false when there is nothing to resign — an ordinary co-op or solo run, or a
    /// match already decided — in which case the caller should let vanilla's abandon proceed.
    /// </summary>
    public static bool Resign()
    {
        if (!CanResign)
        {
            return false;
        }

        ulong opponent = OpponentNetId();
        if (opponent == 0)
        {
            // No opponent to award the win to. Better to fall through to vanilla's abandon
            // than to declare a win nobody can receive.
            Log.Warn("[SpirePvp] resign: no opponent found — falling back to a normal abandon");
            return false;
        }

        Log.Warn($"[SpirePvp] resigned — {opponent} wins");

        RunManager.Instance.NetService.SendMessage(new DuelResultMessage
        {
            winnerId = opponent,
            reason = DuelEndReason.Resign
        });

        // Broadcast first, declare second. Declaring runs RunManager.OnEnded, and doing that
        // before the message is on the wire risks the run teardown taking the transport with
        // it — the same ordering that left the host talking to a disconnected service.
        DuelFlagDisarmAndStop();
        DuelResult.DeclareWinner(localPlayerWon: false);
        return true;
    }

    /// <summary>
    /// Offer the opponent a draw. Ends nothing on its own — they have to accept.
    ///
    /// Except when they have already offered: then this button is the accept. Making the player
    /// dismiss their opponent's prompt and hunt for the same button they just pressed would be
    /// a worse answer to "we both want a draw" than simply agreeing.
    /// </summary>
    public static void OfferDraw()
    {
        if (!CanResign || DrawOfferPending)
        {
            return;
        }

        if (IncomingOfferPending)
        {
            RespondToDraw(accept: true);
            return;
        }

        DrawOfferPending = true;
        Log.Warn("[SpirePvp] draw offered");

        RunManager.Instance.NetService.SendMessage(new DuelDrawOfferMessage
        {
            isResponse = false,
            accepted = false
        });
    }

    /// <summary>Answer an offer the opponent made.</summary>
    public static void RespondToDraw(bool accept)
    {
        if (RunManager.Instance?.NetService == null)
        {
            return;
        }

        IncomingOfferPending = false;

        Log.Warn($"[SpirePvp] draw offer {(accept ? "accepted" : "declined")}");

        RunManager.Instance.NetService.SendMessage(new DuelDrawOfferMessage
        {
            isResponse = true,
            accepted = accept
        });

        if (accept)
        {
            DuelFlagDisarmAndStop();
            DuelResult.DeclareDraw();
        }
    }

    private static void OnDrawOffer(DuelDrawOfferMessage message, ulong senderId)
    {
        if (LocalContext.NetId == senderId || !CanResign)
        {
            return;
        }

        if (!message.isResponse)
        {
            Log.Warn($"[SpirePvp] opponent {senderId} offers a draw");

            // Offers crossing on the wire is agreement, not a conflict: we each said we would
            // take a draw. Resolving it here means neither player has to answer a prompt for a
            // question they had just asked themselves. Observed in play 2026-08-06, where both
            // players offering left each staring at the other's prompt.
            if (DrawOfferPending)
            {
                Log.Warn("[SpirePvp] offers crossed — both players want a draw");
                DrawOfferPending = false;
                DuelDrawPrompt.DismissNotice();
                DuelFlagDisarmAndStop();
                DuelResult.DeclareDraw();
                return;
            }

            IncomingOfferPending = true;
            DuelDrawPrompt.Show();
            return;
        }

        DrawOfferPending = false;

        // The answer is here, so the "waiting for your opponent" notice has served its purpose.
        // Down first, before the result screen goes up behind it.
        DuelDrawPrompt.DismissNotice();

        if (message.accepted)
        {
            Log.Warn("[SpirePvp] opponent accepted the draw");
            DuelFlagDisarmAndStop();
            DuelResult.DeclareDraw();
        }
        else
        {
            Log.Warn("[SpirePvp] opponent declined the draw");
            DuelDrawPrompt.ShowDeclined();
        }
    }

    /// <summary>
    /// Stop the clocks and release the flag watcher before declaring, the same way every other
    /// ending does. Without it the clocks fall out of the duel's chess-clock rule the moment the
    /// phase leaves DuelActive and resume under the race's "both simply run".
    /// </summary>
    private static void DuelFlagDisarmAndStop()
    {
        DuelFlag.Disarm();
        DuelClockService.Stop();
    }

    private static ulong OpponentNetId()
    {
        RunState? state = RunManager.Instance?.State;
        if (state == null)
        {
            return 0;
        }

        foreach (Player player in state.Players)
        {
            if (!LocalContext.IsMe(player))
            {
                return player.NetId;
            }
        }

        return 0;
    }
}
