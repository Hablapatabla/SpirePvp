using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace SpirePvp.Duel;

/// <summary>
/// Writes the host's live combat onto a freshly-built arena, so a returning duelist resumes the
/// duel rather than starting a new one.
///
/// # Why this has to exist at all
///
/// `RunManager.GetRejoinMessage` ships two things: the run, and `NetFullCombatState.FromRun` — a
/// complete snapshot of the combat in progress. Restoring the *run* is vanilla's own path and needs
/// nothing from us. Restoring the *combat* has no consumer anywhere in the engine, because vanilla
/// never rejoins: `NetFullCombatState` is built for `ChecksumTracker`, which only ever hashes it.
/// So the type is a perfectly good description of a combat that nothing knows how to read back.
///
/// **The evidence that this is the missing piece** came from the first successful rejoin: the run
/// came back correctly — top bar, HP, gold, relics, potions — and the arena was empty. No creatures,
/// no hand, no energy, no end turn. The client had rebuilt the run and then set about starting a
/// fresh match on top of it.
///
/// # Order matters, and it is HP before powers before piles
///
/// Powers are applied to creatures that must already exist, and several of them read HP when they
/// land. Piles are last because a card entering a pile can raise hooks that read both. This is the
/// same reasoning `DuelArena` follows against `EnterMapPointInternal` — do the steps in the engine's
/// own order, because each one assumes the last.
///
/// # The counters are not bookkeeping, they are the checksum
///
/// `nextChoiceIds` and `nextRewardIds` are the synchronizer counters, and this project has already
/// lost a match to exactly one of them: a drafted relic gathered a player choice, the host's
/// `PlayerChoiceSynchronizer` reserved id 0, the client's never moved, and the duel's first checksum
/// diverged on a single line reading `Choice IDs: 1` against empty. A rejoining client rebuilds
/// those counters from zero, so restoring them is not optional — it is the difference between
/// resuming and desyncing on the first action.
///
/// # What is deliberately not restored
///
/// **The action queue.** It is not in the snapshot and it should not be: the queue holds actions
/// mid-flight, and a rejoin lands at a turn boundary where the right queue is an empty one. The host
/// keeps arbitrating either way — clients display, the host decides — so anything genuinely pending
/// arrives over the wire as it always did.
/// </summary>
internal static class DuelRejoinCombatState
{
    public static void Apply(NetFullCombatState snapshot, CombatState state, RunManager runManager)
    {
        Log.Warn($"[SpirePvp] rejoin: applying snapshot — {snapshot.Creatures.Count} creature(s), "
                 + $"{snapshot.Players.Count} player state(s)");

        ApplyCreatures(snapshot, state);
        ApplyPlayers(snapshot, state);
        ApplyCounters(snapshot, runManager);

        Log.Warn("[SpirePvp] rejoin: snapshot applied");
    }

    /// <summary>
    /// HP, block and powers, matched by player id.
    ///
    /// **Matched by id rather than by position.** The two duelists are both on `CombatSide.Player`
    /// and the order creatures appear in is a function of how the room was built, which on a rejoin
    /// is not the order the host has. Every layout bug this project has had came from assuming those
    /// agree.
    /// </summary>
    private static void ApplyCreatures(NetFullCombatState snapshot, CombatState state)
    {
        foreach (NetFullCombatState.CreatureState wanted in snapshot.Creatures)
        {
            if (wanted.playerId == null)
            {
                // A duel has no monsters. If one ever appears here it is worth seeing in the log
                // rather than silently skipping.
                Log.Info($"[SpirePvp] rejoin: snapshot holds a non-player creature ({wanted.monsterId}) — skipped");
                continue;
            }

            Creature? creature = FindCreature(state, wanted.playerId.Value);
            if (creature == null)
            {
                Log.Error($"[SpirePvp] rejoin: no creature for player {wanted.playerId} — the arena "
                          + "was built without them, so the duel cannot be resumed faithfully");
                continue;
            }

            creature.MaxHp = wanted.maxHp;
            creature.CurrentHp = wanted.currentHp;
            creature.Block = wanted.block;

            creature._powers.Clear();
            foreach (NetFullCombatState.PowerState power in wanted.powers)
            {
                PowerModel model = ModelDb.GetById<PowerModel>(power.id).ToMutable() as PowerModel;
                model.Amount = power.amount;
                creature._powers.Add(model);
            }

            Log.Info($"[SpirePvp] rejoin: {creature.LogName} restored to {wanted.currentHp}/{wanted.maxHp} "
                     + $"hp, {wanted.block} block, {wanted.powers.Count} power(s)");
        }
    }

    /// <summary>
    /// Energy, stars, turn number, phase and the four card piles.
    ///
    /// **The piles are rebuilt from `SerializableCard` rather than re-pointed at the deck's models.**
    /// A card in combat is not the same object as the card in the deck — it carries its own
    /// affliction, cost override and keywords, all of which the snapshot serialises per card and all
    /// of which would be lost by matching on id alone.
    /// </summary>
    private static void ApplyPlayers(NetFullCombatState snapshot, CombatState state)
    {
        foreach (NetFullCombatState.PlayerState wanted in snapshot.Players)
        {
            Player? player = FindPlayer(state, wanted.playerId);
            if (player?.PlayerCombatState == null)
            {
                Log.Error($"[SpirePvp] rejoin: no combat state for player {wanted.playerId} — skipping their half of the snapshot");
                continue;
            }

            PlayerCombatState combat = player.PlayerCombatState;
            combat.Energy = wanted.energy;
            combat.Stars = wanted.stars;
            combat.TurnNumber = wanted.turnNumber;
            combat.Phase = wanted.phase;

            foreach (NetFullCombatState.CombatPileState pile in wanted.piles)
            {
                CardPile target = pile.pileType.GetPile(player);
                target._cards.Clear();

                foreach (NetFullCombatState.CardState card in pile.cards)
                {
                    CardModel model = CardModel.FromSerializable(card.card);
                    if (card.energyCost.HasValue)
                    {
                        model.EnergyCost.SetCustomBaseCost(card.energyCost.Value);
                    }

                    target._cards.Add(model);
                }
            }

            Log.Info($"[SpirePvp] rejoin: player {wanted.playerId} restored — turn {wanted.turnNumber}, "
                     + $"phase {wanted.phase}, {wanted.energy} energy, "
                     + $"hand {PileType.Hand.GetPile(player).Cards.Count}, "
                     + $"draw {PileType.Draw.GetPile(player).Cards.Count}, "
                     + $"discard {PileType.Discard.GetPile(player).Cards.Count}");
        }
    }

    /// <summary>
    /// The synchronizer counters and the run RNG — see the class note on why these decide checksums.
    /// </summary>
    private static void ApplyCounters(NetFullCombatState snapshot, RunManager runManager)
    {
        runManager.PlayerChoiceSynchronizer.FastForwardChoiceIds(snapshot.nextChoiceIds);
        Log.Info($"[SpirePvp] rejoin: choice ids fast-forwarded to [{string.Join(", ", snapshot.nextChoiceIds)}]");
    }

    private static Creature? FindCreature(CombatState state, ulong playerId)
    {
        foreach (Creature creature in state.PlayerCreatures)
        {
            if (creature.Player?.NetId == playerId)
            {
                return creature;
            }
        }

        return null;
    }

    private static Player? FindPlayer(CombatState state, ulong playerId)
    {
        foreach (Player player in state.Players)
        {
            if (player.NetId == playerId)
            {
                return player;
            }
        }

        return null;
    }
}
