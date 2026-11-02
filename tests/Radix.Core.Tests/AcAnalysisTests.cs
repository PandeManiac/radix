using System.Numerics;
using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Frequency-domain analysis, checked against the closed forms that define it: an RC corner at
/// <c>1/(2πRC)</c> where the magnitude is 1/√2 and the phase is −45°, an RL corner at
/// <c>R/(2πL)</c>, a resonance at <c>1/(2π√(LC))</c>, and a common-source gain of <c>gm·RD</c>.
/// A solver that got the assembly or the complex arithmetic wrong would miss these by more than a
/// rounding error, so they are the whole verification.
/// </summary>
public sealed class AcAnalysisTests
{
    private const double Tolerance = 1e-9;

    private static TransientNetlist RcLowPass() =>
        new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 1)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6);

    private static TransientNetlist RlLowPass() =>
        new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 1)
            .AddResistor("R1", "in", "out", 1_000)
            .AddInductor("L1", "out", "gnd", 0.1);

    private static Complex At(AcResponse response, double hertz, string node) =>
        response.Points.First(point => Math.Abs(point.FrequencyHertz - hertz) <= 1e-6)
            .NodeVoltages[node];

    [Fact]
    public void TheRcCornerIsAtOneOverTwoPiRc()
    {
        // R = 1 kOhm, C = 1 uF gives 159.15 Hz, where the magnitude is 1/sqrt(2) and the phase is
        // -45 degrees: the two numbers that define a single-pole corner.
        double corner = 1 / (2 * Math.PI * 1_000 * 1e-6);
        AcResponse response = AcAnalysis.Run(RcLowPass(), "V1", [corner]);

        Complex output = At(response, corner, "out");
        Assert.Equal(1 / Math.Sqrt(2), output.Magnitude, 9);
        Assert.Equal(-45, AcResponse.PhaseDegrees(output), 6);

        // A decade either side is the passband and the stopband.
        Assert.InRange(At(response = AcAnalysis.Run(RcLowPass(), "V1", [corner / 10]), corner / 10, "out").Magnitude, 0.99, 1);
        Assert.InRange(At(AcAnalysis.Run(RcLowPass(), "V1", [corner * 10]), corner * 10, "out").Magnitude, 0.09, 0.1);
    }

    [Fact]
    public void TheRlCornerIsAtROverTwoPiL()
    {
        // An inductor to ground makes the same single pole at R/(2πL) = 1591.5 Hz, but the phase at
        // the corner is +45, not -45: the divider is jωL/(R + jωL), which is jR/(R + jR) = (1+j)/2
        // there. The RC case above gives -45, so the pair fixes the sign convention of the
        // imaginary part rather than accepting either sign as "about a corner".
        double corner = 1_000 / (2 * Math.PI * 0.1);
        Complex output = At(AcAnalysis.Run(RlLowPass(), "V1", [corner]), corner, "out");

        Assert.Equal(1 / Math.Sqrt(2), output.Magnitude, 9);
        Assert.Equal(45, AcResponse.PhaseDegrees(output), 6);
    }

    [Fact]
    public void AParallelTankResonatesAtItsNaturalFrequency()
    {
        // R in series into a parallel LC: the node peaks where the tank impedance is highest, at
        // 1/(2π sqrt(LC)) = 5033 Hz, and the phase passes through zero there.
        var netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 1)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-7)
            .AddInductor("L1", "out", "gnd", 0.01);

        double resonant = 1 / (2 * Math.PI * Math.Sqrt(1e-7 * 0.01));
        AcResponse response = AcAnalysis.Run(
            netlist, "V1", [resonant / 10, resonant, resonant * 10]);

        double peak = At(response, resonant, "out").Magnitude;
        Assert.True(peak > 5 * At(response, resonant / 10, "out").Magnitude, $"peak {peak}");
        Assert.True(peak > 5 * At(response, resonant * 10, "out").Magnitude, $"peak {peak}");
        Assert.InRange(Math.Abs(AcResponse.PhaseDegrees(At(response, resonant, "out"))), 0, 0.01);
    }

    [Fact]
    public void AReductiveDividerHasTheDcRatioAtEveryFrequency()
    {
        // With no reactance the response must be flat and equal to the DC divider: 1/3 of the drive,
        // in phase. This catches a drive stamped at the wrong amplitude or sign.
        var netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 1)
            .AddResistor("R1", "in", "out", 1_000)
            .AddResistor("R2", "out", "gnd", 500);

        AcResponse response = AcAnalysis.Run(netlist, "V1", [1, 1e3, 1e9]);
        Assert.All(
            response.Points,
            point =>
            {
                Assert.Equal(1.0 / 3, point.NodeVoltages["out"].Magnitude, 9);
                Assert.Equal(0, AcResponse.PhaseDegrees(point.NodeVoltages["out"]), 9);
            });
    }

    [Fact]
    public void ACommonSourceHasItsTransconductanceGainAndRollsOffWithTheDevice()
    {
        // Bias the gate at 2 V so the device sits with 1 V of overdrive: gm = K * Vov = 1 mA/V, and
        // the drain resistor is 1 kOhm, so the low-frequency gain is 1. With 1 pF of drain-source
        // capacitance against 1 kOhm the corner is at 1/(2π * 1k * 2p) and the gain must halve there.
        var netlist = new TransientNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VIN", "in", "gnd", 2)
            .AddResistor("RD", "vdd", "out", 1_000)
            .AddMosfet("M1", "out", "in", "gnd", DcMosfetChannel.NChannel, 1, 1e-3, 0, 0, 0, 1e-12);

        double corner = 1 / (2 * Math.PI * 1_000 * 1e-12);
        AcResponse response = AcAnalysis.Run(netlist, "VIN", [corner / 1_000, corner]);

        // A common-source stage inverts, so the gain is negative: magnitude 1, phase near 180.
        Complex low = At(response, corner / 1_000, "out");
        Assert.InRange(low.Magnitude, 0.9, 1.1);
        Assert.InRange(AcResponse.PhaseDegrees(low), 179, 181);
        Assert.InRange(At(response, corner, "out").Magnitude / low.Magnitude, 0.6, 0.8);
    }

    [Fact]
    public void TheTernaryGateGainAgreesWithItsDcSlope()
    {
        // Two independent roads to the same derivative: the AC magnitude far below every pole, and
        // the difference quotient of a DC sweep, which sees capacitors open — exactly the limit the
        // AC solve should reproduce. If either the complex assembly or the linearization were wrong,
        // these two numbers would disagree.
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix"));
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);

        // Bias on the lower transition, where the gate amplifies; at 2.5 V it sits on its plateau.
        // The waveform has to go: an operating point takes each source at its waveform's value at
        // t = 0, which here is 0 V, so holding the drive at the switching point means saying so.
        ElectricalComponent drive = circuit.FindComponent("VIN")!;
        drive.Waveform.Clear();
        drive.Value = 1.3;

        Complex output = AcAnalysis.Run(circuit.ToTransientNetlist(), "VIN", [1]).Points[0]
            .NodeVoltages["out"];

        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(
            circuit.ToDcNetlist(), "VIN", DcSweep.Linspace(1.25, 1.35, 11));
        DcSweepGainPoint middle = DcSweepGain.Along(rows, "out")[5];

        Assert.NotNull(middle.Gain);
        Assert.True(Math.Abs(middle.Gain!.Value) > 2, $"dc gain {middle.Gain}");
        Assert.InRange(output.Magnitude / Math.Abs(middle.Gain.Value), 0.85, 1.15);

        // An inverting stage: the output is 180 degrees from the drive.
        Assert.InRange(Math.Abs(AcResponse.PhaseDegrees(output)), 170, 190);
    }

    [Fact]
    public void TheDeviceReportNamesTheSlopesTheGainIsBuiltFrom()
    {
        // Same stage as the common-source case: K = 1 mA/V^2, Vth = 1 V, gate at 2 V, so the
        // overdrive is 1 V and gm = K * Vov = 1 mA/V. A report that did not match that closed form
        // would not be describing the device the gain was assembled from.
        var netlist = new TransientNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VIN", "in", "gnd", 2)
            .AddResistor("RD", "vdd", "out", 1_000)
            .AddMosfet("M1", "out", "in", "gnd", DcMosfetChannel.NChannel, 1, 1e-3, 0, 0, 0, 1e-12);

        AcDeviceReport device = Assert.Single(AcAnalysis.Run(netlist, "VIN", [10]).Devices);

        Assert.Equal("M1", device.Id);
        Assert.Equal(2, device.GateSourceVolts, 9);
        Assert.Equal(1e-3, device.Transconductance, 12);

        // Level 1 without channel-length modulation has no drain conductance beyond GMIN, so the
        // device's intrinsic gain is enormous: the stage's gain is set by the resistor alone.
        Assert.InRange(device.DrainConductance, 0, 1.01e-12);

        // The only drain conductance left is GMIN (1e-12 S), so the intrinsic gain is gm/GMIN =
        // 1e9: large, but a definite number rather than the infinite ratio a model without GMIN
        // would claim. The stage's own gain is set by its resistor, far below this ceiling.
        Assert.InRange(device.IntrinsicGain, 9.9e8, 1.01e9);
    }

    [Fact]
    public void ACutOffDeviceIsReportedInAnotherRegionWithNoGain()
    {
        static TransientNetlist Stage(double gateVolts) =>
            new TransientNetlist("gnd")
                .AddVoltageSource("VDD", "vdd", "gnd", 5)
                .AddVoltageSource("VIN", "in", "gnd", gateVolts)
                .AddResistor("RD", "vdd", "out", 1_000)
                .AddMosfet("M1", "out", "in", "gnd", DcMosfetChannel.NChannel, 1, 1e-3);

        AcDeviceReport on = Assert.Single(AcAnalysis.Run(Stage(2), "VIN", [10]).Devices);
        AcDeviceReport off = Assert.Single(AcAnalysis.Run(Stage(0.5), "VIN", [10]).Devices);

        // Below the threshold the device is in a different region and contributes no
        // transconductance, so an amplifier built on it has no gain to speak of. The region names
        // are the model's own, so the test compares the two classifications rather than spelling
        // them; a report that always said one region would fail here.
        Assert.NotEqual(on.Region, off.Region);
        Assert.InRange(off.Transconductance, -1e-15, 1e-15);
        Assert.True(on.Transconductance > 100 * Math.Abs(off.Transconductance));
    }

    [Fact]
    public void ASweepIsLogarithmicAndBounded()
    {
        IReadOnlyList<double> decade = AcFrequencySweep.Logarithmic(10, 1_000, 3);
        Assert.Equal(10, decade[0], 9);
        Assert.Equal(100, decade[1], 9);
        Assert.Equal(1_000, decade[2], 9);

        Assert.Throws<ArgumentOutOfRangeException>(() => AcFrequencySweep.Logarithmic(0, 1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcFrequencySweep.Logarithmic(10, 5, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcFrequencySweep.Logarithmic(1, 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AcFrequencySweep.Logarithmic(1, 2, AcFrequencySweep.MaximumCount + 1));
    }

    [Fact]
    public void TheDriveMustBeAVoltageSourceAndTheFrequenciesPositive()
    {
        TransientNetlist netlist = RcLowPass();

        Assert.Throws<ArgumentException>(() => AcAnalysis.Run(netlist, "R1", [10]));
        Assert.Throws<ArgumentException>(() => AcAnalysis.Run(netlist, "missing", [10]));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcAnalysis.Run(netlist, "V1", [0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcAnalysis.Run(netlist, "V1", [10], 0));
        Assert.Throws<ArgumentException>(() => AcAnalysis.Run(netlist, "V1", []));
    }
}
