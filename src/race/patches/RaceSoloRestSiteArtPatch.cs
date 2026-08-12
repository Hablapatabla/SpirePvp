using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// One duelist at the campfire, not two.
///
/// NRestSiteRoom._Ready seats a character per player and mirrors the odd ones:
///
///     for (int i = 0; i &lt; _runState.Players.Count; i++)
///     {
///         var c = NRestSiteCharacter.Create(_runState.Players[i], i);
///         _characterContainers[i].AddChildSafely(c);
///         if (i % 2 == 1) c.FlipX();
///     }
///
/// So a race rest site drew the opponent sitting across the fire from you — companionably, in a
/// mode whose entire premise is that they are somewhere else.
///
/// **This is the third instance of one pattern, and it is worth naming: the engine indexes
/// presentation by player slot, and in a race the local player must present as slot 0.** The
/// same assumption produced the treasure room's sideways arm (NHandImage rotates by
/// `Index % 4`) and the relic holder that threw for a slot-1 client
/// (`_holdersInUse[playerSlotIndex]`). Expect it again in any room art that seats a party.
///
/// So the opponent's character is hidden, and the local one is moved into the slot-0 seat and
/// un-mirrored. Doing both is what makes the two clients look alike: hiding alone would leave
/// the host at the left seat and the client at the right, facing the wrong way, which is the
/// same screenshot asymmetry in a new place.
///
/// FlipX negates scale.X and position.X, so it is its own inverse — calling it again is the
/// documented way to undo it rather than a coincidence.
///
/// The characters are hidden rather than never created. Characters.First(...) is used by three
/// hover/selection handlers and throws when it finds nothing; those are all gated on
/// Players.Count &gt; 1, which is still true here, and a rest-site hover from a peer at the same
/// coord would reach them. Keeping the node and hiding it leaves every lookup valid — the same
/// reasoning as the treasure room's phantom hand.
/// </summary>
[HarmonyPatch(typeof(NRestSiteRoom), nameof(NRestSiteRoom._Ready))]
public static class RaceSoloRestSiteArtPatch
{
    public static void Postfix(NRestSiteRoom __instance)
    {
        if (!DuelSession.IsRaceActive)
        {
            return;
        }

        for (int i = 0; i < __instance.Characters.Count; i++)
        {
            NRestSiteCharacter character = __instance.Characters[i];

            if (!LocalContext.IsMe(character.Player))
            {
                character.Visible = false;
                continue;
            }

            if (i == 0 || __instance._characterContainers.Count == 0)
            {
                continue;
            }

            // Undo the mirroring vanilla applied for the odd seat, then move to the seat a
            // singleplayer run would have used.
            if (i % 2 == 1)
            {
                character.FlipX();
            }

            character.GetParent()?.RemoveChild(character);
            __instance._characterContainers[0].AddChildSafely(character);
            character.Position = Vector2.Zero;
        }
    }
}
