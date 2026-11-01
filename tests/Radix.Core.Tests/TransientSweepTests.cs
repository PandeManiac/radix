using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A sweep is only trustworthy if the swept value is the sole difference between rows, so these
/// tests pin the clone (identical otherwise), the physical trend the study has to reveal
/// (tau = R*C makes a wider resistor settle later), and the rejection of values and elements that
/// cannot be swept.
/// </summary>
public sealed class TransientSweepTests
{
    [Fact]
    public void CloneSolvesIdenticallyAndLeavesTheOriginalUntouched()
    {
        TransientNetlist netlist = Rc(1_000);
        TransientOptions options = new() { StopTime = 1e-3, Steps = 50 };

        TransientNetlist copy = netlist.Clone();
        TransientResult original = netlist.Run(options);
        TransientResult cloned = copy.Run(options);

        Assert.Equal(netlist.Fingerprint(), copy.Fingerprint());
        Assert.Equal(
            original.FinalPoint!.NodeVoltages["out"],
            cloned.FinalPoint!.NodeVoltages["out"],
            12);

        TransientNetlist varied = copy.WithElementValue("R1", 500);

        // Varying is a copy, not a mutation: both originals still hold their own value.
        Assert.Equal(1_000, netlist.Elements.Single(element => element.Id == "R1").Value);
        Assert.Equal(1_000, copy.Elements.Single(element => element.Id == "R1").Value);
        Assert.Equal(500, varied.Elements.Single(element => element.Id == "R1").Value);
        Assert.NotEqual(netlist.Fingerprint(), varied.Fingerprint());
    }

    [Fact]
    public void AWiderResistanceSettlesLaterAndEachRowMatchesItsOwnRun()
    {
        TransientNetlist netlist = Rc(1_000);
        TransientOptions options = new() { StopTime = 5e-2, Steps = 4_000 };
        double[] values = [500, 1_000, 2_000];

        IReadOnlyList<TransientSweepRow> rows = TransientSweep.Run(netlist, "R1", values, options);

        Assert.Equal(values, rows.Select(row => row.Value));
        Assert.All(rows, row => Assert.True(row.Result.Completed, row.Result.Message));
        Assert.True(Settling(rows[0]) < Settling(rows[1]));
        Assert.True(Settling(rows[1]) < Settling(rows[2]));

        // The rows are different circuits, and the study says so.
        Assert.Equal(3, rows.Select(row => row.Result.NetlistFingerprint).Distinct().Count());

        TransientResult direct = netlist.WithElementValue("R1", 2_000).Run(options);
        Assert.Equal(
            direct.FinalPoint!.NodeVoltages["out"],
            rows[2].Result.FinalPoint!.NodeVoltages["out"],
            12);
    }

    [Fact]
    public void SweepingTheSourceScalesTheSteadyStateLinearly()
    {
        TransientNetlist netlist = Rc(1_000);
        TransientOptions options = new() { StopTime = 5e-2, Steps = 4_000 };

        IReadOnlyList<TransientSweepRow> rows = TransientSweep.Run(netlist, "V1", [1, 2, 4], options);

        foreach (TransientSweepRow row in rows)
        {
            // The circuit is linear, so the settled output must equal the drive.
            Assert.Equal(row.Value, row.Result.FinalPoint!.NodeVoltages["out"], 2);
            // A monotone charge from rest peaks at its own drive, so the peak tracks the value.
            Assert.Equal(
                row.Value,
                row.Result.Metrics!.Nodes.Single(node => node.NodeId == "out").MaximumVolts,
                2);
        }
    }

    [Fact]
    public void ASweptValueMustBePositiveAndInvertible()
    {
        TransientNetlist netlist = Rc(1_000);

        Assert.Throws<ArgumentOutOfRangeException>(() => netlist.WithElementValue("R1", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => netlist.WithElementValue("R1", -5));
        Assert.Throws<ArgumentOutOfRangeException>(() => netlist.WithElementValue("R1", double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => netlist.WithElementValue("C1", double.PositiveInfinity));
    }

    [Fact]
    public void UnknownElementsAndDiodesAreRejectedWithAReason()
    {
        TransientNetlist netlist = Rc(1_000);

        ArgumentException missing = Assert.Throws<ArgumentException>(
            () => netlist.WithElementValue("R9", 10));
        Assert.Contains("R9", missing.Message, StringComparison.Ordinal);

        TransientNetlist withDiode = netlist.AddDiode("D1", "out", "gnd", 1e-12, 1, 0.02585, 0);
        ArgumentException diode = Assert.Throws<ArgumentException>(
            () => withDiode.WithElementValue("D1", 2));
        Assert.Contains("diode", diode.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinspaceIsInclusiveAndRejectsUnusableRanges()
    {
        Assert.Equal(
            new[] { 800d, 900d, 1_000d, 1_100d, 1_200d },
            TransientSweep.Linspace(800, 1_200, 5));
        Assert.Equal(new[] { 10d, 5d, 0d }, TransientSweep.Linspace(10, 0, 3));
        Assert.Equal(1_000, TransientSweep.Linspace(1_000, 1_000, 2)[0]);

        Assert.Throws<ArgumentOutOfRangeException>(() => TransientSweep.Linspace(1, 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransientSweep.Linspace(1, 2, 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransientSweep.Linspace(double.NaN, 2, 2));
        Assert.Throws<ArgumentException>(() => TransientSweep.Run(
            Rc(1_000), "R1", [], new TransientOptions { StopTime = 1e-3, Steps = 4 }));
    }

    private static double Settling(TransientSweepRow row) =>
        row.Result.Metrics!.Nodes.Single(node => node.NodeId == "out").SettlingTimeSeconds!.Value;

    private static TransientNetlist Rc(double ohms) => new TransientNetlist("gnd")
        .AddVoltageSource("V1", "in", "gnd", 5)
        .AddResistor("R1", "in", "out", ohms)
        .AddCapacitor("C1", "out", "gnd", 1e-6)
        .SetCapacitorInitialVoltage("C1", 0);
}
