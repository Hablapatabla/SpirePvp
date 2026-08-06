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
[HarmonyPatch(typeof(ModelDb), "GoodModifiers", MethodType.Getter)]
public static class DuelModifierListPatch
{
    public static void Postfix(ref IReadOnlyList<ModifierModel> __result)
    {
        List<ModifierModel> withDuel = new List<ModifierModel>(__result)
        {
            ModelDb.Modifier<DuelBlitz>(),
            ModelDb.Modifier<DuelTurnBased>(),
            ModelDb.Modifier<RaceClockOne>(),
            ModelDb.Modifier<RaceClockTen>(),
            ModelDb.Modifier<RaceClockFifteen>(),
            ModelDb.Modifier<RaceClockTwenty>(),
            ModelDb.Modifier<RaceClockNone>(),
            ModelDb.Modifier<DuelClockOne>(),
            ModelDb.Modifier<DuelClockTwo>(),
            ModelDb.Modifier<DuelClockThree>(),
            ModelDb.Modifier<DuelClockFive>(),
            ModelDb.Modifier<DuelClockNone>()
        };
        __result = withDuel;
    }
}

[HarmonyPatch(typeof(ModelDb), "MutuallyExclusiveModifiers", MethodType.Getter)]
public static class DuelModifierExclusivityPatch
{
    public static void Postfix(ref IReadOnlyList<IReadOnlySet<ModifierModel>> __result)
    {
        List<IReadOnlySet<ModifierModel>> sets = new List<IReadOnlySet<ModifierModel>>(__result)
        {
            new HashSet<ModifierModel>
            {
                ModelDb.Modifier<DuelBlitz>(),
                ModelDb.Modifier<DuelTurnBased>()
            },
            new HashSet<ModifierModel>
            {
                ModelDb.Modifier<RaceClockOne>(),
                ModelDb.Modifier<RaceClockTen>(),
                ModelDb.Modifier<RaceClockFifteen>(),
                ModelDb.Modifier<RaceClockTwenty>(),
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
        __result = sets;
    }
}
