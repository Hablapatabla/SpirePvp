using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using SpirePvp.Modifiers;

namespace SpirePvp.Duel;

/// <summary>
/// The "host duel" route from the main menu (M7, DESIGN §5b).
///
/// A duel is still a Custom run underneath, and deliberately so. `GameMode` is a vanilla enum
/// we cannot extend, and more importantly the constraint that shaped §5b has not moved: whatever
/// the menu does, it must end in the same `ModifierModel`s on the `RunState`, because that is
/// what makes a saved PvP run reload as one and what puts the agreed settings in front of the
/// joining player before they commit. Hosting Custom and configuring it is not a workaround —
/// it is the only route that keeps those properties.
///
/// What the menu entry actually removes is the *burial*: today a host has to know to pick
/// Custom and then find one tickbox in each of three groups, in a flat list, with no indication
/// that the three go together or that picking no clock silently means "off". Pressing Duel
/// picks them for you.
///
/// This class is only the handoff between the button and the screen, because those are two
/// different places in the menu stack: the button starts a Custom host, and the lobby screen
/// that results has to notice it was opened for a duel. A one-shot flag is enough — it is set
/// on press and consumed by the first lobby that opens.
/// </summary>
public static class DuelHostFlow
{
    /// <summary>
    /// Set when the Duel entry is pressed, cleared by the lobby that consumes it.
    ///
    /// One-shot on purpose. If the host backs out of the lobby and hosts a plain Custom run
    /// instead, that run must not silently arrive pre-configured as a duel.
    /// </summary>
    public static bool Requested { get; set; }

    /// <summary>
    /// The default time control, and the one chess convention the design already committed to:
    /// **blitz is a 10 minute race followed by a 2 minute duel** (DESIGN §5b).
    ///
    /// Real-time rather than turn-based because turn-based is M8 and currently plays as blitz
    /// anyway — offering it as the default would be offering something that does not exist yet.
    ///
    /// **`ToMutable()` is not optional.** `ModelDb.Modifier&lt;T&gt;()` hands back the canonical
    /// instance, and `ModifierModel.IsEquivalent` — which is how `SetTickedModifiers` decides
    /// which boxes to tick — refuses to match across that boundary:
    ///
    ///     if (IsCanonical == other.IsCanonical) return GetType() == other.GetType();
    ///     return false;
    ///
    /// The tickboxes hold mutable copies (`GetAllModifiers` yields `item.ToMutable()`), so a
    /// canonical preset is the same type with the opposite flag and matches nothing. It fails
    /// *silently* — the loop simply ticks no boxes — which is the failure mode to remember: an
    /// empty lobby with both log lines present and no exception anywhere.
    ///
    /// It is also the instance kind anything downstream wants, since `ToSerializable` asserts
    /// mutability.
    /// </summary>
    public static IReadOnlyList<ModifierModel> BlitzPreset => new List<ModifierModel>
    {
        ModelDb.Modifier<DuelBlitz>().ToMutable(),
        ModelDb.Modifier<RaceClockTen>().ToMutable(),
        ModelDb.Modifier<DuelClockTwo>().ToMutable()
    };

    /// <summary>
    /// Named time controls, on chess conventions — bullet, blitz, rapid, and none.
    ///
    /// **Clocks only.** A preset deliberately does not touch the turn model: that is a different
    /// decision about what kind of game this is, not how long it lasts, and conflating them would
    /// mean picking Rapid silently changed the rules. It also means a preset can never leave the
    /// lobby without a turn model, which is the one modifier that marks the run as PvP at all.
    ///
    /// Bullet is the pair that makes flagging reachable inside a single test run, which is why it
    /// stays in the real list rather than behind a dev flag — it is a legitimate time control as
    /// well as the only practical way to exercise the clock.
    /// </summary>
    public static IReadOnlyList<(string LocKey, ModifierModel Race, ModifierModel Duel)> Presets =>
        new List<(string, ModifierModel, ModifierModel)>
        {
            ("SPIREPVP_PRESET.bullet",
                ModelDb.Modifier<RaceClockOne>().ToMutable(),
                ModelDb.Modifier<DuelClockOne>().ToMutable()),
            ("SPIREPVP_PRESET.blitz",
                ModelDb.Modifier<RaceClockTen>().ToMutable(),
                ModelDb.Modifier<DuelClockTwo>().ToMutable()),
            ("SPIREPVP_PRESET.rapid",
                ModelDb.Modifier<RaceClockTwenty>().ToMutable(),
                ModelDb.Modifier<DuelClockFive>().ToMutable()),
            ("SPIREPVP_PRESET.untimed",
                ModelDb.Modifier<RaceClockNone>().ToMutable(),
                ModelDb.Modifier<DuelClockNone>().ToMutable())
        };
}
