using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.Core.Serialization;

/// <summary>
/// Reads and writes the versioned RADIX project format. Loading never executes code:
/// primitive kinds are restricted to the built-in set, and every reference is
/// validated against the circuit model.
/// </summary>
public static class ProjectSerializer
{
    public const string FormatName = "radix-project";

    /// <summary>
    /// Version 2 adds optional per-net wire colors and author bendpoints. Version 3
    /// adds optional definition-scoped numeric lanes. Older files load unchanged;
    /// every version up to <see cref="CurrentVersion"/> is accepted, and saving always
    /// writes the current version.
    /// </summary>
    public const int CurrentVersion = 3;

    private const string Extension = ".radix";

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

    public static string DefaultExtension => Extension;

    public static CircuitProject Load(string path, bool validate = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = File.ReadAllText(path, Encoding.UTF8);
        return LoadFromString(json, validate);
    }

    public static CircuitProject LoadFromString(string json, bool validate = true)
    {
        ArgumentNullException.ThrowIfNull(json);

        ProjectDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProjectDto>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ProjectFormatException($"Invalid project JSON: {exception.Message}", exception);
        }

        if (dto is null)
        {
            throw new ProjectFormatException("Project JSON is empty or null.");
        }

        CircuitProject project = MapProject(dto);
        if (validate)
        {
            DiagnosticBag report = CircuitValidator.Validate(project);
            if (report.HasErrors)
            {
                throw new CircuitValidationException(report);
            }
        }

