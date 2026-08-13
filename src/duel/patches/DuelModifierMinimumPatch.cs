using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Keeps exactly one chip ticked in each of the duel's three rows, where vanilla only keeps at
/// most one.
///
/// **Unticking the last option in a group left the row empty, and an empty turn-model row means the
/// run is not a duel at all.** Reported 2026-08-12 and left open since: without a
/// `DuelBlitz`/`DuelTurnBased` modifier, `DuelMatch.IsPvpRun` is false, `OnRunCreated` bails, and
/// two people start an ordinary co-op run having just configured a match. The failure is silent —
/// the lobby looks fine, the run starts fine, and the first sign is that none of the duel exists.
/// Until now the workaround was "check that one chip in each row is ticked before starting."
///
/// **Vanilla is not wrong, it is answering a different question.**
/// `NCustomRunModifiersList.UntickMutuallyExclusiveModifiersForTickbox` returns immediately unless
/// the tickbox was just ticked *on*:
///
/// <code>
/// if (!tickbox.IsTicked) { return; }
/// </code>
///
/// So the mechanism is "ticking this unticks its siblings" — at most one — which is all a vanilla
/// group needs, because vanilla's own exclusive modifiers are optional. Ours are decisions: there is
/// no such thing as a duel with no turn model. That minimum is ours to add, so it is added here
/// rather than by changing what vanilla means by a group.
///
/// **Only our groups.** Vanilla's set is left strictly alone — enforcing a minimum on it would make
/// its modifiers impossible to switch off, which is a real behaviour change to ordinary custom runs
/// and exactly the kind of collateral a mod should not have.
///
/// **Postfixing this method rather than `AfterModifiersChanged` is deliberate**: the caller runs
/// `EmitSignal(ModifiersChanged)` immediately afterwards, and that signal is what
/// `DuelLobbyPanel` broadcasts to the joined client. Correcting the state here means the peer is
/// told the corrected row, not a momentarily empty one — the same reasoning as every other place in
/// this mod where a message must not carry a state that is about to change.
///
/// Re-ticking is safe and cannot recurse: `NTickbox.IsTicked`'s setter only assigns the field and
/// flips the two images, and does **not** raise `Toggled`, so nothing re-enters this method.
/// </summary>
[HarmonyPatch(typeof(NCustomRunModifiersList),
    nameof(NCustomRunModifiersList.UntickMutuallyExclusiveModifiersForTickbox))]
public static class DuelModifierMinimumPatch
{
    public static void Postfix(NRunModifierTickbox tickbox, List<NRunModifierTickbox> ____modifierTickboxes)
    {
        // Only an untick can empty a row; a tick has just filled one.
        if (tickbox.IsTicked)
        {
            return;
        }

        HashSet<ModifierModel>? group = null;
        foreach (HashSet<ModifierModel> candidate in DuelModifierExclusivityPatch.DuelGroups())
        {
            foreach (ModifierModel member in candidate)
            {
                if (member.GetType() == tickbox.Modifier.GetType())
                {
                    group = candidate;
                    break;
                }
            }

            if (group != null)
            {
                break;
            }
        }

        if (group == null)
        {
            return;
        }

        // Compared by type, exactly as vanilla compares them: the tickboxes hold their own modifier
        // instances, so reference equality against the model database would never match.
        foreach (NRunModifierTickbox other in ____modifierTickboxes)
        {
            if (!other.IsTicked)
            {
                continue;
            }

            foreach (ModifierModel member in group)
            {
                if (member.GetType() == other.Modifier.GetType())
                {
                    return;
                }
            }
        }

        tickbox.IsTicked = true;
        Log.Info($"[SpirePvp] lobby: {tickbox.Modifier.GetType().Name} is the last chip in its row — "
                 + "kept ticked, a duel row is a choice and not an option");
    }
}
