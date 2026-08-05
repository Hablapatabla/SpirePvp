using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Runs;
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
    protected override string IconPath => ImageHelper.GetImagePath("packed/modifiers/draft");
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
/// Clock length. Separate mutually exclusive group from the turn model, so the two decisions
/// stay independent — any turn model can be played at any time control.
/// </summary>
public abstract class DuelClockModifier : DuelModifierBase
{
    public abstract double Minutes { get; }
}

public sealed class DuelClockThree : DuelClockModifier
{
    public override double Minutes => 3;
}

public sealed class DuelClockFive : DuelClockModifier
{
    public override double Minutes => 5;
}

public sealed class DuelClockTen : DuelClockModifier
{
    public override double Minutes => 10;
}

/// <summary>No clock: nobody can lose on time. Matches the mod's default of 0 (§9).</summary>
public sealed class DuelClockNone : DuelClockModifier
{
    public override double Minutes => 0;
}
