using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Radix.Core.Electrical;

namespace Radix.Core.Serialization;

/// <summary>
/// A recorded AC sweep: the drive, the amplitude and every node's phasor at every frequency.
/// <para>
/// The point of recording one is comparison. A gain that moved because a capacitance changed is a
/// result, but it is only visible against a stored answer, so the archive is versioned and carries
/// the drive it was taken with — comparing sweeps of different sources would be meaningless.
/// </para>
/// </summary>
public sealed class AcReport
{
    public const string FormatName = "radix-ac-report";
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AcReport(
        string drive,
        double amplitude,
        string? source,
        string? circuit,
        IReadOnlyList<AcPoint> points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drive);
        ArgumentNullException.ThrowIfNull(points);

        Drive = drive;
        Amplitude = amplitude;
        Source = source;
        Circuit = circuit;
        Points = points;
    }

    public string Drive { get; }

    public double Amplitude { get; }

    public string? Source { get; }

    public string? Circuit { get; }

    public IReadOnlyList<AcPoint> Points { get; }

    /// <summary>The non-ground node IDs, in ordinal order.</summary>
    public IReadOnlyList<string> Nodes =>
        Points.Count == 0
            ? []
            : Points[0].NodeVoltages.Keys.OrderBy(node => node, StringComparer.Ordinal).ToArray();

    public static AcReport From(AcResponse response, string? source = null, string? circuit = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new AcReport(response.DriveSourceId, response.Amplitude, source, circuit, response.Points);
    }

    public string ToJson()
    {
        var report = new ReportDto(
            FormatName,
            FormatVersion,
            Source,
            Circuit,
            Drive,
            Amplitude,
            Points
                .Select(point => new PointDto(
                    point.FrequencyHertz,
                    Nodes.ToDictionary(
                        node => node,
                        node =>
                        {
                            Complex value = point.NodeVoltages[node];
                            return new PhasorDto(value.Real, value.Imaginary);
                        },
                        StringComparer.Ordinal)))
                .ToArray());

        return JsonSerializer.Serialize(report, JsonOptions);
    }

    /// <summary>
    /// A long-format table: one row per node per frequency, which is what a plotting or diffing
    /// script wants, unlike the aligned text table meant for reading.
    /// </summary>
    public string ToCsv()
    {
        var builder = new StringBuilder();
        Comment(builder, "format", FormatName);
        Comment(builder, "version", FormatVersion.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(Source))
        {
            Comment(builder, "source", Source!);
        }

        if (!string.IsNullOrWhiteSpace(Circuit))
        {
            Comment(builder, "circuit", Circuit!);
        }

        Comment(builder, "drive", Drive);
        Comment(builder, "amplitude", Number(Amplitude));
        builder.Append("hz,node,gain_db,phase_deg\n");

        IReadOnlyList<string> nodes = Nodes;
        foreach (AcPoint point in Points)
        {
            foreach (string node in nodes)
            {
                Complex value = point.NodeVoltages[node];
                builder.Append(Number(point.FrequencyHertz)).Append(',')
                    .Append(value.Magnitude <= 0 ? node : Text(node)).Append(',')
                    .Append(Number(value.Magnitude <= 0 ? -300 : 20 * Math.Log10(value.Magnitude)))
                    .Append(',')
                    .Append(Number(AcResponse.PhaseDegrees(value)))
                    .Append('\n');
            }
        }

        return builder.ToString();
    }

    public static AcReport Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectFormatException($"could not read '{path}': {exception.Message}");
        }

        return Parse(json);
    }

    public static AcReport Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        ReportDto? report;
        try
        {
            report = JsonSerializer.Deserialize<ReportDto>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ProjectFormatException($"{FormatName}: {exception.Message}");
        }

        if (report is null)
        {
            throw new ProjectFormatException($"{FormatName}: the report is empty.");
        }

        if (!string.Equals(report.Format, FormatName, StringComparison.Ordinal))
        {
            throw new ProjectFormatException(
                $"{FormatName}: expected format '{FormatName}', found '{report.Format}'.");
        }

        if (report.Version != FormatVersion)
        {
            throw new ProjectFormatException(
                $"{FormatName}: unsupported version {report.Version}, this build writes version {FormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(report.Drive))
        {
            throw new ProjectFormatException($"{FormatName}: the report does not name its drive source.");
        }

        var points = new List<AcPoint>(report.Points.Count);
        foreach (PointDto point in report.Points)
        {
            if (!double.IsFinite(point.FrequencyHz) || point.FrequencyHz <= 0)
            {
                throw new ProjectFormatException(
                    $"{FormatName}: frequency {point.FrequencyHz.ToString(CultureInfo.InvariantCulture)} is not a positive finite number.");
            }

            var voltages = new Dictionary<string, Complex>(StringComparer.Ordinal);
            foreach ((string node, PhasorDto phasor) in point.Nodes)
            {
                voltages[node] = new Complex(phasor.Real, phasor.Imaginary);
            }

            points.Add(new AcPoint(point.FrequencyHz, voltages));
        }

        return new AcReport(report.Drive, report.Amplitude, report.Source, report.Circuit, points);
    }

    private static void Comment(StringBuilder builder, string key, string value) =>
        builder.Append("# ").Append(key).Append(": ")
            .Append(value.ReplaceLineEndings(" ")).Append('\n');

    private static string Text(string value) =>
        value.Contains(',') || value.Contains('"')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private sealed record ReportDto(
        string Format,
        int Version,
        string? Source,
        string? Circuit,
        string Drive,
        double Amplitude,
        IReadOnlyList<PointDto> Points);

    private sealed record PointDto(
        double FrequencyHz,
        IReadOnlyDictionary<string, PhasorDto> Nodes);

    private sealed record PhasorDto(double Real, double Imaginary);
}

