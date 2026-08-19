using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace SpirePvp.Duel.Patches;

/// <summary>
/// Builds the potion round's row inside the relic screen, so the two rounds are the same screen.
///
/// **Asked for exactly that way**: *"can we make potion draft look exactly like relic? ... exactly
/// how relic draft is now except with potions instead of relics."* Taken literally — this does not
/// imitate `NChooseARelicSelection`, it *is* `NChooseARelicSelection`. Same scene, same banner, same
/// backdrop, same skip button, same row geometry, same tweens, same focus wiring. The only thing
/// that differs is what sits in the row, which is the only thing that should.
///
/// # Why a prefix that replaces `_Ready` rather than a shim relic
///
/// The screen is relic-typed all the way down: `_relics` is `IReadOnlyList&lt;RelicModel&gt;`,
/// the row is built from `NRelicBasicHolder.Create(RelicModel)`, and the holder hands an
/// `NRelic` its `Model`. The other route to "potions in this screen" is a `RelicModel` subclass
/// wearing a potion's icon and tips — and that means a fake relic in the model layer, which can
/// leak into a relic pool, a save, or a grab bag. **A display lie in a model is a worse bug than
/// the one it fixes**, and it is the same instinct that stopped the draft giving pool cards a real
/// pile just to make a preview render.
///
/// So the potion round hands `ShowScreen` an empty relic list and this prefix does the layout
/// instead. Vanilla's `_Ready` never runs on that round, which is why the empty list is safe rather
/// than merely unused.
///
/// # The layout is vanilla's, copied deliberately
///
/// The geometry below — the 200px column pitch, the half-width left shift, the expo position tween
/// and the cubic modulate-from-black — is `NChooseARelicSelection._Ready`'s, line for line, because
/// matching it *is* the requirement. If that method's layout ever changes, this is the twin that
/// has to change with it. Same relationship `DuelArena` has with `EnterMapPointInternal`, and the
/// same warning: six omissions were found there one at a time.
/// </summary>
[HarmonyPatch(typeof(NChooseARelicSelection), nameof(NChooseARelicSelection._Ready))]
public static class DuelDraftPotionScreenPatch
{
    /// <summary>Vanilla's column pitch between items in the row.</summary>
    private const float ColumnPitch = 200f;

    public static bool Prefix(NChooseARelicSelection __instance)
    {
        IReadOnlyList<PotionModel> potions = DuelDraft.RemainingPotions;
        if (potions.Count == 0)
        {
            return true;
        }

        try
        {
            BuildPotionRow(__instance, potions);
            Log.Info($"[SpirePvp] draft: potion row built with {potions.Count} potion(s)");
            DumpPlacementLater(__instance);
            return false;
        }
        catch (Exception e)
        {
            // Falling through to vanilla would build a row from an empty relic list and index into
            // it, so the honest failure is no screen rather than a crash inside one.
            Log.Error($"[SpirePvp] draft: could not build the potion row: {e}");
            return false;
        }
    }

