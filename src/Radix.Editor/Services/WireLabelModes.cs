namespace Radix.Editor.Services;

/// <summary>
/// Presentation-only wire label detail. Wire labels include the net name and its
/// current value; hiding them never changes routing, connectivity, or simulation.
/// </summary>
public static class WireLabelModes
{
    public const string All = "All";

    public const string Named = "Named";

    public const string None = "None";

    public static IReadOnlyList<string> Options { get; } = [All, Named, None];

    public static string Normalize(string? mode) =>
        mode is All or Named or None ? mode : All;

    public static bool Shows(string? mode, string? label) =>
        Normalize(mode) switch
        {
            None => false,
            Named => !string.IsNullOrWhiteSpace(label),
            _ => true,
        };
}
