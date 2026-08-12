using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// The opponent does not get an opinion on your choices.
///
/// Vanilla decorates choice UI with each player's portrait so a co-op party can see who wants
/// what: map points, event options, relic holders and the treasure room's skip button all feed
/// an NMultiplayerVoteContainer from a per-player predicate. In a race those are your choices
/// alone, and the opponent is answering a different copy of the same room.
///
/// **The skip button is not hypothetical, and it is our own doing.** RaceSoloTreasurePatch
/// resolves the absent opponent's relic vote by marking it received with a null index, because
/// that is what AwardRelics reads as "skipped" and it is the only encoding that does not have
/// them contest the one relic on offer. But NTreasureRoom.IsPlayerVotingForSkip asks the
/// identical question:
///
///     playerVote.voteReceived &amp;&amp; !playerVote.index.HasValue
///
/// — so the bookkeeping that unblocks the chest also announces, on screen, that your opponent
/// voted to skip it. Fixing the barrier without this would have traded a hang for a lie.
///
/// The relic holders next to it are already fine and are deliberately left alone:
/// PlayerVotedForRelic tests `index == Index`, and a null index matches no relic.
///
/// Event options are suppressed on the same principle rather than on a reproduction. Events are
/// per-player (`shared: False`), so the opponent is voting on their own event, and
/// GetPlayerVote is keyed by index — two different events whose indices happen to coincide
/// would put their portrait on your option. Cheap to rule out, and the alternative is finding
/// out during a real match.
///
/// Map points are handled separately by RaceMapPositionPatch, which does not suppress the
/// portraits but *repurposes* them: in a race they stop meaning "where they voted" and start
/// meaning "where they are".
/// </summary>
[HarmonyPatch]
public static class RaceNoOpponentVoteIconsPatch
{
    [HarmonyPatch(typeof(NTreasureRoom), nameof(NTreasureRoom.IsPlayerVotingForSkip))]
    [HarmonyPrefix]
    public static bool NoOpponentSkipVote(Player player, ref bool __result)
    {
        if (!DuelSession.IsRaceActive || LocalContext.IsMe(player))
        {
            return true;
        }

        __result = false;
        return false;
    }

    [HarmonyPatch(typeof(NEventOptionButton), nameof(NEventOptionButton.ShouldDisplayPlayerVote))]
    [HarmonyPrefix]
    public static bool NoOpponentEventVote(Player player, ref bool __result)
    {
        if (!DuelSession.IsRaceActive || LocalContext.IsMe(player))
        {
            return true;
        }

        __result = false;
        return false;
    }
}
