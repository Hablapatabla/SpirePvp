using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// Guards the black screen on entering a room, and reports who actually caused it.
///
/// `PlayerCmd.GainStars` passes `player.Creature.CombatState` straight into
/// `Hook.ShouldGainStars`, which iterates it without a null check — so a star gain for a player
/// who is not in the current combat NREs inside the hook iterator. The throw escapes through
/// `CombatRoom.StartCombat`, the room never finishes loading, and the screen stays black.
/// Divine Right's `AfterRoomEntered` is the trigger we keep hitting.
///
/// Deactivating the opponent's hooks was supposed to prevent this — `RunState.IterateHookListeners`
/// does check `IsActiveForHooks` before adding a player's relics — and the log confirms only
/// the remote player is deactivated now. Yet it still fires, which means one of two things is
/// true and reading the code has not settled which: the owner is the *local* player and their
/// creature somehow has no combat state at this point, or the deactivation is being undone
/// between run launch and room entry.
///
/// So this logs the owner and the state it found, once per occurrence, and then skips the star
/// gain rather than letting a cosmetic relic trigger take down room loading. Skipping is the
/// right call regardless of which cause it turns out to be: a player outside the combat has no
/// combat state to gain stars into.
/// </summary>
[HarmonyPatch(typeof(PlayerCmd), "GainStars")]
public static class RaceStarsWithoutCombatPatch
{
    public static bool Prefix(Player player)
    {
        if (!DuelSession.IsRaceActive && !DuelSession.IsDuelActive)
        {
            return true;
        }

        if (player?.Creature?.CombatState != null)
        {
            return true;
        }

        Log.Warn($"[SpirePvp] race: skipped GainStars for player {player?.NetId} — " +
                 $"no combat state (isMe={MegaCrit.Sts2.Core.Context.LocalContext.IsMe(player)}, " +
                 $"activeForHooks={player?.IsActiveForHooks}). This is the black-screen guard.");
        return false;
    }
}