        return project;
    }

    public static void Save(string path, CircuitProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);

        string json = SaveToString(project);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static string SaveToString(CircuitProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectDto dto = MapProjectDto(project);
        string json = JsonSerializer.Serialize(dto, JsonOptions);
        return json + Environment.NewLine;
    }

    private static CircuitProject MapProject(ProjectDto dto)
    {
        if (!string.Equals(dto.Format, FormatName, StringComparison.Ordinal))
        {
            throw new ProjectFormatException($"Expected format '{FormatName}' but found '{dto.Format ?? "<missing>"}'.");
        }

        if (dto.Version < 1)
        {
            throw new ProjectFormatException($"Invalid project version {dto.Version}.");
        }

        if (dto.Version > CurrentVersion)
        {
            throw new ProjectFormatException(
                $"Project version {dto.Version} is newer than this build supports ({CurrentVersion}).");
        }

        if (string.IsNullOrWhiteSpace(dto.Root))
        {
            throw new ProjectFormatException("Project field 'root' is required.");
        }

        var project = new CircuitProject
        {
            FormatVersion = dto.Version,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? "Untitled" : dto.Name,
            Description = dto.Description,
            RootDefinitionId = dto.Root,
        };

        if (dto.Settings is SettingsDto settings)
        {
            project.Settings.MaxEvaluationsPerComponentPerTimestamp = settings.MaxEvaluationsPerComponentPerTimestamp;
            project.Settings.MaxRecordedNetChanges = settings.MaxRecordedNetChanges;
            project.Settings.DefaultAdvanceTicks = settings.DefaultAdvanceTicks;
            project.Settings.TraceAllNets = settings.TraceAllNets;
        }

        foreach (DomainDto domainDto in dto.Domains ?? [])
        {
            project.Domains.Add(MapDomain(domainDto));
        }

        foreach (DefinitionDto definitionDto in dto.Definitions ?? [])
        {
            project.Definitions.Add(MapDefinition(definitionDto));
        }

        return project;
    }

    private static SignalDomain MapDomain(DomainDto dto)
    {
        string id = dto.Id ?? "<missing>";
        try
        {
            return new SignalDomain(
                dto.Id ?? throw new ProjectFormatException($"Domain is missing 'id'."),
                dto.Name ?? dto.Id,
                dto.Values ?? throw new ProjectFormatException($"Domain '{id}' is missing 'values'."),
                MapDrivers(dto.Drivers, id),
                dto.Description);
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException($"Invalid domain '{id}': {exception.Message}", exception);
        }
    }

    private static IDriverResolution MapDrivers(DriverDto? dto, string domainId)
    {
        if (dto is null)
        {
            return new SingleDriverResolution();
        }

        string kind = dto.Kind?.ToLowerInvariant() ?? "single";
        switch (kind)
        {
            case "single":
                return new SingleDriverResolution();
            case "priority":
                if (dto.Priorities is null)
                {
                    throw new ProjectFormatException($"Domain '{domainId}' priority driver has no 'priorities'.");
                }

                if (dto.ConflictValue is not int conflict)
                {
                    throw new ProjectFormatException($"Domain '{domainId}' priority driver has no 'conflictValue'.");
                }

                try
                {
                    return new PriorityDriverResolution(dto.Priorities, conflict);
                }
                catch (ArgumentException exception)
                {
                    throw new ProjectFormatException($"Domain '{domainId}' has an invalid priority driver: {exception.Message}", exception);
                }

            default:
                throw new ProjectFormatException($"Domain '{domainId}' uses unknown driver kind '{kind}'.");
        }
    }

    private static CircuitDefinition MapDefinition(DefinitionDto dto)
    {
        string id = dto.Id ?? "<missing>";
        var definition = new CircuitDefinition
        {
            Id = dto.Id ?? throw new ProjectFormatException("Definition is missing 'id'."),
            Name = string.IsNullOrWhiteSpace(dto.Name) ? id : dto.Name,
            Description = dto.Description,
        };

        foreach (PinDto pin in dto.Pins ?? [])
        {
            definition.Pins.Add(new PinDeclaration
            {
                Id = pin.Id ?? throw new ProjectFormatException($"Definition '{id}' has a pin without 'id'."),
                Name = pin.Name ?? pin.Id ?? string.Empty,
                Direction = ParseDirection(pin.Direction, $"definition '{id}' pin '{pin.Id}'"),
                DomainId = pin.Domain ?? throw new ProjectFormatException($"Pin '{pin.Id}' in definition '{id}' is missing 'domain'."),
            });
        }

        foreach (InstanceDto instance in dto.Instances ?? [])
        {
            definition.Instances.Add(MapInstance(instance, id));
        }

        foreach (NetDto net in dto.Nets ?? [])
        {
            definition.Nets.Add(MapNet(net, id));
        }

        foreach (PortDto port in dto.Ports ?? [])
        {
            definition.Ports.Add(new PortBinding(
                port.Pin ?? throw new ProjectFormatException($"Definition '{id}' has a port without 'pin'."),
                port.Net ?? throw new ProjectFormatException($"Definition '{id}' has a port without 'net'.")));
        }

        foreach (NumericLaneDto lane in dto.NumericLanes ?? [])
        {
            var numericLane = new NumericLane
            {
                Id = lane.Id ?? throw new ProjectFormatException($"Definition '{id}' has a numeric lane without 'id'."),
                Name = lane.Name ?? lane.Id ?? string.Empty,
                FormatId = lane.Format ?? throw new ProjectFormatException($"Numeric lane '{lane.Id}' in definition '{id}' is missing 'format'."),
            };
            foreach (string pinId in lane.Pins ?? [])
            {
                numericLane.PinIds.Add(pinId);
            }

            definition.NumericLanes.Add(numericLane);
        }

        return definition;
    }

    private static ComponentInstance MapInstance(InstanceDto dto, string definitionId)
    {
        string? id = dto.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ProjectFormatException($"Definition '{definitionId}' has an instance without 'id'.");
        }

        if ((dto.Definition is null) == (dto.Primitive is null))
        {
            throw new ProjectFormatException(
                $"Instance '{definitionId}.{id}' must have exactly one of 'definition' or 'primitive'.");
        }

        var instance = new ComponentInstance
        {
            Id = id,
            DefinitionId = dto.Definition,
            Label = dto.Label,
            X = dto.X,
            Y = dto.Y,
        };

        if (dto.Primitive is PrimitiveDto primitive)
        {
            instance.Primitive = MapPrimitive(primitive, definitionId, id);
        }

        return instance;
    }

    private static PrimitiveSpec MapPrimitive(PrimitiveDto dto, string definitionId, string instanceId)
    {
        string location = $"instance '{definitionId}.{instanceId}'";
        string kind = dto.Kind?.ToLowerInvariant() ?? throw new ProjectFormatException($"{location} primitive is missing 'kind'.");
        try
        {
            return kind switch
            {
                "constant" => new ConstantSpec(
                    dto.Value ?? throw new ProjectFormatException($"{location} constant is missing 'value'.")),
                "lookup" => new LookupSpec(
                    dto.InputCount ?? throw new ProjectFormatException($"{location} lookup is missing 'inputCount'."),
                    dto.Table ?? throw new ProjectFormatException($"{location} lookup is missing 'table'."),
                    dto.Delay ?? 0),
                "delay" => new DelaySpec(
                    dto.Delay ?? throw new ProjectFormatException($"{location} delay is missing 'delay'."),
                    dto.Initial ?? 0),
                "waveform" => new WaveformSpec(
                    dto.Durations ?? throw new ProjectFormatException($"{location} waveform is missing 'durations'."),
                    dto.Values ?? throw new ProjectFormatException($"{location} waveform is missing 'values'.")),
                "probe" => new ProbeSpec(dto.Label ?? string.Empty),
                _ => throw new ProjectFormatException($"{location} uses unknown primitive kind '{kind}'."),
            };
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException($"{location} has invalid primitive parameters: {exception.Message}", exception);
        }
    }

    private static Net MapNet(NetDto dto, string definitionId)
    {
        string id = dto.Id ?? throw new ProjectFormatException($"Definition '{definitionId}' has a net without 'id'.");
        if (!WireAnnotation.TryNormalizeColor(dto.Color, out string? wireColor))
        {
            throw new ProjectFormatException(
                $"Net '{definitionId}.{id}' has invalid wire color '{dto.Color}'; use #RRGGBB or 'auto'.");
        }

        var net = new Net
        {
            Id = id,
            DomainId = dto.Domain ?? throw new ProjectFormatException($"Net '{definitionId}.{id}' is missing 'domain'."),
            Label = dto.Label,
            InitialValue = dto.Initial,
            WireColor = wireColor,
        };

        foreach (PointDto point in dto.Route ?? [])
        {
            var waypoint = new NetWaypoint(point.X, point.Y);
            if (!waypoint.IsFinite)
            {
                throw new ProjectFormatException($"Net '{definitionId}.{id}' has a non-finite or out-of-range route point.");
            }

            net.RoutePoints.Add(waypoint);
        }

        if (net.RoutePoints.Count > WireAnnotation.MaxRoutePoints)
        {
            throw new ProjectFormatException(
                $"Net '{definitionId}.{id}' has {net.RoutePoints.Count} route points; the maximum is {WireAnnotation.MaxRoutePoints}.");
        }

        foreach (PinRefDto pin in dto.Pins ?? [])
        {
            net.Pins.Add(new PinRef(
                pin.Instance ?? throw new ProjectFormatException($"Net '{definitionId}.{id}' has a pin reference without 'instance'."),
                pin.Pin ?? throw new ProjectFormatException($"Net '{definitionId}.{id}' has a pin reference without 'pin'.")));
        }

        return net;
    }

    private static PinDirection ParseDirection(string? value, string location) =>
        value?.ToLowerInvariant() switch
        {
            "input" => PinDirection.Input,
            "output" => PinDirection.Output,
            null => throw new ProjectFormatException($"{location} is missing 'direction'."),
            _ => throw new ProjectFormatException($"{location} has unknown direction '{value}'."),
        };

    private static ProjectDto MapProjectDto(CircuitProject project)
    {
        var dto = new ProjectDto
        {
            Format = FormatName,
            Version = CurrentVersion,
            Name = project.Name,
            Description = project.Description,
            Root = project.RootDefinitionId,
            Settings = new SettingsDto
            {
                MaxEvaluationsPerComponentPerTimestamp = project.Settings.MaxEvaluationsPerComponentPerTimestamp,
                MaxRecordedNetChanges = project.Settings.MaxRecordedNetChanges,
                DefaultAdvanceTicks = project.Settings.DefaultAdvanceTicks,
                TraceAllNets = project.Settings.TraceAllNets,
            },
            Domains = project.Domains.Select(MapDomainDto).ToList(),
            Definitions = project.Definitions.Select(MapDefinitionDto).ToList(),
        };

        return dto;
    }

    private static DomainDto MapDomainDto(SignalDomain domain)
    {
        DriverDto drivers = domain.Drivers switch
        {
            PriorityDriverResolution priority => new DriverDto
            {
                Kind = "priority",
                Priorities = priority.Priorities.ToList(),
                ConflictValue = priority.ConflictValue,
            },
            _ => new DriverDto { Kind = "single" },
        };

        return new DomainDto
        {
            Id = domain.Id,
            Name = domain.Name,
            Description = domain.Description,
            Values = domain.ValueNames.ToList(),
            Drivers = drivers,
        };
    }

    private static DefinitionDto MapDefinitionDto(CircuitDefinition definition) => new()
    {
        Id = definition.Id,
        Name = definition.Name,
        Description = definition.Description,
        Pins = definition.Pins.Select(pin => new PinDto
        {
            Id = pin.Id,
            Name = pin.Name,
            Direction = pin.Direction == PinDirection.Input ? "input" : "output",
            Domain = pin.DomainId,
        }).ToList(),
        Instances = definition.Instances.Select(MapInstanceDto).ToList(),
        Nets = definition.Nets.Select(net => new NetDto
        {
            Id = net.Id,
            Domain = net.DomainId,
            Label = net.Label,
            Initial = net.InitialValue,
            Color = net.WireColor,
            Route = net.RoutePoints.Count == 0
                ? null
                : net.RoutePoints.Select(point => new PointDto { X = point.X, Y = point.Y }).ToList(),
            Pins = net.Pins.Select(pin => new PinRefDto { Instance = pin.InstanceId, Pin = pin.PinId }).ToList(),
        }).ToList(),
        Ports = definition.Ports.Select(port => new PortDto { Pin = port.PinId, Net = port.NetId }).ToList(),
        NumericLanes = definition.NumericLanes.Count == 0
            ? null
            : definition.NumericLanes.Select(lane => new NumericLaneDto
            {
                Id = lane.Id,
                Name = lane.Name,
                Format = lane.FormatId,
                Pins = lane.PinIds.ToList(),
            }).ToList(),
    };

    private static InstanceDto MapInstanceDto(ComponentInstance instance)
    {
        var dto = new InstanceDto
        {
            Id = instance.Id,
            Definition = instance.DefinitionId,
            Label = instance.Label,
            X = instance.X,
            Y = instance.Y,
        };

        if (instance.Primitive is not null)
        {
            dto.Primitive = MapPrimitiveDto(instance.Primitive);
        }

        return dto;
    }

    private static PrimitiveDto MapPrimitiveDto(PrimitiveSpec spec)
    {
        var dto = new PrimitiveDto { Kind = spec.Kind };
        switch (spec)
        {
            case ConstantSpec constant:
                dto.Value = constant.Value;
                break;
            case LookupSpec lookup:
                dto.InputCount = lookup.InputCount;
                dto.Table = lookup.Table.ToArray();
                dto.Delay = lookup.DelayTicks;
                break;
            case DelaySpec delay:
                dto.Delay = delay.DelayTicks;
                dto.Initial = delay.InitialValue;
                break;
            case WaveformSpec waveform:
                dto.Durations = waveform.Durations.ToArray();
                dto.Values = waveform.Values.ToArray();
                break;
            case ProbeSpec probe:
                dto.Label = probe.Label;
                break;
        }

        return dto;
    }
}
