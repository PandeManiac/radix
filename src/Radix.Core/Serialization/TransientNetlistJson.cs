using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Radix.Core.Electrical;

namespace Radix.Core.Serialization;

/// <summary>
/// Versioned JSON input and report format for the transient electrical analysis.
/// Input is strict: unknown fields, unsupported versions, missing terminals,
/// mismatched units, and unsupported analysis methods are rejected rather than
/// silently ignored.
/// </summary>
public static class TransientNetlistJson
{
    public const string NetlistFormatName = "radix-transient-netlist";
    public const string ReportFormatName = "radix-transient-report";
    public const int FormatVersion = 1;

    /// <summary>Largest netlist file accepted by <see cref="Load"/>.</summary>
    public const long DefaultMaxNetlistFileBytes = 16L * 1024 * 1024;

    private const string BackwardEulerName = "backward-euler";
    private const string TrapezoidalName = "trapezoidal";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static TransientDocument Load(string path, long maxBytes = DefaultMaxNetlistFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        string fullPath = Path.GetFullPath(path);
        long length = new FileInfo(fullPath).Length;
        if (length > maxBytes)
        {
            throw new ProjectFormatException(
                $"Netlist file '{Path.GetFileName(fullPath)}' is {length} bytes, above the supported limit of {maxBytes} bytes.");
        }

        return LoadFromString(File.ReadAllText(fullPath, Encoding.UTF8));
    }

    public static TransientDocument LoadFromString(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        DocumentDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DocumentDto>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ProjectFormatException($"Invalid transient netlist JSON: {exception.Message}", exception);
        }

        if (dto is null)
        {
            throw new ProjectFormatException("Transient netlist JSON is empty or null.");
        }

        if (!string.Equals(dto.Format, NetlistFormatName, StringComparison.Ordinal))
        {
            throw new ProjectFormatException(
                $"Expected format '{NetlistFormatName}' but found '{dto.Format ?? "<missing>"}'.");
        }

        if (dto.Version != FormatVersion)
        {
            throw new ProjectFormatException(
                $"Transient netlist version {dto.Version} is not supported; this build supports version {FormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(dto.Ground))
        {
            throw new ProjectFormatException("Transient netlist field 'ground' is required.");
        }

        if (dto.Elements is null)
        {
            throw new ProjectFormatException("Transient netlist field 'elements' is required.");
        }

        var netlist = new TransientNetlist(dto.Ground);
        foreach (ElementDto element in dto.Elements)
        {
            AddElement(netlist, element);
        }

        return new TransientDocument(netlist, ReadAnalysis(dto.Analysis));
    }

    /// <summary>Serializes a transient run as a stable JSON report.</summary>
    public static string ToReportJson(string? source, TransientNetlist netlist, TransientResult result)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentNullException.ThrowIfNull(result);

        PointDto[] points = result.Points
            .Select(point => new PointDto(
                point.Time,
                point.RelativeResidual,
                point.NodeVoltages
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new NodeVoltDto(entry.Key, entry.Value))
                    .ToArray(),
                point.ElementCurrents.Keys
                    .Order(StringComparer.Ordinal)
                    .Select(id => new PointElementDto(
                        id,
                        point.ElementVoltages[id],
                        point.ElementCurrents[id],
                        point.ElementPowers[id]))
                    .ToArray()))
            .ToArray();

