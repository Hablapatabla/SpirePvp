using HarmonyLib;
// Note the lowercase `sts2` — this namespace does not follow the MegaCrit.Sts2.* convention the
// rest of the assembly uses, and naming it the usual way is a compile error, not a missing type.
using MegaCrit.sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Keeps the top bar showing one boss, the way Acts 1 and 2 always have.
///
/// The arena is installed as the act's *second boss* (that is the whole trick behind the
/// back-to-back node — see DESIGN M6), and NTopBarBossIcon takes that literally:
///
///     if (secondBossEncounter != null &amp;&amp; !ShouldOnlyShowSecondBossIcon) { ...show it... }
///     else { hide it }
///
/// So every PvP run drew the duel node tucked in beside the act boss for the entire race. In a
/// double-boss act that pairing means "you will fight both of these"; here it means neither of
/// those things, since the arena is a rendezvous rather than a second boss fight, and the run
/// only reaches it after the boss is already dead.
///
/// The fix reuses vanilla's own hidden state rather than inventing one: the else branch above
/// already hides both nodes, so the postfix simply applies that outcome. Hiding after the fact
/// rather than prefixing the whole method leaves the *primary* boss icon — which is real, and
/// which the player does need — refreshed entirely by vanilla.
///
/// Note this is separate from DuelRoomIconPatch, which redirects the icon *paths* so the duel
/// node has art at all. That one is still needed: the arena is a genuine room in the run
/// history, and the second-boss slot is not the only thing that asks for its icon.
/// </summary>
[HarmonyPatch(typeof(NTopBarBossIcon), nameof(NTopBarBossIcon.RefreshBossIcon))]
public static class DuelTopBarBossIconPatch
{
    public static void Postfix(NTopBarBossIcon __instance)
    {
        if (!DuelMatch.IsPvpRun(RunManager.Instance?.State))
        {
            return;
        }

        __instance._secondBossIcon?.SetVisible(false);
        __instance._secondBossIconOutline?.SetVisible(false);
    }
}
