using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Independent checks for the Shichman-Hodges MOSFET model. Every expected value is
/// derived in the comment beside it from the current law rather than from a previous
/// run of the solver, so a change in the model cannot silently redefine the answer.
/// </summary>
public sealed class MosfetSolverTests
{
    private const double K = 1e-3;

    private static void AssertClose(double expected, double actual, double tolerance = 1e-6)
    {
        double scale = Math.Max(Math.Abs(expected), 1e-12);
        Assert.InRange(actual, expected - (tolerance * scale), expected + (tolerance * scale));
    }

    /// <summary>
    /// An n-channel enhancement device with a 1 kΩ resistor to a 5 V rail: the standard
    /// common-source stage. Its operating point has a closed form in each region.
    /// </summary>
    private static DcNetlist NmosLoadStage(double gateVolts, double threshold = 1, double lambda = 0) =>
        new DcNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VG", "gate", "gnd", gateVolts)
            .AddResistor("RL", "vdd", "out", 1_000)
            .AddMosfet("M1", "out", "gate", "gnd", DcMosfetChannel.NChannel, threshold, K, lambda);

    [Fact]
    public void NmosBelowThresholdConductsOnlyTheLeakageFloor()
    {
        // Vov = 0.5 - 1 = -0.5 < 0, so the channel is off. Only the 1 pS numerical
        // leakage remains: 1e-12 S * 5 V = 5 pA, which drops 5 pV across 1 kΩ.
        DcSolution solution = NmosLoadStage(gateVolts: 0.5).Solve();

        AssertClose(5, solution.NodeVoltages["out"]);
        AssertClose(5e-12, solution.ElementCurrents["M1"]);
        Assert.Equal(
            DcMosfetRegion.Cutoff,
            ((DcMosfet)NmosLoadStage(0.5).Elements[3]).Evaluate(5, 0.5).Region);
    }

    [Fact]
    public void NmosSaturationOperatingPointMatchesTheSquareLaw()
    {
        // Vgs = 2 V, Vt = 1 V, so Vov = 1 V. Assume saturation: Id = K*Vov^2/2
        //   = 0.5 mA, Vout = 5 - 0.5 mA * 1 kΩ = 4.5 V.
        // Vds = 4.5 V > Vov = 1 V, so the assumption is self-consistent.
        DcSolution solution = NmosLoadStage(gateVolts: 2).Solve();

        AssertClose(4.5, solution.NodeVoltages["out"], 1e-7);
        AssertClose(5e-4, solution.ElementCurrents["M1"], 1e-6);
    }

    [Fact]
    public void NmosTriodeOperatingPointMatchesTheSquareLaw()
    {
        // Vgs = 5 V, so Vov = 4 V. In triode, Id = K(Vov*Vds - Vds^2/2) and the load
        // gives Id = (5 - Vds)/1kΩ. Eliminating Id: Vds^2 - 10 Vds + 10 = 0, whose
        // physical root is 5 - sqrt(15) = 1.127017 V. Check Vds < Vov: 1.127 < 4.
        double expected = 5 - Math.Sqrt(15);
        DcSolution solution = NmosLoadStage(gateVolts: 5).Solve();

        AssertClose(expected, solution.NodeVoltages["out"], 1e-7);
        AssertClose((5 - expected) / 1_000, solution.ElementCurrents["M1"], 1e-6);
    }

    [Fact]
    public void ChannelLengthModulationChangesSaturationCurrentByTheDocumentedFactor()
    {
        // With lambda = 0.02 1/V and Vds = 4.5 V the saturation current is multiplied
        // by (1 + 0.02 * 4.5) = 1.09 relative to the lambda = 0 case: 0.545 mA.
        DcSolution solution = NmosLoadStage(gateVolts: 2, lambda: 0.02).Solve();

        // Solve 5 - Vds = 1000 * 0.5 mA * 1.09 * ... iteratively in closed form is not
        // required here; instead assert the current law holds at the reported point.
        double vds = solution.NodeVoltages["out"];
        double expectedCurrent = 0.5 * K * 1 * 1 * (1 + (0.02 * vds));
        AssertClose(expectedCurrent, solution.ElementCurrents["M1"], 1e-6);
        Assert.InRange(solution.ElementCurrents["M1"], 5.4e-4, 5.6e-4);
    }

