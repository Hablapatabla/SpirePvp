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
    /// </summary>
    public static IReadOnlyList<ModifierModel> BlitzPreset => new List<ModifierModel>
    {
        ModelDb.Modifier<DuelBlitz>(),
        ModelDb.Modifier<RaceClockTen>(),
        ModelDb.Modifier<DuelClockTwo>()
    };
}
