using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Core.Testing;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The whole path in one call: a solved circuit, its authored binding, and a simulation that takes
/// the drives. The assertions are the runner's verdict and its step count, so a script that skipped
/// or duplicated a drive fails here rather than somewhere downstream.
/// </summary>
public sealed class ElectricalDriveSimulationTests
{
    private static CircuitProject TernaryInverter()
    {
        var builder = new ProjectBuilder("ternary inverter");
        DefinitionBuilder definition = builder.Define("invert", "Ternary inverter");
        Net a = definition.AddPin("a", "A", PinDirection.Input, StandardDomains.TernaryId);
        Net y = definition.AddPin("y", "Y", PinDirection.Output, StandardDomains.TernaryId);
        definition.AddPrimitive("gate", new LookupSpec(1, [2, 1, 0]));
        definition.Connect(a, definition.Pin("gate", "in0"));
        definition.Connect(y, definition.Pin("gate", "out"));
        return builder.Build("invert");
    }

    private static ElectricalCircuit BoundCircuit()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix"),
            validate: false);
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        circuit.DriveBindings.Add(new ElectricalDriveBinding
        {
            NodeId = "out",
            PinId = "a",
            TicksPerSecond = 1e9,
        });
        return circuit;
    }

    private static TransientResult Run(ElectricalCircuit circuit) => circuit.ToTransientNetlist()
        .Run(new TransientOptions { StartTime = 0, StopTime = 120e-9, Steps = 1280 });

    [Fact]
    public void TheDrivesReachTheSimulationAndTheOutputFollows()
    {
        ElectricalCircuit circuit = BoundCircuit();
        TransientResult run = Run(circuit);
        ElectricalLevelSampler sampler = Assert.IsType<ElectricalLevelSampler>(
            ElectricalLevelSampler.ForCircuit(circuit));

        SimulationScript script = ElectricalDriveSimulation.Script(circuit, run, sampler);
        Assert.Equal(2, script.Steps.Count);

        SimulationScriptResult result = ElectricalDriveSimulation.Run(
            TernaryInverter(), circuit, run, sampler, "invert");

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal("invert", result.DefinitionId);
        Assert.Equal(2, result.StepsExecuted);
    }

    private static CircuitProject BinaryBuffer()
    {
        var builder = new ProjectBuilder("buffer");
        DefinitionBuilder definition = builder.Define("buffer", "Buffer");
        Net d = definition.AddPin("d", "D", PinDirection.Input, StandardDomains.BinaryId);
        Net q = definition.AddPin("q", "Q", PinDirection.Output, StandardDomains.BinaryId);
        definition.AddPrimitive("gate", new LookupSpec(1, [0, 1]));
        definition.Connect(d, definition.Pin("gate", "in0"));
        definition.Connect(q, definition.Pin("gate", "out"));
        return builder.Build("buffer");
    }

    [Fact]
    public void ADefinitionWithoutThePinFailsWithTheRunnersMessage()
    {
        ElectricalCircuit circuit = BoundCircuit();
        TransientResult run = Run(circuit);
        ElectricalLevelSampler sampler = Assert.IsType<ElectricalLevelSampler>(
            ElectricalLevelSampler.ForCircuit(circuit));

        // A buffer has pins d and q, so the binding's 'a' cannot be applied: the failure has to
        // name the pin rather than pass because no expectation was written.
        SimulationScriptResult result = ElectricalDriveSimulation.Run(
            BinaryBuffer(), circuit, run, sampler);

        Assert.False(result.Passed);

        // One failure per drive step: the runner reports each application it could not make, which
        // is what makes the message useful when a project has several bindings.
        Assert.Equal(2, result.Failures.Count);
        Assert.All(result.Failures, failure => Assert.Contains(
            "unknown interface pin 'a'",
            failure.Message,
            StringComparison.Ordinal));
    }
}
