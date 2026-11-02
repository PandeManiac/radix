using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The audit compares levels at settled plateaus, so it cannot see a definition that answers
/// correctly but late. This measures the delay itself: each output change paired with the input change
/// that preceded it, beside the transport delay the definition declares. The numbers are the
/// circuit's, not an assumption - the fall is measurably slower than the rise because the gate bus and
/// the summing node charge through different paths.
/// </summary>
public sealed class ElectricalPropagationTests
{
    private static (CircuitProject Project, ElectricalCircuit Circuit, TransientResult Run, ElectricalLevelSampler Sampler)
        Load()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "electrical-ternary-sweep.radix"),
            validate: false);
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        ElectricalLevelSampler sampler = Assert.IsType<ElectricalLevelSampler>(
            ElectricalLevelSampler.ForCircuit(circuit));
        TransientResult run = circuit.ToTransientNetlist().Run(new TransientOptions
        {
            StartTime = 0,
            StopTime = 2.5e-7,
            Steps = 2500,
            MaximumStepCount = 100_000,
        });
        return (project, circuit, run, sampler);
    }

    [Fact]
    public void EachOutputChangeIsPairedWithTheInputChangeThatCausedIt()
    {
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load();

        ElectricalPropagationReport report = Assert.IsType<ElectricalPropagationReport>(
            ElectricalDriveSimulation.Propagation(project, circuit, run, sampler));

        Assert.Equal("a", report.InputPinId);
        Assert.Equal("y", report.OutputPinId);
        Assert.Equal(2, report.Steps.Count);

        // The file declares no transport delay and a 44-tick tolerance, which admits both measured
        // transitions: the network is slower than the table says, and the file records by how much.
        Assert.Equal(0, report.DeclaredTicks);
        Assert.Equal(44, report.ToleranceTicks);
        Assert.True(report.Agrees);

        // The input reached level 1 at tick 21; the output only crossed at 44.
        ElectricalPropagationStep first = report.Steps[0];
        Assert.Equal(2, first.PreviousOutputLevel);
        Assert.Equal(1, first.OutputLevel);
        Assert.Equal(21, first.InputAt);
        Assert.Equal(44, first.OutputAt);
        Assert.Equal(23, first.Ticks);
        Assert.Equal(2.3e-8, first.Seconds, 1e-15);

        // The second step is slower in this network, and saying so is the point of measuring it.
        ElectricalPropagationStep second = report.Steps[1];
        Assert.Equal(100, second.InputAt);
        Assert.Equal(144, second.OutputAt);
        Assert.Equal(44, second.Ticks);
        Assert.True(second.Ticks > first.Ticks);
    }

    [Fact]
    public void ADeclaredDelayIsReportedAsTheFileWroteIt()
    {
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load();

        ComponentInstance gate = Assert.Single(
            project.FindDefinition("demo.inverter")!.Instances,
            instance => instance.Id == "gate");
        gate.Primitive = new LookupSpec(1, [2, 1, 0], 5);

        ElectricalPropagationReport report = Assert.IsType<ElectricalPropagationReport>(
            ElectricalDriveSimulation.Propagation(project, circuit, run, sampler));

        Assert.Equal(5, report.DeclaredTicks);

        // The authored 44-tick tolerance admits both transitions even against five declared ticks;
        // the tightened case below is where the judgement actually bites.
        Assert.Equal(44, report.ToleranceTicks);
        Assert.True(report.Agrees);

        // The measurement does not move: it describes the circuit, not the declaration.
        Assert.Equal(23, report.Steps[0].Ticks);
        Assert.NotEqual(5L, report.Steps[0].Ticks);
    }

    [Fact]
    public void TighteningTheToleranceFailsTheRun()
    {
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load();
        Assert.True(Assert.IsType<ElectricalPropagationReport>(
            ElectricalDriveSimulation.Propagation(project, circuit, run, sampler)).Agrees);

        // The authored tolerance is what makes the file checkable; zero demands the declared delay.
        circuit.DelayToleranceTicks = 0;
        ElectricalPropagationReport tight = Assert.IsType<ElectricalPropagationReport>(
            ElectricalDriveSimulation.Propagation(project, circuit, run, sampler));

        Assert.False(tight.Agrees);
        Assert.Equal(2, tight.Mismatches.Count);
    }

    [Fact]
    public void TheToleranceSurvivesAFile()
    {
        (CircuitProject project, _, _, _) = Load();
        string path = Path.Combine(
            Path.GetTempPath(),
            $"radix-tolerance-{Guid.NewGuid():N}.radix");
        try
        {
            ProjectSerializer.Save(path, project);
            CircuitProject reloaded = ProjectSerializer.Load(path, validate: false);

            Assert.Equal(44, Assert.Single(reloaded.ElectricalCircuits).DelayToleranceTicks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ACircuitWithNothingToPairHasNoReport()
    {
        // The drive example drives and claims the same node, so there is no output to time.
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load();
        project.FindDefinition("demo.inverter")!.FindPin("y")!.Direction = PinDirection.Input;

        Assert.Null(ElectricalDriveSimulation.Propagation(project, circuit, run, sampler));
    }
}
