using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Comparing two studies is how a change gets judged, so the comparison must be exact when nothing
/// changed, must name the metric and node when something did, and must not silently drop a value or
/// node that exists on only one side.
/// </summary>
public sealed class TransientSweepComparisonTests
{
    [Fact]
    public void ASavedStudyLoadsBackWithItsRowsAndNodes()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000, 1e-6), "R1", [800, 1_200]);
        string json = TransientSweepJson.ToReportJson("rc.json", "rc-charge", "R1", rows);

        TransientSweepReport report = TransientSweepReport.LoadFromString(json);

        Assert.Equal("rc-charge", report.Circuit);
        Assert.Equal("R1", report.Element);
        Assert.Equal("backward-euler", report.Method);
        Assert.Equal(2, report.Values);
        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(rows[0].Result.NetlistFingerprint, report.Rows[0].Netlist);
        Assert.Equal(
            rows[0].Result.Metrics!.Nodes.Select(node => node.NodeId),
            report.Rows[0].Nodes.Select(node => node.Id));
        Assert.Equal(
            rows[0].Result.Metrics!.Nodes.Single(node => node.NodeId == "out").SettlingTimeSeconds,
            report.Rows[0].Nodes.Single(node => node.Id == "out").SettlingTimeSeconds);
    }

    [Fact]
    public void LoadingRejectsAReportOfAnotherFormatOrVersion()
    {
        ProjectFormatException format = Assert.Throws<ProjectFormatException>(
            () => TransientSweepReport.LoadFromString(
                """{"format":"radix-transient-netlist","version":1,"element":"R1","rows":[{"value":1,"completed":true,"nodes":[]}]}"""));
        Assert.Contains("radix-transient-sweep", format.Message, StringComparison.Ordinal);

        ProjectFormatException version = Assert.Throws<ProjectFormatException>(
            () => TransientSweepReport.LoadFromString(
                """{"format":"radix-transient-sweep","version":2,"element":"R1","rows":[{"value":1,"completed":true,"nodes":[]}]}"""));
        Assert.Contains("version 2", version.Message, StringComparison.Ordinal);

        Assert.Throws<ProjectFormatException>(
            () => TransientSweepReport.LoadFromString(
                """{"format":"radix-transient-sweep","version":1,"element":"R1","rows":[]}"""));
    }

    [Fact]
    public void AnIdenticalStudyComparesIdentical()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000, 1e-6), "R1", [800, 1_200]);
        TransientSweepReport archived = TransientSweepReport.LoadFromString(
            TransientSweepJson.ToReportJson("rc.json", "rc", "R1", rows));

        SweepComparison comparison = TransientSweepComparison.Compare(archived, rows);

        Assert.True(comparison.IsIdentical);
        Assert.Empty(comparison.Differences);
        Assert.Empty(comparison.AddedValues);
        Assert.Empty(comparison.RemovedValues);
        Assert.Equal(0, comparison.ChangedValues);
    }

    [Fact]
    public void ADoubledCapacitanceIsReportedAsSlowerSettling()
    {
        IReadOnlyList<TransientSweepRow> archivedRows = Rows(Rc(1_000, 1e-6), "R1", [800, 1_200]);
        TransientSweepReport archived = TransientSweepReport.LoadFromString(
            TransientSweepJson.ToReportJson("rc.json", "rc", "R1", archivedRows));
        // tau doubles, so every value must settle later. Peak and final barely move.
        IReadOnlyList<TransientSweepRow> current = Rows(Rc(1_000, 2e-6), "R1", [800, 1_200]);

        SweepComparison comparison = TransientSweepComparison.Compare(archived, current);

        Assert.False(comparison.IsIdentical);
        Assert.Equal(2, comparison.ChangedValues);
        SweepDifference[] settling = comparison.Differences
            .Where(difference => difference.Metric == "settle_s")
            .ToArray();
        Assert.Equal(2, settling.Length);
        Assert.All(settling, difference =>
        {
            Assert.Equal("out", difference.NodeId);
            Assert.NotNull(difference.Before);
            Assert.NotNull(difference.After);
            Assert.True(
                difference.After > difference.Before,
                $"settling should grow: {difference.Before} -> {difference.After}");
        });
    }

    [Fact]
    public void ValuesOnlyOnOneSideAreReported()
    {
        TransientSweepReport archived = TransientSweepReport.LoadFromString(
            TransientSweepJson.ToReportJson(null, null, "R1", Rows(Rc(1_000, 1e-6), "R1", [800, 1_200])));

        SweepComparison comparison = TransientSweepComparison.Compare(
            archived, Rows(Rc(1_000, 1e-6), "R1", [800, 1_600]));

        Assert.Equal(new[] { 1_600d }, comparison.AddedValues);
        Assert.Equal(new[] { 1_200d }, comparison.RemovedValues);
        Assert.False(comparison.IsIdentical);
    }

    [Fact]
    public void AValueThatStoppedSolvingIsReportedAsACompletionChange()
    {
        TransientSweepReport archived = TransientSweepReport.LoadFromString(
            TransientSweepJson.ToReportJson(null, null, "R1", Rows(Rc(1_000, 1e-6), "R1", [800, 1_200])));
        // The floating C2 has no DC path, so every run now fails before integrating.
        TransientNetlist broken = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .AddCapacitor("C2", "floating", "gnd", 1e-6);

        SweepComparison comparison = TransientSweepComparison.Compare(
            archived, Rows(broken, "R1", [800, 1_200]));

        SweepDifference[] completions = comparison.Differences
            .Where(difference => difference.Metric == "completed")
            .ToArray();
        Assert.Equal(2, completions.Length);
        Assert.All(completions, difference =>
        {
            Assert.Equal(1d, difference.Before!.Value);
            Assert.Equal(0d, difference.After!.Value);
        });
        // The node metrics that used to exist are gone, and the comparison says so.
        Assert.Contains(
            comparison.Differences,
            difference => difference.NodeId == "out" && difference.Metric == "final_v" && difference.After is null);
    }

    [Fact]
    public void ComparisonIsDeterministic()
    {
        TransientSweepReport archived = TransientSweepReport.LoadFromString(
            TransientSweepJson.ToReportJson(null, null, "R1", Rows(Rc(1_000, 1e-6), "R1", [800, 1_200])));
        IReadOnlyList<TransientSweepRow> current = Rows(Rc(1_000, 2e-6), "R1", [1_200, 800]);

        SweepComparison first = TransientSweepComparison.Compare(archived, current);
        SweepComparison second = TransientSweepComparison.Compare(archived, current);

        Assert.Equal(first.Differences, second.Differences);
        Assert.Equal(first.AddedValues, second.AddedValues);
        Assert.Equal(first.RemovedValues, second.RemovedValues);
        // Ordering is by value, then node, then metric in the declared order (peak, final, settle),
        // independent of the order of the current study. The metric order is deliberate: it matches
        // the columns of the printed table.
        string[] metricOrder = ["peak_v", "final_v", "settle_s"];
        Assert.Equal(
            first.Differences.Select(difference => (difference.Value, difference.NodeId, difference.Metric)),
            first.Differences
                .OrderBy(difference => difference.Value)
                .ThenBy(difference => difference.NodeId, StringComparer.Ordinal)
                .ThenBy(difference => Array.IndexOf(metricOrder, difference.Metric))
                .Select(difference => (difference.Value, difference.NodeId, difference.Metric)));
    }

    [Fact]
    public void AnArchivedReportWithoutALedgerDoesNotManufactureADifference()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000, 1e-6), "R1", [800, 1_200]);
        string json = TransientSweepJson.ToReportJson(null, null, "R1", rows);
        string without = string.Join(
            '\n',
            json.Split('\n').Where(line => !line.Contains("energyResidual", StringComparison.Ordinal)));

        TransientSweepReport archived = TransientSweepReport.LoadFromString(without);
        SweepComparison comparison = TransientSweepComparison.Compare(archived, rows);

        Assert.DoesNotContain(
            comparison.Differences, difference => difference.Metric == "energy_residual");
        Assert.True(comparison.IsIdentical);
    }

    [Fact]
    public void AToleranceSuppressesNoiseButKeepsRealChanges()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000, 1e-6), "R1", [800]);
        TransientSweepReport archived = TransientSweepReport.LoadFromString(
            TransientSweepJson.ToReportJson(null, null, "R1", rows));

        // A last-bit difference between builds is not a circuit change; a real move is.
        TransientSweepReport noise = Perturb(archived, 1e-9);
        Assert.False(TransientSweepComparison.Compare(archived, noise).IsIdentical);
        Assert.True(TransientSweepComparison.Compare(archived, noise, 1e-6).IsIdentical);

        TransientSweepReport change = Perturb(archived, 0.5);
        Assert.False(TransientSweepComparison.Compare(archived, change, 1e-6).IsIdentical);
    }

    [Fact]
    public void AnInvalidToleranceIsRejected()
    {
        TransientSweepReport report = TransientSweepReport.LoadFromString(
            TransientSweepJson.ToReportJson(null, null, "R1", Rows(Rc(1_000, 1e-6), "R1", [800])));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransientSweepComparison.Compare(report, report, -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransientSweepComparison.Compare(report, report, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransientSweepComparison.Compare(report, report, double.NaN));
    }

    [Fact]
    public void TheLedgerIsComparedWhenBothStudiesRecordIt()
    {
        IReadOnlyList<TransientSweepRow> rows = Rows(Rc(1_000, 1e-6), "R1", [800]);
        TransientSweepReport archived = TransientSweepReport.LoadFromString(
            TransientSweepJson.ToReportJson(null, null, "R1", rows));
        TransientSweepReport coarser = archived with
        {
            Rows = archived.Rows
                .Select(row => row with { EnergyResidual = (row.EnergyResidual ?? 0) + 0.01 })
                .ToArray(),
        };

        SweepComparison comparison = TransientSweepComparison.Compare(archived, coarser);

        SweepDifference ledger = Assert.Single(
            comparison.Differences, difference => difference.Metric == "energy_residual");
        Assert.Equal(0.01, ledger.After!.Value - ledger.Before!.Value, 9);
    }

    /// <summary>
    /// Shifts every non-ground final voltage, which is what a last-bit difference between builds
    /// looks like. Ground is left alone: a metric at exactly zero has no relative scale, so a
    /// tolerance cannot judge it and the comparison deliberately reports any change there.
    /// </summary>
    private static TransientSweepReport Perturb(TransientSweepReport report, double delta) =>
        report with
        {
            Rows = report.Rows
                .Select(row => row with
                {
                    Nodes = row.Nodes
                        .Select(node => node.Id == "gnd"
                            ? node
                            : node with { FinalVolts = node.FinalVolts + delta })
                        .ToArray(),
                })
                .ToArray(),
        };

    private static IReadOnlyList<TransientSweepRow> Rows(
        TransientNetlist netlist,
        string elementId,
        double[] values) =>
        TransientSweep.Run(
            netlist,
            elementId,
            values,
            new TransientOptions { StopTime = 5e-2, Steps = 2_000 });

    private static TransientNetlist Rc(double ohms, double farads) => new TransientNetlist("gnd")
        .AddVoltageSource("V1", "in", "gnd", 5)
        .AddResistor("R1", "in", "out", ohms)
        .AddCapacitor("C1", "out", "gnd", farads)
        .SetCapacitorInitialVoltage("C1", 0);
}
