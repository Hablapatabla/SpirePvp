using System;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Race;

/// <summary>
/// Helpers for the single recurring problem of the race phase: **the engine assumes the party
/// is standing together, and in a race it is not.**
///
/// DESIGN I3 predicted this would keep recurring as the race covered more of the game, and it
/// has. Each room has its own synchronizer with its own version of the assumption, and they
/// fail differently — a hang, a silent freeze, a crash, or just something drawn in the wrong
/// place — but they come in two shapes, and both live here.
///
/// **Shape one: a barrier that waits for everyone.** A room gathers a per-player slot of some
/// kind and will not release until every slot is satisfied. The opponent is racing their own
/// run and will never satisfy theirs, so the barrier never falls:
///
/// - `RestSiteRoom.Exit` awaits every player's `completionTaskSource`, which is what left a
///   client unable to walk away from a campfire.
/// - `TreasureRoomRelicSynchronizer.PickRelic` returns early until `_votes.All(voteReceived)`,
///   which is what left a client unable to take the relic it had just clicked.
///
/// Vanilla already knows this can happen and has a blessed answer for it — `OnPeerDisconnected`
/// clears an absent player's rest site precisely so the await cannot "hang forever" (its words).
/// A racing opponent is that same absent player, so the fix is always to satisfy their slot up
/// front rather than to remove it.
///
/// **Shape two: presentation indexed by player slot.** Room art seats the party by slot index,
/// which is only ever correct when there is a party. A client sitting at slot 1 gets the second
/// seat, the second hand, the second holder — and in a race there is no first one to sit beside:
///
/// - `NHandImage` rotates by `Index % 4`, so a slot-1 client reached into the chest sideways.
/// - `NTreasureRoomRelicCollection.DefaultFocusedControl` indexes `_holdersInUse` by slot, which
///   is fine only while holders and players are equal in number — and threw once they were not.
/// - `NRestSiteRoom._Ready` seats a character per player and mirrors the odd ones.
///
/// The rule that falls out, and the one to reach for first when the next room misbehaves: **in
/// a race the local player must present as slot 0.** Where vanilla has a genuine singleplayer
/// path, prefer that to correcting the multiplayer one — checking what the engine draws when
/// alone is what replaced two hand patches with none.
/// </summary>
public static class RaceSolo
{
    /// <summary>
    /// Runs <paramref name="satisfy"/> for each player who is not the local one, passing their
    /// slot index, and returns how many it actually changed.
    ///
    /// The slot index rather than the Player is what callers need, because these barriers are
    /// all backed by lists indexed that way — and the bounds check belongs here rather than at
    /// each call site, since getting it wrong converts a hang into an IndexOutOfRangeException
    /// and the list is not always as long as the player count.
    ///
    /// <paramref name="satisfy"/> returns whether it did anything, so a slot that was already
    /// resolved is not counted; the count exists to keep the "did this fire at all" log lines
    /// honest.
    /// </summary>
    public static int SatisfyAbsentPlayers(IPlayerCollection players, int slotCount,
                                           Func<int, bool> satisfy)
    {
        int satisfied = 0;

        foreach (Player player in players.Players)
        {
            if (LocalContext.IsMe(player))
            {
                continue;
            }

            int slot = players.GetPlayerSlotIndex(player);
            if (slot < 0 || slot >= slotCount)
            {
                continue;
            }

            if (satisfy(slot))
            {
                satisfied++;
            }
        }

        return satisfied;
    }
}
