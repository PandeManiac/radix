using System.Text;

namespace Radix.Core.Analysis;

/// <summary>
/// Plain-text rendering of an <see cref="InputSweepResult"/>. The CLI and the
/// workbench share this formatter so the same sweep reads the same way everywhere.
/// </summary>
public static class InputSweepTable
{
    public static string Format(InputSweepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Inputs.Count == 0 && result.Outputs.Count == 0)
        {
            return "(no interface ports)";
        }

        var widths = new int[result.Inputs.Count + result.Outputs.Count];
        for (int i = 0; i < result.Inputs.Count; i++)
        {
            SweepPort port = result.Inputs[i];
            widths[i] = Math.Max(port.PinId.Length, MaxValueWidth(port, result.Rows, row => row.Inputs[i]));
        }

        for (int i = 0; i < result.Outputs.Count; i++)
        {
            SweepPort port = result.Outputs[i];
            int column = result.Inputs.Count + i;
            widths[column] = Math.Max(port.PinId.Length, MaxValueWidth(port, result.Rows, row => row.Outputs[i]));
        }

        var builder = new StringBuilder();
        var header = new List<string>(widths.Length);
        for (int i = 0; i < result.Inputs.Count; i++)
        {
            header.Add(result.Inputs[i].PinId.PadRight(widths[i]));
        }

        for (int i = 0; i < result.Outputs.Count; i++)
        {
            int column = result.Inputs.Count + i;
            header.Add(result.Outputs[i].PinId.PadRight(widths[column]));
        }

        builder.AppendLine(string.Join(" | ", header));
        builder.AppendLine(string.Join("-+-", widths.Select(width => new string('-', width))));

        foreach (SweepRow row in result.Rows)
        {
            var cells = new List<string>(widths.Length);
            for (int i = 0; i < result.Inputs.Count; i++)
            {
                cells.Add(FormatValue(result.Inputs[i], row.Inputs[i]).PadRight(widths[i]));
            }

            for (int i = 0; i < result.Outputs.Count; i++)
            {
                int column = result.Inputs.Count + i;
                cells.Add(FormatValue(result.Outputs[i], row.Outputs[i]).PadRight(widths[column]));
            }

            builder.AppendLine(string.Join(" | ", cells));
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatValue(SweepPort port, int value) =>
        value < 0 ? "-" : port.Domain.ValueName(value);

    private static int MaxValueWidth(
        SweepPort port,
        IReadOnlyList<SweepRow> rows,
        Func<SweepRow, int> select)
    {
        int width = 1;
        foreach (SweepRow row in rows)
        {
            width = Math.Max(width, FormatValue(port, select(row)).Length);
        }

        return width;
    }
}
