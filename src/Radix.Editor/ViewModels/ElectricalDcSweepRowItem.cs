using System.Globalization;

namespace Radix.Editor.ViewModels;

/// <summary>
/// One line of a DC parameter sweep: a swept value, a node, that node's solved voltage, and the
/// measured local gain there. The value is printed only on the first row of each value, so a study
/// reads as blocks instead of repeating one number down the column. A value that could not solve
/// keeps a row of its own, because a missing point in a transfer curve is information rather than
/// an absence of data.
/// </summary>
public sealed class ElectricalDcSweepRowItem
{
    public ElectricalDcSweepRowItem(
        double value,
        string nodeId,
        double? volts,
        double? gain,
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
        VoltsText = volts is double solved
            ? solved.ToString("G6", CultureInfo.InvariantCulture) + " V"
            : "-";

        // A slope needs two solved points to exist; "-" is the honest report of one that does
        // not, and is deliberately distinct from a measured zero.
        GainText = gain is double slope
            ? slope.ToString("G4", CultureInfo.InvariantCulture)
            : "-";
    }

    public string ValueText { get; }

    public string NodeId { get; }

    public string Scope { get; }

    public string Name { get; }

    public string DisplayName { get; }

    /// <summary>Solved node voltage, or <c>-</c> when this value could not solve.</summary>
    public string VoltsText { get; }

    /// <summary>Measured dV(node)/d(swept) in V/V, or <c>-</c> when no slope is defined.</summary>
    public string GainText { get; }
}
