using System.Globalization;
using System.Text;
using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Testing;

namespace Radix.Core.Electrical;

/// <summary>
/// One tick at which a binding says what a pin should read, with the level the solved network gave.
/// Recording these lets a report say how much of the run was actually compared, instead of only
/// whether the whole thing passed.
/// </summary>
/// <param name="PinId">The pin the claim is about.</param>
/// <param name="At">The simulated tick.</param>
/// <param name="Level">The level the bound node held there.</param>
public readonly record struct ElectricalLevelClaim(string PinId, long At, int Level);

/// <summary>The script a circuit's bindings produce, and every claim it makes.</summary>
public sealed class ElectricalDrivePlan
{
    public ElectricalDrivePlan(
        SimulationScript script,
        IReadOnlyList<ElectricalLevelClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(claims);
        Script = script;
        Claims = claims;
    }

    public SimulationScript Script { get; }

    /// <summary>Every claim, one per tick per claiming binding.</summary>
    public IReadOnlyList<ElectricalLevelClaim> Claims { get; }

    /// <summary>How many distinct pins are claimed.</summary>
    public int ClaimedPins => Claims.Select(claim => claim.PinId).Distinct(StringComparer.Ordinal).Count();
}

/// <summary>
/// A tolerance for one transition of an output pin, keyed by the levels it leaves and reaches.
/// </summary>
/// <param name="PinId">The output pin this entry is for, or null when it applies to any pin.</param>
/// <param name="FromLevel">The output level before the transition.</param>
/// <param name="ToLevel">The output level after it.</param>
/// <param name="Ticks">How far the measured delay may differ from the declared one.</param>
public readonly record struct ElectricalDelayTolerance(
    string? PinId,
    int FromLevel,
    int ToLevel,
    int Ticks);

/// <summary>One output change, paired with the input change that preceded it.</summary>
/// <param name="InputLevel">The level the driving pin had taken.</param>
/// <param name="OutputLevel">The level the output pin then reached.</param>
/// <param name="InputAt">The tick the input changed at.</param>
/// <param name="OutputAt">The tick the output changed at.</param>
/// <param name="Ticks">The delay in simulator ticks.</param>
/// <param name="Seconds">The same delay in solved seconds.</param>
public readonly record struct ElectricalPropagationStep(
    int InputLevel,
    int PreviousOutputLevel,
    int OutputLevel,
    long InputAt,
    long OutputAt,
    long Ticks,
    double Seconds);

/// <summary>
/// How long the network takes to answer: every output change with the input change that caused it,
/// beside the transport delay the definition declares for that pin.
/// </summary>
public sealed class ElectricalPropagationReport
{
    public ElectricalPropagationReport(
        string inputPinId,
        string outputPinId,
        int? declaredTicks,
        int toleranceTicks,
        IReadOnlyList<ElectricalDelayTolerance> overrides,
        IReadOnlySet<string> outputPins,
        IReadOnlyList<ElectricalPropagationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(outputPins);
        ArgumentNullException.ThrowIfNull(steps);
        InputPinId = inputPinId;
        OutputPinId = outputPinId;
        DeclaredTicks = declaredTicks;
        ToleranceTicks = toleranceTicks;
        Overrides = overrides;
        Steps = steps;

        // A pin with no declared delay cannot be judged: silence is not agreement, and it is not a
        // failure either, so nothing is listed.
        Mismatches = declaredTicks is int declared
            ? [.. steps.Where(step => Math.Abs(step.Ticks - declared) > ToleranceFor(step))]
            : [];

        // An entry that no transition consulted is inert: the file stated something the check never
        // used. That is worth saying loudly, because a tolerance that silently does nothing reads as
        // protection the circuit does not actually have.
        var used = new HashSet<int>();
        foreach (ElectricalPropagationStep step in steps)
        {
            int index = FindOverrideIndex(step);
            if (index >= 0)
            {
                used.Add(index);
            }
        }

        UnusedTolerances = [.. overrides.Where((_, index) => !used.Contains(index))];

        // A pin the definition does not declare is a different mistake from a transition this run
        // never produced: the first can never apply, the second may apply to a longer run.
        UnknownPinTolerances = [.. overrides.Where(tolerance =>
            tolerance.PinId is string pin && !outputPins.Contains(pin))];
    }

    /// <summary>Tolerances no measured transition consulted.</summary>
    public IReadOnlyList<ElectricalDelayTolerance> UnusedTolerances { get; }

    /// <summary>Tolerances naming a pin the definition does not declare as an output.</summary>
    public IReadOnlyList<ElectricalDelayTolerance> UnknownPinTolerances { get; }

    /// <summary>Tolerances that replace the circuit-wide one for a named transition.</summary>
    public IReadOnlyList<ElectricalDelayTolerance> Overrides { get; }

