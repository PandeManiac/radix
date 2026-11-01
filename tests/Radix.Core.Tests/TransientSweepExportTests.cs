using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A study that cannot be archived is a study that cannot be compared, so the exports have to be
/// complete (a failed value is still a row), machine-readable, deterministic, and safe with labels
/// that contain separators. These tests pin all four.
/// </summary>
public sealed class TransientSweepExportTests
{
    [Fact]
    public void TheCsvHasACommentBlockAndOneRowPerNodeAndValue()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000), "R1", [800, 1_200]);

        string csv = TransientSweepCsv.Write("rc.json", "rc-charge", "R1", rows);
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] data = lines.Where(line => !line.StartsWith('#')).ToArray();

        Assert.Contains("# format: radix-transient-sweep", lines);
        Assert.Contains("# element: R1", lines);
        Assert.Contains("# values: 2", lines);
        Assert.Contains("# method: backward-euler", lines);
        Assert.Equal(
            "value,netlist,completed,node,peak_v,final_v,settle_s,energy_residual,failure,message",
            data[0]);
        // Two values, three nodes each (ground included): a complete record, not a summary.
        Assert.Equal(1 + (2 * 3), data.Length);
        Assert.All(rows, row => Assert.Contains(row.Result.NetlistFingerprint!, csv, StringComparison.Ordinal));
    }

    [Fact]
    public void TheCsvIsDeterministic()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000), "R1", [800, 1_200]);

        Assert.Equal(
            TransientSweepCsv.Write("rc.json", "rc", "R1", rows),
            TransientSweepCsv.Write("rc.json", "rc", "R1", rows));
    }

    [Fact]
    public void AFailedValueKeepsItsRowWithNoMetrics()
    {
        // A capacitor with no resistive DC path cannot start: every value fails, and the study has
        // to say so rather than appear empty.
        TransientNetlist netlist = new TransientNetlist("gnd").AddCapacitor("C1", "out", "gnd", 1e-6);
        IReadOnlyList<TransientSweepRow> rows = Rows(netlist, "C1", [1e-6, 2e-6]);

        string csv = TransientSweepCsv.Write(null, null, "C1", rows);
        string[] data = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();

        Assert.Equal(3, data.Length);
        Assert.All(rows, row => Assert.False(row.Result.Completed));
        Assert.Contains("false,,,", csv, StringComparison.Ordinal);

        using JsonDocument report = JsonDocument.Parse(
            TransientSweepJson.ToReportJson(null, null, "C1", rows));
        JsonElement first = report.RootElement.GetProperty("rows")[0];
        Assert.False(first.GetProperty("completed").GetBoolean());
        Assert.True(first.TryGetProperty("failure", out _));
        Assert.False(first.TryGetProperty("nodes", out _));
    }

    [Fact]
    public void TheStudyRecordsTheEnergyLedgerPerValue()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000), "R1", [800, 1_200]);

        string csv = TransientSweepCsv.Write(null, null, "R1", rows);
        string header = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(line => !line.StartsWith('#'));
        Assert.Contains("energy_residual", header, StringComparison.Ordinal);

        string json = TransientSweepJson.ToReportJson(null, null, "R1", rows);
        using JsonDocument report = JsonDocument.Parse(json);
        double residual = report.RootElement.GetProperty("rows")[0]
            .GetProperty("energyResidual").GetDouble();
        Assert.Equal(rows[0].Result.Energy!.RelativeResidual, residual);
        Assert.InRange(residual, 0, 0.05);

        TransientSweepReport loaded = TransientSweepReport.LoadFromString(json);
        Assert.Equal(residual, loaded.Rows[0].EnergyResidual);
    }

    [Fact]
    public void TheJsonDescribesTheStudyAndEveryRow()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000), "R1", [800, 1_200]);

        using JsonDocument report = JsonDocument.Parse(
            TransientSweepJson.ToReportJson("rc.json", "rc-charge", "R1", rows));
        JsonElement root = report.RootElement;

        Assert.Equal("radix-transient-sweep", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("rc-charge", root.GetProperty("circuit").GetString());
        Assert.Equal("R1", root.GetProperty("element").GetString());
        Assert.Equal(2, root.GetProperty("values").GetInt32());
        Assert.Equal(2, root.GetProperty("rows").GetArrayLength());

        JsonElement first = root.GetProperty("rows")[0];
        Assert.Equal(800, first.GetProperty("value").GetDouble());
        Assert.Equal(rows[0].Result.NetlistFingerprint, first.GetProperty("netlist").GetString());
        JsonElement outNode = first.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("id").GetString() == "out");
        Assert.True(outNode.GetProperty("finalVolts").GetDouble() > 4.9);
        Assert.True(outNode.GetProperty("settlingTimeSeconds").GetDouble() > 0);
        Assert.NotEqual(
            root.GetProperty("rows")[0].GetProperty("netlist").GetString(),
            root.GetProperty("rows")[1].GetProperty("netlist").GetString());
    }

    [Fact]
    public void TheJsonIsDeterministic()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000), "R1", [800, 1_200]);

        Assert.Equal(
            TransientSweepJson.ToReportJson("rc.json", "rc", "R1", rows),
            TransientSweepJson.ToReportJson("rc.json", "rc", "R1", rows));
    }

    [Fact]
    public void ALabelWithSeparatorsCannotForgeACsvRow()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000), "R1", [800, 1_200]);
        const string hostile = "rc,\ncharge";

        string csv = TransientSweepCsv.Write(hostile, hostile, "R1", rows);
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("# source: rc, charge", lines);
        Assert.Contains("# circuit: rc, charge", lines);
        // The comment stayed one line, so the header and data rows are where they belong.
        Assert.Single(lines, line => line.StartsWith("value,netlist", StringComparison.Ordinal));
        Assert.Equal(1 + 12 + (2 * 3), lines.Length);
    }

    private static IReadOnlyList<TransientSweepRow> Rows(
        TransientNetlist netlist,
        string elementId,
        double[] values) =>
        TransientSweep.Run(
            netlist,
            elementId,
            values,
            new TransientOptions { StopTime = 5e-2, Steps = 2_000 });

    private static TransientNetlist Rc(double ohms) => new TransientNetlist("gnd")
        .AddVoltageSource("V1", "in", "gnd", 5)
        .AddResistor("R1", "in", "out", ohms)
        .AddCapacitor("C1", "out", "gnd", 1e-6)
        .SetCapacitorInitialVoltage("C1", 0);
}
