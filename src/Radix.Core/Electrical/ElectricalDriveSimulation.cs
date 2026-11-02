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

        foreach (ElectricalDriveBinding binding in circuit.DriveBindings.Where(binding =>
                     binding.Checks || (auditedPins?.Contains(binding.PinId) ?? false)))
        {
            List<(ElectricalDriveStep Step, long At)> steps = [.. binding.Steps(run, sampler)];
            for (long tick = 0; tick < endTick; tick += stride)
            {
                AddClaim(tick);
            }

            AddClaim(endTick);

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
