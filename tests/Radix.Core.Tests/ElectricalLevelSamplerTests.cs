using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Reading volts as a level is where the electrical model meets the digital one, so the boundary
/// behaviour and the interpolation are both checked, and then the ternary inverter is read at two
/// times where its level is not in doubt.
/// </summary>
public sealed class ElectricalLevelSamplerTests
{
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

    private static TransientResult Run(ElectricalCircuit circuit) => circuit.ToTransientNetlist()
        .Run(new TransientOptions { StartTime = 0, StopTime = 120e-9, Steps = 1280 });

    [Fact]
    public void ThreeLevelsComeFromTwoAscendingBoundaries()
    {
        ElectricalLevelSampler sampler = ElectricalLevelSampler.TernaryFromRail(5);

        Assert.Equal(3, sampler.LevelCount);
        Assert.Equal(0, sampler.LevelFor(0));
        Assert.Equal(0, sampler.LevelFor(1.6));
        Assert.Equal(1, sampler.LevelFor(2.5));
        Assert.Equal(2, sampler.LevelFor(5));

        // Exactly on a boundary reads as the level above, like a comparator with no offset.
        Assert.Equal(1, sampler.LevelFor(5.0 / 3.0));
        Assert.Equal(2, sampler.LevelFor(10.0 / 3.0));
        Assert.Equal(0, sampler.LevelFor(Math.BitDecrement(5.0 / 3.0)));

        // Overshoot is still the rail, so it clamps rather than fails.
        Assert.Equal(2, sampler.LevelFor(5.0194));
        Assert.Equal(0, sampler.LevelFor(-0.2));
    }

    [Fact]
    public void TheTernaryInverterReadsMidRailAsTheMiddleLevel()
    {
        ElectricalLevelSampler sampler = ElectricalLevelSampler.TernaryFromRail(5);
        TransientResult result = Run(Example());

        SampledLevel start = sampler.SampleAt(result, "out", 0);
        Assert.Equal(2, start.Level);
        Assert.InRange(start.Volts, 4.9, 5.0);

        SampledLevel settled = sampler.SampleAt(result, "out", 120e-9);
        Assert.Equal(1, settled.Level);
        Assert.InRange(settled.Volts, 2.45, 2.55);
    }

    [Fact]
    public void ATimeBetweenSamplesIsInterpolatedRatherThanSnapped()
    {
        ElectricalLevelSampler sampler = ElectricalLevelSampler.TernaryFromRail(5);
        TransientResult result = Run(Example());

        TransientPoint before = result.Points[100];
        TransientPoint after = result.Points[101];
        double middle = (before.Time + after.Time) / 2;

        SampledLevel sampled = sampler.SampleAt(result, "out", middle);
        Assert.Equal(
            (before.NodeVoltages["out"] + after.NodeVoltages["out"]) / 2,
            sampled.Volts,
            precision: 12);

        // Outside the run the ends answer, so a caller cannot get a made-up value back.
        Assert.Equal(result.Points[0].NodeVoltages["out"], sampler.SampleAt(result, "out", -1).Volts);
        Assert.Equal(result.Points[^1].NodeVoltages["out"], sampler.SampleAt(result, "out", 1).Volts);
    }

    [Fact]
    public void BadTablesAndMissingNodesAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new ElectricalLevelSampler([3.0, 1.0]));
        Assert.Throws<ArgumentException>(() => new ElectricalLevelSampler([1.0, 1.0]));
        Assert.Throws<ArgumentException>(() => new ElectricalLevelSampler([double.NaN]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElectricalLevelSampler.TernaryFromRail(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ElectricalLevelSampler.TernaryFromRail(5).LevelFor(double.PositiveInfinity));

        ElectricalLevelSampler sampler = ElectricalLevelSampler.TernaryFromRail(5);
        TransientResult result = Run(Example());
        Assert.Throws<ArgumentException>(() => sampler.SampleAt(result, "nope", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.SampleAt(result, "out", double.NaN));
    }
}