/// <summary>How far one node of a sweep moved, and where it moved furthest.</summary>
public sealed record AcDeviation(
    string Node,
    double WorstGainFrequencyHz,
    double GainDeviationDb,
    double MaximumPhaseDeviationDegrees);

/// <summary>
/// Compares a stored sweep with a fresh one at the same frequencies.
/// <para>
/// Gain is compared in decibels because that is the scale a response is read on, and a ratio of two
/// magnitudes is a difference of decibels with no reference level to pick. Phase is compared
/// circularly, so +179° and −179° are two degrees apart rather than 358.
/// </para>
/// </summary>
public static class AcComparison
{
    /// <summary>Magnitudes below this are treated as zero, so their decibels are a defined floor.</summary>
    private const double MagnitudeFloor = 1e-30;

    public static IReadOnlyList<AcDeviation> Compare(AcReport archived, AcResponse current)
    {
        ArgumentNullException.ThrowIfNull(archived);
        ArgumentNullException.ThrowIfNull(current);

        IReadOnlyList<AcPoint> points = current.Points;
        if (archived.Points.Count != points.Count)
        {
            throw new ArgumentException(
                $"the archived sweep has {archived.Points.Count} frequencies and this run has {points.Count}; " +
                "compare sweeps taken on the same grid.");
        }

        if (!string.Equals(archived.Drive, current.DriveSourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"the archived sweep drove '{archived.Drive}' and this run drives '{current.DriveSourceId}'.");
        }

        IReadOnlyList<string> nodes = archived.Nodes;
        string[] currentNodes = points[0].NodeVoltages.Keys.OrderBy(node => node, StringComparer.Ordinal).ToArray();
        if (!nodes.SequenceEqual(currentNodes, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"the archived sweep has nodes [{string.Join(", ", nodes)}] and this run has [{string.Join(", ", currentNodes)}].");
        }

        for (int index = 0; index < points.Count; index++)
        {
            double archivedHz = archived.Points[index].FrequencyHertz;
            double currentHz = points[index].FrequencyHertz;
            double scale = Math.Max(Math.Abs(archivedHz), Math.Abs(currentHz));
            if (Math.Abs(archivedHz - currentHz) > scale * 1e-9)
            {
                throw new ArgumentException(
                    $"frequency {index + 1} is {archivedHz.ToString("G6", CultureInfo.InvariantCulture)} Hz in the archive " +
                    $"and {currentHz.ToString("G6", CultureInfo.InvariantCulture)} Hz in this run.");
            }
        }

        var deviations = new List<AcDeviation>(nodes.Count);
        foreach (string node in nodes)
        {
            double worstDb = 0;
            double worstHz = points[0].FrequencyHertz;
            double maximumPhase = 0;

            for (int index = 0; index < points.Count; index++)
            {
                Complex before = archived.Points[index].NodeVoltages[node];
                Complex after = points[index].NodeVoltages[node];
                double deviation = Math.Abs(Decibels(after) - Decibels(before));
                if (deviation > worstDb)
                {
                    worstDb = deviation;
                    worstHz = points[index].FrequencyHertz;
                }

                maximumPhase = Math.Max(maximumPhase, PhaseGapDegrees(before, after));
            }

            deviations.Add(new AcDeviation(node, worstHz, worstDb, maximumPhase));
        }

        return deviations;
    }

    private static double Decibels(Complex value) =>
        20 * Math.Log10(Math.Max(value.Magnitude, MagnitudeFloor));

    private static double PhaseGapDegrees(Complex before, Complex after)
    {
        double gap = Math.Abs(AcResponse.PhaseDegrees(after) - AcResponse.PhaseDegrees(before)) % 360;
        return gap > 180 ? 360 - gap : gap;
    }
}
