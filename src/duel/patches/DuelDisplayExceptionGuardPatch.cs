using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Stops a health-bar repaint from being able to desync a match.
///
/// # The desync this exists for, and it was not our logic
///
/// Measured 2026-08-20, first duel run with Workshop mods enabled. The two state dumps differed in
/// exactly two lines: the host had `BRIGHTEST_FLAME` in `Pile Play` with max HP 70, the client had
/// it in `Pile Discard` with max HP 68. The cause was above both:
///
///     [ERROR] GameAction PlayCardAction card: CARD.BRIGHTEST_FLAME completed with exception:
///             System.NullReferenceException
///        at MintySpire2...SummedIncomingDamageRender.CalculateIncomingDamage(Creature)
///        at MegaCrit...NMultiplayerPlayerState.RefreshValues()
///        at MegaCrit...NMultiplayerPlayerState.OnCreatureValueChanged(Int32, Int32)
///        at MegaCrit...Creature.set_CurrentHp(Int32 value)
///        at MegaCrit...CreatureCmd.LoseMaxHp(...)
///        at MegaCrit...Models.Cards.BrightestFlame.OnPlay(...)
///
/// Brightest Flame's `OnPlay` gains energy, draws, **then** loses max HP. The throw landed inside
/// that third step, killed the action mid-way, and did so **four times on the host and three times
/// on the client** — so the two simulations stopped at different points inside the same card. That
/// is the whole divergence.
///
/// # Whose bug it is, which matters for where the fix goes
///
/// Minty's code is reasonable for vanilla:
///
///     foreach (Creature hittableEnemy in creature.CombatState.HittableEnemies)
///         foreach (AbstractIntent intent in hittableEnemy.Monster.NextMove.Intents)
///
/// Every hittable enemy is a monster — in vanilla. **In a duel it is the opposing player**, because
/// `DuelAoeTargetingPatch` resolves that getter to exactly that, which is the point of the AoE fix
/// and is already noted against Fur Coat in `docs/BALANCE.md`. So a player creature arrives where a
/// monster is assumed, `.Monster` is null, and a display helper throws. We changed the meaning of a
/// getter the whole game reads; expecting every other mod to have anticipated that is not a plan.
///
/// # Why the guard is here rather than on Minty's method
///
/// **These are vanilla methods, so the targets stay `nameof`.** A Harmony finalizer wraps the whole
/// patched method — original *and* every other mod's prefixes and postfixes — so an exception
/// thrown by a third-party patch on anything these call is caught here without this mod ever naming
/// it. That matters more than tidiness: patching Minty by string would protect against Minty and
/// nothing else, would break on their next rename, and would do nothing for the next display mod
/// that makes the same fair assumption. This protects against the *class* of fault.
///
/// # What it does and does not swallow
///
/// It suppresses exceptions escaping a **repaint**, and only during a PvP run. Nothing downstream of
/// these methods can change the simulation — they read state and set labels — so a failed repaint
/// costs one stale number on screen for one frame, and the next refresh fixes it. Weighed against a
/// voided match, that is not a close call.
///
/// It is deliberately *not* a general exception filter: the guard names two display methods rather
/// than wrapping anything broad, so a throw anywhere that can actually move the sim still fails
/// loudly, the way this project needs.
///
/// # It counts, because "it only logs an error" is a claim worth measuring
///
/// The arena's missing room icon was recorded as one line per run and was 19 per client per session.
/// A swallowed exception is exactly the kind of thing that becomes invisible and permanent, so the
/// first few are logged whole and the rest are counted, with the running total on every line that
/// does print. A guard that fires thousands of times is a bug report, not a fix.
/// </summary>
[HarmonyPatch]
public static class DuelDisplayExceptionGuardPatch
{
    private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

    // **The health bar is the choke point, not its callers.** The first version of this guarded
    // `NMultiplayerPlayerState.RefreshValues` and missed the next divergence entirely, because that
    // one escaped through `Creature.set_Block` → `NMultiplayerPlayerState.BlockChanged` instead —
    // same third-party postfix, different route in. Guarding routes is a losing game: every creature
    // setter that raises a display event is another one. `NHealthBar` is where the offending patches
    // actually attach (`[HarmonyPatch(typeof(NHealthBar))]`, postfixing `RefreshValues` and
    // `SetCreature`), so a finalizer there catches them however they were reached.
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(NHealthBar), nameof(NHealthBar.RefreshValues))]
    public static Exception? GuardHealthBarRefresh(Exception? __exception) =>
        Swallow(__exception, "NHealthBar.RefreshValues");

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(NHealthBar), nameof(NHealthBar.SetCreature))]
    public static Exception? GuardHealthBarSetCreature(Exception? __exception) =>
        Swallow(__exception, "NHealthBar.SetCreature");

    // Kept as a backstop for anything that attaches above the bar rather than to it. Cheap, and the
    // counter tells us if one of them ever actually fires.
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState.RefreshValues))]
    public static Exception? GuardMultiplayerPlayerState(Exception? __exception) =>
        Swallow(__exception, "NMultiplayerPlayerState.RefreshValues");

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState.BlockChanged))]
    public static Exception? GuardBlockChanged(Exception? __exception) =>
        Swallow(__exception, "NMultiplayerPlayerState.BlockChanged");

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(NCreatureStateDisplay), nameof(NCreatureStateDisplay.RefreshValues))]
    public static Exception? GuardCreatureStateDisplay(Exception? __exception) =>
        Swallow(__exception, "NCreatureStateDisplay.RefreshValues");

    /// <summary>
    /// Returning null tells Harmony the exception is handled; returning it rethrows.
    /// </summary>
    private static Exception? Swallow(Exception? exception, string where)
    {
        if (exception == null)
        {
            return null;
        }

        // Outside a PvP run this mod has changed nothing about what the display sees, so a throw
        // here is vanilla's own or another mod's and is not ours to hide.
        if (!DuelMatch.IsPvpRun(RunManager.Instance?.State))
        {
            return exception;
        }

        _counts.TryGetValue(where, out int seen);
        seen++;
        _counts[where] = seen;

        if (seen <= 3 || seen % 25 == 0)
        {
            Log.Error($"[SpirePvp] display guard: {where} threw and was contained — occurrence "
                      + $"{seen} this session. A repaint cannot be allowed to kill the action that "
                      + $"triggered it, because the two clients would stop at different points. "
                      + $"{exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
        }

        return null;
    }

    /// <summary>Cleared with the run, like every other static in this mod.</summary>
    public static void Reset() => _counts.Clear();
}
