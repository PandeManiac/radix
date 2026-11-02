using System.Globalization;
using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Core.Testing;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The last mile of the handoff, proved rather than assumed: a solved electrical circuit's levels
/// are written into a script, the script drives a real ternary inverter through the simulation
/// engine, and the engine's own expectations check that the output followed.
/// </summary>
public sealed class ElectricalDriveEndToEndTests
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

    private static ElectricalCircuit Example()
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
        return Assert.Single(project.ElectricalCircuits);
    }

    [Fact]
    public void ASolvedCircuitDrivesATernaryNetThroughTheEngine()
    {
        ElectricalLevelSampler sampler = ElectricalLevelSampler.TernaryFromRail(5);
        TransientResult run = Example().ToTransientNetlist().Run(
            new TransientOptions { StartTime = 0, StopTime = 120e-9, Steps = 1280 });

        // One tick per nanosecond resolves the crossing, which happens tens of nanoseconds in.
        IReadOnlyList<(long At, ScriptDrive Drive)> scheduled =
            ElectricalDriveSchedule.Scheduled(run, "out", sampler, "a", ticksPerSecond: 1e9);

        Assert.Equal([2, 1], scheduled.Select(entry => entry.Drive.Value.Index!.Value));

        // The circuit reads 2 then 1 as its output falls to mid-rail; a ternary inverter turns 2
        // into 0 and 1 into 1. The expectation is checked by the runner after it settles the drive.
        string steps = string.Join(
            ",\n",
            scheduled.Select(entry => string.Create(
                CultureInfo.InvariantCulture,
                $"{{ \"at\": {entry.At}, \"set\": {{ \"a\": {entry.Drive.Value.Index!.Value} }}, \"expect\": {{ \"y\": {2 - entry.Drive.Value.Index!.Value} }} }}")));

        SimulationScript script = SimulationScript.LoadFromString(
            $$"""
            {
              "format": "radix-test",
              "version": 1,
              "steps": [ {{steps}} ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(TernaryInverter(), script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(scheduled.Count, result.StepsExecuted);
    }
}
