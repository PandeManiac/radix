using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The shipped example is the claim that the electrical-first path is demonstrable from the
/// repository: one file holds both the solved inverter and the digital definition its binding drives,
/// so <c>radix drive --simulate</c> passes without anyone assembling a project first.
/// </summary>
public sealed class ElectricalDriveExampleTests
{
    private static CircuitProject Load()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "electrical-ternary-drive.radix"));
    }

    [Fact]
    public void TheExampleDrivesItsOwnDefinitionFromItsOwnCircuit()
    {
        CircuitProject project = Load();
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);

        Assert.Equal(2, circuit.DriveBindings.Count);
        ElectricalDriveBinding drive = circuit.DriveBindings[0];
        Assert.Equal("out", drive.NodeId);
        Assert.Equal("a", drive.PinId);
        Assert.False(drive.Checks);

        // The second binding states what the definition's output must read, so the run checks the
        // digital side against the solved network instead of only feeding it.
        ElectricalDriveBinding claim = circuit.DriveBindings[1];
        Assert.Equal("out", claim.NodeId);
        Assert.Equal("y", claim.PinId);
        Assert.True(claim.Checks);

        TransientResult run = circuit.ToTransientNetlist().Run(
            new TransientOptions { StartTime = 0, StopTime = 120e-9, Steps = 1280 });
        ElectricalLevelSampler sampler = Assert.IsType<ElectricalLevelSampler>(
            ElectricalLevelSampler.ForCircuit(circuit));

        SimulationResult(project, circuit, run, sampler);
    }

    private static void SimulationResult(
        CircuitProject project,
        ElectricalCircuit circuit,
        TransientResult run,
        ElectricalLevelSampler sampler)
    {
        Core.Testing.SimulationScriptResult result = ElectricalDriveSimulation.Run(
            project, circuit, run, sampler);

        Assert.True(
            result.Passed,
            string.Join(Environment.NewLine, result.Failures.Select(failure => failure.Message)));
        Assert.Equal("demo.follow", result.DefinitionId);
        // The claim is checked on every tick of the run, not only where the level changes.
        Assert.Equal(45, result.StepsExecuted);
        Assert.Equal(44, result.FinalTime);
    }
}
