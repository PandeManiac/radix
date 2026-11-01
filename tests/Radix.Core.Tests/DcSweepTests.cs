using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// DC sweep tests. Every expected point is checked against an independent single
/// solve built from scratch, not against the sweep's own bookkeeping.
/// </summary>
public sealed class DcSweepTests
{
    [Fact]
    public void SweepPointsMatchIndependentSingleSolves()
    {
        DcNetlist netlist = CreateDivider();
        IReadOnlyList<DcSweepPoint> points = DcSweep.VoltageSource(netlist, "V1", 0, 10, 6);

        Assert.Equal(6, points.Count);
        for (int index = 0; index < points.Count; index++)
        {
            double value = index * 2.0;
            DcSweepPoint point = points[index];
            Assert.Equal(value, point.SourceValue);
            Assert.True(point.Solved);

            DcSolution expected = CreateDivider().WithVoltageSource("V1", value).Solve();
            Assert.Equal(expected.NodeVoltages["out"], point.Solution!.NodeVoltages["out"], 12);
            Assert.Equal(value / 2, point.Solution.NodeVoltages["out"], 12);
            Assert.Equal(-value / 2_000, point.Solution.ElementCurrents["V1"], 12);
            Assert.Equal(-(value * value) / 2_000, point.Solution.ElementPowers["V1"], 9);
        }
    }

    [Fact]
    public void ZenerSweepClampsAboveTheKnee()
    {
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 0)
            .AddResistor("R1", "in", "out", 1_000)
            .AddDiode(
                "D1",
                "gnd",
                "out",
                1e-12,
                breakdownVoltage: 5.1,
                breakdownCurrent: 1e-3,
                seriesResistance: 10);

        IReadOnlyList<DcSweepPoint> points = DcSweep.VoltageSource(netlist, "V1", 0, 9, 10);

        Assert.All(points, point => Assert.True(point.Solved));
        Assert.InRange(points[0].Solution!.NodeVoltages["out"], -1e-9, 1e-9);
        for (int index = 1; index < points.Count; index++)
        {
            Assert.True(
                points[index].Solution!.NodeVoltages["out"] >=
                points[index - 1].Solution!.NodeVoltages["out"]);
        }

        Assert.InRange(points[^1].Solution!.NodeVoltages["out"], 5.15, 5.19);
        Assert.True(points[^1].Solution!.ElementCurrents["D1"] < -3e-3);
    }

    [Fact]
    public void SweepRecordsFailuresWithoutAborting()
    {
        var netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "a", "gnd", 0)
            .AddVoltageSource("V2", "a", "gnd", 1)
            .AddResistor("R1", "a", "gnd", 1_000);

        IReadOnlyList<DcSweepPoint> points = DcSweep.VoltageSource(netlist, "V1", 0, 2, 3);

        Assert.Equal(3, points.Count);
        Assert.False(points[0].Solved);
        Assert.Equal(DcSolveFailure.InconsistentSourceConstraints, points[0].Failure);
        Assert.False(points[1].Solved);
        Assert.Equal(DcSolveFailure.SingularMatrix, points[1].Failure);
        Assert.False(points[2].Solved);
        Assert.Equal(DcSolveFailure.InconsistentSourceConstraints, points[2].Failure);
        Assert.All(points, point => Assert.False(string.IsNullOrWhiteSpace(point.Message)));
    }

    [Fact]
    public void SweepRejectsInvalidArguments()
    {
        DcNetlist netlist = CreateDivider();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DcSweep.VoltageSource(netlist, "V1", 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DcSweep.VoltageSource(netlist, "V1", 0, 1, DcSweep.MaximumPoints + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DcSweep.VoltageSource(netlist, "V1", double.NaN, 1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DcSweep.VoltageSource(netlist, "V1", 0, double.PositiveInfinity, 3));
        Assert.Throws<ArgumentException>(() =>
            DcSweep.VoltageSource(netlist, "R1", 0, 1, 3));
        Assert.Throws<ArgumentException>(() =>
            netlist.WithVoltageSource("R1", 3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            netlist.WithVoltageSource("V1", double.NaN));
    }

    [Fact]
    public void WithVoltageSourceDoesNotMutateTheOriginal()
    {
        DcNetlist original = CreateDivider();
        DcNetlist modified = original.WithVoltageSource("V1", 20);

        Assert.Equal(10, original.Elements.OfType<DcVoltageSource>().Single().Volts);
        Assert.Equal(20, modified.Elements.OfType<DcVoltageSource>().Single().Volts);
        Assert.Equal(5, original.Solve().NodeVoltages["out"], 12);
        Assert.Equal(10, modified.Solve().NodeVoltages["out"], 12);
        Assert.Equal(5, original.Solve().NodeVoltages["out"], 12);
    }

    [Fact]
    public void SweepJsonReportContainsEachPoint()
    {
        DcNetlist netlist = CreateDivider();
        IReadOnlyList<DcSweepPoint> points = DcSweep.VoltageSource(netlist, "V1", 0, 4, 3);
        string json = DcNetlistJson.ToSweepJson(
            "divider.json",
            null,
            "V1",
            "out",
            0,
            4,
            points);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("radix-dc-sweep", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("V1", root.GetProperty("sourceId").GetString());
        Assert.Equal("out", root.GetProperty("node").GetString());
        Assert.Equal(3, root.GetProperty("points").GetInt32());

        JsonElement results = root.GetProperty("results");
        Assert.Equal(3, results.GetArrayLength());
        Assert.Equal(0, results[0].GetProperty("sourceValue").GetDouble());
        Assert.Equal(0, results[0].GetProperty("nodeVolts").GetDouble());
        Assert.True(results[1].GetProperty("solved").GetBoolean());
        Assert.Equal(1, results[1].GetProperty("nodeVolts").GetDouble());
        Assert.Equal(2, results[2].GetProperty("nodeVolts").GetDouble());
        Assert.Equal(-0.002, results[2].GetProperty("sourceAmperes").GetDouble(), 12);
    }

    private static DcNetlist CreateDivider() =>
        new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 10)
            .AddResistor("R1", "in", "out", 1_000)
            .AddResistor("R2", "out", "gnd", 1_000);
}