    /// <summary>The tolerance that applies to one transition: its own entry, else the circuit's.</summary>
    /// <remarks>
    /// A pin's own entry wins over one that names no pin, so a circuit can set a general allowance
    /// and still hold one output to a tighter standard. An entry for another pin is never consulted.
    /// </remarks>
    public int ToleranceFor(ElectricalPropagationStep step) =>
        FindOverrideIndex(step) is int index && index >= 0
            ? Overrides[index].Ticks
            : ToleranceTicks;

    /// <summary>The entry that governs a transition, or -1 when the circuit-wide tolerance does.</summary>
    private int FindOverrideIndex(ElectricalPropagationStep step)
    {
        for (int index = 0; index < Overrides.Count; index++)
        {
            ElectricalDelayTolerance tolerance = Overrides[index];
            if (tolerance.PinId is not null &&
                tolerance.FromLevel == step.PreviousOutputLevel &&
                tolerance.ToLevel == step.OutputLevel &&
                string.Equals(tolerance.PinId, OutputPinId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        for (int index = 0; index < Overrides.Count; index++)
        {
            ElectricalDelayTolerance tolerance = Overrides[index];
            if (tolerance.PinId is null &&
                tolerance.FromLevel == step.PreviousOutputLevel &&
                tolerance.ToLevel == step.OutputLevel)
            {
                return index;
            }
        }

        return -1;
    }

    public string InputPinId { get; }

    public string OutputPinId { get; }

    /// <summary>The declared transport delay, or null when the driving instance declares none.</summary>
    public int? DeclaredTicks { get; }

    /// <summary>How far a measured delay may differ from the declared one.</summary>
    public int ToleranceTicks { get; }

    public IReadOnlyList<ElectricalPropagationStep> Steps { get; }

    /// <summary>Every measured transition outside the declared delay and its tolerance.</summary>
    public IReadOnlyList<ElectricalPropagationStep> Mismatches { get; }

    /// <summary>Whether every measured transition landed within the declared delay.</summary>
    public bool Agrees => Mismatches.Count == 0;
}

/// <summary>
/// A second run in which the supplied outputs are claimed instead of written: what the definition
/// would have computed for the pins the network took over.
/// </summary>
public sealed class ElectricalDriveAudit
{
    public ElectricalDriveAudit(ElectricalDrivePlan plan, SimulationScriptResult result)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(result);
        Plan = plan;
        Result = result;
    }

    public ElectricalDrivePlan Plan { get; }

    public SimulationScriptResult Result { get; }

    public bool Passed => Result.Passed;

    /// <summary>How many supplied ticks were compared.</summary>
    public int CheckedTicks => Plan.Claims.Count;

    /// <summary>How many supplied pins were compared.</summary>
    public int AuditedPins => Plan.ClaimedPins;
}

/// <summary>
/// Applying a circuit's drive bindings to a simulation: the levels a solved network produces become
/// a script, and the script goes through the same runner the editor, the CLI and the tests use.
/// </summary>
/// <remarks>
/// Drives become <c>set</c> steps and claims about an output become <c>expect</c> steps at the same
/// ticks, so a definition has to agree with the network in the same moment it is fed. A claim that
/// fails is reported by the runner as an ordinary expectation mismatch.
/// </remarks>
public static class ElectricalDriveSimulation
{
    /// <summary>
    /// How many ticks one claim is checked at before the window is sampled instead. A rate can map a
    /// long run onto millions of ticks, and a script that size is not a better check than a sampled
    /// one - it just takes longer to fail.
    /// </summary>
    public const int MaximumChecksPerBinding = 512;

    /// <summary>
    /// The script and the claims a circuit's bindings produce for a completed run. Pins named in
    /// <paramref name="auditedPins"/> are asked about instead of written, which is how a supplied
    /// output is shown what the definition would have computed.
    /// </summary>
    public static ElectricalDrivePlan Plan(
        ElectricalCircuit circuit,
        TransientResult run,
        ElectricalLevelSampler sampler,
        IReadOnlySet<string>? auditedPins = null)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(sampler);

        // One step per tick, carrying every drive and every claim that lands there: a binding that
        // checks an output and a binding that drives an input at the same tick describe one moment.
        var ticks = new SortedDictionary<long, (List<string> Drives, List<string> Checks)>();
        foreach (ElectricalDriveBinding binding in circuit.DriveBindings)
        {
            foreach ((ElectricalDriveStep step, long at) in binding.Steps(run, sampler))
            {
                if (!ticks.TryGetValue(at, out var entry))
                {
                    entry = ([], []);
                    ticks[at] = entry;
                }

                bool asClaim = binding.Checks || (auditedPins?.Contains(binding.PinId) ?? false);
                (asClaim ? entry.Checks : entry.Drives).Add(
                    $"\"{binding.PinId}\": {step.Level}");
            }
        }

        // A claim is made at every tick the run covers, not only where its node changes level: a
        // definition that drifts between changes has to fail too, and a check at the changes alone
        // could not see it.
        var claims = new List<ElectricalLevelClaim>();
        long endTick = ticks.Count == 0 ? 0 : ticks.Keys.Max();
        long window = endTick + 1;
        long stride = window <= MaximumChecksPerBinding
            ? 1
            : (long)Math.Ceiling(window / (double)MaximumChecksPerBinding);

        // Which ticks an audit compares at: the last tick of each plateau of the pins that drive the
        // definition. Comparing every tick would fail a zero-delay table while the circuit is still
        // settling, which is a difference in delay and not in what the network computes at rest.
        List<long>? settledTicks = null;
        if (auditedPins is not null)
        {
            var ends = new SortedSet<long>();
            foreach (ElectricalDriveBinding driven in circuit.DriveBindings.Where(binding =>
                         !auditedPins.Contains(binding.PinId)))
            {
                foreach ((_, long at) in driven.Steps(run, sampler))
                {
                    if (at > 0)
                    {
                        ends.Add(at - 1);
                    }
                }
            }

            settledTicks = [.. ends];
        }

        foreach (ElectricalDriveBinding binding in circuit.DriveBindings.Where(binding =>
                     binding.Checks || (auditedPins?.Contains(binding.PinId) ?? false)))
        {
            List<(ElectricalDriveStep Step, long At)> steps = [.. binding.Steps(run, sampler)];
            if (settledTicks is null)
            {
                for (long tick = 0; tick < endTick; tick += stride)
                {
                    AddClaim(tick);
                }

                AddClaim(endTick);
            }
            else
            {
                foreach (long tick in settledTicks.Append(endTick).Distinct())
                {
                    AddClaim(tick);
                }
            }

            void AddClaim(long tick)
            {
                if (!ticks.TryGetValue(tick, out var entry))
                {
                    entry = ([], []);
                    ticks[tick] = entry;
                }

                int level = LevelAt(steps, tick);
                entry.Checks.Add($"\"{binding.PinId}\": {level}");
                claims.Add(new ElectricalLevelClaim(binding.PinId, tick, level));
            }
        }

        var text = new StringBuilder();
        text.Append("{\"format\":\"radix-test\",\"version\":1,\"name\":\"electrical drive\",\"steps\":[");
        bool first = true;
        foreach ((long at, (List<string> drives, List<string> checks)) in ticks)
        {
            if (!first)
            {
                text.Append(',');
            }

            first = false;
            text.Append(CultureInfo.InvariantCulture, $"{{ \"at\": {at}");
            if (drives.Count > 0)
            {
                text.Append(", \"set\": { ").Append(string.Join(", ", drives)).Append(" }");
            }

            if (checks.Count > 0)
            {
                text.Append(", \"expect\": { ").Append(string.Join(", ", checks)).Append(" }");
            }

            text.Append(" }");
        }

        text.Append("]}");
        return new ElectricalDrivePlan(SimulationScript.LoadFromString(text.ToString()), claims);
    }

    /// <summary>The script a circuit's bindings produce for a completed run.</summary>
    public static SimulationScript Script(
        ElectricalCircuit circuit,
        TransientResult run,
        ElectricalLevelSampler sampler) => Plan(circuit, run, sampler).Script;

    /// <summary>
    /// Runs the drives against a definition of the same project. A definition that has no such pin
    /// fails in the runner with its own message, which is better than a message invented here.
    /// </summary>
    public static SimulationScriptResult Run(
        CircuitProject project,
        ElectricalCircuit circuit,
        TransientResult run,
        ElectricalLevelSampler sampler,
        string? definitionId = null) =>
        SimulationScriptRunner.Run(project, Plan(circuit, run, sampler).Script, definitionId);

    /// <summary>Runs an already-built plan, so a caller that reports it does not build it twice.</summary>
    public static SimulationScriptResult Run(
        CircuitProject project,
        ElectricalDrivePlan plan,
        string? definitionId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return SimulationScriptRunner.Run(project, plan.Script, definitionId);
    }

    /// <summary>
    /// What the definition would have computed for the outputs the network supplied, as a separate
    /// run in which those pins are claimed instead of written. A supplied pin is masked while it is
    /// driven, so its own answer can only be seen when nothing supplies it - which is exactly why
    /// this is a second run and not an observation of the first. Inputs are never audited: they are
    /// the definition's own values and are written from outside by design.
    /// </summary>
    /// <returns>Null when no output is supplied, so a caller can report silence honestly.</returns>
    public static ElectricalDriveAudit? Audit(
        CircuitProject project,
        ElectricalCircuit circuit,
        TransientResult run,
        ElectricalLevelSampler sampler,
        string? definitionId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        CircuitDefinition? definition = project.FindDefinition(definitionId ?? project.RootDefinitionId);
        if (definition is null)
        {
            return null;
        }

        HashSet<string> audited = [.. circuit.DriveBindings
            .Where(binding => !binding.Checks &&
                              definition.FindPin(binding.PinId) is { Direction: PinDirection.Output })
            .Select(binding => binding.PinId)];
        if (audited.Count == 0)
        {
            return null;
        }

        ElectricalDrivePlan auditPlan = Plan(circuit, run, sampler, audited);
        return new ElectricalDriveAudit(
            auditPlan,
            SimulationScriptRunner.Run(project, auditPlan.Script, definitionId));
    }

    /// <summary>
    /// Measures how long the network takes to answer: for each change of the supplied output, the
    /// most recent change of the driven input, and the delay between them. A definition is static, so
    /// this is the half of the comparison the audit cannot make - it says where the answer comes from,
    /// not when.
    /// </summary>
    /// <returns>Null when the definition has no driven input and supplied output to pair.</returns>
    public static ElectricalPropagationReport? Propagation(
        CircuitProject project,
        ElectricalCircuit circuit,
        TransientResult run,
        ElectricalLevelSampler sampler,
        string? definitionId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        CircuitDefinition? definition = project.FindDefinition(definitionId ?? project.RootDefinitionId);
        if (definition is null)
        {
            return null;
        }

        ElectricalDriveBinding? input = circuit.DriveBindings.FirstOrDefault(binding =>
            !binding.Checks && definition.FindPin(binding.PinId) is { Direction: PinDirection.Input });
        ElectricalDriveBinding? output = circuit.DriveBindings.FirstOrDefault(binding =>
            definition.FindPin(binding.PinId) is { Direction: PinDirection.Output });
        if (input is null || output is null)
        {
            return null;
        }

        List<(ElectricalDriveStep Step, long At)> inputs = [.. input.Steps(run, sampler)];
        List<(ElectricalDriveStep Step, long At)> outputs = [.. output.Steps(run, sampler)];
        var steps = new List<ElectricalPropagationStep>();

        // Index 0 is the initial state, not an answer to a change, so pairs start at the second one.
        for (int index = 1; index < outputs.Count && inputs.Count > 0; index++)
        {
            (ElectricalDriveStep step, long at) = outputs[index];
            (ElectricalDriveStep Step, long At) prior = inputs[0];
            foreach ((ElectricalDriveStep candidateStep, long candidateAt) in inputs)
            {
                if (candidateAt <= at)
                {
                    prior = (candidateStep, candidateAt);
                }
                else
                {
                    break;
                }
            }

            long ticks = at - prior.At;
            steps.Add(new ElectricalPropagationStep(
                prior.Step.Level,
                outputs[index - 1].Step.Level,
                step.Level,
                prior.At,
                at,
                ticks,
                ticks / output.TicksPerSecond));
        }

        HashSet<string> outputPins = [.. definition.Pins
            .Where(pin => pin.Direction == PinDirection.Output)
            .Select(pin => pin.Id)];
        return new ElectricalPropagationReport(
            input.PinId,
            output.PinId,
            DeclaredDelayTicks(definition, output.PinId),
            circuit.DelayToleranceTicks,
            circuit.DelayTolerances,
            outputPins,
            steps);
    }

    /// <summary>
    /// The transport delay the definition declares on the instance that drives a pin. Only primitives
    /// carry a delay, so a pin fed through a sub-definition reports none rather than guessing.
    /// </summary>
    private static int? DeclaredDelayTicks(CircuitDefinition definition, string pinId)
    {
        Net? net = definition.FindPortNet(pinId);
        if (net is null)
        {
            return null;
        }

        foreach (PinRef pin in net.Pins)
        {
            ComponentInstance? instance = definition.Instances.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, pin.InstanceId, StringComparison.Ordinal));
            long? delay = instance?.Primitive switch
            {
                LookupSpec lookup => lookup.DelayTicks,
                DelaySpec delayed => delayed.DelayTicks,
                _ => null,
            };
            if (delay is long declared)
            {
                return (int)declared;
            }
        }

        return null;
    }

    /// <summary>The level the node held at a tick: the last change at or before it.</summary>
    private static int LevelAt(IReadOnlyList<(ElectricalDriveStep Step, long At)> steps, long tick)
    {
        int level = steps[0].Step.Level;
        foreach ((ElectricalDriveStep step, long at) in steps)
        {
            if (at > tick)
            {
                break;
            }

            level = step.Level;
        }

        return level;
    }
}
