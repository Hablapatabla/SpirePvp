namespace SpirePvp.Race;

/// <summary>
/// M5 (DESIGN §4, I3, I4): decouples the two players during the race phase so each client
/// simulates its own run on the shared seed, and broadcasts RaceProgressMessage for the HUD.
///
/// Responsibilities when implemented:
///  - On race start: set CombatStateSynchronizer.IsDisabled = true, neutralize
///    ChecksumTracker, make MapSelectionSynchronizer treat the local vote as final, and
///    remove room-entry waits on peers (spike first — see M5 acceptance).
///  - Mirror per-player RNG seeds (Player.PlayerRng / PlayerOdds) across both players so
///    rewards/shops/events are identical (I4).
///  - Broadcast RaceProgressMessage on room enter/exit and HP change.
///  - On local Act 1 boss reward completion: send DuelReadyMessage{modVersion}; host
///    transitions to DuelPending when both are in.
/// </summary>
public static class RaceCoordinator
{
}
