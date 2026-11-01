using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radix.Core.Serialization;

/// <summary>Node metrics as recorded in a saved study.</summary>
public sealed record TransientSweepNodeMetrics(
    string Id,
    double MinimumVolts,
    double MaximumVolts,
    double FinalVolts,
    double PeakTimeSeconds,
    double? SettlingTimeSeconds);

/// <summary>One saved value and the node metrics it produced.</summary>
public sealed record TransientSweepReportRow(
    double Value,
    string? Netlist,
    bool Completed,
    string? Failure,
    string? Message,
    IReadOnlyList<TransientSweepNodeMetrics> Nodes);

/// <summary>
/// A sweep study read back from <see cref="TransientSweepJson"/> output. Loading is strict about
/// the format name and version — a report from a different tool or a future layout is refused
/// rather than half-understood — but tolerant of extra fields, so an archive written by a later
/// build stays readable here.
/// </summary>
public sealed record TransientSweepReport(
    string? Source,
    string? Circuit,
    string Element,
    string Method,
    int Values,
    IReadOnlyList<TransientSweepReportRow> Rows)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static TransientSweepReport Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return LoadFromString(File.ReadAllText(fullPath));
    }

    public static TransientSweepReport LoadFromString(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        DocumentDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DocumentDto>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ProjectFormatException($"Invalid sweep report JSON: {exception.Message}", exception);
        }

        if (dto is null)
        {
            throw new ProjectFormatException("Sweep report JSON is empty or null.");
        }

        if (!string.Equals(dto.Format, TransientSweepJson.FormatName, StringComparison.Ordinal))
        {
            throw new ProjectFormatException(
                $"Expected format '{TransientSweepJson.FormatName}' but found '{dto.Format ?? "<missing>"}'.");
        }

        if (dto.Version != TransientSweepJson.FormatVersion)
        {
            throw new ProjectFormatException(
                $"Sweep report version {dto.Version} is not supported; this build supports version {TransientSweepJson.FormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(dto.Element))
        {
            throw new ProjectFormatException("Sweep report field 'element' is required.");
        }

        if (dto.Rows is null || dto.Rows.Count == 0)
        {
            throw new ProjectFormatException("Sweep report field 'rows' must contain at least one row.");
        }

        var rows = new TransientSweepReportRow[dto.Rows.Count];
        for (int index = 0; index < dto.Rows.Count; index++)
        {
            RowDto row = dto.Rows[index];
            if (!double.IsFinite(row.Value))
            {
                throw new ProjectFormatException(
                    $"Sweep report row {index} has a non-finite value.");
            }

            var nodes = new TransientSweepNodeMetrics[row.Nodes?.Count ?? 0];
            for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
            {
                NodeDto node = row.Nodes![nodeIndex];
                if (string.IsNullOrWhiteSpace(node.Id))
                {
                    throw new ProjectFormatException(
                        $"Sweep report row {index} has a node without an 'id'.");
                }

                nodes[nodeIndex] = new TransientSweepNodeMetrics(
                    node.Id!,
                    node.MinimumVolts,
                    node.MaximumVolts,
                    node.FinalVolts,
                    node.PeakTimeSeconds,
                    node.SettlingTimeSeconds);
            }

            rows[index] = new TransientSweepReportRow(
                row.Value,
                row.Netlist,
                row.Completed,
                row.Failure,
                row.Message,
                nodes);
        }

        return new TransientSweepReport(
            dto.Source,
            dto.Circuit,
            dto.Element!,
            dto.Method ?? string.Empty,
            dto.Values == 0 ? rows.Length : dto.Values,
            rows);
    }

    private sealed record DocumentDto(
        string? Format,
        int Version,
        string? Source,
        string? Circuit,
        string? Element,
        string? Method,
        int Values,
        IReadOnlyList<RowDto>? Rows);

    private sealed record RowDto(
        double Value,
        string? Netlist,
        bool Completed,
        string? Failure,
        string? Message,
        IReadOnlyList<NodeDto>? Nodes);

    private sealed record NodeDto(
        string? Id,
        double MinimumVolts,
        double MaximumVolts,
        double FinalVolts,
        double PeakTimeSeconds,
        double? SettlingTimeSeconds);
}
