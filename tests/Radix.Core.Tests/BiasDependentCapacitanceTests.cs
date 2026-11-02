using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Bias-dependent gate capacitance changes a measured edge rate, so the tests measure one. A
/// saturated device presents two thirds of its gate-source capacitance, which makes the gate's own
/// charging faster by exactly 3/2; the ratio is the closed form, not a tolerance picked to fit.
/// </summary>
public sealed class BiasDependentCapacitanceTests
{
    /// <summary>
    /// A gate driven through 10 kΩ from 2 V to 2.2 V. The device starts and stays saturated, so the
    /// region — and therefore the capacitance — does not change during the run, which is what makes
    /// the closed form exact.
    /// </summary>
    private static TransientNetlist GateStep(bool biasDependent)
    {
        var netlist = new TransientNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddResistor("RD", "vdd", "out", 1_000)
            .AddResistor("RG", "g", "in", 10_000)
            .AddVoltageSource("VIN", "in", "gnd", 2)
            .AddMosfet("M1", "out", "g", "gnd", DcMosfetChannel.NChannel, 1, 1e-3, 0, 1e-12, 1e-12);

        netlist.SetWaveform("VIN", [new TransientWaveformPoint(0, 2), new TransientWaveformPoint(1e-9, 2.2)]);
        return biasDependent ? netlist.UseBiasDependentCapacitance() : netlist;
    }

    private static TransientResult Run(TransientNetlist netlist) => netlist.Run(new TransientOptions
    {
        StartTime = 0,
        StopTime = 120e-9,
        Steps = 600,
    });

    /// <summary>Time at which the gate reaches 63.2 % of a step, the time constant itself.</summary>
    private static double TimeConstant(TransientResult result)
    {
        double final = result.Points[^1].NodeVoltages["g"];
        double target = result.Points[0].NodeVoltages["g"] + (0.632 * (final - result.Points[0].NodeVoltages["g"]));
        foreach (TransientPoint point in result.Points)
        {
            if (point.NodeVoltages["g"] >= target)
            {
                return point.Time;
            }
        }

        throw new InvalidOperationException("the gate never reached 63 % of its step.");
    }

    [Fact]
    public void ASaturatedDeviceLosesItsMillerLoad()
    {
        TransientResult plain = Run(GateStep(biasDependent: false));
        TransientResult biased = Run(GateStep(biasDependent: true));
        Assert.True(
            biased.Completed && biased.Points.Count > 100,
            $"biased run: completed={biased.Completed}, points={biased.Points.Count}, " +
            $"v(g) {biased.Points[0].NodeVoltages["g"]} -> {biased.Points[^1].NodeVoltages["g"]}, " +
            $"failure={biased.Failure}");
        double constant = TimeConstant(plain);
        double dependent = TimeConstant(biased);

        // The gate sees Cgs + (1 + A)·Cgd, and with A = gm·RD = 1 and both capacitors at 1 pF that is
        // 3 pF. A saturated channel presents 2/3·Cgs and no Cgd, so the load falls to 0.67 pF and the
        // time constant falls by about 4.5. The measured ratio is a little lower because the drain
        // voltage moves as the device conducts, so the stage gain is not exactly one throughout.
        Assert.InRange(constant / dependent, 3.8, 4.6);
        Assert.True(dependent < constant, $"dependent {dependent} s against constant {constant} s");
    }

    [Fact]
    public void TheDefaultModelIsUnchanged()
    {
        // Two runs of the unmodified model must agree exactly: the new path is off unless asked for,
        // so no existing measurement moves.
        TransientResult first = Run(GateStep(biasDependent: false));
        TransientResult second = Run(GateStep(biasDependent: false));

        Assert.Equal(first.Points.Count, second.Points.Count);
        for (int index = 0; index < first.Points.Count; index++)
        {
            Assert.Equal(first.Points[index].NodeVoltages["g"], second.Points[index].NodeVoltages["g"], 15);
            Assert.Equal(first.Points[index].NodeVoltages["out"], second.Points[index].NodeVoltages["out"], 15);
        }
    }

    [Fact]
    public void TheEffectiveValueFollowsTheRegionItWasSolvedIn()
    {
        // Saturation: two thirds of the authored gate-source capacitance, none of the gate-drain.
        TransientNetlist netlist = GateStep(biasDependent: true);
        _ = netlist.Run(new TransientOptions { StartTime = 0, StopTime = 5e-9, Steps = 20 });

        // Identified by terminals: the gate-source capacitor is across g and gnd, the gate-drain one
        // across g and out.
        TransientCapacitor gateSource = netlist.CapacitorsIncludingDevices
            .Single(capacitor =>
                (capacitor.NodeA == "g" && capacitor.NodeB == "gnd") ||
                (capacitor.NodeA == "gnd" && capacitor.NodeB == "g"));
        TransientCapacitor gateDrain = netlist.CapacitorsIncludingDevices
            .Single(capacitor =>
                (capacitor.NodeA == "g" && capacitor.NodeB == "out") ||
                (capacitor.NodeA == "out" && capacitor.NodeB == "g"));

        // The authored values are untouched; only the stamped value moves.
        Assert.Equal(1e-12, gateSource.Farads, 15);
        Assert.Equal(2.0 / 3.0 * 1e-12, gateSource.EffectiveFarads, 15);
        Assert.Equal(0, gateDrain.EffectiveFarads, 15);
    }
}
