using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Radix.Core.Electrical;

namespace Radix.Core.Serialization;

/// <summary>
/// Versioned JSON input and report format for the independent DC linear solver.
/// Input is strict: unknown fields, unsupported versions, missing terminals, and
/// mismatched units are rejected instead of being silently ignored.
/// </summary>
public static class DcNetlistJson
{
    public const string NetlistFormatName = "radix-dc-netlist";
    public const string ReportFormatName = "radix-dc-report";
    public const int FormatVersion = 1;

    /// <summary>Largest netlist file accepted by <see cref="Load"/>.</summary>
    public const long DefaultMaxNetlistFileBytes = 16L * 1024 * 1024;

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

    public static DcNetlist Load(
        string path,
        long maxBytes = DefaultMaxNetlistFileBytes)
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

    public static DcNetlist LoadFromString(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        NetlistDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<NetlistDto>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ProjectFormatException($"Invalid DC netlist JSON: {exception.Message}", exception);
        }

        if (dto is null)
        {
            throw new ProjectFormatException("DC netlist JSON is empty or null.");
        }

        if (!string.Equals(dto.Format, NetlistFormatName, StringComparison.Ordinal))
        {
            throw new ProjectFormatException(
                $"Expected format '{NetlistFormatName}' but found '{dto.Format ?? "<missing>"}'.");
        }

        if (dto.Version != FormatVersion)
        {
            throw new ProjectFormatException(
                $"DC netlist version {dto.Version} is not supported; this build supports version {FormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(dto.Ground))
        {
            throw new ProjectFormatException("DC netlist field 'ground' is required.");
        }

        if (dto.Elements is null)
        {
            throw new ProjectFormatException("DC netlist field 'elements' is required.");
        }

        var netlist = new DcNetlist(dto.Ground);
        foreach (ElementDto element in dto.Elements)
        {
            AddElement(netlist, element);
        }

        return netlist;
    }

    /// <summary>Serializes a solved operating point as a stable JSON report.</summary>
    public static string ToReportJson(
        string? source,
        DcNetlist netlist,
        DcSolution solution,
        string? circuit = null)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentNullException.ThrowIfNull(solution);

        NodeDto[] nodes = netlist.Nodes
            .Select(node => new NodeDto(node, solution.NodeVoltages[node]))
            .ToArray();
        ElementResultDto[] elements = netlist.Elements
            .Select(element =>
            {
                DcDiode? diode = element as DcDiode;
                DcMosfet? mosfet = element as DcMosfet;
                return new ElementResultDto(
                    element.Id,
                    KindName(element),
                    element.NodeA,
                    element.NodeB,
                    element.Value,
                    element.Unit,
                    solution.ElementVoltages[element.Id],
                    solution.ElementCurrents[element.Id],
                    solution.ElementPowers[element.Id],
                    diode?.EmissionCoefficient,
                    diode?.ThermalVoltage,
                    diode?.SeriesResistance,
                    diode?.BreakdownVoltage,
                    diode?.BreakdownCurrent,
                    mosfet is null ? null : (mosfet.IsPChannel ? "p" : "n"),
                    mosfet?.Gate,
                    mosfet?.ThresholdVoltage,
                    mosfet?.Transconductance,
                    mosfet?.ChannelLengthModulation);
            })
            .ToArray();

        return Serialize(new ReportDto(
            ReportFormatName,
            FormatVersion,
            source,
            circuit,
            Solved: true,
            Failure: null,
            Message: null,
            netlist.GroundNodeId,
            nodes,
            elements));
    }

    /// <summary>Serializes a solve failure so CI can distinguish it from a parse error.</summary>
    public static string ToFailureJson(
        string? source,
        string? ground,
        DcSolveFailure failure,
        string message,
        string? circuit = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Serialize(new ReportDto(
            ReportFormatName,
            FormatVersion,
            source,
            circuit,
            Solved: false,
            FailureName(failure),
            message,
            ground,
            [],
            []));
    }

