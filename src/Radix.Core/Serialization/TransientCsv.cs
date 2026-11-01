using System.Globalization;
using System.Text;
using Radix.Core.Electrical;

namespace Radix.Core.Serialization;

/// <summary>
/// Writes a transient run as a wide CSV table: one row per solved point with its time and one
/// column per node, in ordinal node order. Numbers use the invariant round-trip format, so a
/// reader gets back exactly the doubles the solver produced; node names are quoted and their
/// quotes doubled, so an id containing a comma cannot break the table; and a node missing from
/// a point leaves an empty field rather than a fabricated zero. The panel and the CLI share this
/// writer, so their files cannot drift apart.
/// </summary>
public static class TransientCsv
{
    /// <summary>The full table, header included, with <c>\n</c> row endings.</summary>
    public static string Write(TransientResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        string[] nodes = result.Points
            .SelectMany(point => point.NodeVoltages.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        builder.Append("time_s");
        foreach (string node in nodes)
        {
            builder.Append(',').Append(Text(node));
        }

        builder.Append('\n');
        foreach (TransientPoint point in result.Points)
        {
            builder.Append(Number(point.Time));
            foreach (string node in nodes)
            {
                builder.Append(',');
                if (point.NodeVoltages.TryGetValue(node, out double volts))
                {
                    builder.Append(Number(volts));
                }
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string Text(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
