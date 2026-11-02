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

    /// <summary>Optional authored DC electrical circuits; omitted when none exist.</summary>
    public List<ElectricalCircuitDto>? ElectricalCircuits { get; set; }
}

/// <summary>One authored node-to-pin binding inside an electrical circuit.</summary>
internal sealed class ElectricalDriveBindingDto
{
    public string? Node { get; set; }

    public string? Pin { get; set; }

    public double? TicksPerSecond { get; set; }

    /// <summary>True when the binding states what the pin should read; omitted means it drives it.</summary>
    public bool? Checks { get; set; }
}

internal sealed class ElectricalCircuitDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Ground { get; set; }

    /// <summary>
    /// Ascending voltage boundaries for reading this circuit's nodes as levels; omitted means the
    /// reader falls back to equal thirds of the circuit's rail.
    /// </summary>
    public List<double>? LevelThresholds { get; set; }

    /// <summary>Digital pins this circuit's nodes drive; omitted when the circuit drives none.</summary>
    public List<ElectricalDriveBindingDto>? DriveBindings { get; set; }

    /// <summary>
    /// Ticks a measured delay may differ from the declared one; omitted means zero, so a file that
    /// says nothing is judged exactly.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int? DelayToleranceTicks { get; set; }

    /// <summary>Tolerances for individual transitions; omitted when the circuit-wide one governs all.</summary>
    public List<ElectricalDelayToleranceDto>? DelayTolerances { get; set; }

    public List<ElectricalNodeDto>? Nodes { get; set; }

    /// <summary>Named interface nodes exposed to a parent circuit; omitted when none are exposed.</summary>
    public List<ElectricalPortDto>? Ports { get; set; }

    public List<ElectricalComponentDto>? Components { get; set; }
}

internal sealed class ElectricalPortDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Node { get; set; }
}

internal sealed class ElectricalDelayToleranceDto
{
    /// <summary>The output pin this entry is for; omitted when it applies to any pin.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Pin { get; set; }

    public int From { get; set; }

    public int To { get; set; }

    public int Ticks { get; set; }
}

internal sealed class ElectricalNodeDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }
}

internal sealed class ElectricalComponentDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Kind { get; set; }

    /// <summary>Referenced circuit for a subcircuit kind; omitted for every built-in kind.</summary>
    public string? Circuit { get; set; }

    public double? Value { get; set; }

    public double? InitialValue { get; set; }

    /// <summary>MOSFET channel polarity: "n" or "p". Stored only for a mosfet.</summary>
    public string? Channel { get; set; }

    /// <summary>MOSFET threshold voltage magnitude in volts. Stored only for a mosfet.</summary>
    public double? ThresholdVoltage { get; set; }

    /// <summary>
    /// MOSFET channel-length modulation <c>lambda</c> in 1/V; omitted means zero. Stored only for
    /// a mosfet.
    /// </summary>
    public double? ChannelLengthModulation { get; set; }

    /// <summary>
    /// MOSFET gate-source capacitance in farads; omitted means zero. Stored only for a mosfet.
    /// </summary>
    public double? GateSourceCapacitance { get; set; }

    /// <summary>
    /// MOSFET gate-drain capacitance in farads; omitted means zero. Stored only for a mosfet.
    /// </summary>
    public double? GateDrainCapacitance { get; set; }

    /// <summary>
    /// MOSFET drain-source capacitance in farads; omitted means zero. Stored only for a mosfet.
    /// </summary>
    public double? DrainSourceCapacitance { get; set; }

    /// <summary>
    /// Whether the device's gate capacitances follow the solved bias instead of holding their
    /// authored values; omitted means constant. Stored only for a mosfet.
    /// </summary>
    public bool? BiasDependentCapacitance { get; set; }

    public List<ElectricalWaveformPointDto>? Waveform { get; set; }

    public List<ElectricalTerminalDto>? Terminals { get; set; }
}

internal sealed class ElectricalWaveformPointDto
{
    public double? Time { get; set; }

    public double? Value { get; set; }
}

internal sealed class ElectricalTerminalDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Polarity { get; set; }

    public string? Node { get; set; }
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

    public int MaxPendingEvents { get; set; } = 1_000_000;

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

    /// <summary>
    /// Written only when true, so a project without externally supplied pins keeps the file it had.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExternalSupply { get; set; }
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
