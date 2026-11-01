using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The reported time grid is part of the solver's contract: a fixed-step run reports exactly
/// <c>steps + 1</c> points, the first at the start time and the last at the stop time, and no
/// step collapses to a sliver. Accumulated addition used to leave a remainder larger than the
/// old tolerance for common inputs, which produced one extra near-zero step; these cases pin
/// that down across stop times and step counts.
/// </summary>
public sealed class TransientTimeGridTests
{
    [Theory]
    [InlineData(0.01, 1_000)]
    [InlineData(0.005, 200)]
    [InlineData(0.001, 999)]
    [InlineData(0.0033, 1_234)]
    [InlineData(0.0025, 600)]
    [InlineData(1.0, 1_000)]
    [InlineData(0.01, 4_000)]
    public void EveryRequestedStepProducesExactlyOnePoint(double stop, int steps)
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 0);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = stop,
            Steps = steps,
            Method = TransientMethod.Trapezoidal,
        });

        Assert.True(result.Completed, result.Message);
        Assert.Equal(steps + 1, result.Points.Count);
        Assert.Equal(0, result.Points[0].Time);
        Assert.Equal(stop, result.FinalPoint!.Time);

        double nominal = stop / steps;
        for (int index = 1; index < result.Points.Count; index++)
        {
            double delta = result.Points[index].Time - result.Points[index - 1].Time;
            Assert.True(delta > 0, $"step {index} did not advance time");
            Assert.True(
                delta > nominal * 0.5,
                $"step {index} collapsed to {delta:G6} s against a nominal {nominal:G6} s");
        }
    }

    [Fact]
    public void TheInitialValueDrivenStartDoesNotChangeTheGrid()
    {
        // A charged capacitor starts from a different initial state but the same time grid.
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 2);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 0.01,
            Steps = 1_000,
        });

        Assert.True(result.Completed, result.Message);
        Assert.Equal(1_001, result.Points.Count);
        Assert.Equal(0.01, result.FinalPoint!.Time);
    }
}
