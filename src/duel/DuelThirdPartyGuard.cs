using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel;

/// <summary>
/// Contains a known third-party fault that can void a match, at the one place all its routes meet.
///
/// # Why this is not more of <see cref="Patches.DuelDisplayExceptionGuardPatch"/>
///
/// That guard names *vanilla* methods and catches whatever throws inside them, which is the right
/// shape and was still the wrong altitude — twice. MintySpire2's incoming-damage readout reaches
/// its faulty code by at least three different roads:
///
/// - `Creature.set_CurrentHp` → `NMultiplayerPlayerState.OnCreatureValueChanged` → `RefreshValues`
/// - `Creature.set_Block` → `NMultiplayerPlayerState.BlockChanged` → `AnimateInBlock`
/// - `Creature.InvokeDiedEvent` → `SummedIncomingDamageRender.CatchMonsterDeath` → straight into
///   `RefreshVisibilityAndText`, touching no `NHealthBar` method at all
///
/// Each one cost a playtest to find, and the third does not pass through the choke point the second
/// fix chose. **Every creature setter that raises a display event is another road**, so guarding
/// roads is unbounded; guarding the destination is one patch. The destination is two Minty methods.
///
/// # The fault itself, which cannot be fixed from our side
///
///     foreach (Creature hittableEnemy in creature.CombatState.HittableEnemies)
///         foreach (AbstractIntent intent in hittableEnemy.Monster.NextMove.Intents)
///
/// In a duel `HittableEnemies` is the opposing **player**, because `DuelAoeTargetingPatch` resolves
/// it that way and that resolution is the whole AoE fix. A player has no `Monster`, so this NREs —
/// and there is no version of this we could make *work*, because a player has no intents to sum
/// either. The readout is meaningless in a duel whatever we do. So containment is the ceiling, and
/// the only question was where.
///
/// # Why an exception to "patch targets are `nameof`, not strings"
///
/// That rule exists so a game update that moves a target is a build error naming the method rather
/// than a runtime `PATCH FAILED`. It cannot apply here: this target lives in an optional third-party
/// assembly we do not reference and cannot compile against, and which is absent for most players.
/// The rule's protection is replaced by its own logging — absence is normal and silent, a shape
/// change is reported by name — and the blast radius of being wrong is one disabled QoL readout.
///
/// Nothing here runs unless Minty is installed, and the guard only swallows during a PvP run.
/// </summary>
internal static class DuelThirdPartyGuard
{
    private const string MintyRenderer = "MintySpire2.MintySpire2Code.combat.SummedIncomingDamageRender";

    /// <summary>The Minty methods that dereference `.Monster` on a hittable enemy.</summary>
    private static readonly string[] MintyMethods = { "RefreshVisibilityAndText", "CalculateIncomingDamage" };

    private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

    /// <summary>
    /// Applied by <see cref="SpirePvpInit"/> after the attribute-driven classes.
    ///
    /// **Deliberately outside the class tally.** `SpirePvpInit` reports a `[HarmonyPatch]` class that
    /// binds nothing as an error by name, which is exactly right for our own patches and exactly
    /// wrong here: binding nothing is the *expected* outcome for the majority of players, who do not
    /// have Minty. Counting it would turn a normal install into a permanent false alarm, and this
    /// project has spent enough on counts that disagree with reality.
    /// </summary>
    private static bool _applied;

    /// <summary>Idempotent: every run asks, only the first one patches.</summary>
    public static void ApplyOnce()
    {
        if (_applied)
        {
            return;
        }

        _applied = true;

        try
        {
            Apply(new Harmony("SpirePvp.ThirdPartyGuard"));
        }
        catch (Exception e)
        {
            Log.Error($"[SpirePvp] third-party guard failed to apply: {e.Message}");
        }
    }

    private static void Apply(Harmony harmony)
    {
        Type? renderer = AccessTools.TypeByName(MintyRenderer);
        if (renderer == null)
        {
            // Not installed. Not a problem, and not worth a line every launch.
            return;
        }

        HarmonyMethod finalizer = new HarmonyMethod(
            typeof(DuelThirdPartyGuard).GetMethod(nameof(ContainException), BindingFlags.Static | BindingFlags.NonPublic));

        int guarded = 0;
        foreach (string name in MintyMethods)
        {
            MethodInfo? method = AccessTools.Method(renderer, name);
            if (method == null)
            {
                Log.Warn($"[SpirePvp] third-party guard: MintySpire2 is installed but {name} was not "
                         + "found — it has probably been renamed. If duels start desyncing around "
                         + "health-bar updates, this guard is why it stopped covering them.");
                continue;
            }

            try
            {
                harmony.Patch(method, finalizer: finalizer);
                guarded++;
            }
            catch (Exception e)
            {
                Log.Error($"[SpirePvp] third-party guard: failed to guard {name} — {e.Message}");
            }
        }

        Log.Warn($"[SpirePvp] third-party guard: MintySpire2 detected, {guarded} method(s) contained. "
                 + "Its incoming-damage readout does not work in a duel — it reads enemy intents and "
                 + "a duelist has none — so it is suppressed there rather than allowed to kill the "
                 + "action that triggered it.");
    }

    /// <summary>
    /// Returning null tells Harmony the exception is handled.
    ///
    /// **Not named `Finalizer`, and that is not style.** Harmony's class processor recognises
    /// `Prefix`/`Postfix`/`Finalizer`/`Transpiler` **by name**, with or without an attribute, so a
    /// method called `Finalizer` in a class carrying no `[HarmonyPatch]` is a patch with no target.
    /// `SpirePvpInit` duly reported `PATCH FAILED for DuelThirdPartyGuard: Patching exception in
    /// method null` and disabled duelling — the health gate working exactly as intended, on a class
    /// that patches manually and was never meant to be swept up by the attribute pass.
    /// </summary>
    private static Exception? ContainException(Exception? __exception, MethodBase __originalMethod)
    {
        if (__exception == null)
        {
            return null;
        }

        // Outside a PvP run we have not changed what this code sees, so its exceptions are its own.
        if (!DuelMatch.IsPvpRun(RunManager.Instance?.State))
        {
            return __exception;
        }

        string where = __originalMethod?.Name ?? "unknown";
        _counts.TryGetValue(where, out int seen);
        seen++;
        _counts[where] = seen;

        if (seen <= 3 || seen % 50 == 0)
        {
            Log.Error($"[SpirePvp] third-party guard: MintySpire2.{where} threw and was contained — "
                      + $"occurrence {seen} this run. {__exception.GetType().Name}: {__exception.Message}");
        }

        return null;
    }

    /// <summary>Cleared with the run, like every other static here.</summary>
    public static void Reset() => _counts.Clear();
}
