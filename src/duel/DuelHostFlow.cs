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
    /// What a freshly opened Duel lobby starts on: **turn-based, fast animations, and no clocks**.
    ///
    /// Untimed rather than the blitz preset DESIGN §5b names, because a default is what someone
    /// plays before they have an opinion, and a clock is the one setting that can end a match
    /// against a player's will. Someone meeting the mode for the first time should get the race
    /// and the duel, not a deadline they did not know they had agreed to — and the presets are
    /// right there for anyone who wants one. Blitz remains the recommended time control; it is
    /// simply no longer imposed.
    ///
    /// **Turn-based rather than real-time, changed 2026-08-14** (Lucas). The old reasoning here was
    /// that turn-based was M8 and "currently plays as blitz anyway" — that stopped being true when
    /// the lock-in model shipped and was playtested end to end, so the default was pointing at the
    /// less finished of the two. `DuelModifierListPatch` puts the same chip first in the row; the
    /// order and the default are one decision and have to move together.
    ///
    /// **Fast animations on by default.** Unticked means `Normal`, which is vanilla's pacing for a
    /// solo run; a duel has two people waiting on every animation, so the shorter one is the better
    /// default and the chip is there to opt back out. Note it is `Fast` and never `Instant` — see
    /// `DuelSpeedFast` for why `Instant` is not offered at all.
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
    public static IReadOnlyList<ModifierModel> DefaultPreset => new List<ModifierModel>
    {
        ModelDb.Modifier<DuelTurnBased>().ToMutable(),
        ModelDb.Modifier<RaceClockNone>().ToMutable(),
        ModelDb.Modifier<DuelClockNone>().ToMutable(),
        ModelDb.Modifier<DuelSpeedFast>().ToMutable()
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
            ("SPIREPVP_PRESET.blitz",
                ModelDb.Modifier<RaceClockTen>().ToMutable(),
                ModelDb.Modifier<DuelClockOne>().ToMutable()),
            ("SPIREPVP_PRESET.rapid",
                ModelDb.Modifier<RaceClockFifteen>().ToMutable(),
                ModelDb.Modifier<DuelClockThree>().ToMutable()),
            ("SPIREPVP_PRESET.untimed",
                ModelDb.Modifier<RaceClockNone>().ToMutable(),
                ModelDb.Modifier<DuelClockNone>().ToMutable())
        };
}
