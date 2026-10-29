using System.Globalization;

namespace Radix.Core.Model;

/// <summary>
/// A user-authored bendpoint on a net's wire, in editor world coordinates.
/// Route points are presentation metadata: they never affect compiled connectivity
/// or simulation results.
/// </summary>
public readonly record struct NetWaypoint(double X, double Y)
{
    public const double MaxCoordinate = 1_000_000;

    public bool IsFinite =>
        double.IsFinite(X) &&
        double.IsFinite(Y) &&
        Math.Abs(X) <= MaxCoordinate &&
        Math.Abs(Y) <= MaxCoordinate;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:0.#}, {Y:0.#})");
}

/// <summary>Validation and normalization for presentation-only wire annotations.</summary>
public static class WireAnnotation
{
    public const int MaxRoutePoints = 64;

    public const string AutoColor = "auto";

    /// <summary>
    /// Accepts <c>null</c>, <see cref="AutoColor"/>, or a six-digit hexadecimal
    /// <c>#RRGGBB</c> color. Any other text is rejected rather than ignored.
    /// </summary>
    public static bool TryNormalizeColor(string? text, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string value = text.Trim();
        if (string.Equals(value, AutoColor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return false;
            }
        }

        normalized = value.ToUpperInvariant();
        return true;
    }

    public static string DescribeColor(string? color) =>
        string.IsNullOrEmpty(color) ? AutoColor : color;
}
