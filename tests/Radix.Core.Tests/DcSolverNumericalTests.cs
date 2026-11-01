using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Numerical-behaviour tests for the independent DC solver. These cases use
/// analytical expectations and deliberately extreme but physically valid values so
/// they do not depend on the solver's own implementation as an oracle.
/// </summary>
public sealed class DcSolverNumericalTests
{
    private const double RelativeTolerance = 1e-9;

    [Fact]
    public void VeryHighResistanceDividerStillSolves()
    {
        // Two 1 P-ohm resistors are physically extreme but perfectly well defined:
        // the operating point is 0.5 V and the branch current is 5e-16 A.
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 1)
            .AddResistor("R1", "in", "out", 1e15)
            .AddResistor("R2", "out", "gnd", 1e15);

        DcSolution solution = netlist.Solve();

        AssertClose(0.5, solution.NodeVoltages["out"]);
        AssertClose(5e-16, solution.ElementCurrents["R1"]);
        AssertClose(5e-16, solution.ElementCurrents["R2"]);
        AssertClose(-5e-16, solution.ElementCurrents["V1"]);
        Assert.True(solution.RelativeResidual < 1e-12);
    }

    [Fact]
    public void VeryLowResistanceDividerStillSolves()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 1)
            .AddResistor("R1", "in", "out", 1e-9)
            .AddResistor("R2", "out", "gnd", 1e-9);

        DcSolution solution = netlist.Solve();

        AssertClose(0.5, solution.NodeVoltages["out"]);
        AssertClose(5e8, solution.ElementCurrents["R1"]);
        AssertClose(-5e8, solution.ElementCurrents["V1"]);
    }

    [Fact]
    public void ExtremeConductanceSpreadOnIndependentBranchesSolves()
    {
        // A one-milliohm branch and a teraohm branch share a source but no unknown.
        // The weak branch must not be masked by the global matrix magnitude.
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 1)
            .AddResistor("Rstiff", "in", "gnd", 1e-3)
            .AddResistor("RweakA", "in", "weak", 1e15)
            .AddResistor("RweakB", "weak", "gnd", 1e15);

        DcSolution solution = netlist.Solve();

        AssertClose(0.5, solution.NodeVoltages["weak"]);
        AssertClose(1e3, solution.ElementCurrents["Rstiff"]);
        AssertClose(5e-16, solution.ElementCurrents["RweakA"]);
    }

    [Fact]
    public void FloatingIslandIsClassifiedSeparatelyFromASingularSourceLoop()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "gnd", 1_000)
            .AddResistor("R2", "islandA", "islandB", 1_000)
            .AddResistor("R3", "islandA", "islandB", 2_000);

        DcSolveException exception = Assert.Throws<DcSolveException>(netlist.Solve);

        Assert.Equal(DcSolveFailure.FloatingNetwork, exception.Failure);
        Assert.Contains("'islandA'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'islandB'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ground", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConflictingIdealVoltageSourcesAreClassifiedAsInconsistent()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "a", "gnd", 5)
            .AddVoltageSource("V2", "a", "gnd", 6)
            .AddResistor("R1", "a", "gnd", 1_000);

        DcSolveException exception = Assert.Throws<DcSolveException>(netlist.Solve);

        Assert.Equal(DcSolveFailure.InconsistentSourceConstraints, exception.Failure);
        Assert.Contains("'V2'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("conflict", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsistentButRedundantIdealSourcesRemainSingular()
    {
        // 2 V + 3 V in series is consistent with the 5 V source across the same pair
        // of nodes, but the branch currents are still not unique.
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "a", "gnd", 5)
            .AddVoltageSource("V2", "a", "b", 2)
            .AddVoltageSource("V3", "b", "gnd", 3)
            .AddResistor("R1", "a", "gnd", 1_000);

        DcSolveException exception = Assert.Throws<DcSolveException>(netlist.Solve);

        Assert.Equal(DcSolveFailure.SingularMatrix, exception.Failure);
    }

    [Fact]
    public void KirchhoffCurrentLawHoldsAtEveryNodeOfAWheatstoneBridge()
    {
        DcNetlist netlist = BridgeNetlist();
        DcSolution solution = netlist.Solve();

        var injection = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (DcElement element in netlist.Elements)
        {
            double current = solution.ElementCurrents[element.Id];
            injection[element.NodeA] = injection.GetValueOrDefault(element.NodeA) + current;
            injection[element.NodeB] = injection.GetValueOrDefault(element.NodeB) - current;
        }

        double scale = Math.Max(1e-12, netlist.Elements.Max(e => Math.Abs(solution.ElementCurrents[e.Id])));
        foreach ((string node, double sum) in injection)
        {
            Assert.True(
                Math.Abs(sum) <= scale * 1e-9,
                $"KCL residual at node '{node}' is {sum:G6} A for a current scale of {scale:G6} A.");
        }
    }

    [Fact]
    public void KirchhoffVoltageLawAndPowerConservationHoldOnAWheatstoneBridge()
    {
        DcNetlist netlist = BridgeNetlist();
        DcSolution solution = netlist.Solve();

        // Mesh in -> left -> right -> in. Element voltage is V(NodeA) - V(NodeB).
        double mesh =
            solution.ElementVoltages["R1"] +
            solution.ElementVoltages["Rbridge"] -
            solution.ElementVoltages["R3"];
        AssertClose(0, mesh);

        double totalPower = netlist.Elements.Sum(e => solution.ElementPowers[e.Id]);
        double powerScale = netlist.Elements.Max(e => Math.Abs(solution.ElementPowers[e.Id]));
        Assert.True(
            Math.Abs(totalPower) <= Math.Max(1e-18, powerScale * 1e-9),
            $"Absorbed power sums to {totalPower:G6} W instead of 0 W.");
        Assert.True(solution.RelativeResidual < 1e-12);
    }

    [Fact]
    public void ReorderingElementsDoesNotChangeNodeVoltages()
    {
        DcSolution forward = BridgeNetlist().Solve();
        DcSolution reverse = new DcNetlist("gnd")
            .AddResistor("Rbridge", "left", "right", 1_000)
            .AddResistor("R4", "right", "gnd", 4_000)
            .AddResistor("R3", "in", "right", 2_000)
            .AddResistor("R2", "left", "gnd", 2_000)
            .AddResistor("R1", "in", "left", 1_000)
            .AddVoltageSource("V1", "in", "gnd", 10)
            .Solve();

        Assert.Equal(
            forward.NodeVoltages.Keys.Order(StringComparer.Ordinal),
            reverse.NodeVoltages.Keys.Order(StringComparer.Ordinal));
        foreach ((string node, double volts) in forward.NodeVoltages)
        {
            AssertClose(volts, reverse.NodeVoltages[node]);
        }
    }

    [Fact]
    public void RandomizedResistorLaddersMatchAnIndependentRecurrence() =>
        AssertLaddersMatch(
            new Random(20261031),
            trials: 200,
            minExponent: -3,
            maxExponent: 6,
            relativeTolerance: 1e-6);

    [Fact]
    public void ModerateResistorLaddersMatchAnIndependentRecurrenceTightly() =>
        AssertLaddersMatch(
            new Random(20261031),
            trials: 100,
            minExponent: 2,
            maxExponent: 5,
            relativeTolerance: 1e-10);

    private static void AssertLaddersMatch(
        Random random,
        int trials,
        int minExponent,
        int maxExponent,
        double relativeTolerance)
    {
        // The expected node voltages come from the ladder's closed-form recurrence,
        // not from another linear solver. The recurrence runs in decimal so it is an
        // effectively exact oracle for the double solver.
        const int stageCount = 6;
        for (int trial = 0; trial < trials; trial++)
        {
            decimal sourceVolts = 10;
            var series = new decimal[stageCount];
            var shunt = new decimal[stageCount];
            for (int stage = 0; stage < stageCount; stage++)
            {
                series[stage] = Decade(random, minExponent, maxExponent);
                shunt[stage] = Decade(random, minExponent, maxExponent);
            }

            var netlist = new DcNetlist("gnd")
                .AddVoltageSource("V1", "n0", "gnd", (double)sourceVolts);
            for (int stage = 0; stage < stageCount; stage++)
            {
                netlist
                    .AddResistor($"R{stage}", $"n{stage}", $"n{stage + 1}", (double)series[stage])
                    .AddResistor($"S{stage}", $"n{stage + 1}", "gnd", (double)shunt[stage]);
            }

            DcSolution solution = netlist.Solve();

            // Equivalent resistance looking into each node from the previous one.
            var equivalent = new decimal[stageCount + 1];
            equivalent[stageCount] = shunt[stageCount - 1];
            for (int stage = stageCount - 1; stage >= 1; stage--)
            {
                decimal downstream = series[stage] + equivalent[stage + 1];
                equivalent[stage] = shunt[stage - 1] * downstream / (shunt[stage - 1] + downstream);
            }

            decimal voltage = sourceVolts;
            AssertClose((double)voltage, solution.NodeVoltages["n0"], relativeTolerance);
            for (int stage = 0; stage < stageCount; stage++)
            {
                voltage *= equivalent[stage + 1] / (series[stage] + equivalent[stage + 1]);
                AssertClose(
                    (double)voltage,
                    solution.NodeVoltages[$"n{stage + 1}"],
                    relativeTolerance);
            }

            double totalPower = netlist.Elements.Sum(e => solution.ElementPowers[e.Id]);
            double powerScale = netlist.Elements.Max(e => Math.Abs(solution.ElementPowers[e.Id]));
            Assert.True(Math.Abs(totalPower) <= Math.Max(1e-18, powerScale * 1e-8));
            Assert.True(solution.RelativeResidual < 1e-12);
        }
    }

    private static DcNetlist BridgeNetlist() =>
        new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 10)
            .AddResistor("R1", "in", "left", 1_000)
            .AddResistor("R2", "left", "gnd", 2_000)
            .AddResistor("R3", "in", "right", 2_000)
            .AddResistor("R4", "right", "gnd", 4_000)
            .AddResistor("Rbridge", "left", "right", 1_000);

    private static decimal Decade(Random random, int minExponent, int maxExponent)
    {
        int exponent = random.Next(minExponent, maxExponent + 1);
        decimal mantissa = (decimal)(0.1 + (random.NextDouble() * 0.9));
        decimal value = mantissa;
        for (int step = 0; step < Math.Abs(exponent); step++)
        {
            value = exponent < 0 ? value / 10 : value * 10;
        }

        return value;
    }

    private static void AssertClose(
        double expected,
        double actual,
        double relativeTolerance = RelativeTolerance)
    {
        double tolerance = Math.Max(Math.Abs(expected) * relativeTolerance, 1e-18);
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
    }
}
