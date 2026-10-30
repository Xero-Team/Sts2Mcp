using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace STS2_MCP;

public static partial class McpMod
{
    // Monster intent state-machine export.
    //
    // The game keeps the graph topology on MonsterMoveStateMachine, while its
    // current cursor and initial node are private fields. Random branches also
    // carry the repeat/cooldown rules that consult StateLog. Export all of that
    // state so a consumer can advance the machine for an unbounded number of
    // turns with the shared MonsterAi RNG rather than receiving a short,
    // pre-rolled future_moves queue.
    //
    // Conditional branches contain Func<bool> closures into live game objects.
    // Known game predicates are normalized into a small expression language
    // that Rust can re-evaluate against hypothetical combat state. Unknown
    // modded predicates retain their current result as an explicit snapshot.

    private static readonly FieldInfo? s_msmCurrentStateField =
        typeof(MonsterMoveStateMachine).GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? s_msmInitialStateField =
        typeof(MonsterMoveStateMachine).GetField("_initialState", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? s_msmPerformedFirstMoveField =
        typeof(MonsterMoveStateMachine).GetField("_performedFirstMove", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly PropertyInfo? s_conditionalStatesProperty =
        typeof(ConditionalBranchState).GetProperty("States", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Builds the complete intent machine for one enemy.
    /// </summary>
    private static Dictionary<string, object?>? BuildIntentMachine(Creature creature)
        => TryRead("intent.machine", () => BuildIntentMachineCore(creature));

    private static Dictionary<string, object?>? BuildIntentMachineCore(Creature creature)
    {
        var monster = creature.Monster;
        var machine = monster?.MoveStateMachine;
        var current = s_msmCurrentStateField?.GetValue(machine) as MonsterState;
        var initial = s_msmInitialStateField?.GetValue(machine) as MonsterState;
        if (monster == null || machine == null || current == null || initial == null)
            return null;
        if (s_msmPerformedFirstMoveField?.GetValue(machine) is not bool performedFirstMove)
            return null;

        var states = BuildMachineStates(monster, creature, machine, initial, current);
        if (states.States.Count == 0)
            return null;

        var rng = monster.RunRng.MonsterAi;
        var rngCoords = RngCoords(rng);
        if (rngCoords == null)
            return null;
        var stateLog = new List<string>(machine.StateLog.Count);
        foreach (var logged in machine.StateLog)
            stateLog.Add(logged.Id);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["initial_state"] = initial.Id,
            ["current_state"] = current.Id,
            ["can_transition_away"] = current.CanTransitionAway,
            ["performed_first_move"] = performedFirstMove,
            ["state_log"] = stateLog,
            ["rng_counter"] = rngCoords["counter"],
            ["conditional_values_are_snapshots"] = states.ConditionalSnapshots,
            // Set when a weight lambda was seen to read live state, rather
            // than for a named monster: any shipped or modded machine whose
            // weights move is detected the same way.
            ["random_weights_are_snapshots"] = states.WeightSnapshots,
            ["states"] = states.States,
        };
        ApplyDynamicMachineExtras(result, monster, rngCoords);
        return result;
    }

    /// <summary>The serialized machine nodes, plus whether either kind of value was a snapshot.</summary>
    private sealed record MachineStates(
        List<Dictionary<string, object?>> States,
        bool ConditionalSnapshots,
        bool WeightSnapshots);

    private static MachineStates BuildMachineStates(
        MegaCrit.Sts2.Core.Models.MonsterModel monster,
        Creature creature,
        MonsterMoveStateMachine machine,
        MonsterState initial,
        MonsterState current)
    {
        var states = new List<Dictionary<string, object?>>();
        bool hasConditionalSnapshots = false;
        bool hasWeightSnapshots = false;
        // `States` is built from the list the monster passed to the machine's
        // constructor, which does NOT necessarily contain the machine's own
        // cursor or its initial node. Two shipped shapes escape it:
        //
        //   - `PhantasmalGardener` and `Myte` pass their `INIT_MOVE`
        //     conditional as the initial state without ever adding it to the
        //     list, so the graph's entry point is missing.
        //   - `CreatureCmd.Stun` builds a synthetic `MoveState("STUNNED")`
        //     and installs it with `SetMoveImmediate`, so a stunned monster's
        //     cursor names a state the list never had.
        //
        // Either one leaves the consumer unable to resolve an id it was
        // handed, which costs it the ENTIRE machine and drops that enemy to
        // the intent-DB fallback. Both nodes are perfectly serializable, so
        // they are appended rather than left dangling.
        foreach (var state in machine.States.Values.Concat(new[] { initial, current }).Distinct())
        {
            Dictionary<string, object?>? serialized = state switch
            {
                MoveState move => BuildMoveMachineState(monster, creature, move),
                RandomBranchState random => BuildRandomMachineState(random, ref hasWeightSnapshots),
                ConditionalBranchState conditional =>
                    BuildConditionalMachineState(conditional, ref hasConditionalSnapshots),
                _ => null,
            };
            if (serialized == null)
                continue;
            // A monster that DID list its cursor must not export it twice.
            if (states.Any(existing => Equals(existing["id"], serialized["id"])))
                continue;
            states.Add(serialized);
        }

        return new MachineStates(states, hasConditionalSnapshots, hasWeightSnapshots);
    }

    /// <summary>
    /// Adds the fields only some monsters have: a named dynamic-weight counter, an
    /// off-balance flag, and the raw RNG stream.
    /// </summary>
    private static void ApplyDynamicMachineExtras(
        Dictionary<string, object?> result,
        MegaCrit.Sts2.Core.Models.MonsterModel monster,
        Dictionary<string, object?> rngCoords)
    {
        if (string.Equals(monster.GetType().Name, "TwoTailedRat", StringComparison.Ordinal))
        {
            result["dynamic_weight_state"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["turns_until_summonable"] = ReadMonsterInt(monster, "TurnsUntilSummonable"),
                ["call_for_backup_count"] = ReadMonsterInt(monster, "CallForBackupCount"),
            };
        }
        if (monster is MegaCrit.Sts2.Core.Models.Monsters.BowlbugRock bowlbug)
            result["bowlbug_off_balance"] = bowlbug.IsOffBalance;
        if (rngCoords.TryGetValue("seed", out var seed))
            result["rng_seed"] = seed;
        if (rngCoords.TryGetValue("state", out var rngState))
            result["rng_state"] = rngState;
    }

    private static Dictionary<string, object?> BuildMoveMachineState(
        MegaCrit.Sts2.Core.Models.MonsterModel monster,
        Creature creature,
        MoveState move)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = move.Id,
            ["kind"] = "move",
            ["follow_up"] = move.FollowUpState?.Id ?? move.FollowUpStateId,
            ["must_perform_once_before_transitioning"] = move.MustPerformOnceBeforeTransitioning,
            ["intents"] = BuildIntentList(move.Intents, creature),
        };
        var effects = GetMoveEffects(monster, move);
        if (effects != null)
            state["effects"] = effects;
        return state;
    }

