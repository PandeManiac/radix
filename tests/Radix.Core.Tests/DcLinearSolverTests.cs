using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

public sealed class DcLinearSolverTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void VoltageDividerMatchesAnalyticalNodeVoltagesCurrentsAndPower()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 10)
            .AddResistor("R1", "in", "out", 1_000)
            .AddResistor("R2", "out", "gnd", 1_000);

        DcSolution solution = netlist.Solve();

        AssertClose(10, solution.NodeVoltages["in"]);
        AssertClose(5, solution.NodeVoltages["out"]);
        AssertClose(5e-3, solution.ElementCurrents["R1"]);
        AssertClose(5e-3, solution.ElementCurrents["R2"]);
        AssertClose(-5e-3, solution.ElementCurrents["V1"]);
        AssertClose(25e-3, solution.ElementPowers["R1"]);
        AssertClose(25e-3, solution.ElementPowers["R2"]);
        AssertClose(-50e-3, solution.ElementPowers["V1"]);
    }

    [Fact]
    public void ParallelResistorsShareCurrentAndEquivalentResistance()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 12)
            .AddResistor("R1", "in", "gnd", 1_000)
            .AddResistor("R2", "in", "gnd", 1_000);

        DcSolution solution = netlist.Solve();

        AssertClose(12, solution.NodeVoltages["in"]);
        AssertClose(12e-3, solution.ElementCurrents["R1"]);
        AssertClose(12e-3, solution.ElementCurrents["R2"]);
        AssertClose(12, solution.ElementVoltages["R1"]);
        AssertClose(144e-3, solution.ElementPowers["R1"]);
    }

    [Fact]
    public void CurrentSourceInjectionIntoResistorProducesExpectedVoltage()
    {
        var netlist = new DcNetlist("gnd")
            .AddCurrentSource("I1", "gnd", "out", 1e-3)
            .AddResistor("R1", "out", "gnd", 1_000);

        DcSolution solution = netlist.Solve();

        AssertClose(0, solution.NodeVoltages["gnd"]);
        AssertClose(1, solution.NodeVoltages["out"]);
        AssertClose(1e-3, solution.ElementCurrents["I1"]);
        AssertClose(1e-3, solution.ElementCurrents["R1"]);
        AssertClose(-1e-3, solution.ElementPowers["I1"]);
        AssertClose(1e-3, solution.ElementPowers["R1"]);
    }

    [Fact]
    public void CurrentSourceDirectionIsFromNodeAToNodeBThroughTheSource()
    {
        var netlist = new DcNetlist("gnd")
            .AddCurrentSource("I1", "out", "gnd", 1e-3)
            .AddResistor("R1", "out", "gnd", 1_000);

        DcSolution solution = netlist.Solve();

        AssertClose(-1, solution.NodeVoltages["out"]);
        AssertClose(1e-3, solution.ElementCurrents["I1"]);
        AssertClose(-1e-3, solution.ElementCurrents["R1"]);
    }

    [Fact]
    public void SeriesVoltageSourcesAddAndSeriesResistanceSetsCurrent()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "a", "mid", 5)
            .AddVoltageSource("V2", "mid", "gnd", 5)
            .AddResistor("R1", "a", "gnd", 1_000);

        DcSolution solution = netlist.Solve();

        AssertClose(10, solution.NodeVoltages["a"]);
        AssertClose(5, solution.NodeVoltages["mid"]);
        AssertClose(10e-3, solution.ElementCurrents["R1"]);
        AssertClose(10, solution.ElementVoltages["R1"]);
    }

    [Fact]
    public void BalancedWheatstoneBridgeHasZeroBridgeCurrent()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 10)
            .AddResistor("R1", "in", "left", 1_000)
            .AddResistor("R2", "left", "gnd", 2_000)
            .AddResistor("R3", "in", "right", 2_000)
            .AddResistor("R4", "right", "gnd", 4_000)
            .AddResistor("Rbridge", "left", "right", 1_000);

        DcSolution solution = netlist.Solve();

        AssertClose(20.0 / 3.0, solution.NodeVoltages["left"]);
        AssertClose(20.0 / 3.0, solution.NodeVoltages["right"]);
        AssertClose(0, solution.ElementCurrents["Rbridge"]);
        AssertClose(0, solution.ElementPowers["Rbridge"]);
    }

    [Fact]
    public void IdealCurrentSourceIntoAnOpenCircuitHasNoOperatingPoint()
    {
        var netlist = new DcNetlist("gnd")
            .AddCurrentSource("I1", "gnd", "out", 1e-3);

        DcSolveException exception = Assert.Throws<DcSolveException>(netlist.Solve);

        Assert.Equal(DcSolveFailure.SingularMatrix, exception.Failure);
        Assert.Contains("singular", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParallelIdealVoltageSourcesHaveNoUniqueOperatingPoint()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "a", "gnd", 5)
            .AddVoltageSource("V2", "a", "gnd", 5)
            .AddResistor("R1", "a", "gnd", 1_000);

        DcSolveException exception = Assert.Throws<DcSolveException>(netlist.Solve);

        Assert.Equal(DcSolveFailure.SingularMatrix, exception.Failure);
    }

    [Fact]
    public void NetlistWithoutAGroundReferenceIsRejected()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "a", "b", 5)
            .AddResistor("R1", "a", "b", 1_000);

        DcSolveException exception = Assert.Throws<DcSolveException>(netlist.Solve);

        Assert.Equal(DcSolveFailure.NoGround, exception.Failure);
        Assert.Contains("reference", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyNetlistSolvesGroundToZeroVolts()
    {
        DcSolution solution = new DcNetlist("gnd").Solve();

        Assert.Equal(0, solution.NodeVoltages["gnd"]);
        Assert.Empty(solution.ElementCurrents);
        Assert.Empty(solution.ElementVoltages);
        Assert.Empty(solution.ElementPowers);
    }

    [Fact]
    public void InvalidElementValuesAreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DcNetlist("gnd").AddResistor("R1", "a", "gnd", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DcNetlist("gnd").AddResistor("R1", "a", "gnd", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DcNetlist("gnd").AddResistor("R1", "a", "gnd", double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DcNetlist("gnd").AddVoltageSource("V1", "a", "gnd", double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DcNetlist("gnd").AddCurrentSource("I1", "a", "gnd", double.NaN));
    }

    [Fact]
    public void DuplicateElementIdsAndSelfLoopsAreRejected()
    {
        var netlist = new DcNetlist("gnd")
            .AddResistor("R1", "a", "gnd", 1_000);

        Assert.Throws<ArgumentException>(() =>
            netlist.AddResistor("R1", "b", "gnd", 1_000));
        Assert.Throws<ArgumentException>(() =>
            new DcNetlist("gnd").AddResistor("R2", "a", "a", 1_000));
    }

    [Fact]
    public void NodeListIsDeterministicAndIncludesGround()
    {
        var netlist = new DcNetlist("gnd")
            .AddResistor("R2", "b", "gnd", 1_000)
            .AddResistor("R1", "a", "b", 1_000);

        Assert.Equal(["a", "b", "gnd"], netlist.Nodes);
    }

    [Fact]
    public void ManyIndependentDividersSolveWithExpectedSourceCurrent()
    {
        const int channelCount = 100;
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 10);
        for (int channel = 0; channel < channelCount; channel++)
        {
            netlist
                .AddResistor($"R1-{channel}", "in", $"out{channel}", 1_000)
                .AddResistor($"R2-{channel}", $"out{channel}", "gnd", 1_000);
        }

        DcSolution solution = netlist.Solve();

        for (int channel = 0; channel < channelCount; channel++)
        {
            AssertClose(5, solution.NodeVoltages[$"out{channel}"]);
            AssertClose(5e-3, solution.ElementCurrents[$"R1-{channel}"]);
            AssertClose(5e-3, solution.ElementCurrents[$"R2-{channel}"]);
        }

        AssertClose(-channelCount * 5e-3, solution.ElementCurrents["V1"]);
    }

    private static void AssertClose(double expected, double actual)
    {
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);
    }
}