        return JsonSerializer.Serialize(
            new ReportDto(
                ReportFormatName,
                FormatVersion,
                source,
                netlist.GroundNodeId,
                MethodName(result.Options.Method),
                result.Options.StartTime,
                result.Options.StopTime,
                result.Options.Steps,
                result.TimeStep,
                result.Options.IsAdaptive,
                result.Options.AdaptiveTolerance,
                result.Completed,
                result.Failure is null ? null : DcNetlistJson.FailureName(result.Failure.Value),
                result.Message,
                result.FailedStep,
                points),
            JsonOptions);
    }

    /// <summary>The JSON name of an integration method, matching <see cref="LoadFromString"/>.</summary>
    public static string MethodName(TransientMethod method) =>
        method switch
        {
            TransientMethod.BackwardEuler => BackwardEulerName,
            TransientMethod.Trapezoidal => TrapezoidalName,
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

    private static TransientOptions? ReadAnalysis(AnalysisDto? analysis)
    {
        if (analysis is null)
        {
            return null;
        }

        TransientMethod method = TransientMethod.BackwardEuler;
        if (analysis.Method is not null)
        {
            method = analysis.Method.Trim().ToLowerInvariant() switch
            {
                BackwardEulerName => TransientMethod.BackwardEuler,
                TrapezoidalName => TransientMethod.Trapezoidal,
                _ => throw new ProjectFormatException(
                    $"Analysis method '{analysis.Method}' is not supported; expected '{BackwardEulerName}' or '{TrapezoidalName}'."),
            };
        }

        var options = new TransientOptions
        {
            StartTime = analysis.Start ?? 0,
            StopTime = analysis.Stop ?? 1,
            Steps = analysis.Steps ?? 100,
            Method = method,
            AdaptiveTolerance = analysis.Tolerance,
            AdaptiveAbsoluteTolerance = analysis.AbsoluteTolerance ?? 0,
            InitialTimeStep = analysis.InitialStep ?? 0,
            MinimumTimeStep = analysis.MinStep ?? 0,
            MaximumTimeStep = analysis.MaxStep ?? 0,
            MaximumStepCount = analysis.MaxSteps ?? 100_000,
        };
        try
        {
            options.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException($"Analysis block is invalid: {exception.Message}", exception);
        }

        return options;
    }

    private static void AddElement(TransientNetlist netlist, ElementDto element)
    {
        if (string.IsNullOrWhiteSpace(element.Id))
        {
            throw new ProjectFormatException("Every transient netlist element needs a non-empty 'id'.");
        }

        string id = element.Id;
        string nodeA = RequireNode(element.NodeA, id, "nodeA");
        string nodeB = RequireNode(element.NodeB, id, "nodeB");
        string type = element.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        try
        {
            switch (type)
            {
                case "resistor":
                    RejectUnused(element, id, type, "ohms");
                    netlist.AddResistor(id, nodeA, nodeB, RequireValue(element.Ohms, id, "ohms"));
                    break;
                case "voltage-source":
                    RejectUnused(element, id, type, "volts", "waveform");
                    netlist.AddVoltageSource(id, nodeA, nodeB, RequireValue(element.Volts, id, "volts"));
                    ApplyWaveform(netlist, element, id);
                    break;
                case "current-source":
                    RejectUnused(element, id, type, "amperes", "waveform");
                    netlist.AddCurrentSource(id, nodeA, nodeB, RequireValue(element.Amperes, id, "amperes"));
                    ApplyWaveform(netlist, element, id);
                    break;
                case "diode":
                    RejectUnused(
                        element,
                        id,
                        type,
                        "saturationCurrent",
                        "emissionCoefficient",
                        "thermalVoltage",
                        "seriesResistance");
                    netlist.AddDiode(
                        id,
                        nodeA,
                        nodeB,
                        RequireValue(element.SaturationCurrent, id, "saturationCurrent"),
                        element.EmissionCoefficient ?? 1.0,
                        element.ThermalVoltage ?? DcDiode.DefaultThermalVoltage,
                        element.SeriesResistance ?? 0);
                    break;
                case "capacitor":
                    RejectUnused(element, id, type, "farads", "initialVolts");
                    netlist.AddCapacitor(id, nodeA, nodeB, RequireValue(element.Farads, id, "farads"));
                    if (element.InitialVolts is double initialVolts)
                    {
                        netlist.SetCapacitorInitialVoltage(id, RequireFinite(initialVolts, id, "initialVolts"));
                    }

                    break;
                case "inductor":
                    RejectUnused(element, id, type, "henries", "initialAmperes");
                    netlist.AddInductor(id, nodeA, nodeB, RequireValue(element.Henries, id, "henries"));
                    if (element.InitialAmperes is double initialAmperes)
                    {
                        netlist.SetInductorInitialCurrent(
                            id, RequireFinite(initialAmperes, id, "initialAmperes"));
                    }

                    break;
                default:
                    throw new ProjectFormatException(
                        $"Element '{id}' has unsupported type '{element.Type ?? "<missing>"}'. Expected resistor, voltage-source, current-source, diode, capacitor, or inductor.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException($"Element '{id}' is invalid: {exception.Message}", exception);
        }
    }

    private static void ApplyWaveform(TransientNetlist netlist, ElementDto element, string id)
    {
        if (element.Waveform is null)
        {
            return;
        }

        if (element.Waveform.Count == 0)
        {
            throw new ProjectFormatException(
                $"Element '{id}' has an empty waveform; omit the field for a constant source.");
        }

        var points = new TransientWaveformPoint[element.Waveform.Count];
        for (int index = 0; index < element.Waveform.Count; index++)
        {
            WaveformPointDto point = element.Waveform[index];
            if (point.Time is not double time)
            {
                throw new ProjectFormatException(
                    $"Element '{id}' waveform breakpoint {index} needs a 'time'.");
            }

            double value = point.Value
                ?? throw new ProjectFormatException(
                    $"Element '{id}' waveform breakpoint {index} needs a 'value'.");
            points[index] = new TransientWaveformPoint(
                RequireFinite(time, id, $"waveform[{index}].time"),
                RequireFinite(value, id, $"waveform[{index}].value"));
        }

        netlist.SetWaveform(id, points);
    }

    private static void RejectUnused(
        ElementDto element,
        string id,
        string type,
        params string[] allowed)
    {
        var present = new List<string>();
        if (element.Ohms is not null)
        {
            present.Add("ohms");
        }

        if (element.Volts is not null)
        {
            present.Add("volts");
        }

        if (element.Amperes is not null)
        {
            present.Add("amperes");
        }

        if (element.SaturationCurrent is not null)
        {
            present.Add("saturationCurrent");
        }

        if (element.EmissionCoefficient is not null)
        {
            present.Add("emissionCoefficient");
        }

        if (element.ThermalVoltage is not null)
        {
            present.Add("thermalVoltage");
        }

        if (element.SeriesResistance is not null)
        {
            present.Add("seriesResistance");
        }

        if (element.Farads is not null)
        {
            present.Add("farads");
        }

        if (element.Henries is not null)
        {
            present.Add("henries");
        }

        if (element.InitialVolts is not null)
        {
            present.Add("initialVolts");
        }

        if (element.InitialAmperes is not null)
        {
            present.Add("initialAmperes");
        }

        if (element.Waveform is not null)
        {
            present.Add("waveform");
        }

        foreach (string property in present)
        {
            if (!allowed.Contains(property, StringComparer.Ordinal))
            {
                throw new ProjectFormatException(
                    $"Element '{id}' is a {type} and must not set '{property}'.");
            }
        }
    }

    private static string RequireNode(string? node, string id, string property)
    {
        if (string.IsNullOrWhiteSpace(node))
        {
            throw new ProjectFormatException($"Element '{id}' field '{property}' is required.");
        }

        return node;
    }

    private static double RequireValue(double? value, string id, string property)
    {
        if (value is not double number)
        {
            throw new ProjectFormatException($"Element '{id}' field '{property}' is required.");
        }

        return RequireFinite(number, id, property);
    }

    private static double RequireFinite(double value, string id, string property)
    {
        if (!double.IsFinite(value))
        {
            throw new ProjectFormatException($"Element '{id}' field '{property}' must be a finite number.");
        }

        return value;
    }

    private sealed record DocumentDto(
        string? Format,
        int Version,
        string? Ground,
        IReadOnlyList<ElementDto>? Elements,
        AnalysisDto? Analysis);

    private sealed record AnalysisDto(
        double? Start,
        double? Stop,
        int? Steps,
        string? Method,
        double? Tolerance,
        double? AbsoluteTolerance,
        double? InitialStep,
        double? MinStep,
        double? MaxStep,
        int? MaxSteps);

    private sealed record ElementDto(
        string? Id,
        string? Type,
        string? NodeA,
        string? NodeB,
        double? Ohms,
        double? Volts,
        double? Amperes,
        double? SaturationCurrent,
        double? EmissionCoefficient,
        double? ThermalVoltage,
        double? SeriesResistance,
        double? Farads,
        double? Henries,
        double? InitialVolts,
        double? InitialAmperes,
        IReadOnlyList<WaveformPointDto>? Waveform);

    private sealed record WaveformPointDto(double? Time, double? Value);

    private sealed record ReportDto(
        string Format,
        int Version,
        string? Source,
        string Ground,
        string Method,
        double StartTime,
        double StopTime,
        int Steps,
        double TimeStep,
        bool Adaptive,
        double? Tolerance,
        bool Completed,
        string? Failure,
        string? Message,
        int? FailedStep,
        IReadOnlyList<PointDto> Points);

    private sealed record PointDto(
        double Time,
        double RelativeResidual,
        IReadOnlyList<NodeVoltDto> Nodes,
        IReadOnlyList<PointElementDto> Elements);

    private sealed record NodeVoltDto(string Id, double Volts);

    private sealed record PointElementDto(
        string Id,
        double Volts,
        double Amperes,
        double AbsorbedPowerWatts);
}

/// <summary>A parsed transient netlist plus its optional embedded analysis settings.</summary>
public sealed record TransientDocument(TransientNetlist Netlist, TransientOptions? Analysis);
