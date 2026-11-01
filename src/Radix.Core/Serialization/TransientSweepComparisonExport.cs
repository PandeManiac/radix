using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radix.Core.Serialization;

/// <summary>
/// Writes a study comparison as CSV so a review of "what changed" can be attached to a change, not
/// just read on screen. Rows are the differences themselves plus one row per value held by only one
/// study, in the comparison's own order, under a comment block that names both sides.
/// </summary>
public static class TransientSweepComparisonCsv
{
    public const string FormatName = "radix-transient-sweep-comparison";

    public static string Write(
        string? source,
        string? circuit,
        SweepComparison comparison,
        string? against)
    {
        ArgumentNullException.ThrowIfNull(comparison);

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

        Comment(builder, "element", comparison.Element);
        if (!string.IsNullOrWhiteSpace(against))
        {
            Comment(builder, "against", against!);
        }

        Comment(builder, "identical", comparison.IsIdentical ? "true" : "false");
        Comment(
            builder,
            "differences",
            comparison.Differences.Count.ToString(CultureInfo.InvariantCulture));
        Comment(
            builder,
            "values_added",
            comparison.AddedValues.Count.ToString(CultureInfo.InvariantCulture));
        Comment(
            builder,
            "values_removed",
            comparison.RemovedValues.Count.ToString(CultureInfo.InvariantCulture));

        builder.Append("value,node,metric,before,after\n");
        foreach (double removed in comparison.RemovedValues)
        {
            builder.Append(TransientCsv.Number(removed))
                .Append(",,").Append("removed,,\n");
        }

        foreach (double added in comparison.AddedValues)
        {
            builder.Append(TransientCsv.Number(added))
                .Append(",,").Append("added,,\n");
        }

        foreach (SweepDifference difference in comparison.Differences)
        {
            builder.Append(TransientCsv.Number(difference.Value)).Append(',')
                .Append(TransientCsv.Text(difference.NodeId.Length == 0 ? "(run)" : difference.NodeId))
                .Append(',').Append(TransientCsv.Text(difference.Metric)).Append(',')
                .Append(difference.Before is double before ? TransientCsv.Number(before) : string.Empty)
                .Append(',')
                .Append(difference.After is double after ? TransientCsv.Number(after) : string.Empty)
                .Append('\n');
        }

        return builder.ToString();
    }

    private static void Comment(StringBuilder builder, string key, string value) =>
        builder.Append("# ").Append(key).Append(": ")
            .Append(TransientCsv.SanitizeComment(value)).Append('\n');
}

/// <summary>
/// The same comparison as a versioned JSON report. It records the side it was taken against, so a
/// diff parked next to a commit still says what it compared with.
/// </summary>
public static class TransientSweepComparisonJson
{
    public const string FormatName = TransientSweepComparisonCsv.FormatName;
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
        SweepComparison comparison,
        string? against)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        return JsonSerializer.Serialize(
            new ComparisonDto(
                FormatName,
                FormatVersion,
                source,
                circuit,
                comparison.Element,
                against,
                comparison.IsIdentical,
                comparison.Differences
                    .Select(difference => new DifferenceDto(
                        difference.Value,
                        difference.NodeId.Length == 0 ? "(run)" : difference.NodeId,
                        difference.Metric,
                        difference.Before,
                        difference.After))
                    .ToArray(),
                comparison.AddedValues,
                comparison.RemovedValues),
            JsonOptions);
    }

    private sealed record ComparisonDto(
        string Format,
        int Version,
        string? Source,
        string? Circuit,
        string Element,
        string? Against,
        bool Identical,
        IReadOnlyList<DifferenceDto> Differences,
        IReadOnlyList<double> AddedValues,
        IReadOnlyList<double> RemovedValues);

    private sealed record DifferenceDto(
        double Value,
        string Node,
        string Metric,
        double? Before,
        double? After);
}
