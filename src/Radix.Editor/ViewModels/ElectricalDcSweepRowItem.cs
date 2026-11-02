using System.Globalization;

namespace Radix.Editor.ViewModels;

/// <summary>
/// One line of a DC parameter sweep: a swept value, a node, and that node's solved voltage. The
/// value is printed only on the first row of each value, so a study reads as blocks instead of
/// repeating one number down the column. A value that could not solve keeps a row of its own,
/// because a missing point in a transfer curve is information rather than an absence of data.
/// </summary>
public sealed class ElectricalDcSweepRowItem
{
    public ElectricalDcSweepRowItem(double value, string nodeId, double? volts, bool showValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        ValueText = showValue ? value.ToString("G4", CultureInfo.InvariantCulture) : string.Empty;
        NodeId = nodeId;
        VoltsText = volts is double solved
            ? solved.ToString("G6", CultureInfo.InvariantCulture) + " V"
            : "-";
    }

    public string ValueText { get; }

    public string NodeId { get; }

    public string VoltsText { get; }
}