    [Fact]
    public void PmosOperatingPointMirrorsTheNmosStage()
    {
        // A p-channel device with its source on the 5 V rail, |Vtp| = 1 V and Vg = 3 V
        // has |Vov| = 1 V, so it conducts 0.5 mA into a 1 kΩ load: Vout = 0.5 V.
        // |Vds| = 4.5 V > |Vov|, so it is saturated as assumed.
        DcSolution solution = new DcNetlist("gnd")
            .AddVoltageSource("VSS", "vdd", "gnd", 5)
            .AddVoltageSource("VG", "gate", "gnd", 3)
            .AddResistor("RL", "out", "gnd", 1_000)
            .AddMosfet("M1", "out", "gate", "vdd", DcMosfetChannel.PChannel, 1, K)
            .Solve();

        AssertClose(0.5, solution.NodeVoltages["out"], 1e-7);
        AssertClose(-5e-4, solution.ElementCurrents["M1"], 1e-5);
    }

    [Fact]
    public void ReversedDrainAndSourceConductWithTheCorrectSign()
    {
        // V(drain) = 0, V(source) = 2, V(gate) = 5 gives Vgs = 3 and Vds = -2, so the
        // terminals act as an n-channel device with Vgs' = 5, Vds' = 2: Vov' = 4 and
        // Vds' < Vov', hence Id' = K(4*2 - 2) = 6 mA. Reported drain-to-source current
        // is therefore -6 mA: the device sources 6 mA into the grounded drain.
        DcSolution solution = new DcNetlist("gnd")
            .AddVoltageSource("VS", "src", "gnd", 2)
            .AddVoltageSource("VG", "gate", "gnd", 5)
            .AddMosfet("M1", "gnd", "gate", "src", DcMosfetChannel.NChannel, 1, K)
            .Solve();

        AssertClose(-6e-3, solution.ElementCurrents["M1"], 1e-5);
        // Absorbed power is Vds * Id = (-2) * (-6 mA) = 12 mW for the channel.
        AssertClose(12e-3, solution.ElementPowers["M1"], 1e-4);
        // The 2 V source delivers exactly that power.
        AssertClose(-12e-3, solution.ElementPowers["VS"], 1e-4);
    }

    [Fact]
    public void ResistorLoadStageConservesPower()
    {
        // KCL makes the algebraic sum of absorbed element power zero for any solved
        // operating point. The 5 V rail must deliver what the resistor and channel absorb.
        DcSolution solution = NmosLoadStage(gateVolts: 3).Solve();
        double total = solution.ElementPowers.Values.Sum();

        double scale = solution.ElementPowers.Values.Sum(Math.Abs);
        Assert.InRange(Math.Abs(total), 0, 1e-9 * Math.Max(1, scale));
    }

    [Fact]
    public void ExtremelyWideTransistorStillConverges()
    {
        // A 1e-1 A/V^2 device driving 1 kΩ from a 5 V rail is a strong switch, not a
        // small-signal amplifier; Newton must still find the operating point rather
        // than overflow or oscillate.
        DcSolution solution = new DcNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VG", "gate", "gnd", 5)
            .AddResistor("RL", "vdd", "out", 1_000)
            .AddMosfet("M1", "out", "gate", "gnd", DcMosfetChannel.NChannel, 0.7, 0.1)
            .Solve();

        Assert.InRange(solution.NodeVoltages["out"], 0, 0.2);
        AssertClose((5 - solution.NodeVoltages["out"]) / 1_000, solution.ElementCurrents["M1"], 1e-6);
    }

