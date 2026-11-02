using System.Globalization;
using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The DC sweep is how a transfer characteristic becomes a table of real operating
/// points. These tests pin the sweep's contract: one fresh copy per value, a failed row
/// that keeps its value, and deterministic exports.
/// </summary>
public sealed class DcSweepTests
{
    private static DcNetlist Divider() =>
        new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 10)
            .AddResistor("R1", "in", "out", 1_000)
            .AddResistor("R2", "out", "gnd", 1_000);

    /// <summary>A static CMOS inverter driven by the VIN source the sweep varies.</summary>
    private static DcNetlist CmosInverter() =>
        new DcNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VIN", "in", "gnd", 0)
            .AddMosfet("MP", "out", "in", "vdd", DcMosfetChannel.PChannel, 1, 1e-3, 0.02)
            .AddMosfet("MN", "out", "in", "gnd", DcMosfetChannel.NChannel, 1, 1e-3, 0.02);

    [Fact]
    public void SweepingASourceTracesTheDividerExactly()
    {
        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(Divider(), "V1", DcSweep.Linspace(0, 10, 11));

        Assert.Equal(11, rows.Count);
        foreach (DcSweepRow row in rows)
        {
            Assert.True(row.Solved);
            Assert.Equal(row.Value / 2, row.Solution!.NodeVoltages["out"], 12);
            Assert.Equal(row.Value, row.Solution.NodeVoltages["in"], 12);
        }
    }

    [Fact]
    public void SweepingTheInputOfACmosInverterProducesItsTransferCurve()
    {
        // 21 points from 0 V to 5 V. The curve must be the solved circuit's own: both
        // rails at the ends, an analog edge in the middle, and finite gain throughout.
        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(CmosInverter(), "VIN", DcSweep.Linspace(0, 5, 21));

        Assert.All(rows, row => Assert.True(row.Solved));
        Assert.True(rows[0].Solution!.NodeVoltages["out"] > 4.9);
        Assert.True(rows[^1].Solution!.NodeVoltages["out"] < 0.1);

        for (int index = 1; index < rows.Count; index++)
        {
            Assert.True(
                rows[index].Solution!.NodeVoltages["out"] <=
                    rows[index - 1].Solution!.NodeVoltages["out"] + 1e-6);
        }

        // A matched pair has its switching point at half the supply by symmetry.
        DcSweepRow middle = rows.Single(row => Math.Abs(row.Value - 2.5) < 1e-12);
        Assert.Equal(2.5, middle.Solution!.NodeVoltages["out"], 3);
        Assert.Contains(rows, row =>
            row.Solution!.NodeVoltages["out"] is > 0.5 and < 4.5);
    }

    [Fact]
    public void AFailedRowKeepsItsValueInsteadOfVanishing()
    {
        // Two ideal sources in parallel across the same nodes cannot both be satisfied.
        // At equal values the loop is consistent but its individual currents are not
        // unique; at different values the constraints outright contradict. Every probed
        // value must still appear as a failed row rather than aborting the study.
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "vdd", "gnd", 5)
            .AddVoltageSource("V2", "vdd", "gnd", 5)
            .AddResistor("R1", "vdd", "gnd", 1_000);

        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(netlist, "V2", [5, 4]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.False(row.Solved);
            Assert.NotNull(row.Failure);
            Assert.False(string.IsNullOrWhiteSpace(row.Message));
        });
        Assert.Equal(DcSolveFailure.SingularMatrix, rows[0].Failure);
        Assert.Equal(DcSolveFailure.InconsistentSourceConstraints, rows[1].Failure);
    }

    [Fact]
    public void AnInvalidSweptParameterIsACallerError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DcSweep.Run(Divider(), "R1", [1_000, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => DcSweep.Run(Divider(), "V1", [1, double.NaN]));
        Assert.Throws<ArgumentException>(() => DcSweep.Run(Divider(), "R404", [1]));
        Assert.Throws<ArgumentException>(() => DcSweep.Run(Divider(), "V1", []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DcSweep.Run(Divider(), "V1", DcSweep.Linspace(0, 1, DcSweep.MaximumRows + 1)));
    }

    [Fact]
    public void ADiodeCannotBeSweptAsOneValue()
    {
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddDiode("D1", "out", "gnd", 1e-12);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => DcSweep.Run(netlist, "D1", [1e-12, 2e-12]));
        Assert.Contains("diode", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMosfetTransconductanceCanBeSwept()
    {
        // A stronger transistor pulls the common-source stage lower: the swept parameter
        // must reach the device, not just the netlist copy.
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VG", "gate", "gnd", 3)
            .AddResistor("RL", "vdd", "out", 1_000)
            .AddMosfet("M1", "out", "gate", "gnd", DcMosfetChannel.NChannel, 1, 1e-4, 0.02);

        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(netlist, "M1", [1e-4, 1e-3, 1e-2]);

        Assert.All(rows, row => Assert.True(row.Solved));
        Assert.True(rows[0].Solution!.NodeVoltages["out"] > rows[1].Solution!.NodeVoltages["out"]);
        Assert.True(rows[1].Solution!.NodeVoltages["out"] > rows[2].Solution!.NodeVoltages["out"]);
    }

    [Fact]
    public void ExportsAreDeterministicAndDescribeTheSameStudy()
    {
        DcNetlist netlist = CmosInverter();
        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(netlist, "VIN", DcSweep.Linspace(0, 5, 11));

        string firstCsv = DcSweepReport.ToCsv(netlist, rows);
        string secondCsv = DcSweepReport.ToCsv(netlist, rows);
        Assert.Equal(firstCsv, secondCsv);
        Assert.StartsWith("value,gnd,in,out,vdd,failure", firstCsv, StringComparison.Ordinal);
        Assert.Equal(12, firstCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        string text = DcSweepReport.ToText(netlist, "VIN", rows);
        Assert.Contains("VIN sweep: 11 values, 4 nodes", text, StringComparison.Ordinal);
        Assert.Contains("failed", DcSweepReport.ToText(
            netlist,
            "VIN",
            [new DcSweepRow(1, null, DcSolveFailure.NonConvergent, "did not converge")]),
            StringComparison.Ordinal);

        // A node whose id contains a comma must not break the table into extra columns.
        Assert.Contains(
            "\"a,b\"",
            DcSweepReport.ToCsv(
                new DcNetlist("gnd")
                    .AddVoltageSource("V1", "in", "gnd", 1)
                    .AddResistor("R1", "in", "a,b", 1_000)
                    .AddResistor("R2", "a,b", "gnd", 1_000),
                [new DcSweepRow(1, null, null, null)]),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinspaceEndsExactlyOnTheRequestedBounds()
    {
        double[] values = DcSweep.Linspace(-1, 1, 5);
        Assert.Equal([-1, -0.5, 0, 0.5, 1], values);
        Assert.Equal("0.5", values[3].ToString(CultureInfo.InvariantCulture));
    }
}
