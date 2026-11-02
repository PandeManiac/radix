using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Two inverter stages in series, stage two driven from stage one's output through its own gate
/// resistor. The measurement that matters is the cost of that loading: the same devices that take 23
/// and 44 ticks alone take far longer when the second gate bus sits on the first output.
/// </summary>
public sealed class ElectricalChainTests
{
    private static (CircuitProject Project, ElectricalCircuit Circuit, TransientResult Run, ElectricalLevelSampler Sampler)
        Load(string file, double stop, int steps)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", file),
            validate: false);
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        ElectricalLevelSampler sampler = Assert.IsType<ElectricalLevelSampler>(
            ElectricalLevelSampler.ForCircuit(circuit));
        TransientResult run = circuit.ToTransientNetlist().Run(new TransientOptions
        {
            StartTime = 0,
            StopTime = stop,
            Steps = steps,
            MaximumStepCount = 100_000,
        });
        return (project, circuit, run, sampler);
    }

    [Fact]
    public void TheSecondStageFollowsTheInputThroughTwoInversions()
    {
        (_, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load("electrical-ternary-chain.radix", 5e-7, 5000);
        ElectricalDriveBinding output = Assert.Single(
            circuit.DriveBindings,
            binding => binding.PinId == "y");

        int[] levels = [.. output.Steps(run, sampler).Select(entry => entry.Step.Level)];

        // Two inversions: the output follows the input rather than inverting it.
        Assert.Equal(new[] { 0, 1, 2 }, levels);
    }

    [Fact]
    public void LoadingTheFirstStageCostsMostOfItsSpeed()
    {
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load("electrical-ternary-chain.radix", 5e-7, 5000);

        ElectricalPropagationReport chain = Assert.IsType<ElectricalPropagationReport>(
            ElectricalDriveSimulation.Propagation(project, circuit, run, sampler));

        Assert.Equal(2, chain.Steps.Count);
        Assert.Equal(105, chain.Steps[0].Ticks);
        Assert.Equal(178, chain.Steps[1].Ticks);
        Assert.Equal(105, chain.ToleranceFor(chain.Steps[0]));
        Assert.Equal(178, chain.ToleranceFor(chain.Steps[1]));
        Assert.True(chain.Agrees);

        // The same devices measured alone: the second gate bus is what makes the difference.
        (CircuitProject sweepProject, ElectricalCircuit sweepCircuit, TransientResult sweepRun,
            ElectricalLevelSampler sweepSampler) = Load("electrical-ternary-sweep.radix", 2.5e-7, 2500);
        ElectricalPropagationReport single = Assert.IsType<ElectricalPropagationReport>(
            ElectricalDriveSimulation.Propagation(sweepProject, sweepCircuit, sweepRun, sweepSampler));

        Assert.True(chain.Steps[0].Ticks > single.Steps[0].Ticks);
        Assert.True(chain.Steps[1].Ticks > single.Steps[1].Ticks);
    }

    [Fact]
    public void TheChainAuditAgreesWithTwoInverterTables()
    {
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load("electrical-ternary-chain.radix", 5e-7, 5000);

        ElectricalDriveAudit audit = Assert.IsType<ElectricalDriveAudit>(
            ElectricalDriveSimulation.Audit(project, circuit, run, sampler));

        Assert.True(
            audit.Passed,
            string.Join("; ", audit.Result.Failures.Select(failure => failure.Message)));
        Assert.Equal(3, audit.CheckedTicks);
    }
}
