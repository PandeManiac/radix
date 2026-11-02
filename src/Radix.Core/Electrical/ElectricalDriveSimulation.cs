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
/// The script carries drives only. Expectations belong to whoever knows what the digital side should
/// answer, so a caller that wants them checks the resulting trace or adds expectations itself; this
/// type's job is to get a solved voltage onto a pin without a second description of the binding.
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

        var text = new StringBuilder();
        text.Append("{\"format\":\"radix-test\",\"version\":1,\"name\":\"electrical drive\",\"steps\":[");
        bool first = true;
        foreach (ElectricalDriveBinding binding in circuit.DriveBindings)
        {
            foreach ((ElectricalDriveStep step, long at) in binding.Steps(run, sampler))
            {
                if (!first)
                {
                    text.Append(',');
                }

                first = false;
                text.Append(CultureInfo.InvariantCulture,
                    $"{{ \"at\": {at}, \"set\": {{ \"{binding.PinId}\": {step.Level} }} }}");
            }
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
