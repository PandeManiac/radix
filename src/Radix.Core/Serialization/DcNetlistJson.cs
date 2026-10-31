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
                    diode?.ThermalVoltage);
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
            _ => throw new ArgumentOutOfRangeException(nameof(element)),
        };

    private static string FailureName(DcSolveFailure failure) =>
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
        string nodeA = RequireNode(element.NodeA, id, "nodeA");
        string nodeB = RequireNode(element.NodeB, id, "nodeB");
        string type = element.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        try
        {
            switch (type)
            {
                case "resistor":
                    RejectUnused(
                        element,
                        id,
                        "resistor",
                        ["volts", "amperes", "saturationCurrent", "emissionCoefficient", "thermalVoltage"]);
                    netlist.AddResistor(id, nodeA, nodeB, RequireValue(element.Ohms, id, "ohms"));
                    break;
                case "voltage-source":
                    RejectUnused(
                        element,
                        id,
                        "voltage-source",
                        ["ohms", "amperes", "saturationCurrent", "emissionCoefficient", "thermalVoltage"]);
                    netlist.AddVoltageSource(id, nodeA, nodeB, RequireValue(element.Volts, id, "volts"));
                    break;
                case "current-source":
                    RejectUnused(
                        element,
                        id,
                        "current-source",
                        ["ohms", "volts", "saturationCurrent", "emissionCoefficient", "thermalVoltage"]);
                    netlist.AddCurrentSource(id, nodeA, nodeB, RequireValue(element.Amperes, id, "amperes"));
                    break;
                case "diode":
                    RejectUnused(element, id, "diode", ["ohms", "volts", "amperes"]);
                    netlist.AddDiode(
                        id,
                        nodeA,
                        nodeB,
                        RequireValue(element.SaturationCurrent, id, "saturationCurrent"),
                        element.EmissionCoefficient ?? 1.0,
                        element.ThermalVoltage ?? DcDiode.DefaultThermalVoltage);
                    break;
                default:
                    throw new ProjectFormatException(
                        $"Element '{id}' has unsupported type '{element.Type ?? "<missing>"}'. Expected resistor, voltage-source, current-source, or diode.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException($"Element '{id}' is invalid: {exception.Message}", exception);
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
        if (value is not double number || !double.IsFinite(number))
        {
            throw new ProjectFormatException($"Element '{id}' field '{property}' must be a finite number.");
        }

        return number;
    }

    private static void RejectUnused(
        ElementDto element,
        string id,
        string type,
        string[] unusedProperties)
    {
        foreach (string property in unusedProperties)
        {
            bool present = property switch
            {
                "ohms" => element.Ohms is not null,
                "volts" => element.Volts is not null,
                "amperes" => element.Amperes is not null,
                "saturationCurrent" => element.SaturationCurrent is not null,
                "emissionCoefficient" => element.EmissionCoefficient is not null,
                "thermalVoltage" => element.ThermalVoltage is not null,
                _ => false,
            };
            if (present)
            {
                throw new ProjectFormatException(
                    $"Element '{id}' is a {type} and must not set '{property}'.");
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
        double? ThermalVoltage);

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
        double? ThermalVoltage);
}
