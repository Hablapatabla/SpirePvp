using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Tells the survivor's countdown that the opponent came back.
///
/// **The host already does the whole rejoin and tells nobody who cares.**
/// `RunLobby.HandleClientRejoinRequestMessage` accepts any peer it still recognises, ships the run,
/// re-adds the player and raises `PlayerRejoined` — but `DuelDisconnect` was listening only for
/// heartbeat silence ending on the *existing* link. A rejoin is a brand new connection, so that
/// signal never fires, and without this the returning player wins the race back only to be declared
/// a forfeit by a countdown that never heard them knock.
///
/// **A postfix on the handler rather than a subscription to `PlayerRejoined`**, because the
/// subscription would need a `RunLobby` to hang off and the arming rule here is "at run start, never
/// lazily" — and the lobby a rejoin concerns is the one the *host* has had all along. Patching the
/// handler needs no lifecycle at all and cannot be armed late, which is the failure this project has
/// now had five times.
///
/// It fires whether or not a wait is running: <see cref="DuelDisconnect.NotePeerRejoined"/> asks the
/// condition it means rather than one that merely correlates.
/// </summary>
[HarmonyPatch(typeof(RunLobby), nameof(RunLobby.HandleClientRejoinRequestMessage))]
public static class DuelRejoinHostPatch
{
    public static void Postfix(ClientRejoinRequestMessage message, ulong senderId)
    {
        DuelDisconnect.NotePeerRejoined(senderId);
    }
}
