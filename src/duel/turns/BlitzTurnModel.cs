using MegaCrit.Sts2.Core.GameActions;

namespace SpirePvp.Duel.Turns;

/// <summary>
/// Real-time blitz: nothing is held back, and host-arrival order decides everything.
///
/// **This is the mode M1–M7 built and playtested, moved behind the interface without a behaviour
/// change.** That is the whole of its job. `ShouldDefer` answering `false` unconditionally is not a
/// stub — it is the accurate statement of what blitz is, and it means the seam can be live and
/// exercised in every existing match before the second model exists. A seam that only runs once
/// its alternative is written is a seam nobody has tested.
///
/// The first-strike mechanic falls out of this and needs no code: two players racing plays into a
/// shared queue resolve in the order the host receives them (DESIGN §3.1). Note the latency
/// asymmetry that comes with it — the host's own requests do not cross the network, so it has an
/// inherent ~½ RTT edge. Accepted for friendly play, and it is one of the real arguments for the
/// lock-in model, which removes it entirely.
/// </summary>
public sealed class BlitzTurnModel : IDuelTurnModel
{
    public string Name => "blitz";

    public bool ShouldDefer(GameAction action) => false;
}
