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
/// The arena became a real map node in M6, and the RoomType question that used to sit here is
/// answered on the property below: Boss, because `SetSecondBossEncounter` requires it. No custom
/// MapPointType was needed after all — riding vanilla's second-boss slot supplied the node, its
/// placement and its presentation for free.
/// </summary>
public sealed class DuelEncounter : EncounterModel
{
    /// <summary>
    /// Boss, not Monster — and load-bearing rather than cosmetic.
    ///
    /// `ActModel.SetSecondBossEncounter` rejects anything whose RoomType is not Boss, and that
    /// method is how the arena becomes a real map node: setting it makes `HasSecondBoss` true,
    /// and `StandardActMap` then places a second node one row below the boss and chains it as
    /// the boss's child — the back-to-back layout Act 3 already uses for double bosses. No map
    /// generation code needed patching at all.
    ///
    /// It also gets the arena boss-sized node art and the 2x selection VFX for free.
    /// </summary>
    public override RoomType RoomType => RoomType.Boss;

    /// <summary>
    /// Art for the duel's map node.
    ///
    /// Vanilla resolves this to a Spine skeleton (`..._node_skel_data.tres`) for animated boss
    /// nodes, and `BossNodeSpineResource` returns null when that resource is missing — at which
    /// point `MapNodeAssetPaths` falls back to two static textures, `<path>.png` and
    /// `<path>_outline.png`. We have no Spine rig, so pointing at a path with no `.tres` puts
    /// us on the static branch deliberately rather than by accident.
    ///
    /// Both files ship in the mod's `.pck`; remember `host.ps1` only re-exports the pack when
    /// something under `SpirePvp/` changed, and a missing texture here shows as a blank node
    /// rather than an error.
    /// </summary>
    public override string BossNodePath => "res://SpirePvp/map/duel_node";

    /// A duel pays out a winner, not a card reward. CombatRoom.OnCombatEnded checks this
    /// before calling OfferRoomEndRewards, so returning false routes to ProceedWithoutRewards.
    public override bool ShouldGiveRewards => false;

    public override IEnumerable<MonsterModel> AllPossibleMonsters => Array.Empty<MonsterModel>();

    protected override IReadOnlyList<(MonsterModel, string?)> GenerateMonsters()
    {
        return Array.Empty<(MonsterModel, string?)>();
    }
}
