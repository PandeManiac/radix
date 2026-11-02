using System.Globalization;
using System.Text;
using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Testing;

namespace Radix.Core.Electrical;

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
    /// <summary>The script a circuit's bindings produce for a completed run.</summary>
    public static SimulationScript Script(
        ElectricalCircuit circuit,
        TransientResult run,
        ElectricalLevelSampler sampler)
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

                (binding.Checks ? entry.Checks : entry.Drives).Add(
                    $"\"{binding.PinId}\": {step.Level}");
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
        return SimulationScript.LoadFromString(text.ToString());
    }

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
        SimulationScriptRunner.Run(project, Script(circuit, run, sampler), definitionId);
}
