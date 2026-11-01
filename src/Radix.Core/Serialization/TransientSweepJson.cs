using System.Text.Json;
using System.Text.Json.Serialization;
using Radix.Core.Electrical;

namespace Radix.Core.Serialization;

/// <summary>
/// Versioned JSON report for a parameter sweep. It mirrors <see cref="TransientSweepCsv"/>: the
/// study's settings once, then one object per value with its netlist fingerprint and node metrics.
/// A value that failed keeps its object with a failure name and no nodes, so "not computed" and
/// "computed as zero" cannot be confused by a reader.
/// </summary>
public static class TransientSweepJson
{
    public const string FormatName = TransientSweepCsv.FormatName;
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToReportJson(
        string? source,
        string? circuit,
        string elementId,
        IReadOnlyList<TransientSweepRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            throw new ArgumentException("A sweep report needs at least one row.", nameof(rows));
        }

        TransientOptions options = rows[0].Result.Options;
        return JsonSerializer.Serialize(
            new SweepDto(
                FormatName,
                FormatVersion,
                source,
                circuit,
                elementId,
                TransientNetlistJson.MethodName(options.Method),
                options.StartTime,
                options.StopTime,
                options.Steps,
                options.IsAdaptive,
                options.AdaptiveTolerance,
                options.SettlingBand,
                rows.Count,
                rows.Select(ToRowDto).ToArray()),
            JsonOptions);
    }

    private static RowDto ToRowDto(TransientSweepRow row) =>
        new(
            row.Value,
            row.Result.NetlistFingerprint,
            row.Result.Completed,
            row.Result.Failure?.ToString(),
            row.Result.Message,
            row.Result.Completed && row.Result.Metrics is TransientMetrics metrics
                ? metrics.Nodes
                    .Select(node => new NodeDto(
                        node.NodeId,
                        node.MinimumVolts,
                        node.MaximumVolts,
                        node.FinalVolts,
                        node.PeakTimeSeconds,
                        node.SettlingTimeSeconds))
                    .ToArray()
                : null);

    private sealed record SweepDto(
        string Format,
        int Version,
        string? Source,
        string? Circuit,
        string Element,
        string Method,
        double StartTime,
        double StopTime,
        int Steps,
        bool Adaptive,
        double? Tolerance,
        double SettlingBand,
        int Values,
        IReadOnlyList<RowDto> Rows);

    private sealed record RowDto(
        double Value,
        string? Netlist,
        bool Completed,
        string? Failure,
        string? Message,
        IReadOnlyList<NodeDto>? Nodes);

    private sealed record NodeDto(
        string Id,
        double MinimumVolts,
        double MaximumVolts,
        double FinalVolts,
        double PeakTimeSeconds,
        double? SettlingTimeSeconds);
}
