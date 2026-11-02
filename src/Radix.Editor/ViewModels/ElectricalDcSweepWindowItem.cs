using System.Globalization;
using Radix.Core.Electrical;

namespace Radix.Editor.ViewModels;

/// <summary>
/// One node's measured input windows from a DC sweep: the ranges where the curve does not amplify,
/// and therefore the levels that node's input can hold. The bands between the windows are where a
/// disturbance is magnified, so they are the forbidden zones rather than a gap in the report.
/// </summary>
public sealed class ElectricalDcSweepWindowItem
{
    public ElectricalDcSweepWindowItem(
        string nodeId,
        IReadOnlyList<DcInputWindow> windows,
        string? scope = null,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(windows);

        NodeId = nodeId;
        Scope = string.IsNullOrWhiteSpace(scope) ? "root" : scope;
        Name = string.IsNullOrWhiteSpace(name) ? nodeId : name;
        DisplayName = Scope == "root" ? Name : $"{Scope} · {Name}";
        WindowsText = windows.Count == 0
            ? "no window: the curve amplifies across the whole sweep"
            : string.Join(
                " · ",
                windows.Select(window => $"{Number(window.From)} → {Number(window.To)}"));

        SummaryText = windows.Count switch
        {
            0 => "0 windows",
            1 => $"1 window, {Number(windows[0].Width)} wide",
            _ => $"{windows.Count} windows",
        };
    }

    public string NodeId { get; }

    public string Scope { get; }

    public string Name { get; }

    public string DisplayName { get; }

    /// <summary>The windows as <c>from → to</c> ranges, or a sentence when there are none.</summary>
    public string WindowsText { get; }

    /// <summary>How many windows there are, with the width when there is only one.</summary>
    public string SummaryText { get; }

    private static string Number(double value) => value.ToString("G4", CultureInfo.InvariantCulture);
}
