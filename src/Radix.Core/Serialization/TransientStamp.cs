using System.Globalization;
using Radix.Core.Electrical;

namespace Radix.Core.Serialization;

/// <summary>
/// Deterministic provenance for an exported run: what was solved and with which settings, with no
/// wall-clock timestamp, so two exports of the same run are byte-identical and a test can compare
/// them. Numbers use the invariant round-trip format, matching the CSV body.
/// </summary>
public sealed record TransientStamp(
    string? Source,
    string? Circuit,
    string Method,
    double StartTime,
    double StopTime,
    int Steps,
    double TimeStep,
    bool Adaptive,
    double? AdaptiveTolerance,
    double SettlingBand,
    int Points,
    bool Completed,
    string? Netlist = null)
{
    /// <summary>Describes a finished run; <paramref name="source"/> is a path or label, if known.</summary>
    public static TransientStamp From(
        TransientResult result,
        string? source = null,
        string? circuit = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        TransientOptions options = result.Options;
        return new TransientStamp(
            source,
            circuit,
            TransientNetlistJson.MethodName(options.Method),
            options.StartTime,
            options.StopTime,
            options.Steps,
            result.Points.Count > 0 ? result.TimeStep : options.TimeStep,
            options.IsAdaptive,
            options.AdaptiveTolerance,
            options.SettlingBand,
            result.Points.Count,
            result.Completed,
            result.NetlistFingerprint);
    }

    /// <summary>
    /// The stamp as ordered key/value pairs. Keys are stable and lower-case so a comment header
    /// and a metadata block describe the same run the same way.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields
    {
        get
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                Field("method", Method),
                Field("start_s", Number(StartTime)),
                Field("stop_s", Number(StopTime)),
                Field("steps", Steps.ToString(CultureInfo.InvariantCulture)),
                Field("step_s", Number(TimeStep)),
                Field("adaptive", Adaptive ? "true" : "false"),
                Field("settling_band", Number(SettlingBand)),
                Field("points", Points.ToString(CultureInfo.InvariantCulture)),
                Field("completed", Completed ? "true" : "false"),
            };
            if (!string.IsNullOrWhiteSpace(Source))
            {
                fields.Insert(0, Field("source", Source!));
            }

            if (!string.IsNullOrWhiteSpace(Circuit))
            {
                fields.Insert(Source is null ? 0 : 1, Field("circuit", Circuit!));
            }

            if (AdaptiveTolerance is double tolerance)
            {
                fields.Add(Field("tolerance", Number(tolerance)));
            }

            if (!string.IsNullOrWhiteSpace(Netlist))
            {
                fields.Add(Field("netlist", Netlist!));
            }

            return fields;
        }
    }

    private static KeyValuePair<string, string> Field(string key, string value) => new(key, value);

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
