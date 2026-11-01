using Radix.Core.Electrical;

namespace Radix.Core.Serialization;

/// <summary>
/// One metric that differs between an archived study and a current one. A null side means the node
/// was absent there, which is information in itself: a circuit that gained or lost a node should not
/// look like an unchanged one.
/// </summary>
public sealed record SweepDifference(
    double Value,
    string NodeId,
    string Metric,
    double? Before,
    double? After);

/// <summary>
/// The result of comparing a study against an archived one. Values are matched by exact equality
/// (both formats write round-trip numbers, so a value that was not touched matches exactly), and
/// ordering is by value, then node, then metric, so the report is stable enough to diff.
/// </summary>
public sealed record SweepComparison(
    string Element,
    IReadOnlyList<SweepDifference> Differences,
    IReadOnlyList<double> AddedValues,
    IReadOnlyList<double> RemovedValues)
{
    public bool IsIdentical =>
        Differences.Count == 0 && AddedValues.Count == 0 && RemovedValues.Count == 0;

    /// <summary>How many swept values have at least one differing metric.</summary>
    public int ChangedValues => Differences.Select(difference => difference.Value).Distinct().Count();
}

/// <summary>
/// Compares a saved study with a study just computed, so "did my change help?" is answered by
/// numbers rather than by reading two tables. The comparison is exact: identical circuits produce
/// identical doubles, so any difference is a real change in the circuit rather than formatting.
/// </summary>
public static class TransientSweepComparison
{
    public static SweepComparison Compare(
        TransientSweepReport baseline,
        IReadOnlyList<TransientSweepRow> current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        return Compare(baseline, IndexRows(current));
    }

    public static SweepComparison Compare(TransientSweepReport baseline, TransientSweepReport current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var index = new Dictionary<double, Side>(current.Rows.Count);
        foreach (TransientSweepReportRow row in current.Rows)
        {
            index[row.Value] = new Side(
                row.Completed,
                row.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal));
        }

        return Compare(baseline, index);
    }

    private static SweepComparison Compare(
        TransientSweepReport baseline,
        IReadOnlyDictionary<double, Side> current)
    {
        var before = new Dictionary<double, Side>(baseline.Rows.Count);
        foreach (TransientSweepReportRow row in baseline.Rows)
        {
            before[row.Value] = new Side(
                row.Completed,
                row.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal));
        }

        var removed = before.Keys.Where(value => !current.ContainsKey(value)).Order().ToList();
        var added = current.Keys.Where(value => !before.ContainsKey(value)).Order().ToList();
        var differences = new List<SweepDifference>();

        foreach (double value in before.Keys.Where(current.ContainsKey).Order())
        {
            Side archived = before[value];
            Side fresh = current[value];
            // A run that stopped solving at one value is the most important difference of all.
            AddException(differences, value, "completed", archived.Completed, fresh.Completed);

            string[] nodes = archived.Nodes.Keys
                .Union(fresh.Nodes.Keys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            foreach (string node in nodes)
            {
                archived.Nodes.TryGetValue(node, out TransientSweepNodeMetrics? old);
                fresh.Nodes.TryGetValue(node, out TransientSweepNodeMetrics? updated);
                AddDifference(differences, value, node, "peak_v", old?.MaximumVolts, updated?.MaximumVolts);
                AddDifference(differences, value, node, "final_v", old?.FinalVolts, updated?.FinalVolts);
                AddDifference(
                    differences, value, node, "settle_s",
                    old?.SettlingTimeSeconds, updated?.SettlingTimeSeconds);
            }
        }

        return new SweepComparison(baseline.Element, differences, added, removed);
    }

    private static IReadOnlyDictionary<double, Side> IndexRows(IReadOnlyList<TransientSweepRow> rows)
    {
        var index = new Dictionary<double, Side>(rows.Count);
        foreach (TransientSweepRow row in rows)
        {
            if (index.ContainsKey(row.Value))
            {
                throw new ArgumentException(
                    $"A study cannot contain the value {row.Value} twice.", nameof(rows));
            }

            index[row.Value] = new Side(
                row.Result.Completed,
                row.Result.Completed && row.Result.Metrics is TransientMetrics metrics
                    ? metrics.Nodes.ToDictionary(
                        node => node.NodeId,
                        node => new TransientSweepNodeMetrics(
                            node.NodeId,
                            node.MinimumVolts,
                            node.MaximumVolts,
                            node.FinalVolts,
                            node.PeakTimeSeconds,
                            node.SettlingTimeSeconds),
                        StringComparer.Ordinal)
                    : new Dictionary<string, TransientSweepNodeMetrics>(StringComparer.Ordinal));
        }

        return index;
    }

    private static void AddDifference(
        List<SweepDifference> differences,
        double value,
        string node,
        string metric,
        double? before,
        double? after)
    {
        // Null == null covers "no metrics on either side"; equal doubles compare equal, because a
        // byte-identical solve produces bit-identical results.
        if (before == after)
        {
            return;
        }

        differences.Add(new SweepDifference(value, node, metric, before, after));
    }

    private static void AddException(
        List<SweepDifference> differences,
        double value,
        string metric,
        bool before,
        bool after)
    {
        if (before == after)
        {
            return;
        }

        differences.Add(new SweepDifference(value, string.Empty, metric, before ? 1 : 0, after ? 1 : 0));
    }

    private sealed record Side(
        bool Completed,
        IReadOnlyDictionary<string, TransientSweepNodeMetrics> Nodes);
}
