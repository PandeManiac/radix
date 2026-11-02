using System.Globalization;

namespace Radix.Editor.ViewModels;

/// <summary>
/// One line of an AC sweep: a frequency, a node, and that node's gain and phase there. The
/// frequency prints only on the first row of its block, so a sweep reads as a sequence of
/// frequencies instead of repeating the same number down the column. A node with no response has
/// no phase, and <c>-</c> says so rather than inventing zero degrees for a dead output.
/// </summary>
public sealed class ElectricalAcSweepRowItem
{
    public ElectricalAcSweepRowItem(
        double hertz,
        string nodeId,
        double? gainDecibels,
        double? phaseDegrees,
        bool showFrequency,
        string? scope = null,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        FrequencyText = showFrequency
            ? hertz.ToString("G4", CultureInfo.InvariantCulture) + " Hz"
            : string.Empty;
        NodeId = nodeId;
        Scope = string.IsNullOrWhiteSpace(scope) ? "root" : scope;
        Name = string.IsNullOrWhiteSpace(name) ? nodeId : name;
        DisplayName = Scope == "root" ? Name : $"{Scope} · {Name}";
        GainText = gainDecibels is double gain
            ? gain.ToString("G4", CultureInfo.InvariantCulture) + " dB"
            : "-";
        PhaseText = phaseDegrees is double phase
            ? phase.ToString("G4", CultureInfo.InvariantCulture) + "°"
            : "-";
    }

    public string FrequencyText { get; }

    public string NodeId { get; }

    public string Scope { get; }

    public string Name { get; }

    public string DisplayName { get; }

    /// <summary>Gain in decibels, or <c>-</c> when the node has no measurable response.</summary>
    public string GainText { get; }

    /// <summary>Phase in degrees, or <c>-</c> when there is no response to measure.</summary>
    public string PhaseText { get; }
}
