using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Gate capacitance belongs to the device: the gate has to be charged through whatever drives it
/// before the channel can follow. Driving the gate through a 10 kOhm resistor makes the resulting
/// constant a closed form, so a capacitance that never reached the solver would show up as an
/// instant rise rather than as a quietly plausible curve.
/// </summary>
public sealed class TransientGateChargeTests
{
    private const double StepAt = 1e-12;
    private const double GateResistance = 10_000;

    /// <summary>
    /// A gate driven through 10 kOhm. The threshold sits above the drive, so the channel stays off
    /// and the gate sees nothing but the resistor and its own capacitances.
    /// <para>
    /// The drain is tied to ground through 1 ohm rather than to an ideal rail. For a 20 ns gate
    /// transient that is indistinguishable from fixed, and it keeps the circuit out of a genuine
    /// dead end: the initial-condition solve turns every capacitor into an ideal voltage source,
    /// so a gate capacitatively tied to both rails would form a consistent but redundant source
    /// loop whose individual currents have no unique solution.
    /// </para>
    /// </summary>
    private static TransientNetlist GateDriven(double gateSourceFarads, double gateDrainFarads = 0) =>
        new TransientNetlist("gnd")
            .AddVoltageSource("VIN", "in", "gnd", 0)
            .SetWaveform("VIN", [new TransientWaveformPoint(0, 0), new TransientWaveformPoint(StepAt, 1)])
            .AddResistor("RG", "in", "g", GateResistance)
            .AddResistor("RD", "d", "gnd", 1)
            .AddMosfet(
                "M1",
                "d",
                "g",
                "gnd",
                DcMosfetChannel.NChannel,
                5,
                1e-3,
                0,
                gateSourceFarads,
                gateDrainFarads);

    private static TransientResult Run(TransientNetlist netlist)
    {
        TransientResult result = netlist.Run(new TransientOptions
        {
            StartTime = 0,
            StopTime = 200e-9,
            Steps = 4_000,
        });

        Assert.True(result.Failure is null, $"{result.Failure}: {result.Message}");
        Assert.True(result.Completed);
        return result;
    }

    /// <summary>
    /// The constant from where the gate crosses half of its step, <c>tau = (t_half - step) / ln 2</c>,
    /// so it needs neither the asymptote nor a settled run.
    /// </summary>
    private static double TimeConstant(TransientResult result)
    {
        TransientPoint crossing = result.Points.First(point =>
            point.Time > StepAt * 2 && point.NodeVoltages["g"] >= 0.5);
        return (crossing.Time - StepAt) / Math.Log(2);
    }

    [Fact]
    public void TheGateChargesThroughItsSeriesResistance()
    {
        // 10 kOhm into 1 pF is 10 ns; the drive is 1 V through the same resistor.
        TransientResult result = Run(GateDriven(1e-12));

        Assert.InRange(TimeConstant(result), 9.5e-9, 10.5e-9);
        Assert.Equal(1, result.Points[^1].NodeVoltages["g"], 3);

        // Without the capacitance the same gate is algebraic: it follows the drive at once.
        Assert.Equal(1, Run(GateDriven(0)).Points[^1].NodeVoltages["g"], 9);
    }

    [Fact]
    public void GateDrainCapacitanceAddsToTheDrivenLoad()
    {
        // The drain is held at the rail, so both plates of Cgd see an AC ground and the drive sees
        // 2 pF rather than 1 pF: the constant must double. The 1 ohm tie adds 1 ps, which is 5e-5
        // of the constant, so it cannot mask a missing capacitance.
        double sourceOnly = TimeConstant(Run(GateDriven(1e-12)));
        double both = TimeConstant(Run(GateDriven(1e-12, 1e-12)));

        Assert.InRange(both, 19e-9, 21e-9);
        Assert.InRange(both / sourceOnly, 1.9, 2.1);
    }

    [Fact]
    public void GateCapacitanceIsNotAnAuthoredElement()
    {
        TransientNetlist netlist = GateDriven(1e-12, 1e-12);

        // It carries state but is not part of the circuit the caller wrote.
        Assert.Empty(netlist.Capacitors);

        TransientResult result = Run(netlist);
        TransientPoint last = result.Points[^1];
        Assert.DoesNotContain(
            last.ElementVoltages.Keys,
            key => key.StartsWith('~'));
        Assert.DoesNotContain(
            last.ElementCurrents.Keys,
            key => key.StartsWith('~'));

        // The initial operating point is unchanged: capacitance stores no charge at DC.
        Assert.Equal(0, result.Points[0].NodeVoltages["g"], 12);
    }

    [Fact]
    public void GateCapacitanceCanBeGivenInANetlistFile()
    {
        TransientDocument document = TransientNetlistJson.LoadFromString("""
            {
              "format": "radix-transient-netlist", "version": 1, "ground": "gnd",
              "elements": [
                { "id": "VIN", "type": "voltage-source", "nodeA": "in", "nodeB": "gnd", "volts": 0,
                  "waveform": [ { "time": 0, "value": 0 }, { "time": 1e-12, "value": 1 } ] },
                { "id": "RG", "type": "resistor", "nodeA": "in", "nodeB": "g", "ohms": 10000 },
                { "id": "RD", "type": "resistor", "nodeA": "d", "nodeB": "gnd", "ohms": 1 },
                { "id": "M1", "type": "mosfet", "nodeA": "d", "nodeB": "gnd",
                  "drain": "d", "gate": "g", "source": "gnd", "channel": "n",
                  "thresholdVoltage": 5, "transconductance": 0.001,
                  "cgs": 1e-12, "cgd": 1e-12, "cds": 0.05e-12 }
              ],
              "analysis": { "start": 0, "stop": 2e-7, "steps": 4000 }
            }
            """);

        TransientOptions options = Assert.IsType<TransientOptions>(document.Analysis);
        TransientResult result = document.Netlist.Run(options);

        Assert.True(result.Failure is null, $"{result.Failure}: {result.Message}");
        Assert.Empty(document.Netlist.Capacitors);
        Assert.InRange(TimeConstant(result), 19e-9, 21e-9);
    }

    [Fact]
    public void ANegativeGateCapacitanceIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GateDriven(-1e-12));
        Assert.Throws<ArgumentOutOfRangeException>(() => GateDriven(1e-12, -1e-12));
        Assert.Throws<ArgumentOutOfRangeException>(() => GateDriven(double.NaN));
    }
}
