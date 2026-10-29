namespace Radix.Core.Serialization;

internal sealed class ProjectDto
{
    public string? Format { get; set; }

    public int Version { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Root { get; set; }

    public SettingsDto? Settings { get; set; }

    public ViewDto? View { get; set; }

    public List<DomainDto>? Domains { get; set; }

    public List<NumericFormatDto>? CustomNumericFormats { get; set; }

    public List<DefinitionDto>? Definitions { get; set; }
}

internal sealed class NumericFormatDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Domain { get; set; }

    public List<long>? Digits { get; set; }

    public long? Radix { get; set; }

    public string? Weighting { get; set; }

    public bool LeastSignificantFirst { get; set; } = true;

    public int? NegativeSignValue { get; set; }

    public string? Description { get; set; }
}

internal sealed class ViewDto
{
    public string? Definition { get; set; }

    public double? Scale { get; set; }

    public double? OffsetX { get; set; }

    public double? OffsetY { get; set; }

    public Dictionary<string, List<string>>? WaveformPins { get; set; }
}

internal sealed class SettingsDto
{
    public int MaxEvaluationsPerComponentPerTimestamp { get; set; } = 64;

    public int MaxRecordedNetChanges { get; set; } = 500_000;

    public long DefaultAdvanceTicks { get; set; } = 10;

    public bool TraceAllNets { get; set; } = true;
}

internal sealed class DomainDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public List<string>? Values { get; set; }

    public DriverDto? Drivers { get; set; }

    public string? Description { get; set; }
}

internal sealed class DriverDto
{
    public string? Kind { get; set; }

    public List<int>? Priorities { get; set; }

    public int? ConflictValue { get; set; }
}

internal sealed class DefinitionDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public List<PinDto>? Pins { get; set; }

    public List<InstanceDto>? Instances { get; set; }

    public List<NetDto>? Nets { get; set; }

    public List<PortDto>? Ports { get; set; }

    public List<NumericLaneDto>? NumericLanes { get; set; }
}

internal sealed class NumericLaneDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Format { get; set; }

    public List<string>? Pins { get; set; }
}

internal sealed class PinDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Direction { get; set; }

    public string? Domain { get; set; }
}

internal sealed class InstanceDto
{
    public string? Id { get; set; }

    public string? Definition { get; set; }

    public PrimitiveDto? Primitive { get; set; }

    public string? Label { get; set; }

    public double X { get; set; }

    public double Y { get; set; }
}

internal sealed class PrimitiveDto
{
    public string? Kind { get; set; }

    public int? Value { get; set; }

    public int? InputCount { get; set; }

    public int[]? Table { get; set; }

    public long? Delay { get; set; }

    public int? Initial { get; set; }

    public long[]? Durations { get; set; }

    public int[]? Values { get; set; }

    public string? Label { get; set; }
}

internal sealed class NetDto
{
    public string? Id { get; set; }

    public string? Domain { get; set; }

    public string? Label { get; set; }

    public int? Initial { get; set; }

    /// <summary>Optional #RRGGBB wire color; omitted means domain-derived coloring.</summary>
    public string? Color { get; set; }

    /// <summary>Optional author bendpoints for wire routing.</summary>
    public List<PointDto>? Route { get; set; }

    public List<PinRefDto>? Pins { get; set; }
}

internal sealed class PointDto
{
    public double X { get; set; }

    public double Y { get; set; }
}

internal sealed class PinRefDto
{
    public string? Instance { get; set; }

    public string? Pin { get; set; }
}

internal sealed class PortDto
{
    public string? Pin { get; set; }

    public string? Net { get; set; }
}
