using System.Globalization;
using System.Text;
using Radix.Core.Electrical;

namespace Radix.Core.Serialization;

/// <summary>
/// Writes a parameter sweep as a long CSV table: one row per swept value and node. Long form is
/// the honest shape here, because metrics are per node and a value that fails to solve still has
/// to be recorded; wide form would either drop those rows or invent empty columns. Each row carries
/// its own value and netlist fingerprint, so rows can be diffed and sorted without context, and the
/// comment block names the study the way <see cref="TransientCsv"/> names a single run.
/// </summary>
public static class TransientSweepCsv
{
    public const string FormatName = "radix-transient-sweep";

    /// <summary>
    /// The full table, header included, with <c>\n</c> row endings. The stamp is deterministic: the
    /// same study written twice is byte-identical, so a diff shows what changed in the circuit
    /// rather than when the file was written.
    /// </summary>
    public static string Write(
        string? source,
        string? circuit,
        string elementId,
        IReadOnlyList<TransientSweepRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            throw new ArgumentException("A sweep export needs at least one row.", nameof(rows));
        }

        TransientOptions options = rows[0].Result.Options;
        var builder = new StringBuilder();
        Comment(builder, "format", FormatName);
        Comment(builder, "version", "1");
        if (!string.IsNullOrWhiteSpace(source))
        {
            Comment(builder, "source", source!);
        }

        if (!string.IsNullOrWhiteSpace(circuit))
        {
            Comment(builder, "circuit", circuit!);
        }

        Comment(builder, "element", elementId);
        Comment(builder, "values", rows.Count.ToString(CultureInfo.InvariantCulture));
        Comment(builder, "method", TransientNetlistJson.MethodName(options.Method));
        Comment(builder, "start_s", TransientCsv.Number(options.StartTime));
        Comment(builder, "stop_s", TransientCsv.Number(options.StopTime));
        Comment(builder, "steps", options.Steps.ToString(CultureInfo.InvariantCulture));
        Comment(builder, "adaptive", options.IsAdaptive ? "true" : "false");
        Comment(builder, "settling_band", TransientCsv.Number(options.SettlingBand));

        builder.Append(
            "value,netlist,completed,node,peak_v,final_v,settle_s,energy_residual,failure,message\n");
        foreach (TransientSweepRow row in rows)
        {
            string value = TransientCsv.Number(row.Value);
            string fingerprint = row.Result.NetlistFingerprint ?? string.Empty;
            string completed = row.Result.Completed ? "true" : "false";
            string ledger = row.Result.Energy is TransientEnergyBalance energy
                ? TransientCsv.Number(energy.RelativeResidual)
                : string.Empty;
            if (row.Result.Completed &&
                row.Result.Metrics is TransientMetrics metrics &&
                metrics.Nodes.Count > 0)
            {
                foreach (TransientNodeMetrics node in metrics.Nodes)
                {
                    builder.Append(value).Append(',').Append(fingerprint).Append(',')
                        .Append(completed).Append(',')
                        .Append(TransientCsv.Text(node.NodeId)).Append(',')
                        .Append(TransientCsv.Number(node.MaximumVolts)).Append(',')
                        .Append(TransientCsv.Number(node.FinalVolts)).Append(',')
                        .Append(node.SettlingTimeSeconds is double settled
                            ? TransientCsv.Number(settled)
                            : string.Empty)
                        .Append(',').Append(ledger)
                        .Append(",,\n");
                }

                continue;
            }

            // A failed value keeps its row: the study is a record, and a gap would hide the failure.
            builder.Append(value).Append(',').Append(fingerprint).Append(',')
                .Append(completed).Append(",,,,,") // node, peak, final, settle, ledger
                .Append(row.Result.Failure?.ToString() ?? string.Empty).Append(',')
                .Append(TransientCsv.Text(row.Result.Message ?? string.Empty)).Append('\n');
        }

        return builder.ToString();
    }

    private static void Comment(StringBuilder builder, string key, string value) =>
        builder.Append("# ").Append(key).Append(": ")
            .Append(TransientCsv.SanitizeComment(value)).Append('\n');
}
