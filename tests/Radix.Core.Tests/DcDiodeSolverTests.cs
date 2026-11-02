using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Nonlinear diode operating-point tests. Expected voltages come from an
/// independent scalar bisection of the Shockley equation, never from the MNA
/// solver being tested.
/// </summary>
public sealed class DcDiodeSolverTests
{
    private const double DefaultIs = 1e-12;

    [Fact]
    public void ForwardDiodeWithSeriesResistorMatchesIndependentBisection()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddDiode("D1", "out", "gnd", DefaultIs);

        DcSolution solution = netlist.Solve();

        DcDiode diode = Assert.IsType<DcDiode>(netlist.Elements[2]);
        double expected = BisectForwardVoltage(5, 1_000, diode);
        AssertClose(expected, solution.NodeVoltages["out"], 1e-6);
        Assert.InRange(expected, 0.4, 0.8);

        double current = (5 - solution.NodeVoltages["out"]) / 1_000;
        AssertClose(current, solution.ElementCurrents["R1"], 1e-6);
        AssertClose(current, solution.ElementCurrents["D1"], 1e-6);
        AssertClose(current, diode.CurrentAt(solution.ElementVoltages["D1"]), 1e-6);
        Assert.True(solution.RelativeResidual <= 1);
    }

    [Fact]
    public void IndependentLargeVoltageSourceBranchCurrentDoesNotLoosenDiodeVoltageConvergence()
    {
        const double sourceVoltage = 1;
        const double resistance = 1e30;
        const double saturationCurrent = 1e-40;
        const double unrelatedCurrent = 1e12;
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("Vbias", "in", "gnd", sourceVoltage)
            .AddResistor("Rbias", "in", "out", resistance)
            .AddDiode("D1", "out", "gnd", saturationCurrent)
            .AddVoltageSource("Vstress", "stress", "gnd", 1)
            .AddCurrentSource("Istress", "stress", "gnd", unrelatedCurrent)
            .AddResistor("Rstress", "stress", "gnd", 1);

        DcSolution solution = netlist.Solve();
        DcDiode diode = Assert.IsType<DcDiode>(netlist.Elements[2]);
        double expected = BisectForwardVoltage(sourceVoltage, resistance, diode);

        AssertClose(expected, solution.NodeVoltages["out"], 1e-7);
        Assert.InRange(Math.Abs(solution.NodeVoltages["out"] - expected), 0, 1e-7);
        Assert.True(Math.Abs(solution.ElementCurrents["Vstress"]) > unrelatedCurrent * 0.99);
        Assert.True(solution.RelativeResidual <= 1);
    }

    [Fact]
    public void SeriesResistorJunctionSolveConvergesAcrossExtremeVoltageScales()
    {
        const double terminalVoltage = 1e100;
        const double seriesResistance = 1e308;
        const double saturationCurrent = 1e-12;
        DcDiode diode = Assert.IsType<DcDiode>(new DcNetlist("gnd")
            .AddDiode("D1", "a", "gnd", saturationCurrent, seriesResistance: seriesResistance)
            .Elements[0]);

        double actual = diode.JunctionVoltageFor(terminalVoltage);
        double smallSignalReference = terminalVoltage * diode.JunctionVoltage /
            (seriesResistance * saturationCurrent);

        AssertClose(smallSignalReference, actual, 1e-6);
        AssertClose((terminalVoltage - actual) / seriesResistance, diode.CurrentAt(terminalVoltage), 1e-6);
    }

    [Fact]
    public void ReverseBiasedDiodePassesOnlySaturationCurrent()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", -5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddDiode("D1", "out", "gnd", DefaultIs);

        DcSolution solution = netlist.Solve();

        Assert.InRange(solution.NodeVoltages["out"], -5.000001, -4.999);
        AssertClose(-DefaultIs, solution.ElementCurrents["D1"], 1e-3);
        AssertClose(-DefaultIs, solution.ElementCurrents["R1"], 1e-3);
        Assert.True(solution.ElementPowers["D1"] > 0);
    }

    [Fact]
    public void ReverseBreakdownDiodeMatchesIndependentScalarBisectionAndConservesPower()
    {
        const double supply = -12;
        const double resistance = 1_000;
        const double breakdownVoltage = 5.1;
        const double breakdownCurrent = 5e-3;
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", supply)
            .AddResistor("R1", "in", "z", resistance)
            .AddDiode(
                "DZ1",
                "z",
                "gnd",
                DefaultIs,
                breakdownVoltage: breakdownVoltage,
                breakdownCurrent: breakdownCurrent);

        DcSolution solution = netlist.Solve();
        DcDiode diode = Assert.IsType<DcDiode>(netlist.Elements[2]);
        double expected = BisectBreakdownVoltage(
            supply, resistance, diode.SaturationCurrent, diode.JunctionVoltage,
            breakdownVoltage, breakdownCurrent);
        double actual = solution.NodeVoltages["z"];

        AssertClose(expected, actual, 1e-7);
        Assert.InRange(actual, -5.2, -5.1);
        Assert.True(solution.ElementCurrents["DZ1"] < -breakdownCurrent);
        AssertClose(solution.ElementCurrents["R1"], solution.ElementCurrents["DZ1"], 1e-8);
        Assert.True(Math.Abs(solution.ElementPowers.Values.Sum()) < 1e-8);
        Assert.True(solution.RelativeResidual <= 1);

        const double delta = 1e-7;
        double numericalConductance =
            (diode.CurrentAt(actual + delta) - diode.CurrentAt(actual - delta)) / (2 * delta);
        Assert.InRange(
            Math.Abs(numericalConductance - diode.ConductanceAt(actual)) / numericalConductance,
            0,
            1e-6);
    }

    [Fact]
    public void ReverseBreakdownParametersMustBeASpecifiedFinitePositivePair()
    {
        DcNetlist netlist = new("gnd");
        Assert.Throws<ArgumentException>(() =>
            netlist.AddDiode("D1", "a", "gnd", DefaultIs, breakdownVoltage: 5.1));
        Assert.Throws<ArgumentException>(() =>
            netlist.AddDiode("D1", "a", "gnd", DefaultIs, breakdownCurrent: 1e-3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            netlist.AddDiode("D1", "a", "gnd", DefaultIs, breakdownVoltage: 0, breakdownCurrent: 1e-3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            netlist.AddDiode("D1", "a", "gnd", DefaultIs, breakdownVoltage: 5.1, breakdownCurrent: double.NaN));
    }

    [Fact]
    public void ReverseBreakdownWithSeriesResistanceMatchesIndependentScalarBisection()
    {
        const double supply = -12;
        const double externalResistance = 1_000;
        const double seriesResistance = 25;
        const double breakdownVoltage = 5.1;
        const double breakdownCurrent = 5e-3;
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", supply)
            .AddResistor("R1", "in", "z", externalResistance)
            .AddDiode(
                "DZ1",
                "z",
                "gnd",
                DefaultIs,
                seriesResistance: seriesResistance,
                breakdownVoltage: breakdownVoltage,
                breakdownCurrent: breakdownCurrent);

        DcSolution solution = netlist.Solve();
        DcDiode diode = Assert.IsType<DcDiode>(netlist.Elements[2]);
        double junction = BisectBreakdownJunction(
            supply,
            externalResistance + seriesResistance,
            diode.SaturationCurrent,
            diode.JunctionVoltage,
            breakdownVoltage,
            breakdownCurrent);
        double current = diode.JunctionCurrent(junction);
        double expectedTerminal = junction + (seriesResistance * current);

        AssertClose(expectedTerminal, solution.NodeVoltages["z"], 1e-7);
        AssertClose(current, solution.ElementCurrents["DZ1"], 1e-8);
        AssertClose(junction, diode.JunctionVoltageFor(solution.NodeVoltages["z"]), 1e-7);
        AssertClose(solution.ElementCurrents["R1"], current, 1e-8);
        Assert.True(Math.Abs(solution.ElementPowers.Values.Sum()) < 1e-8);
        Assert.True(solution.RelativeResidual <= 1);

        const double delta = 1e-7;
        double numericalConductance =
            (diode.CurrentAt(expectedTerminal + delta) - diode.CurrentAt(expectedTerminal - delta)) /
            (2 * delta);
        Assert.InRange(
            Math.Abs(numericalConductance - diode.ConductanceAt(expectedTerminal)) / numericalConductance,
            0,
            1e-6);
    }

    [Fact]
    public void SeededReverseBreakdownCasesMatchIndependentScalarBisection()
    {
        const int seed = 0x5A3E12;
        const int cases = 80;
        var random = new Random(seed);

        for (int trial = 0; trial < cases; trial++)
        {
            double breakdownVoltage = 3 + (5 * random.NextDouble());
            double breakdownCurrent = Math.Pow(10, -4 + (1.5 * random.NextDouble()));
            double externalResistance = Math.Pow(10, 2 + (3 * random.NextDouble()));
            double seriesResistance = Math.Pow(10, 3 * random.NextDouble()) - 1;
            double supply = -breakdownVoltage - (0.1 + (20 * random.NextDouble()));
            DcNetlist netlist = new DcNetlist("gnd")
                .AddVoltageSource("V1", "in", "gnd", supply)
                .AddResistor("R1", "in", "z", externalResistance)
                .AddDiode(
                    "DZ1",
                    "z",
                    "gnd",
                    DefaultIs,
                    seriesResistance: seriesResistance,
                    breakdownVoltage: breakdownVoltage,
                    breakdownCurrent: breakdownCurrent);

            DcSolution solution;
            try
            {
                solution = netlist.Solve();
            }
            catch (DcSolveException exception)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Seed {seed}, case {trial}: solve failed for BV={breakdownVoltage:G6} V, " +
                    $"IBV={breakdownCurrent:G6} A, R={externalResistance:G6} Ω, " +
                    $"Rs={seriesResistance:G6} Ω, supply={supply:G6} V: {exception.Message}");
            }

            DcDiode diode = Assert.IsType<DcDiode>(netlist.Elements[2]);
            double expectedJunction = BisectBreakdownJunction(
                supply,
                externalResistance + seriesResistance,
                diode.SaturationCurrent,
                diode.JunctionVoltage,
                breakdownVoltage,
                breakdownCurrent);
            double expectedCurrent = diode.JunctionCurrent(expectedJunction);
            double expectedTerminal = expectedJunction + (seriesResistance * expectedCurrent);

            Assert.True(
                Math.Abs(solution.NodeVoltages["z"] - expectedTerminal) <= 2e-6,
                $"Seed {seed}, case {trial}: terminal voltage {solution.NodeVoltages["z"]:G17} V " +
                $"differs from bisection {expectedTerminal:G17} V.");
            Assert.True(
                Math.Abs(solution.ElementCurrents["DZ1"] - expectedCurrent) <= Math.Max(1e-9, Math.Abs(expectedCurrent) * 2e-6),
                $"Seed {seed}, case {trial}: diode current {solution.ElementCurrents["DZ1"]:G17} A " +
                $"differs from bisection {expectedCurrent:G17} A.");
            Assert.True(
                Math.Abs(solution.ElementPowers.Values.Sum()) <= 1e-8,
                $"Seed {seed}, case {trial}: power residual is {solution.ElementPowers.Values.Sum():G6} W.");
            Assert.True(solution.RelativeResidual <= 1);
        }
    }

    [Fact]
    public void DiodeExponentContinuationKeepsCurrentAndConductanceConsistent()
    {
        DcDiode forward = Assert.IsType<DcDiode>(
            new DcNetlist("gnd").AddDiode("D1", "a", "gnd", DefaultIs).Elements[0]);
        DcDiode breakdown = Assert.IsType<DcDiode>(
            new DcNetlist("gnd").AddDiode(
                "DZ1", "a", "gnd", DefaultIs,
                breakdownVoltage: 5.1,
                breakdownCurrent: 5e-3).Elements[0]);

        AssertContinuationDerivative(forward, 80 * forward.JunctionVoltage);
        AssertContinuationDerivative(forward, 82 * forward.JunctionVoltage);
        AssertContinuationDerivative(
            breakdown,
            -breakdown.BreakdownVoltage!.Value - (80 * breakdown.JunctionVoltage));
        AssertContinuationDerivative(
            breakdown,
            -breakdown.BreakdownVoltage!.Value - (82 * breakdown.JunctionVoltage));

        Assert.True(double.IsNaN(forward.JunctionCurrent(double.NaN)));
        Assert.True(double.IsNaN(forward.JunctionConductance(double.NaN)));
        Assert.True(double.IsNaN(breakdown.JunctionCurrent(double.NaN)));
        Assert.True(double.IsNaN(breakdown.JunctionConductance(double.NaN)));
    }

    private static void AssertContinuationDerivative(DcDiode diode, double volts)
    {
        double delta = diode.JunctionVoltage * 1e-7;
        double numerical =
            (diode.JunctionCurrent(volts + delta) - diode.JunctionCurrent(volts - delta)) /
            (2 * delta);
        double analytic = diode.JunctionConductance(volts);
        Assert.True(double.IsFinite(numerical) && double.IsFinite(analytic));
        Assert.InRange(Math.Abs(numerical - analytic) / analytic, 0, 1e-6);
    }

    [Fact]
    public void LargeUnrelatedCurrentDoesNotLoosenDiodeKclConvergence()
    {
        // The reverse-biased diode draws 0.1 A, so a 5 V source and 10 ohm series
        // resistor put the output at 4 V. A separate 1e12 A branch must not allow a
        // large KCL error in this small branch to satisfy a globally scaled residual.
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 10)
            .AddDiode("D1", "gnd", "out", 0.1);
        DcNetlist augmented = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 10)
            .AddDiode("D1", "gnd", "out", 0.1)
            .AddVoltageSource("Vlarge", "large", "gnd", 1e12)
            .AddResistor("Rlarge", "large", "gnd", 1);

        DcSolution reference = netlist.Solve();
        DcSolution withLargeBranch = augmented.Solve();

        Assert.InRange(reference.NodeVoltages["out"], 3.999999, 4.000001);
        Assert.InRange(
            withLargeBranch.NodeVoltages["out"],
            reference.NodeVoltages["out"] - 1e-6,
            reference.NodeVoltages["out"] + 1e-6);
    }

    [Fact]
    public void LargeUnrelatedVoltageDoesNotHideAnUnresolvableSmallSourceConstraint()
    {
        // At a 1e12 V common mode, double precision cannot represent a 1 uV
        // difference between these nodes. A separate nonlinear branch exercises the
        // Newton residual check; it must reject the source equation instead of scaling
        // its residual against the unrelated 1e12 V source.
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("Vlarge", "a", "gnd", 1e12)
            .AddVoltageSource("Vsmall", "a", "b", 1e-6)
            .AddResistor("Rlarge", "b", "gnd", 1)
            .AddVoltageSource("Vdiode", "in", "gnd", 5)
            .AddResistor("Rdiode", "in", "out", 1_000)
            .AddDiode("D1", "out", "gnd", DefaultIs);

        DcSolveException error = Assert.Throws<DcSolveException>(netlist.Solve);

        Assert.Equal(DcSolveFailure.NonConvergent, error.Failure);
        Assert.Contains("nonlinear DC operating point did not converge", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoSeriesDiodesSplitTheForwardDropEvenly()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "n1", 1_000)
            .AddDiode("D1", "n1", "n2", DefaultIs)
            .AddDiode("D2", "n2", "gnd", DefaultIs);

        DcSolution solution = netlist.Solve();

        DcDiode diode = Assert.IsType<DcDiode>(netlist.Elements[3]);
        double expected = BisectSeriesDiodeVoltage(5, 1_000, diode, 2);
        AssertClose(expected, solution.ElementVoltages["D1"], 1e-6);
        AssertClose(expected, solution.ElementVoltages["D2"], 1e-6);
        AssertClose(solution.ElementCurrents["D1"], solution.ElementCurrents["D2"], 1e-6);
        Assert.True(solution.RelativeResidual <= 1);
    }

    [Fact]
    public void HigherEmissionCoefficientNeedsMoreForwardVoltageForTheSameCurrent()
    {
        DcSolution ideal = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddDiode("D1", "out", "gnd", DefaultIs, emissionCoefficient: 1)
            .Solve();
        DcSolution nonideal = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddDiode("D1", "out", "gnd", DefaultIs, emissionCoefficient: 1.8)
            .Solve();

        Assert.True(nonideal.ElementVoltages["D1"] > ideal.ElementVoltages["D1"]);
        Assert.True(nonideal.ElementCurrents["D1"] < ideal.ElementCurrents["D1"]);
    }

    [Fact]
    public void KirchhoffCurrentLawAndPowerConservationHoldWithDiodes()
    {
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddDiode("D1", "out", "gnd", DefaultIs);
        DcSolution solution = netlist.Solve();

        var injection = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (DcElement element in netlist.Elements)
        {
            double current = solution.ElementCurrents[element.Id];
            injection[element.NodeA] = injection.GetValueOrDefault(element.NodeA) + current;
            injection[element.NodeB] = injection.GetValueOrDefault(element.NodeB) - current;
        }

        double scale = netlist.Elements.Max(element => Math.Abs(solution.ElementCurrents[element.Id]));
        foreach ((string node, double sum) in injection)
        {
            Assert.True(
                Math.Abs(sum) <= scale * 1e-8,
                $"KCL residual at node '{node}' is {sum:G6} A for a current scale of {scale:G6} A.");
        }

        double totalPower = netlist.Elements.Sum(element => solution.ElementPowers[element.Id]);
        Assert.True(Math.Abs(totalPower) <= scale * 5 * 1e-8);
    }

    [Fact]
    public void SeriesResistanceMatchesIndependentBisection()
    {
        foreach (double seriesResistance in new[] { 0.1, 10.0, 100.0 })
        {
            var netlist = new DcNetlist("gnd")
                .AddVoltageSource("V1", "in", "gnd", 5)
                .AddResistor("R1", "in", "out", 1_000)
                .AddDiode(
                    "D1",
                    "out",
                    "gnd",
                    DefaultIs,
                    seriesResistance: seriesResistance);
            DcSolution solution = netlist.Solve();
            DcDiode diode = Assert.IsType<DcDiode>(netlist.Elements[2]);

            (double junction, double terminal, double current) =
                BisectWithSeries(5, 1_000, diode);

            AssertClose(terminal, solution.NodeVoltages["out"], 1e-6);
            AssertClose(current, solution.ElementCurrents["D1"], 1e-6);
            AssertClose(
                junction,
                diode.JunctionVoltageFor(solution.NodeVoltages["out"]),
                1e-6);
            AssertClose(
                junction + (seriesResistance * current),
                solution.NodeVoltages["out"],
                1e-6);
            Assert.True(solution.RelativeResidual <= 1);
        }
    }

    [Fact]
    public void SeriesResistanceReducesCurrentAndRaisesTerminalVoltage()
    {
        DcSolution ideal = SolveWithSeriesResistance(0);
        DcSolution resistive = SolveWithSeriesResistance(100);

        Assert.True(resistive.NodeVoltages["out"] > ideal.NodeVoltages["out"]);
        Assert.True(resistive.ElementCurrents["D1"] < ideal.ElementCurrents["D1"]);
        Assert.True(ideal.NodeVoltages["out"] < 1);
        Assert.True(resistive.NodeVoltages["out"] < 2);
    }

    [Fact]
    public void NegativeSeriesResistanceIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DcNetlist("gnd").AddDiode(
                "D1", "a", "gnd", DefaultIs, seriesResistance: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DcNetlist("gnd").AddDiode(
                "D1", "a", "gnd", DefaultIs, seriesResistance: double.NaN));
    }

    [Fact]
    public void InvalidDiodeParametersAreRejectedAtConstruction()
    {
        var netlist = new DcNetlist("gnd");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            netlist.AddDiode("D1", "a", "gnd", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            netlist.AddDiode("D1", "a", "gnd", double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            netlist.AddDiode("D1", "a", "gnd", DefaultIs, emissionCoefficient: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            netlist.AddDiode("D1", "a", "gnd", DefaultIs, thermalVoltage: -1));
    }

    [Fact]
    public void JsonNetlistLoadsADiodeAndReportsItsParameters()
    {
        DcNetlist netlist = DcNetlistJson.LoadFromString("""
            {
              "format": "radix-dc-netlist",
              "version": 1,
              "ground": "gnd",
              "elements": [
                { "id": "V1", "type": "voltage-source", "nodeA": "in", "nodeB": "gnd", "volts": 5 },
                { "id": "R1", "type": "resistor", "nodeA": "in", "nodeB": "out", "ohms": 1000 },
                { "id": "D1", "type": "diode", "nodeA": "out", "nodeB": "gnd",
                  "saturationCurrent": 1e-12, "emissionCoefficient": 1.5, "thermalVoltage": 0.025852,
                  "seriesResistance": 10, "breakdownVoltage": 5.1, "breakdownCurrent": 0.005 }
              ]
            }
            """);

        DcSolution solution = netlist.Solve();
        string json = DcNetlistJson.ToReportJson("diode.json", netlist, solution);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement diode = document.RootElement.GetProperty("elements").EnumerateArray()
            .Single(element => element.GetProperty("id").GetString() == "D1");

        Assert.Equal("diode", diode.GetProperty("kind").GetString());
        Assert.Equal(1.5, diode.GetProperty("emissionCoefficient").GetDouble());
        Assert.Equal(0.025852, diode.GetProperty("thermalVoltage").GetDouble());
        Assert.Equal(10, diode.GetProperty("seriesResistance").GetDouble());
        Assert.Equal(5.1, diode.GetProperty("breakdownVoltage").GetDouble());
        Assert.Equal(0.005, diode.GetProperty("breakdownCurrent").GetDouble());
        Assert.InRange(diode.GetProperty("volts").GetDouble(), 0.6, 1.2);
        Assert.True(diode.GetProperty("volts").GetDouble() > 0);
    }

    [Fact]
    public void JsonParserRejectsMalformedDiodeElements()
    {
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString("""
            {
              "format": "radix-dc-netlist", "version": 1, "ground": "gnd",
              "elements": [
                { "id": "D1", "type": "diode", "nodeA": "a", "nodeB": "gnd" }
              ]
            }
            """));
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString("""
            {
              "format": "radix-dc-netlist", "version": 1, "ground": "gnd",
              "elements": [
                { "id": "D1", "type": "diode", "nodeA": "a", "nodeB": "gnd",
                  "saturationCurrent": 1e-12, "ohms": 1000 }
              ]
            }
            """));
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString("""
            {
              "format": "radix-dc-netlist", "version": 1, "ground": "gnd",
              "elements": [
                { "id": "R1", "type": "resistor", "nodeA": "a", "nodeB": "gnd",
                  "ohms": 1000, "saturationCurrent": 1e-12 }
              ]
            }
            """));
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString("""
            {
              "format": "radix-dc-netlist", "version": 1, "ground": "gnd",
              "elements": [
                { "id": "D1", "type": "diode", "nodeA": "a", "nodeB": "gnd",
                  "saturationCurrent": 1e-12, "breakdownVoltage": 5.1 }
              ]
            }
            """));
    }

    private static DcSolution SolveWithSeriesResistance(double seriesResistance) =>
        new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddDiode(
                "D1",
                "out",
                "gnd",
                DefaultIs,
                seriesResistance: seriesResistance)
            .Solve();

    /// <summary>
    /// Independent scalar solution for a source, series resistor, and diode with
    /// series resistance: bisect the junction voltage against
    /// I = (Vs - Vj)/(R + Rs) = Is(exp(Vj/(N Vt)) - 1).
    /// </summary>
    private static (double Junction, double Terminal, double Current) BisectWithSeries(
        double source,
        double resistance,
        DcDiode diode)
    {
        double low = -1;
        double high = source;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            double mid = (low + high) / 2;
            double current = (source - mid) / (resistance + diode.SeriesResistance);
            if (current - diode.JunctionCurrent(mid) > 0)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        double junction = (low + high) / 2;
        double branchCurrent = (source - junction) / (resistance + diode.SeriesResistance);
        return (
            junction,
            junction + (diode.SeriesResistance * branchCurrent),
            branchCurrent);
    }

    /// <summary>Solves (Vs - Vd)/R = Is(exp(Vd/(N Vt)) - 1) by bisection.</summary>
    private static double BisectForwardVoltage(double source, double resistance, DcDiode diode)
    {
        double low = -1;
        double high = source;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            double mid = (low + high) / 2;
            double residual = ((source - mid) / resistance) - diode.CurrentAt(mid);
            if (residual > 0)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }

    private static double BisectBreakdownVoltage(
        double supply,
        double resistance,
        double saturationCurrent,
        double junctionVoltage,
        double breakdownVoltage,
        double breakdownCurrent)
    {
        double low = supply;
        double high = 0;
        for (int iteration = 0; iteration < 240; iteration++)
        {
            double voltage = (low + high) / 2;
            double forward = saturationCurrent * (Math.Exp(Math.Min(voltage / junctionVoltage, 80)) - 1);
            double reverse = breakdownCurrent * Math.Exp(Math.Min((-voltage - breakdownVoltage) / junctionVoltage, 80));
            double residual = ((voltage - supply) / resistance) + forward - reverse;
            if (residual > 0)
            {
                high = voltage;
            }
            else
            {
                low = voltage;
            }
        }

        return (low + high) / 2;
    }

    private static double BisectBreakdownJunction(
        double supply,
        double totalResistance,
        double saturationCurrent,
        double junctionVoltage,
        double breakdownVoltage,
        double breakdownCurrent)
    {
        double low = supply;
        double high = 0;
        for (int iteration = 0; iteration < 240; iteration++)
        {
            double voltage = (low + high) / 2;
            double forward = saturationCurrent * (Math.Exp(Math.Min(voltage / junctionVoltage, 80)) - 1);
            double reverse = breakdownCurrent * Math.Exp(Math.Min((-voltage - breakdownVoltage) / junctionVoltage, 80));
            double residual = voltage + (totalResistance * (forward - reverse)) - supply;
            if (residual > 0)
            {
                high = voltage;
            }
            else
            {
                low = voltage;
            }
        }

        return (low + high) / 2;
    }

    private static double BisectSeriesDiodeVoltage(
        double source,
        double resistance,
        DcDiode diode,
        int diodeCount)
    {
        double low = -1;
        double high = source / diodeCount;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            double mid = (low + high) / 2;
            double residual = ((source - (diodeCount * mid)) / resistance) - diode.CurrentAt(mid);
            if (residual > 0)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }

    private static void AssertClose(double expected, double actual, double relativeTolerance)
    {
        double tolerance = Math.Max(Math.Abs(expected) * relativeTolerance, 1e-15);
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
    }
}