    private static Dictionary<string, object?> BuildRandomMachineState(RandomBranchState random, ref bool hasSnapshots)
    {
        var branches = new List<Dictionary<string, object?>>(random.States.Count);
        foreach (var branch in random.States)
        {
            // A weight that cannot be evaluated is exported as zero, which reads as
            // "unknown" alongside the snapshot flag below.
            var weight = TryReadQuiet<float?>(() => branch.GetWeight()) ?? 0f;
            string? dynamicRule = DynamicWeightRule(branch.weightLambda);
            // A supported live rule is re-evaluated by the consumer. Any other
            // dynamic lambda remains an explicit snapshot.
            hasSnapshots |= IsDynamicWeight(branch.weightLambda) && dynamicRule == null;
            branches.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["state"] = branch.stateId,
                ["weight"] = weight,
                ["repeat"] = branch.repeatType.ToString(),
                ["max_repeats"] = branch.maxTimes,
                ["cooldown"] = branch.cooldown,
                ["weight_rule"] = dynamicRule == null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = dynamicRule,
                    ["summon_branch"] = string.Equals(branch.stateId, "CALL_FOR_BACKUP_MOVE", StringComparison.Ordinal),
                },
            });
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = random.Id,
            ["kind"] = "random",
            ["branches"] = branches,
        };
    }

    private static Dictionary<string, object?> BuildConditionalMachineState(
        ConditionalBranchState conditional,
        ref bool hasSnapshots)
    {
        var branches = new List<Dictionary<string, object?>>();
        if (s_conditionalStatesProperty?.GetValue(conditional) is IEnumerable values)
        {
            foreach (var value in values)
            {
                var valueType = value.GetType();
                var id = valueType.GetField("id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) as string;
                var evaluate = valueType.GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Instance);
                if (id == null || evaluate == null)
                    continue;
                // A condition that cannot be evaluated is reported as disabled, not guessed.
                var enabled = TryReadQuiet<bool?>(() =>
                    Convert.ToSingle(evaluate.Invoke(value, null), CultureInfo.InvariantCulture) > 0f) ?? false;
                var lambda = valueType
                    .GetField("_conditionalLambda", BindingFlags.NonPublic | BindingFlags.Instance)?
                    .GetValue(value) as Delegate;
                var condition = BuildCondition(lambda, branches.Count, enabled, out bool isSnapshot);
                hasSnapshots |= isSnapshot;
                branches.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["state"] = id,
                    ["enabled"] = enabled,
                    ["condition"] = condition,
                });
            }
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = conditional.Id,
            ["kind"] = "conditional",
            ["branches"] = branches,
        };
    }
}
