using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Settings;
using SpirePvp.Duel;

namespace SpirePvp.Modifiers;

/// <summary>
/// A PvP match is configured in the lobby, before the run exists (DESIGN §5b).
///
/// Two decisions, agreed between the players like time control and ruleset in chess:
/// which turn model, and how long the clock is. Both ride on `ModifierModel`, which is the
/// engine's own concept for "this run is played under special rules" — chosen by the host in
/// the custom-run modifier list, synced to clients by the vanilla `LobbyModifiersChangedMessage`,
/// installed by `RunState.CreateForNewRun` *before* players are seeded, and serialized with
/// the run so a reload is still a PvP run.
///
/// That ordering is the whole point: deciding at run creation removes the mid-run `race on`
/// command, the after-the-fact re-seed, and the un-mirrored Neow that came with them.
///
/// `ModifierModel` has no abstract members, so these are deliberately tiny. The icon path is
/// overridden onto a vanilla asset — this project ships no modifier art yet, and a missing
/// texture is not worth a crash.
/// </summary>
public abstract class DuelModifierBase : ModifierModel
{
    /// Reuses a vanilla modifier icon until this project has art of its own.
    ///
    /// **The `.png` is load-bearing.** ImageHelper.GetImagePath only prefixes `res://images/`,
    /// so without an extension this named a resource that cannot exist, ResourceLoader.Exists
    /// returned false, and ModifierModel fell back to MissingIconPath — `powers/missing_power`,
    /// which is the placeholder art that drew three "NOPE"s across the top bar for the whole of
    /// every run (one per active modifier). Vanilla builds its own path as
    /// `"packed/modifiers/" + entry.ToLowerInvariant() + ".png"`; this has to match.
    ///
    /// This property is the one-line-per-group seam for real art, the same shape as
    /// DuelBadgeIconPatch: override it on RaceClockModifier and DuelClockModifier to give the
    /// three lobby groups three distinct icons once they exist.
    protected override string IconPath => ImageHelper.GetImagePath("packed/modifiers/draft.png");
}

/// <summary>
/// Turn model: real-time blitz (DESIGN §3.1b model A).
///
/// Presence of *either* turn-model modifier is what marks a run as a PvP match — there is no
/// separate "enable duel" toggle, because a duel with no turn model chosen is not a thing.
/// </summary>
public sealed class DuelBlitz : DuelModifierBase
{
    protected override void AfterRunCreated(RunState runState) => DuelMatch.OnRunCreated(runState);

    protected override void AfterRunLoaded(RunState runState) => DuelMatch.OnRunCreated(runState);
}

/// <summary>Turn model: simultaneous turn-based (DESIGN §3.1b model B, built in M8).</summary>
public sealed class DuelTurnBased : DuelModifierBase
{
    protected override void AfterRunCreated(RunState runState) => DuelMatch.OnRunCreated(runState);

    protected override void AfterRunLoaded(RunState runState) => DuelMatch.OnRunCreated(runState);
}

/// <summary>
/// A time bank, in minutes. Zero means that phase is untimed and nobody can lose on time
/// there — which is also what you get by picking no clock modifier at all, so the mod stays
/// inert for anyone who has not opted in.
/// </summary>
public abstract class ClockModifierBase : DuelModifierBase
{
    public abstract double Minutes { get; }
}

/// <summary>
/// How long you have to reach the arena (DESIGN §9).
///
/// Its own mutually exclusive group, separate from the duel clock, because the two measure
/// different things: an act is a long haul against a hard deadline, one duel is a handful of
/// turns. A single shared number either rushed the race or made the duel interminable, which
/// is the decision this group and the next exist to undo.
/// </summary>
public abstract class RaceClockModifier : ClockModifierBase;

public sealed class RaceClockEight : RaceClockModifier
{
    public override double Minutes => 8;
}

public sealed class RaceClockTen : RaceClockModifier
{
    public override double Minutes => 10;
}

public sealed class RaceClockFifteen : RaceClockModifier
{
    public override double Minutes => 15;
}

public sealed class RaceClockTwelve : RaceClockModifier
{
    public override double Minutes => 12;
}

/// <summary>Untimed race: take as long as you like getting to the arena.</summary>
public sealed class RaceClockNone : RaceClockModifier
{
    public override double Minutes => 0;
}

/// <summary>
/// How long the duel itself gets (DESIGN §9).
///
/// A *fresh* bank granted when the duel begins, not the race's remainder — so arriving at the
/// arena early no longer buys you duel time, and the two phases are timed independently.
/// </summary>
public abstract class DuelClockModifier : ClockModifierBase;

/// <summary>Short enough to flag on purpose. Kept in the real list so it needs no dev build.</summary>
public sealed class DuelClockOne : DuelClockModifier
{
    public override double Minutes => 1;
}

public sealed class DuelClockTwo : DuelClockModifier
{
    public override double Minutes => 2;
}

public sealed class DuelClockThree : DuelClockModifier
{
    public override double Minutes => 3;
}

public sealed class DuelClockFive : DuelClockModifier
{
    public override double Minutes => 5;
}

/// <summary>Untimed duel: nobody can lose on time once the arena is reached.</summary>
public sealed class DuelClockNone : DuelClockModifier
{
    public override double Minutes => 0;
}

/// <summary>
/// How fast a resolving round plays out — agreed in the lobby, like the clocks and the turn model.
///
/// **Both players must experience the same pacing, and the clock is why.** Lucas, 2026-08-14: *"I
/// think it's important the 2 players are experiencing the same thing, otherwise the clocks would
/// get disjoint no?"* Yes, and the earlier attempt to let each player use their own Fast Mode was
/// wrong for a reason that is easy to miss: **the clocks are host-authoritative.**
/// `DuelClockService` decides both banks from the host's own model state, so a client whose
/// animations run slower is charged from the *host's* timeline — billed for seconds in which it is
/// still watching cards land and cannot act. The two clocks would be measuring two different
/// experiences, which is exactly the disjointness named above.
///
/// So the speed joins the other three lobby decisions rather than living in a settings screen. That
/// is the same argument that removed `duel clock &lt;minutes&gt;`: a mid-run change can only hand
/// someone terms they never agreed to.
/// </summary>
public abstract class DuelSpeedModifier : DuelModifierBase
{
    /// <summary>The Fast Mode level both clients read for the duration of the duel.</summary>
    public abstract FastModeType Level { get; }

    protected override void AfterRunCreated(RunState runState) => DuelMatch.OnRunCreated(runState);

    protected override void AfterRunLoaded(RunState runState) => DuelMatch.OnRunCreated(runState);
}

/// <summary>Vanilla's ordinary animation speed — the setting duels have always been pinned to.</summary>
public sealed class DuelSpeedNormal : DuelSpeedModifier
{
    public override FastModeType Level => FastModeType.Normal;
}

/// <summary>
/// Shorter animations. **Not `Instant`, and there is deliberately no `Instant` option:**
/// `Cmd.Wait` returns immediately there, so `DuelPace`'s beat would not shorten but vanish, taking
/// the readable gap between cards with it. Offering it in the lobby would be offering a way to
/// delete a mechanic both players rely on.
/// </summary>
public sealed class DuelSpeedFast : DuelSpeedModifier
{
    public override FastModeType Level => FastModeType.Fast;
}
