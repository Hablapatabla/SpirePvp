using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Rooms;

namespace SpirePvp.Duel;

/// <summary>
/// The duel arena: a combat encounter with no monsters at all.
///
/// No registration call needed. ModelDb.AllAbstractModelSubtypes merges
/// ReflectionHelper.GetSubtypesInMods&lt;AbstractModel&gt;(), so an EncounterModel living in a
/// mod assembly is discovered by the vanilla model database on its own — a custom encounter
/// costs no BaseLib dependency.
///
/// Why an encounter rather than converting the combat you are already in: entering a real
/// room means the duel starts from clean state — full hand, fresh energy, correct creature
/// layout at setup time — instead of mid-combat with the previous fight's wreckage lying
/// around. Several early M1 bugs were artifacts of the mid-combat activation rather than of
/// the duel itself.
///
/// RoomType.Monster is a placeholder; when the arena becomes a real map node after the Act 1
/// boss (M6, DESIGN §5), it likely wants its own MapPointType and room visuals.
/// </summary>
public sealed class DuelEncounter : EncounterModel
{
    public override RoomType RoomType => RoomType.Monster;

    /// A duel pays out a winner, not a card reward. CombatRoom.OnCombatEnded checks this
    /// before calling OfferRoomEndRewards, so returning false routes to ProceedWithoutRewards.
    public override bool ShouldGiveRewards => false;

    public override IEnumerable<MonsterModel> AllPossibleMonsters => Array.Empty<MonsterModel>();

    protected override IReadOnlyList<(MonsterModel, string?)> GenerateMonsters()
    {
        return Array.Empty<(MonsterModel, string?)>();
    }
}
