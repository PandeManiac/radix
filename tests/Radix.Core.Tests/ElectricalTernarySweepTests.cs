using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The electrical network driven through all three levels, with a definition that is a real ternary
/// inverter: pin a follows the input node and pin y is supplied from the output node. Because the two
/// bindings read different nodes, the audit compares a table against something it does not itself
/// define - the only arrangement in which the comparison says anything about inversion.
/// </summary>
public sealed class ElectricalTernarySweepTests
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
    public void TheNetworkInvertsEveryLevel()
    {
        (_, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) = Load();
        ElectricalDriveBinding output = Assert.Single(
            circuit.DriveBindings,
            binding => binding.PinId == "y");

        int[] levels = [.. output.Steps(run, sampler).Select(entry => entry.Step.Level)];

        // Driven 0 V, 2.5 V, 5 V: the output leaves the rail for the middle level and then the
        // ground rail. This is the electrical inverter's claim, measured rather than assumed.
        Assert.Equal(new[] { 2, 1, 0 }, levels);
    }

    [Fact]
    public void TheAuditAgreesAtEachSettledPlateau()
    {
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load();

        ElectricalDriveAudit audit = Assert.IsType<ElectricalDriveAudit>(
            ElectricalDriveSimulation.Audit(project, circuit, run, sampler));

        Assert.True(
            audit.Passed,
            string.Join("; ", audit.Result.Failures.Select(failure => failure.Message)));

        // One comparison per plateau of the driven input, plus the final tick: three levels, three
        // answers. An audit that compared every tick would fail here on the circuit's rise and fall.
        Assert.Equal(3, audit.CheckedTicks);
        Assert.Equal(1, audit.AuditedPins);
    }

    [Fact]
    public void ADefinitionThatDoesNotInvertIsCaught()
    {
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load();

        // The identity table agrees with the input node and would have passed an audit that compared
        // a node with itself; against the output it is simply the wrong function.
        ComponentInstance gate = Assert.Single(
            project.FindDefinition("demo.inverter")!.Instances,
            instance => instance.Id == "gate");
        gate.Primitive = new LookupSpec(1, [0, 1, 2]);

        ElectricalDriveAudit audit = Assert.IsType<ElectricalDriveAudit>(
            ElectricalDriveSimulation.Audit(project, circuit, run, sampler));

        Assert.False(audit.Passed);
    }
}