    [Fact]
    public void GateNodeWithNoConductivePathIsReportedAsFloating()
    {
        // The gate oxide conducts nothing, so a node attached only to gates has no DC
        // operating point. Reporting it is the honest answer; inventing a gate voltage
        // would fabricate a result.
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddResistor("RL", "vdd", "out", 1_000)
            .AddMosfet("M1", "out", "gate", "gnd", DcMosfetChannel.NChannel, 1, K);

        DcSolveException exception = Assert.Throws<DcSolveException>(() => netlist.Solve());
        Assert.Equal(DcSolveFailure.FloatingNetwork, exception.Failure);
        Assert.Contains("gate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidMosfetParametersAreRejectedBeforeAnySolve()
    {
        DcNetlist Netlist() => new DcNetlist("gnd").AddVoltageSource("V1", "vdd", "gnd", 5);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Netlist().AddMosfet("M1", "d", "g", "s", DcMosfetChannel.NChannel, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Netlist().AddMosfet("M1", "d", "g", "s", DcMosfetChannel.NChannel, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Netlist().AddMosfet("M1", "d", "g", "s", DcMosfetChannel.NChannel, 1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Netlist().AddMosfet("M1", "d", "g", "s", DcMosfetChannel.NChannel, double.PositiveInfinity, K));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Netlist().AddMosfet("M1", "d", "g", "s", DcMosfetChannel.NChannel, 1, K, -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Netlist().AddMosfet("M1", "d", "g", "s", (DcMosfetChannel)7, 1, K));
        Assert.Throws<ArgumentException>(() =>
            Netlist().AddMosfet("M1", "d", " ", "s", DcMosfetChannel.NChannel, 1, K));
        Assert.Throws<ArgumentException>(() =>
            Netlist().AddMosfet("M1", "d", "g", "d", DcMosfetChannel.NChannel, 1, K));
    }

    /// <summary>
    /// A static CMOS inverter: the p-channel device pulls up, the n-channel pulls down,
    /// and both gates are driven by the same input source.
    /// </summary>
    private static DcNetlist CmosInverter(double inputVolts) =>
        new DcNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VIN", "in", "gnd", inputVolts)
            .AddMosfet("MP", "out", "in", "vdd", DcMosfetChannel.PChannel, 1, K, 0.02)
            .AddMosfet("MN", "out", "in", "gnd", DcMosfetChannel.NChannel, 1, K, 0.02);

    [Fact]
    public void CmosInverterSwingsRailToRailWithNegligibleStaticCurrent()
    {
        DcSolution low = CmosInverter(0).Solve();
        DcSolution high = CmosInverter(5).Solve();

        Assert.True(low.NodeVoltages["out"] > 4.999, $"expected a high output at 0 V, got {low.NodeVoltages["out"]}");
        Assert.True(high.NodeVoltages["out"] < 1e-3, $"expected a low output at 5 V, got {high.NodeVoltages["out"]}");

        // At either rail one device is cut off, so the only current is the leakage floor.
        Assert.InRange(Math.Abs(low.ElementCurrents["VDD"]), 0, 1e-9);
        Assert.InRange(Math.Abs(high.ElementCurrents["VDD"]), 0, 1e-9);
    }

    [Fact]
    public void CmosInverterSwitchesAtHalfTheSupplyForMatchedDevices()
    {
        // With equal K, equal threshold magnitudes and lambda, the two devices conduct
        // identical saturation currents at Vin = VDD/2, so the output settles there.
        DcSolution solution = CmosInverter(2.5).Solve();

        AssertClose(2.5, solution.NodeVoltages["out"], 2e-3);
    }

    [Fact]
    public void CmosInverterTransferCurveIsMonotoneAndInverting()
    {
        // A 101-point DC sweep at 50 mV spacing: decreasing output, and a transition
        // confined to the middle of the input range rather than a hardcoded rail value.
        var sweep = Enumerable.Range(0, 101)
            .Select(index => index * 0.05)
            .Select(volts => (Input: volts, Output: CmosInverter(volts).Solve().NodeVoltages["out"]))
            .ToArray();

        // The leakage floor is a nanovolt, so monotonicity is asserted beyond that
        // floor rather than at the last bit of two nearly identical rail values.
        for (int index = 1; index < sweep.Length; index++)
        {
            Assert.True(
                sweep[index].Output <= sweep[index - 1].Output + 1e-6,
                $"output rose between {sweep[index - 1].Input} V and {sweep[index].Input} V");
        }

        Assert.True(sweep[0].Output > 4.9, $"expected a high output at 0 V, got {sweep[0].Output}");
        Assert.True(sweep[^1].Output < 0.1, $"expected a low output at 5 V, got {sweep[^1].Output}");

        // The transition is a real analog edge with finite gain, not a fabricated
        // rail assignment: measure the largest slope the sweep actually produced.
        double maximumGain = 0;
        for (int index = 1; index < sweep.Length; index++)
        {
            double gain = Math.Abs(sweep[index].Output - sweep[index - 1].Output) /
                (sweep[index].Input - sweep[index - 1].Input);
            maximumGain = Math.Max(maximumGain, gain);
        }

        Assert.InRange(maximumGain, 10, 200);
        // Both shoulders still conduct: the output is mid-scale somewhere strictly
        // between the rails, which a behavioural inverter could not produce.
        Assert.Contains(sweep, row => row.Output > 0.5 && row.Output < 4.5);
    }

    [Fact]
    public void MosfetNetlistRoundTripsThroughTheVersionedJsonFormat()
    {
        const string json = """
            {
              "format": "radix-dc-netlist",
              "version": 1,
              "ground": "gnd",
              "elements": [
                { "id": "VDD", "type": "voltage-source", "nodeA": "vdd", "nodeB": "gnd", "volts": 5 },
                { "id": "VG", "type": "voltage-source", "nodeA": "gate", "nodeB": "gnd", "volts": 2 },
                { "id": "RL", "type": "resistor", "nodeA": "vdd", "nodeB": "out", "ohms": 1000 },
                { "id": "M1", "type": "mosfet", "drain": "out", "gate": "gate", "source": "gnd",
                  "channel": "n", "thresholdVoltage": 1, "transconductance": 0.001,
                  "channelLengthModulation": 0.02 }
              ]
            }
            """;

        DcNetlist netlist = DcNetlistJson.LoadFromString(json);
        DcMosfet mosfet = Assert.IsType<DcMosfet>(netlist.Elements[3]);
        Assert.Equal(DcMosfetChannel.NChannel, mosfet.Channel);
        Assert.Equal(1, mosfet.ThresholdVoltage);
        Assert.Equal(0.001, mosfet.Transconductance);
        Assert.Equal(0.02, mosfet.ChannelLengthModulation);

        DcSolution solution = netlist.Solve();
        string report = DcNetlistJson.ToReportJson("inline", netlist, solution);
        Assert.Contains("\"kind\": \"mosfet\"", report, StringComparison.Ordinal);
        Assert.Contains("\"gate\": \"gate\"", report, StringComparison.Ordinal);

        // Vgs = 2 V gives Vov = 1 V, so Id = 0.5 mA (1 + 0.02 Vds) and the load enforces
        // Vds = 5 - 1000 Id. Substituting: Vds = 4.5 - 0.01 Vds, so Vds = 4.5 / 1.01.
        AssertClose(4.5 / 1.01, solution.NodeVoltages["out"], 1e-9);
    }

    [Fact]
    public void MosfetJsonRejectsConflictingOrMissingFields()
    {
        string Template(string body) => $$"""
            {
              "format": "radix-dc-netlist",
              "version": 1,
              "ground": "gnd",
              "elements": [ {{body}} ]
            }
            """;

        const string valid = """
            { "id": "M1", "type": "mosfet", "drain": "d", "gate": "g", "source": "s",
              "channel": "n", "thresholdVoltage": 1, "transconductance": 0.001 }
            """;

        // A two-terminal field on a three-terminal device is a mistake, not an extra.
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString(Template(
            """{ "id": "M1", "type": "mosfet", "nodeA": "d", "nodeB": "s", "gate": "g", "channel": "n", "thresholdVoltage": 1, "transconductance": 0.001 }""")));
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString(Template(
            """{ "id": "M1", "type": "mosfet", "drain": "d", "gate": "g", "channel": "n", "thresholdVoltage": 1, "transconductance": 0.001 }""")));
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString(Template(
            """{ "id": "M1", "type": "mosfet", "drain": "d", "gate": "g", "source": "s", "channel": "x", "thresholdVoltage": 1, "transconductance": 0.001 }""")));
        // A mosfet-only field on a resistor is rejected by the same strictness.
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString(Template(
            """{ "id": "R1", "type": "resistor", "nodeA": "a", "nodeB": "gnd", "ohms": 1000, "thresholdVoltage": 1 }""")));

        DcNetlist loaded = DcNetlistJson.LoadFromString(Template(valid));
        Assert.IsType<DcMosfet>(Assert.Single(loaded.Elements));
    }

    [Fact]
    public void AuthoredCircuitWithMosfetSolvesThroughTheTransientBridge()
    {
        // The transient netlist copies every static element into each step, so a MOSFET
        // must survive that copy and still integrate. A 1 µF load and a 1 kΩ pull-up
        // with a 2 V gate gives a 0.5 mA charging current on top of the resistive one.
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VG", "gate", "gnd", 2)
            .AddResistor("RL", "vdd", "out", 1_000)
            .AddMosfet("M1", "out", "gate", "gnd", DcMosfetChannel.NChannel, 1, K, 0.02)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 5);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StartTime = 0,
            StopTime = 5e-3,
            Steps = 2_000,
            Method = TransientMethod.BackwardEuler,
        });

        Assert.True(result.Completed);
        // The capacitor discharges toward the 4.455 V operating point the DC solver finds
        // for this stage (Vov = 1 V, lambda = 0.02).
        Assert.InRange(result.Points[^1].NodeVoltages["out"], 4.40, 4.48);
        Assert.True(result.Points[0].NodeVoltages["out"] > result.Points[^1].NodeVoltages["out"]);
    }
}
