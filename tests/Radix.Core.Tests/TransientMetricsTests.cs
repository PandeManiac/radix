using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Peak and settling metrics are what a designer reads off a transient plot, so they
/// are checked against closed forms: a first-order charge settles at
/// <c>tau*ln(1/band)</c>, and a second-order underdamped step peaks at
/// <c>1 + e^(-pi*zeta/sqrt(1-zeta^2))</c> a quarter period in.
/// </summary>
public sealed class TransientMetricsTests
{
    [Fact]
    public void RcChargeReportsItsPeakAndSettlingTime()
    {
        const double supply = 5;
        const double tau = 1_000 * 1e-6;
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", supply)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 0);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 6 * tau,
            Steps = 6_000,
            Method = TransientMethod.Trapezoidal,
        });

        Assert.True(result.Completed, result.Message);
        TransientMetrics metrics = Assert.IsType<TransientMetrics>(result.Metrics);
        TransientNodeMetrics output = Assert.Single(metrics.Nodes, node => node.NodeId == "out");

        // Six time constants leave 5*(1 - e^-6) = 4.98752 V, not 5 V.
        double expectedFinal = supply * (1 - Math.Exp(-6));
        Assert.Equal(0, output.MinimumVolts, 12);
        AssertClose(expectedFinal, output.MaximumVolts, 1e-6);
        AssertClose(expectedFinal, output.FinalVolts, 1e-6);
        // A monotonic charge peaks at its last point.
        Assert.Equal(result.FinalPoint!.Time, output.PeakTimeSeconds, 12);

        // It settles when 5*(e^(-t/tau) - e^(-6)) falls inside the band of its own final
        // value; the e^-6 term matters because the run stops at six time constants.
        double offset = Math.Exp(-result.Options.StopTime / tau);
        double expectedSettling = tau * Math.Log(
            supply / ((metrics.SettlingBand * expectedFinal) + (supply * offset)));
        Assert.NotNull(output.SettlingTimeSeconds);
        AssertClose(expectedSettling, output.SettlingTimeSeconds!.Value, 0.01);
    }

    [Fact]
    public void UnderdampedRlcReportsTheAnalyticOvershootAndQuarterPeriodPeak()
    {
        const double supply = 10;
        const double resistance = 100;
        const double inductance = 0.01;
        const double capacitance = 1e-6;
        double omega = 1 / Math.Sqrt(inductance * capacitance);
        double damping = (resistance / 2) * Math.Sqrt(capacitance / inductance);
        double damped = omega * Math.Sqrt(1 - (damping * damping));
        double expectedPeak = supply *
            (1 + Math.Exp(-Math.PI * damping / Math.Sqrt(1 - (damping * damping))));

        // Series RLC: an inductor straight across the observed node would short it.
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", supply)
            .AddResistor("R1", "in", "mid", resistance)
            .AddInductor("L1", "mid", "out", inductance)
            .AddCapacitor("C1", "out", "gnd", capacitance)
            .SetCapacitorInitialVoltage("C1", 0);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 4e-3,
            Steps = 4_000,
            Method = TransientMethod.Trapezoidal,
        });

        Assert.True(result.Completed, result.Message);
        TransientNodeMetrics output = Assert.Single(
            Assert.IsType<TransientMetrics>(result.Metrics).Nodes,
            node => node.NodeId == "out");

        AssertClose(expectedPeak, output.MaximumVolts, 2e-3);
        AssertClose(Math.PI / damped, output.PeakTimeSeconds, 0.03);
        // The envelope decays as exp(-zeta*omega*t); a 2% band of the swing is reached at
        // ln(1 / 0.02) / (zeta * omega), measured on the swing rather than the final value.
        Assert.NotNull(output.SettlingTimeSeconds);
        AssertClose(output.SettlingTimeSeconds!.Value, Math.Log(50) / (damping * omega), 0.25);
    }

    [Fact]
    public void APulsePeaksAtThePulseAndThenSettles()
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 0)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 0);

        // A waveform holds its last value, so the drive needs an explicit falling edge.
        netlist.SetWaveform("V1",
        [
            new TransientWaveformPoint(0, 0),
            new TransientWaveformPoint(4e-4, 5),
            new TransientWaveformPoint(8e-4, 5),
            new TransientWaveformPoint(1.2e-3, 0),
        ]);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 5e-3,
            Steps = 200,
            Method = TransientMethod.Trapezoidal,
        });

        Assert.True(result.Completed, result.Message);
        TransientNodeMetrics output = Assert.Single(
            Assert.IsType<TransientMetrics>(result.Metrics).Nodes,
            node => node.NodeId == "out");

        // The drive holds 5 V until 0.8 ms and then falls to zero, so the capacitor keeps
        // charging until the drive drops below its own voltage during that fall.
        Assert.InRange(output.PeakTimeSeconds, 7.9e-4, 1.2e-3);
        Assert.True(
            output.MaximumVolts > 2,
            $"the pulse should charge the capacitor: peak was {output.MaximumVolts} V");
        Assert.NotNull(output.SettlingTimeSeconds);
        Assert.True(output.SettlingTimeSeconds!.Value > 8e-4);
        Assert.True(output.SettlingTimeSeconds.Value < 5e-3);
    }

    [Fact]
    public void SettlingBandIsConfigurablePerRun()
    {
        const double supply = 5;
        const double tau = 1_000 * 1e-6;
        double expectedFinal = supply * (1 - Math.Exp(-6));
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", supply)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 0);
        var options = new TransientOptions
        {
            StopTime = 6 * tau,
            Steps = 2_000,
            Method = TransientMethod.Trapezoidal,
        };

        TransientResult tight = netlist.Run(options);
        TransientResult loose = netlist.Run(options with { SettlingBand = 0.1 });

        Assert.Equal(0.02, tight.Metrics!.SettlingBand);
        Assert.Equal(0.1, loose.Metrics!.SettlingBand);
        double tightSettling = tight.Metrics.Nodes
            .Single(node => node.NodeId == "out").SettlingTimeSeconds!.Value;
        double looseSettling = loose.Metrics.Nodes
            .Single(node => node.NodeId == "out").SettlingTimeSeconds!.Value;
        Assert.True(looseSettling < tightSettling, "a wider band must settle earlier");

        double expected = tau * Math.Log(
            supply / ((0.1 * expectedFinal) + (supply * Math.Exp(-6))));
        AssertClose(expected, looseSettling, 0.01);
    }

    [Fact]
    public void InvalidSettlingBandsAreRejected()
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6);
        var options = new TransientOptions { StopTime = 1e-3, Steps = 10 };

        Assert.Throws<ArgumentOutOfRangeException>(() => netlist.Run(options with { SettlingBand = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => netlist.Run(options with { SettlingBand = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => netlist.Run(options with { SettlingBand = double.NaN }));
    }

    [Fact]
    public void AFailedRunHasNoMetrics()
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddCapacitor("C1", "out", "gnd", 1e-6);

        TransientResult result = netlist.Run(new TransientOptions { StopTime = 1e-3, Steps = 10 });

        Assert.False(result.Completed);
        Assert.Null(result.Metrics);
    }

    [Fact]
    public void ReportJsonCarriesNodeMetrics()
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 0);
        TransientResult result = netlist.Run(new TransientOptions { StopTime = 0.01, Steps = 200 });

        using JsonDocument document = JsonDocument.Parse(
            TransientNetlistJson.ToReportJson("inline", netlist, result));
        JsonElement metrics = document.RootElement.GetProperty("metrics");

        Assert.Equal(0.02, metrics.GetProperty("settlingBand").GetDouble());
        JsonElement output = metrics.GetProperty("nodes")
            .EnumerateArray()
            .Single(node => node.GetProperty("id").GetString() == "out");
        Assert.True(output.GetProperty("maximumVolts").GetDouble() > 4);
        Assert.True(output.GetProperty("peakTimeSeconds").GetDouble() > 0);
    }

    private static void AssertClose(double expected, double actual, double relativeTolerance)
    {
        double tolerance = Math.Max(Math.Abs(expected) * relativeTolerance, 1e-12);
        Assert.True(
            Math.Abs(actual - expected) <= tolerance,
            $"expected {expected:G6}, measured {actual:G6} (tolerance {tolerance:G6})");
    }
}
