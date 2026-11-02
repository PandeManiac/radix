using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Gain is measured by differentiating the solved curve, so these check it against circuits whose
/// slope is known in closed form: a divider attenuates by exactly one half, the driven node moves
/// one volt per volt, and an inverter's steepest point is its switching point.
/// </summary>
public sealed class DcSweepGainTests
{
    [Fact]
    public void ADividerReportsItsMeasuredAttenuation()
    {
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 0)
            .AddResistor("R1", "in", "out", 1_000)
            .AddResistor("R2", "out", "gnd", 1_000);

        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(netlist, "V1", DcSweep.Linspace(0, 10, 5));
        IReadOnlyList<DcSweepGainPoint> output = DcSweepGain.Along(rows, "out");

        Assert.Equal(5, output.Count);
        Assert.All(output, point => Assert.Equal(0.5, point.Gain!.Value, 12));

        // The driven node itself moves one volt per volt, which is the reference slope.
        Assert.All(
            DcSweepGain.Along(rows, "in"),
            point => Assert.Equal(1, point.Gain!.Value, 12));
    }

    [Fact]
    public void ASingleSolvedPointHasNoSlope()
    {
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 0)
            .AddResistor("R1", "in", "gnd", 1_000);

        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(netlist, "V1", [1]);
        DcSweepGainPoint point = Assert.Single(DcSweepGain.Along(rows, "in"));

        // One point cannot be differentiated, and inventing a zero would read as "no gain".
        Assert.Null(point.Gain);
    }

    [Fact]
    public void TheInverterIsSteepestWhereItSwitches()
    {
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VIN", "in", "gnd", 0)
            .AddMosfet("MP", "out", "in", "vdd", DcMosfetChannel.PChannel, 1, 1e-3, 0.02)
            .AddMosfet("MN", "out", "in", "gnd", DcMosfetChannel.NChannel, 1, 1e-3, 0.02);

        // The core sweep is bounded to 64 values, so the grid is about 80 mV wide here.
        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(netlist, "VIN", DcSweep.Linspace(0, 5, 64));
        IReadOnlyList<DcSweepGainPoint> gains = DcSweepGain.Along(rows, "out");

        DcSweepGainPoint peak = gains
            .OrderByDescending(point => Math.Abs(point.Gain ?? 0))
            .First();
        Assert.True(Math.Abs(peak.Gain!.Value) > 10, $"peak gain {peak.Gain:G6} V/V");
        // The steepest sample sits at mid-rail to within one grid step (79 mV).
        Assert.Equal(2.5, peak.Value, 1);

        // At either rail the output does not follow the input at all: one device is simply off,
        // so the slope collapses instead of staying at a finite value.
        Assert.True(Math.Abs(gains[0].Gain!.Value) < 0.01, $"rail gain {gains[0].Gain:G6}");
        Assert.True(Math.Abs(gains[^1].Gain!.Value) < 0.01, $"rail gain {gains[^1].Gain:G6}");
    }
}
