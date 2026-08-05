namespace SpirePvp.Duel.Patches;

/// <summary>
/// M4 (DESIGN §1 information rules, I6): while a duel is active, the opponent must get no
/// signal about what we hover or pre-select.
///
/// NOT YET A HARMONY PATCH. Two candidate chokepoints (pick after reading the code):
///  1. Sender side (preferred — nothing leaves the machine): wherever
///     Core/Multiplayer/Game/PeerInput/PeerInputSynchronizer broadcasts the local hover
///     state that HoveredModelTracker collects — suppress the send when
///     DuelSession.IsDuelActive.
///  2. Display side: wherever remote players' hovered models get rendered.
/// Also audit NetMapDrawingEvent (map pings) and cursor sharing (NetCursorHelper) for the
/// same treatment.
/// </summary>
public static class HoverSuppressionPatch
{
}
