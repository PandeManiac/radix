using System.Globalization;

namespace Radix.Editor.ViewModels;

/// <summary>
/// One line of a panel sweep: a swept value, a node, and that node's peak, final value and settling
/// time. The value is printed only on the first row of each value, so a study reads as blocks
/// without repeating the same number down the column. A value that could not solve keeps a row of
/// its own, because a study with a missing value is not the same study.
/// </summary>
public sealed class ElectricalSweepRowItem
{
    public ElectricalSweepRowItem(
        double value,
        string nodeId,
        Radix.Core.Electrical.TransientNodeMetrics? metrics,
        bool showValue,
        string? scope = null,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        ValueText = showValue ? value.ToString("G4", CultureInfo.InvariantCulture) : string.Empty;
        NodeId = nodeId;
        Scope = string.IsNullOrWhiteSpace(scope) ? "root" : scope;
        Name = string.IsNullOrWhiteSpace(name) ? nodeId : name;
        DisplayName = Scope == "root" ? Name : $"{Scope} · {Name}";
        PeakText = metrics is null
            ? "-"
            : metrics.MaximumVolts.ToString("G4", CultureInfo.InvariantCulture);
        FinalText = metrics is null
            ? "-"
            : metrics.FinalVolts.ToString("G4", CultureInfo.InvariantCulture);
        SettleText = metrics?.SettlingTimeSeconds is double settled
            ? settled.ToString("G4", CultureInfo.InvariantCulture) + " s"
            : metrics is null ? "-" : "not settled";
    }

    public string ValueText { get; }

    public string NodeId { get; }

    public string Scope { get; }

    public string Name { get; }

    public string DisplayName { get; }

    public string PeakText { get; }

    public string FinalText { get; }

    public string SettleText { get; }
}
