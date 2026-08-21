using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Remembers how this client reached the host, so <see cref="DuelRejoin"/> can dial the same
/// address again after a drop.
///
/// **Captured rather than reconstructed**, because the address is transport-shaped: on ENet it is
/// ip/port plus the netId from `--clientId`, on Steam it is a lobby id, and
/// `IClientConnectionInitializer` is the seam vanilla already put between the two. Rebuilding one
/// would mean knowing which transport we are on and hard-coding the dev rig's `127.0.0.1:33771`,
/// which would work on this machine and nowhere else.
///
/// # Why a prefix on an async method is safe here
///
/// `JoinGameAsync` is an `async Task`, and this project has twice paid for patching those — a
/// skipping prefix must assign `__result` or the caller awaits null, and a postfix runs when the
/// state machine is created rather than when it completes. Neither applies: this prefix returns
/// `void`, so Harmony never skips the original, and it only reads its argument. The method's return
/// value is untouched and the state machine runs exactly as vanilla wrote it.
///
/// It fires on every join, successful or not. That is deliberate — a join that fails is still
/// evidence of where the host is, and `DuelRejoin.IsOffered` asks its own question about whether
/// rejoining makes sense rather than trusting this to only record good addresses.
/// </summary>
[HarmonyPatch(typeof(NJoinFriendScreen), nameof(NJoinFriendScreen.JoinGameAsync))]
public static class DuelRejoinCapturePatch
{
    public static void Prefix(IClientConnectionInitializer connInitializer)
    {
        if (connInitializer != null)
        {
            DuelRejoin.RememberJoin(connInitializer);
        }
    }
}