    /// <summary>
    /// Dumps where the row actually ended up, a beat after the tweens land.
    ///
    /// **Because "it says it was built and it is not on screen" is not answerable from a
    /// screenshot.** Reported 2026-08-19 as an empty page on a round whose log says the row was
    /// built with three potions — the same shape as the Rematch button, which reported itself
    /// added, placed and shown while being invisible. Two placement bugs in this project were
    /// "corrected" from screenshots and one of those corrections was wrong; what settled them both
    /// was logging the numbers and reading them.
    ///
    /// Deferred three quarters of a second because `Enable` and the position tween are still
    /// running before that, and the interesting values are the ones they land on.
    /// </summary>
    private static void DumpPlacementLater(NChooseARelicSelection screen)
    {
        SceneTreeTimer? settled = screen.GetTree()?.CreateTimer(0.75);
        if (settled == null)
        {
            return;
        }

        settled.Timeout += () =>
        {
            try
            {
                if (!screen.IsValid() || screen._relicRow == null)
                {
                    Log.Warn("[SpirePvp] draft: potion row gone before it could be measured");
                    return;
                }

                Control row = screen._relicRow;
                Log.Warn($"[SpirePvp] draft: potion row — visible={row.Visible} "
                         + $"global={row.GlobalPosition} size={row.Size} scale={row.Scale} "
                         + $"modulate={row.Modulate} children={row.GetChildCount()}");

                foreach (Node child in row.GetChildren())
                {
                    if (child is not Control c)
                    {
                        continue;
                    }

                    string potion = c is NPotionHolder h && h.Potion != null
                        ? h.Potion.Model?.Id.Entry ?? "(no model)"
                        : "(not a potion holder)";

                    Log.Warn($"[SpirePvp] draft:   {c.Name} [{potion}] visible={c.Visible} "
                             + $"global={c.GlobalPosition} size={c.Size} scale={c.Scale} "
                             + $"modulate={c.Modulate}");
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[SpirePvp] draft: could not measure the potion row: {e.Message}");
            }
        };
    }

    private static void BuildPotionRow(NChooseARelicSelection screen, IReadOnlyList<PotionModel> potions)
    {
        screen._banner = screen.GetNode<NCommonBanner>("Banner");
        screen._banner.label.SetTextAutoSize(
            new LocString("gameplay_ui", "CHOOSE_RELIC_HEADER").GetRawText());
        screen._banner.AnimateIn();

        screen._relicRow = screen.GetNode<Control>("RelicRow");

        Vector2 shift = Vector2.Left * (potions.Count - 1) * ColumnPitch * 0.5f;
        for (int i = 0; i < potions.Count; i++)
        {
            PotionModel potion = potions[i];

            // `isUsable: false` — this is a row to pick from, not a belt. A usable holder wires up
            // the click-to-drink path and the targeting arrow, which is emphatically not what a
            // click here should mean.
            NPotionHolder holder = NPotionHolder.Create(isUsable: false);
            NPotion? node = NPotion.Create(potion);
            if (node == null)
            {
                continue;
            }

            // **Into the tree first, then filled — and `AddChild`, not `AddChildSafely`.**
            // `NPotionHolder.AddPotion` writes `_emptyIcon.Modulate`, and `_emptyIcon` is assigned
            // in the holder's `_Ready`, which only runs once the node is in the tree. Filling first
            // is a `NullReferenceException` inside `AddPotion`, measured 2026-08-19.
            //
            // `AddChildSafely` is not enough either: it *defers* to the end of the frame unless the
            // parent `IsNodeReady()`, and this runs from inside the screen's own `_Ready`, when the
            // row is not. Plain `AddChild` on a parent already in the tree runs the child's `_Ready`
            // synchronously, which is exactly the guarantee needed here.
            //
            // Vanilla's relic row can use the deferring version because `NRelicBasicHolder.Create`
            // takes its model up front and needs nothing from `_Ready`. A potion holder is filled
            // in a second step, so it does.
            screen._relicRow.AddChild(holder);
            holder.AddPotion(node);
            holder.Scale = Vector2.One * 2f;

            PotionModel captured = potion;
            holder.Connect(NClickableControl.SignalName.Released,
                           Callable.From<NButton>(_ => DuelDraft.SubmitPotionPick(captured)));

            // **Assigned, not added to — and this is the whole of why the row was invisible.**
            // Vanilla writes `holder.Position + shift + …` because `NRelicBasicHolder` is created at
            // the origin, so adding *is* assigning there. `NPotionHolder`'s scene is not: measured
            // 2026-08-19 it carries an offset of roughly (-990, -570), so adding the slot offset to
            // it put the whole row off the top-left corner of the screen. The telemetry said so
            // outright — `visible=True size=(60,60) scale=(2,2)` at `global=(-354,-35)` while the
            // row sat at `(936,535)` — which is the difference between "it did not render" and "it
            // rendered somewhere you cannot see", and only one of those is a positioning bug.
            //
            // Zeroing first also gives the entrance tween the same origin vanilla's has, so the
            // slide reads identically.
            holder.Position = Vector2.Zero;

            Tween tween = screen.CreateTween().SetParallel();
            tween.TweenProperty(holder, "position",
                                shift + Vector2.Right * ColumnPitch * i, 0.5)
                 .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
            tween.TweenProperty(holder, "modulate", Colors.White, 1.0)
                 .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic)
                 .From(Colors.Black);
        }

        screen._skipButton = screen.GetNode<NChoiceSelectionSkipButton>("SkipButton");
        screen._skipButton.AnimateIn();

        // **The skip button is not wired to anything**, unlike vanilla's. A draft pick is not
        // optional — the round is drafted to exhaustion and skipping would desync the alternation —
        // and `DuelDraftScreenPatch` already refuses the equivalent on the relic round. Left on
        // screen rather than hidden so the row keeps the layout it is meant to match.
        List<NPotionHolder> holders = screen._relicRow.GetChildren().OfType<NPotionHolder>().ToList();
        if (holders.Count == 0)
        {
            return;
        }

        screen._skipButton.FocusNeighborTop = holders[holders.Count / 2].GetPath();
        screen._skipButton.FocusNeighborBottom = screen._skipButton.GetPath();
        screen._skipButton.FocusNeighborLeft = screen._skipButton.GetPath();
        screen._skipButton.FocusNeighborRight = screen._skipButton.GetPath();

        int count = screen._relicRow.GetChildCount();
        for (int j = 0; j < count; j++)
        {
            Control child = screen._relicRow.GetChild<Control>(j);
            child.FocusNeighborBottom = child.GetPath();
            child.FocusNeighborTop = child.GetPath();
            child.FocusNeighborLeft = (j > 0
                ? screen._relicRow.GetChild(j - 1)
                : screen._relicRow.GetChild(count - 1)).GetPath();
            child.FocusNeighborRight = (j < count - 1
                ? screen._relicRow.GetChild(j + 1)
                : screen._relicRow.GetChild(0)).GetPath();
        }
    }
}
