using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using SpirePvp.Duel;

namespace SpirePvp.Race.Patches;

/// <summary>
/// Keeps co-op cards out of a PvP run (DESIGN §9: "co-op-only cards in the duel pool: ban at
/// draft/reward level during the race").
///
/// The engine already has the concept — <c>CardMultiplayerConstraint</c>, with
/// <c>MultiplayerOnly</c> cards like Beacon Of Hope, Gang Up and Blade Symphony that target or
/// buff an ally. What it does not have is our situation: it decides which set to offer purely
/// from <c>Players.Count > 1</c>, so a PvP run with two players in the lobby looks exactly like
/// co-op and gets the co-op pool. That is the same wrong premise the whole race phase keeps
/// running into — the engine equating "two players in the run" with "two players playing
/// together" — and here it fails as content rather than as a crash.
///
/// A PvP run wants the *singleplayer* pool: each racer plays alone, and there is never an ally
/// to point Gang Up at. So all three patches below say the same thing in the three places the
/// engine asks the question independently.
///
/// Cards already in a deck are untouched. This is a draft-level ban, so a co-op card can only
/// still appear if someone starts with one.
/// </summary>
internal static class NoCoopCards
{
    /// <summary>True when this run is a PvP match and should be offered singleplayer content.</summary>
    internal static bool Applies(IRunState? runState) => DuelMatch.IsPvpRun(runState);

    internal static bool Applies() => Applies(RunManager.Instance.State);

    internal static IEnumerable<CardModel> Strip(IEnumerable<CardModel> cards) =>
        cards.Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly);
}

/// <summary>
/// Card rewards and shop stock. <c>CardFactory.FilterForPlayerCount</c> is the chokepoint both
/// <c>CreateForReward</c> and <c>CreateForMerchant</c> run their candidates through, and it
/// keys straight off <c>runState.Players.Count > 1</c> rather than off the run's constraint
/// property — which is why patching the property alone would miss every card reward in the game.
/// </summary>
[HarmonyPatch(typeof(CardFactory), "FilterForPlayerCount")]
public static class RaceNoCoopCardRewardsPatch
{
    public static void Postfix(IRunState runState, ref IEnumerable<CardModel> __result)
    {
        if (NoCoopCards.Applies(runState))
        {
            __result = NoCoopCards.Strip(__result);
        }
    }
}

/// <summary>
/// Everything that draws from a card pool directly rather than through the reward factory:
/// in-combat generation (Discovery, Attack Potion), Scroll Boxes, the character-cards modifier.
/// They all call <c>GetUnlockedCards</c> with <c>runState.CardMultiplayerConstraint</c>, which
/// is a default interface member computed from the player count and so reports
/// <c>MultiplayerOnly</c> for us.
///
/// Postfixing the pool method rather than that property on purpose: the property is a default
/// interface member, which is an awkward Harmony target, and the pool is where every one of
/// these paths converges anyway.
/// </summary>
[HarmonyPatch(typeof(CardPoolModel), "GetUnlockedCards")]
public static class RaceNoCoopCardPoolPatch
{
    public static void Postfix(ref IEnumerable<CardModel> __result)
    {
        // Guarded on the live run, since pools are also read from the main menu's collection
        // screens where there is no run at all.
        if (NoCoopCards.Applies())
        {
            __result = NoCoopCards.Strip(__result);
        }
    }
}

/// <summary>
/// Massive Scroll — the Neow blessing that started this. It is the one relic in the game whose
/// content is co-op-only: <c>IsAllowed</c> is literally <c>runState.Players.Count > 1</c>, and
/// what it hands you is a choice of three cards filtered to
/// <c>MultiplayerConstraint == MultiplayerOnly</c>.
///
/// So it cannot merely be filtered — with the two patches above its own card pool would come
/// back empty and it would offer a choice of nothing. It has to not be offered, which is what
/// <c>IsAllowed</c> is for: Neow consults it through <c>IsAllowedAtNeow</c>, and
/// <c>RelicGrabBag</c> consults it directly, so one patch covers both.
///
/// Named specifically rather than filtered by a rule, because there is exactly one such relic —
/// it is the only file under <c>Models/Relics</c> that mentions a multiplayer constraint or a
/// player count at all. If a game update adds another, that grep is how to find it.
/// </summary>
[HarmonyPatch(typeof(MassiveScroll), "IsAllowed")]
public static class RaceNoCoopNeowRelicPatch
{
    public static void Postfix(IRunState runState, ref bool __result)
    {
        if (NoCoopCards.Applies(runState))
        {
            __result = false;
        }
    }
}
