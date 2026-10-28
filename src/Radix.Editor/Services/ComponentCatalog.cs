using Radix.Core.Model;

namespace Radix.Editor.Services;

public sealed record LibraryComponent(
    string Category,
    string Name,
    string Description,
    PrimitiveSpec Spec);

/// <summary>
/// Starter parts offered by the palette. These are inline primitive instances, not
/// special engine component types: any project can inspect or redefine them.
/// </summary>
public static class ComponentCatalog
{
    private static readonly LibraryComponent[] Entries =
    [
        new(
            "Combinational",
            "AND gate",
            "Two-input Boolean lookup, table 0001. The table is editable in the inspector.",
            new LookupSpec(2, [0, 0, 0, 1], 1)),
        new(
            "Combinational",
            "OR gate",
            "Two-input Boolean lookup, table 0111. The table is editable in the inspector.",
            new LookupSpec(2, [0, 1, 1, 1], 1)),
        new(
            "Combinational",
            "NOT gate",
            "One-input Boolean lookup, table 10.",
            new LookupSpec(1, [1, 0], 1)),
        new(
            "Combinational",
            "Ternary maximum",
            "Two-input lookup on the 0/1/2 domain. Change the table to build any ternary gate.",
            new LookupSpec(2, [0, 1, 2, 1, 1, 2, 2, 2, 2], 1)),
        new(
            "Signal",
            "Constant",
            "Drives a fixed value; the value index is editable in the inspector.",
            new ConstantSpec(0)),
        new(
            "Signal",
            "Clock wave",
            "Cyclic waveform source with editable phase durations and values.",
            new WaveformSpec([10, 10], [0, 1])),
        new(
            "Delay",
            "Delay line",
            "One-tick transport delay with an explicit power-on value.",
            new DelaySpec(1, 0)),
        new(
            "I/O",
            "Probe",
            "Named observation point; it never changes circuit values.",
            new ProbeSpec("probe")),
    ];

    public static IReadOnlyList<LibraryComponent> Components => Entries;

    public static IEnumerable<string> Categories =>
        Entries.Select(e => e.Category).Distinct(StringComparer.Ordinal);
}
