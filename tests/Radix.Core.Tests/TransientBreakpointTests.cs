using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A piecewise-linear drive changes slope at its breakpoints, so a step that crosses
/// one integrates a source value that never held. These tests pin the behaviour that
/// matters most: a pulse shorter than the requested step must still be seen.
/// </summary>
public sealed class TransientBreakpointTests
{
    private const double Tau = 1e-3;

    // 0 -> 5 V -> 0 over 2 us starting at 1 ms. The triangular shape keeps the pulse
    // continuous, so its charge (5 V * 2 us / 2 = 5e-6 V s) is what both integration
    // schemes accumulate; a hard-edged rectangle would smear by the scheme's own
    // half-step treatment of an endpoint value.
    private static readonly TransientWaveformPoint[] TrianglePulse =
    [
        new(0, 0),
        new(1.000e-3, 0),
        new(1.001e-3, 5),
        new(1.002e-3, 0),
    ];

    /// <summary>
    /// Response a time constant after a short drive: <c>(Q/tau) * e^(-(T - t_end)/tau)</c>
    /// with <c>Q = 5e-6 V s</c>, the area of the triangle.
    /// </summary>
    private static double ExpectedTail(double stopTime) =>
        (5e-6 / Tau) * Math.Exp(-(stopTime - 1.002e-3) / Tau);

    [Fact]
    public void FixedStepsLandOnABreakpointInsteadOfSkippingANarrowPulse()
    {
        // 100 steps over 5 ms gives a 50 us step, so no uniform sample lands inside the
        // 2 us pulse: without breakpoint landing the drive reads zero at every sample
        // and the capacitor never charges.
        TransientNetlist netlist = TrianglePulseNetlist();
        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 2e-3,
            Steps = 100,
        });

        Assert.True(result.Completed, result.Message);
        Assert.Equal(2e-3, result.FinalPoint!.Time, 12);
        Assert.True(
            result.Points.Count > 101,
            $"breakpoint landings should add points, got {result.Points.Count}");
        Assert.Contains(result.Points, point => Math.Abs(point.Time - 1.001e-3) < 1e-12);
        Assert.Contains(result.Points, point => Math.Abs(point.Time - 1.002e-3) < 1e-12);

        // One time constant of observation keeps backward Euler's first-order decay error
        // (about (h/tau)^2 per step) well inside this bound.
        double expected = ExpectedTail(2e-3);
        double measured = result.FinalPoint.NodeVoltages["out"];
        Assert.True(measured > 0, "the pulse was not integrated at all");
        Assert.True(
            Math.Abs(measured - expected) / expected < 0.05,
            $"pulse response {measured:G6} V, expected about {expected:G6} V");
    }

    [Fact]
    public void AdaptiveStepsAlsoLandOnABreakpoint()
    {
        TransientNetlist netlist = TrianglePulseNetlist();
        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 2e-3,
            Steps = 50,
            Method = TransientMethod.Trapezoidal,
            AdaptiveTolerance = 1e-6,
        });

        Assert.True(result.Completed, result.Message);
        Assert.Contains(result.Points, point => Math.Abs(point.Time - 1.001e-3) < 1e-12);
        Assert.Contains(result.Points, point => Math.Abs(point.Time - 1.002e-3) < 1e-12);

        double expected = ExpectedTail(2e-3);
        double measured = result.FinalPoint!.NodeVoltages["out"];
        Assert.True(
            Math.Abs(measured - expected) / expected < 0.02,
            $"pulse response {measured:G6} V, expected about {expected:G6} V");
    }

    [Fact]
    public void AConstantSourceKeepsTheRequestedStepCount()
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 0);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 1e-3,
            Steps = 50,
        });

        Assert.True(result.Completed, result.Message);
        Assert.Equal(51, result.Points.Count);
        Assert.Equal(1e-3, result.FinalPoint!.Time, 12);
    }

    private static TransientNetlist TrianglePulseNetlist() =>
        new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 0)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 0)
            .SetWaveform("V1", TrianglePulse);
}
