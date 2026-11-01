using System.Globalization;

namespace Radix.Editor.ViewModels;

/// <summary>
/// One line of a panel comparison: which value, which node, which metric, and the before/after
/// numbers. A missing side prints as <c>-</c>, which is how a node or value that exists in only one
/// study stays visible instead of being dropped as if it never mattered.
/// </summary>
public sealed class ElectricalSweepDifferenceItem
{
    public ElectricalSweepDifferenceItem(
        double value,
        string nodeId,
        string metric,
        double? before,
        double? after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);

        ValueText = value.ToString("G4", CultureInfo.InvariantCulture);
        NodeId = nodeId;
        Metric = metric;
        BeforeText = before is double old ? old.ToString("G4", CultureInfo.InvariantCulture) : "-";
        AfterText = after is double fresh ? fresh.ToString("G4", CultureInfo.InvariantCulture) : "-";
    }

    public string ValueText { get; }

    public string NodeId { get; }

    public string Metric { get; }

    public string BeforeText { get; }

    public string AfterText { get; }
}
