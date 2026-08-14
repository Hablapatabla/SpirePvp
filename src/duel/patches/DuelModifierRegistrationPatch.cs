using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using SpirePvp.Modifiers;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Puts the duel modifiers into the custom-run modifier list.
///
/// Unlike `ModelDb.All` — which merges `GetSubtypesInMods` and is why `DuelEncounter`
/// registers itself for free — `GoodModifiers`, `BadModifiers` and
/// `MutuallyExclusiveModifiers` are **hardcoded arrays**. A mod's modifiers are instantiated
/// into the model database but never appear in the picker without this.
///
/// Each property builds a fresh array per call, so appending in a postfix mutates nothing
/// shared. `NCustomRunModifiersList.GetAllModifiers` reads `GoodModifiers.Concat(BadModifiers)`,
/// so patching here covers the UI and any other reader at once.
///
/// The exclusivity sets are what make the tickboxes behave as radio buttons: exactly one turn
/// model, one race clock and one duel clock — three groups, three decisions. That is vanilla's
/// own mechanism (`NCustomRunModifiersList` consults `MutuallyExclusiveModifiers` on every
/// toggle), so we declare the groups rather than policing them.
/// </summary>
[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.GoodModifiers), MethodType.Getter)]
public static class DuelModifierListPatch
{
    public static void Postfix(ref IReadOnlyList<ModifierModel> __result)
    {
        List<ModifierModel> withDuel = new List<ModifierModel>(__result)
        {
            ModelDb.Modifier<DuelBlitz>(),
            ModelDb.Modifier<DuelTurnBased>(),
            ModelDb.Modifier<RaceClockEight>(),
            ModelDb.Modifier<RaceClockTen>(),
            ModelDb.Modifier<RaceClockTwelve>(),
            ModelDb.Modifier<RaceClockFifteen>(),
            ModelDb.Modifier<RaceClockNone>(),
            ModelDb.Modifier<DuelClockOne>(),
            ModelDb.Modifier<DuelClockTwo>(),
            ModelDb.Modifier<DuelClockThree>(),
            ModelDb.Modifier<DuelClockFive>(),
            ModelDb.Modifier<DuelClockNone>(),
            ModelDb.Modifier<DuelSpeedFast>()
        };
        __result = withDuel;
    }
}

[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.MutuallyExclusiveModifiers), MethodType.Getter)]
public static class DuelModifierExclusivityPatch
{
    /// <summary>
    /// The three decisions — one source, because two readers need them.
    ///
    /// The getter below turns these into vanilla's exclusivity sets (pick one, and the others
    /// untick). `DuelModifierMinimumPatch` reads the same groups to enforce the half vanilla does
    /// not: that a group is never left *empty*.
    ///
    /// Built fresh per call rather than held in a static, for the same reason the arrays above are:
    /// `ModelDb.Modifier&lt;T&gt;()` needs the model database populated, and a static initialiser
    /// would run at an arbitrary earlier moment.
    /// </summary>
    public static List<HashSet<ModifierModel>> DuelGroups() => new List<HashSet<ModifierModel>>
    {
        new HashSet<ModifierModel>
        {
            ModelDb.Modifier<DuelBlitz>(),
            ModelDb.Modifier<DuelTurnBased>()
        },
        new HashSet<ModifierModel>
        {
            ModelDb.Modifier<RaceClockEight>(),
            ModelDb.Modifier<RaceClockTen>(),
            ModelDb.Modifier<RaceClockTwelve>(),
            ModelDb.Modifier<RaceClockFifteen>(),
            ModelDb.Modifier<RaceClockNone>()
        },
        new HashSet<ModifierModel>
        {
            ModelDb.Modifier<DuelClockOne>(),
            ModelDb.Modifier<DuelClockTwo>(),
            ModelDb.Modifier<DuelClockThree>(),
            ModelDb.Modifier<DuelClockFive>(),
            ModelDb.Modifier<DuelClockNone>()
        }
    };

    public static void Postfix(ref IReadOnlyList<IReadOnlySet<ModifierModel>> __result)
    {
        List<IReadOnlySet<ModifierModel>> sets = new List<IReadOnlySet<ModifierModel>>(__result);
        foreach (HashSet<ModifierModel> group in DuelGroups())
        {
            sets.Add(group);
        }

        __result = sets;
    }
}