    public static string KindName(DcElement element) =>
        element.Kind switch
        {
            DcElementKind.Resistor => "resistor",
            DcElementKind.VoltageSource => "voltage-source",
            DcElementKind.CurrentSource => "current-source",
            DcElementKind.Diode => "diode",
            DcElementKind.Mosfet => "mosfet",
            _ => throw new ArgumentOutOfRangeException(nameof(element)),
        };

    /// <summary>The stable JSON name of a solve failure, shared by every electrical report.</summary>
    public static string FailureName(DcSolveFailure failure) =>
        failure switch
        {
            DcSolveFailure.NoGround => "no-ground",
            DcSolveFailure.SingularMatrix => "singular-matrix",
            DcSolveFailure.NonFiniteResult => "non-finite-result",
            DcSolveFailure.FloatingNetwork => "floating-network",
            DcSolveFailure.InconsistentSourceConstraints => "inconsistent-source-constraints",
            DcSolveFailure.IllConditioned => "ill-conditioned",
            DcSolveFailure.NonConvergent => "non-convergent",
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

    private static string Serialize(ReportDto report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    private static void AddElement(DcNetlist netlist, ElementDto element)
    {
        if (string.IsNullOrWhiteSpace(element.Id))
        {
            throw new ProjectFormatException("Every DC netlist element needs a non-empty 'id'.");
        }

        string id = element.Id;
        string type = element.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        try
        {
            switch (type)
            {
                case "resistor":
                    RejectFieldsOutside(element, id, type, "nodeA", "nodeB", "ohms");
                    netlist.AddResistor(
                        id,
                        RequireNode(element.NodeA, id, "nodeA"),
                        RequireNode(element.NodeB, id, "nodeB"),
                        RequireValue(element.Ohms, id, "ohms"));
                    break;
                case "voltage-source":
                    RejectFieldsOutside(element, id, type, "nodeA", "nodeB", "volts");
                    netlist.AddVoltageSource(
                        id,
                        RequireNode(element.NodeA, id, "nodeA"),
                        RequireNode(element.NodeB, id, "nodeB"),
                        RequireValue(element.Volts, id, "volts"));
                    break;
                case "current-source":
                    RejectFieldsOutside(element, id, type, "nodeA", "nodeB", "amperes");
                    netlist.AddCurrentSource(
                        id,
                        RequireNode(element.NodeA, id, "nodeA"),
                        RequireNode(element.NodeB, id, "nodeB"),
                        RequireValue(element.Amperes, id, "amperes"));
                    break;
                case "diode":
                    RejectFieldsOutside(
                        element,
                        id,
                        type,
                        "nodeA",
                        "nodeB",
                        "saturationCurrent",
                        "emissionCoefficient",
                        "thermalVoltage",
                        "seriesResistance",
                        "breakdownVoltage",
                        "breakdownCurrent");
                    netlist.AddDiode(
                        id,
                        RequireNode(element.NodeA, id, "nodeA"),
                        RequireNode(element.NodeB, id, "nodeB"),
                        RequireValue(element.SaturationCurrent, id, "saturationCurrent"),
                        element.EmissionCoefficient ?? 1.0,
                        element.ThermalVoltage ?? DcDiode.DefaultThermalVoltage,
                        element.SeriesResistance ?? 0,
                        element.BreakdownVoltage,
                        element.BreakdownCurrent);
                    break;
                case "mosfet":
                    RejectFieldsOutside(
                        element,
                        id,
                        type,
                        "drain",
                        "gate",
                        "source",
                        "channel",
                        "thresholdVoltage",
                        "transconductance",
                        "channelLengthModulation");
                    netlist.AddMosfet(
                        id,
                        RequireNode(element.Drain, id, "drain"),
                        RequireNode(element.Gate, id, "gate"),
                        RequireNode(element.Source, id, "source"),
                        ParseChannel(element.Channel, id),
                        RequireValue(element.ThresholdVoltage, id, "thresholdVoltage"),
                        RequireValue(element.Transconductance, id, "transconductance"),
                        element.ChannelLengthModulation ?? 0);
                    break;
                default:
                    throw new ProjectFormatException(
                        $"Element '{id}' has unsupported type '{element.Type ?? "<missing>"}'. Expected resistor, voltage-source, current-source, diode, or mosfet.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException($"Element '{id}' is invalid: {exception.Message}", exception);
        }
    }

    private static DcMosfetChannel ParseChannel(string? value, string id) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "n" or "n-channel" or "nchannel" => DcMosfetChannel.NChannel,
            "p" or "p-channel" or "pchannel" => DcMosfetChannel.PChannel,
            null => throw new ProjectFormatException(
                $"Element '{id}' field 'channel' is required for a mosfet ('n' or 'p')."),
            _ => throw new ProjectFormatException(
                $"Element '{id}' has unknown channel '{value}'; expected 'n' or 'p'."),
        };

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
        if (value is not double number || !double.IsFinite(number))
        {
            throw new ProjectFormatException($"Element '{id}' field '{property}' must be a finite number.");
        }

        return number;
    }

    private static void RejectFieldsOutside(
        ElementDto element,
        string id,
        string type,
        params string[] allowed)
    {
        var present = new List<string>();
        AddIfPresent("nodeA", element.NodeA);
        AddIfPresent("nodeB", element.NodeB);
        AddIfPresent("ohms", element.Ohms);
        AddIfPresent("volts", element.Volts);
        AddIfPresent("amperes", element.Amperes);
        AddIfPresent("saturationCurrent", element.SaturationCurrent);
        AddIfPresent("emissionCoefficient", element.EmissionCoefficient);
        AddIfPresent("thermalVoltage", element.ThermalVoltage);
        AddIfPresent("seriesResistance", element.SeriesResistance);
        AddIfPresent("breakdownVoltage", element.BreakdownVoltage);
        AddIfPresent("breakdownCurrent", element.BreakdownCurrent);
        AddIfPresent("drain", element.Drain);
        AddIfPresent("gate", element.Gate);
        AddIfPresent("source", element.Source);
        AddIfPresent("channel", element.Channel);
        AddIfPresent("thresholdVoltage", element.ThresholdVoltage);
        AddIfPresent("transconductance", element.Transconductance);
        AddIfPresent("channelLengthModulation", element.ChannelLengthModulation);

        foreach (string property in present)
        {
            if (!allowed.Contains(property, StringComparer.Ordinal))
            {
                throw new ProjectFormatException(
                    $"Element '{id}' is a {type} and must not set '{property}'.");
            }
        }

        void AddIfPresent(string property, object? value)
        {
            if (value is not null)
            {
                present.Add(property);
            }
        }
    }

    private sealed record NetlistDto(
        string? Format,
        int Version,
        string? Ground,
        IReadOnlyList<ElementDto>? Elements);

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
        double? BreakdownVoltage,
        double? BreakdownCurrent,
        string? Drain,
        string? Gate,
        string? Source,
        string? Channel,
        double? ThresholdVoltage,
        double? Transconductance,
        double? ChannelLengthModulation);

    private sealed record ReportDto(
        string Format,
        int Version,
        string? Source,
        string? Circuit,
        bool Solved,
        string? Failure,
        string? Message,
        string? Ground,
        IReadOnlyList<NodeDto> Nodes,
        IReadOnlyList<ElementResultDto> Elements);

    private sealed record NodeDto(string Id, double Volts);

    private sealed record ElementResultDto(
        string Id,
        string Kind,
        string NodeA,
        string NodeB,
        double Value,
        string Unit,
        double Volts,
        double Amperes,
        double AbsorbedPowerWatts,
        double? EmissionCoefficient,
        double? ThermalVoltage,
        double? SeriesResistance,
        double? BreakdownVoltage,
        double? BreakdownCurrent,
        string? Channel,
        string? Gate,
        double? ThresholdVoltage,
        double? Transconductance,
        double? ChannelLengthModulation);
}
