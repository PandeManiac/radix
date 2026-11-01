using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A comparison is a claim about two studies, so its export has to name both sides, keep the
/// added/removed values that a metric-only diff would miss, repeat byte for byte, and still produce
/// a valid document when there is nothing to report.
/// </summary>
public sealed class TransientSweepComparisonExportTests
{
    [Fact]
    public void TheCsvNamesBothStudiesAndListsEachDifference()
    {
        SweepComparison comparison = Compare([500, 1_000], [500, 1_500]);

        string csv = TransientSweepComparisonCsv.Write("rc.json", "rc-charge", comparison, "base.json");
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] data = lines.Where(line => !line.StartsWith('#')).ToArray();

        Assert.Contains("# format: radix-transient-sweep-comparison", lines);
        Assert.Contains("# element: R1", lines);
        Assert.Contains("# against: base.json", lines);
        Assert.Contains("# identical: false", lines);
        Assert.Equal("value,node,metric,before,after", data[0]);
        Assert.Contains("1000,,removed,,", csv, StringComparison.Ordinal);
        Assert.Contains("1500,,added,,", csv, StringComparison.Ordinal);
        Assert.Equal(1 + 2 + comparison.Differences.Count, data.Length);
    }

    [Fact]
    public void TheJsonDescribesTheComparisonAndBothSides()
    {
        SweepComparison comparison = Compare([500, 1_000], [500, 1_500]);

        using JsonDocument report = JsonDocument.Parse(
            TransientSweepComparisonJson.ToReportJson("rc.json", "rc-charge", comparison, "base.json"));
        JsonElement root = report.RootElement;

        Assert.Equal("radix-transient-sweep-comparison", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("R1", root.GetProperty("element").GetString());
        Assert.Equal("base.json", root.GetProperty("against").GetString());
        Assert.False(root.GetProperty("identical").GetBoolean());
        Assert.Equal(comparison.Differences.Count, root.GetProperty("differences").GetArrayLength());
        // JsonElement's indexer addresses object properties, so arrays must be enumerated.
        Assert.Equal(
            new[] { 1_500d },
            root.GetProperty("addedValues").EnumerateArray().Select(value => value.GetDouble()));
        Assert.Equal(
            new[] { 1_000d },
            root.GetProperty("removedValues").EnumerateArray().Select(value => value.GetDouble()));
        // Differences may legitimately be empty when only the value set changed; the shape of a
        // difference is covered by the ledger test, which is guaranteed to produce one.
    }

    [Fact]
    public void AnIdenticalComparisonExportsWithoutRows()
    {
        SweepComparison comparison = Compare([500, 1_000], [500, 1_000]);
        Assert.True(comparison.IsIdentical);

        string csv = TransientSweepComparisonCsv.Write(null, null, comparison, "base.json");
        string[] data = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();
        Assert.Single(data);
        Assert.Contains("# identical: true", csv, StringComparison.Ordinal);
        Assert.Contains("# differences: 0", csv, StringComparison.Ordinal);

        using JsonDocument report = JsonDocument.Parse(
            TransientSweepComparisonJson.ToReportJson(null, null, comparison, "base.json"));
        Assert.True(report.RootElement.GetProperty("identical").GetBoolean());
        Assert.Equal(0, report.RootElement.GetProperty("differences").GetArrayLength());
    }

    [Fact]
    public void BothExportsAreDeterministic()
    {
        SweepComparison comparison = Compare([500, 1_000], [500, 1_500]);

        Assert.Equal(
            TransientSweepComparisonCsv.Write("rc.json", "rc", comparison, "base.json"),
            TransientSweepComparisonCsv.Write("rc.json", "rc", comparison, "base.json"));
        Assert.Equal(
            TransientSweepComparisonJson.ToReportJson("rc.json", "rc", comparison, "base.json"),
            TransientSweepComparisonJson.ToReportJson("rc.json", "rc", comparison, "base.json"));
    }

    [Fact]
    public void ARowLevelDifferenceIsNamedWithoutANode()
    {
        // The energy ledger is a whole-run metric: it has no node, and the export says so rather
        // than leaving a column that reads as a missing node.
        TransientSweepRow[] rows = Rows([500]);
        TransientSweepReport archived = TransientSweepReport.LoadFromString(
            TransientSweepJson.ToReportJson(null, null, "R1", rows));
        TransientSweepReport coarser = archived with
        {
            Rows = archived.Rows
                .Select(row => row with { EnergyResidual = (row.EnergyResidual ?? 0) + 0.01 })
                .ToArray(),
        };

        SweepComparison comparison = TransientSweepComparison.Compare(archived, coarser);
        string csv = TransientSweepComparisonCsv.Write(null, null, comparison, "base.json");

        Assert.Contains(",\"(run)\",\"energy_residual\",", csv, StringComparison.Ordinal);

        using JsonDocument report = JsonDocument.Parse(
            TransientSweepComparisonJson.ToReportJson(null, null, comparison, "base.json"));
        JsonElement difference = report.RootElement.GetProperty("differences").EnumerateArray().Single();
        Assert.Equal("(run)", difference.GetProperty("node").GetString());
        Assert.Equal("energy_residual", difference.GetProperty("metric").GetString());
        Assert.True(difference.GetProperty("after").GetDouble() > difference.GetProperty("before").GetDouble());
    }

    private static SweepComparison Compare(double[] archivedValues, double[] currentValues)
    {
        TransientSweepReport archived = TransientSweepReport.LoadFromString(
            TransientSweepJson.ToReportJson(null, "rc", "R1", Rows(archivedValues)));
        return TransientSweepComparison.Compare(archived, Rows(currentValues));
    }

    private static TransientSweepRow[] Rows(double[] values) =>
        TransientSweep.Run(
                new TransientNetlist("gnd")
                    .AddVoltageSource("V1", "in", "gnd", 5)
                    .AddResistor("R1", "in", "out", 1_000)
                    .AddCapacitor("C1", "out", "gnd", 1e-6)
                    .SetCapacitorInitialVoltage("C1", 0),
                "R1",
                values,
                new TransientOptions { StopTime = 5e-2, Steps = 2_000 })
            .ToArray();
}
