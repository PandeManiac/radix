using System.Globalization;
using System.Text;

namespace Radix.Core.Electrical;

/// <summary>
/// One point of a DC parameter sweep: the swept value and either the operating point the
/// solver found or the reason it could not. A failed row keeps its value so a reader can
/// see where the circuit stopped solving instead of the row silently vanishing.
/// </summary>
public sealed record DcSweepRow(
    double Value,
    DcSolution? Solution,
    DcSolveFailure? Failure,
    string? Message)
{
    public bool Solved => Solution is not null;
}

/// <summary>
/// Solves one netlist once per value of a single element parameter. This is how a
/// voltage-transfer characteristic is measured: every row is a real operating point of a
/// fresh copy of the netlist with that one value changed, so a difference between rows
/// belongs to the value rather than to how the circuit was rebuilt.
/// </summary>
public static class DcSweep
{
    /// <summary>The largest number of rows one study may request.</summary>
    public const int MaximumRows = 4_096;

    /// <summary>
    /// Runs the study. A row that cannot solve is recorded with its failure classification
    /// rather than aborting the rest of the sweep; a parameter that is invalid for the
    /// element, such as a zero resistance, is a caller error and is thrown.
    /// </summary>
    public static IReadOnlyList<DcSweepRow> Run(
        DcNetlist netlist,
        string elementId,
        IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        ArgumentNullException.ThrowIfNull(values);

        double[] list = values as double[] ?? values.ToArray();
        if (list.Length == 0)
        {
            throw new ArgumentException("A DC sweep needs at least one value.", nameof(values));
        }

        if (list.Length > MaximumRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                list.Length,
                $"A DC sweep is limited to {MaximumRows} rows.");
        }

        var rows = new List<DcSweepRow>(list.Length);
        foreach (double value in list)
        {
            DcNetlist varied = netlist.WithElementValue(elementId, value);
            try
            {
                rows.Add(new DcSweepRow(value, varied.Solve(), null, null));
            }
            catch (DcSolveException exception)
            {
                rows.Add(new DcSweepRow(value, null, exception.Failure, exception.Message));
            }
        }

        return rows;
    }

    /// <summary>A uniform grid of <paramref name="count"/> values from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static double[] Linspace(double from, double to, int count) =>
        TransientSweep.Linspace(from, to, count);
}

/// <summary>
/// Deterministic text and CSV projections of a DC sweep. Both name the swept element and
/// emit every node in ordinal order, so a table copied from the terminal and a file
/// written by a script describe the same study.
/// </summary>
public static class DcSweepReport
{
    /// <summary>A fixed-width table: one row per swept value, one column per node.</summary>
    public static string ToText(DcNetlist netlist, string elementId, IReadOnlyList<DcSweepRow> rows)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentNullException.ThrowIfNull(rows);
        IReadOnlyList<string> nodes = netlist.Nodes;

        var text = new StringBuilder();
        text.Append(elementId)
            .Append(" sweep: ")
            .Append(rows.Count.ToString(CultureInfo.InvariantCulture))
            .Append(rows.Count == 1 ? " value, " : " values, ")
            .Append(nodes.Count.ToString(CultureInfo.InvariantCulture))
            .Append(nodes.Count == 1 ? " node" : " nodes")
            .AppendLine();
        text.Append(string.Create(CultureInfo.InvariantCulture, $"{elementId,12}"));
        foreach (string node in nodes)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $"{node,12}"));
        }

        text.AppendLine();
        foreach (DcSweepRow row in rows)
        {
            text.Append(Format(row.Value, 12));
            if (row.Solution is null)
            {
                text.Append("  failed: ").Append(DcNetlistJsonFailureName(row.Failure)).AppendLine();
                continue;
            }

            foreach (string node in nodes)
            {
                text.Append(Format(row.Solution.NodeVoltages[node], 12));
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// A wide CSV table: a <c>value</c> column, one column per node, and a <c>failure</c>
    /// column. A failed row leaves its node cells empty so the failure is visible rather
    /// than reported as zero volts.
    /// </summary>
    public static string ToCsv(DcNetlist netlist, IReadOnlyList<DcSweepRow> rows)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentNullException.ThrowIfNull(rows);
        IReadOnlyList<string> nodes = netlist.Nodes;

        var text = new StringBuilder();
        text.Append("value");
        foreach (string node in nodes)
        {
            text.Append(',').Append(Quote(node));
        }

        text.Append(",failure").AppendLine();
        foreach (DcSweepRow row in rows)
        {
            text.Append(row.Value.ToString("R", CultureInfo.InvariantCulture));
            foreach (string node in nodes)
            {
                text.Append(',');
                if (row.Solution is not null)
                {
                    text.Append(row.Solution.NodeVoltages[node].ToString("R", CultureInfo.InvariantCulture));
                }
            }

            text.Append(',');
            if (row.Solution is null)
            {
                text.Append(DcNetlistJsonFailureName(row.Failure));
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Format(double value, int width) =>
        value.ToString("G6", CultureInfo.InvariantCulture).PadLeft(width);

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string DcNetlistJsonFailureName(DcSolveFailure? failure) =>
        failure is DcSolveFailure value
            ? Radix.Core.Serialization.DcNetlistJson.FailureName(value)
            : "failed";
}
