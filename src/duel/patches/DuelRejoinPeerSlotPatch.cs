using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Frees the seat a dropped duelist is still occupying, so they can sit back down in it.
///
/// # The blocker this exists for, read straight out of a log
///
/// A rejoin never reached the game layer at all. The client dialled, sent its handshake, and got:
///
///     [ENetHost] Received handshake packet containing peer ID 1001
///     [ENetHost] Second client attempted to connect with peer ID 1001, disconnecting them
///
/// **`RunLobby`'s rejoin gate is fine and never ran.** The refusal is two layers below it, in the
/// transport: `DoClientHandshake` answers `GetConnectionById(netId).HasValue` with an
/// `IdCollision` and hangs up. That check is correct in a healthy match — two clients must not
/// share an id — and wrong for a returning one, and the reason it cannot tell them apart is the
/// fact this project already had written down: **ENet never reports a hard drop.**
/// `ENetHost.Update` answers the transport's own disconnect event with a bare `continue`, so a
/// killed client leaves its `ClientConnection` sitting in `_connectedPeers` forever. The host was
/// still sending to it — the log is thousands of lines of `Peer not connected` — and still counting
/// it as the occupant of id 1001. So the returning player collides with their own corpse.
///
/// # Why the eviction is here and not where the drop is noticed
///
/// The obvious place is the moment the mod decides a peer is gone: it already knows, and evicting
/// there would also stop the `Peer not connected` spam. **It would also break the case that
/// currently works.** A brief stall is the same event from the transport's point of view, and it
/// heals itself when packets resume on the *same* peer — `DuelDisconnect` clears the wait with
/// "the opponent is talking again". Evicting on suspicion would tear down a connection that was
/// about to recover, turning a self-healing hitch into a forced rejoin.
///
/// So the seat is only freed when someone actually knocks: a handshake arriving *is* the evidence,
/// and it arrives exactly once, for exactly the id in question.
///
/// # Two conditions, both asked rather than assumed
///
/// - **The duel must be waiting on a peer** (`DuelDisconnect.IsWaitingForPeer`). Without it this
///   would let any second client with a colliding id evict the first, which is the security hole
///   vanilla's check exists to close. With it, the only id that can be displaced is one the match
///   has already concluded is missing.
/// - **The stale connection must not be the peer now knocking.** A handshake re-sent on a live
///   connection is not a rejoin, and evicting it would drop a player who never left.
///
/// # `notifyHandler: false`, which is load-bearing rather than tidy
///
/// `HandleClientDisconnection` normally raises `OnPeerDisconnected` up to the game layer, and here
/// that would be actively harmful: the rejoin gate the client is about to hit is
/// `_playerCollection.GetPlayer(senderId)`, so pruning the player is precisely how you turn a
/// recognised duelist into a stranger. The game layer must go on believing the player exists —
/// which it does, because ENet never told it otherwise. We are correcting the transport's
/// bookkeeping only.
///
/// # Timing
///
/// `HandlePacketReceived` is synchronous and returns before `Update()` does, while
/// `DoClientHandshake` polls `_receivedHandshakes` from an async loop on a later frame callback. So
/// a postfix here always lands before the collision check reads the list. That ordering is the
/// reason this patches the receipt rather than `DoClientHandshake`, which is an `async Task` and
/// carries every trap this project has paid for twice.
/// </summary>
[HarmonyPatch(typeof(ENetHost), nameof(ENetHost.HandlePacketReceived))]
public static class DuelRejoinPeerSlotPatch
{
    public static void Postfix(ENetHost __instance, ENetServiceData data)
    {
        if (!DuelDisconnect.IsWaitingForPeer)
        {
            return;
        }

        ENetPacket packet = new ENetPacket(data.packetData);
        if (packet.PacketType != ENetPacketType.HandshakeRequest)
        {
            return;
        }

        ulong netId = packet.AsHandshakeRequest().netId;

        for (int i = __instance._connectedPeers.Count - 1; i >= 0; i--)
        {
            var stale = __instance._connectedPeers[i];
            if (stale.netId != netId || stale.peer == data.peer)
            {
                continue;
            }

            Log.Warn($"[SpirePvp] rejoin: freeing the stale peer slot for {netId} — ENet never "
                     + "reported the drop, so the returning duelist was colliding with their own "
                     + "dead connection");

            // The game layer is deliberately not told: it still holds this player, and the rejoin
            // gate about to run depends on that.
            __instance.HandleClientDisconnection(stale, NetError.Timeout, notifyHandler: false);
        }
    }
}
