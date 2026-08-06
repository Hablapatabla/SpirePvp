using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Removes the run-score lines from a duel's result screen.
///
/// Vanilla's game-over screen scores a *run*: floors climbed, gold gained, elites killed,
/// bosses slain, ascension multiplier. Every one of those is either meaningless or actively
/// misleading after a duel — the match was decided by who was still standing, and "+42 for
/// floors climbed" invites the loser to think they were ahead. A resignation or an agreed draw
/// makes it worse still, since the numbers describe a race that was abandoned.
///
/// All five lines funnel through the private `AddScoreLine`, so one skipping prefix suppresses
/// the set. `AddScoreLine` returns `void`, so skipping it needs no `__result` — checked against
/// the decompiled signature rather than assumed, per HANDOFF's standing rule about skipping
/// prefixes on async methods.
///
/// This is the cheap half of DESIGN §6's `DuelResultScreen`. The other half — winner and
/// per-round damage in their place — needs damage actually tracked through the duel, which
/// nothing does yet. Taking the wrong numbers down is worth doing before the right ones exist:
/// an empty space says nothing, where these said something false.
///
/// Keyed on `DuelPhase.Complete`, so an ordinary run's score screen is untouched.
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), "AddScoreLine")]
public static class DuelResultScoreLinesPatch
{
    public static bool Prefix() => DuelSession.Phase != DuelPhase.Complete;
}
