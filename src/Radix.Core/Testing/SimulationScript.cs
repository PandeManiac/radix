using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radix.Core.Testing;

/// <summary>Thrown when a simulation script is malformed or unsupported.</summary>
public sealed class SimulationScriptException : Exception
{
    public SimulationScriptException(string message)
        : base(message)
    {
    }

    public SimulationScriptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A value written in a script. Numeric indices and domain labels are both accepted;
/// labels are resolved against the target domain when the script runs.
/// </summary>
public readonly record struct ScriptValue(string Raw, int? Index)
{
    /// <summary>Clears an external drive. This is the kernel's undriven marker, not a domain value.</summary>
    public static ScriptValue Undriven { get; } = new("undriven", -1);

    public bool IsNumeric => Index is not null;
}

/// <summary>An external drive applied at a scripted tick.</summary>
public readonly record struct ScriptDrive(string PinId, ScriptValue Value);

/// <summary>An expected interface-pin value at a scripted tick.</summary>
public readonly record struct ScriptPinExpectation(string PinId, ScriptValue Expected);

/// <summary>An expected decoded numeric-lane value at a scripted tick.</summary>
public sealed class ScriptLaneExpectation
{
    public ScriptLaneExpectation(string laneId, BigInteger expected)
    {
        LaneId = laneId;
        Expected = expected;
    }

    public string LaneId { get; }

    public BigInteger Expected { get; }
}

/// <summary>
/// An eventual assertion: every listed pin and lane expectation must hold together
/// at the step's tick or at some later tick within <see cref="Ticks"/>. A match does
/// not have to remain stable after the window, so this checks "settles by", not a
/// stable period or a waveform edge.
/// </summary>
public sealed class ScriptWindow
{
    public ScriptWindow(
        long ticks,
        IReadOnlyList<ScriptPinExpectation> pinExpectations,
        IReadOnlyList<ScriptLaneExpectation> laneExpectations)
    {
        Ticks = ticks;
        PinExpectations = pinExpectations;
        LaneExpectations = laneExpectations;
    }

    /// <summary>Maximum ticks after the step's tick, inclusive, to wait for a match.</summary>
    public long Ticks { get; }

    public IReadOnlyList<ScriptPinExpectation> PinExpectations { get; }

    public IReadOnlyList<ScriptLaneExpectation> LaneExpectations { get; }
}

/// <summary>
/// One ordered step. The runner advances to <see cref="At"/>, applies drives, settles
/// zero-delay events at that timestamp, then checks expectations.
/// </summary>
public sealed class SimulationScriptStep
{
    public SimulationScriptStep(
        long at,
        IReadOnlyList<ScriptDrive> drives,
        IReadOnlyList<ScriptPinExpectation> pinExpectations,
        IReadOnlyList<ScriptLaneExpectation> laneExpectations,
        bool? expectHalted,
        string? description,
        ScriptWindow? window = null)
    {
        At = at;
        Drives = drives;
        PinExpectations = pinExpectations;
        LaneExpectations = laneExpectations;
        ExpectHalted = expectHalted;
        Description = description;
        Window = window;
    }

    /// <summary>Absolute simulated tick. Missing values default to the previous step's tick.</summary>
    public long At { get; }

    public IReadOnlyList<ScriptDrive> Drives { get; }

    public IReadOnlyList<ScriptPinExpectation> PinExpectations { get; }

    public IReadOnlyList<ScriptLaneExpectation> LaneExpectations { get; }

    /// <summary>Optional assertion about the engine's halt state after this step settles.</summary>
    public bool? ExpectHalted { get; }

    public string? Description { get; }

    /// <summary>Optional eventual assertion that may advance simulated time.</summary>
    public ScriptWindow? Window { get; }

    public bool HasActions =>
        Drives.Count > 0 || PinExpectations.Count > 0 || LaneExpectations.Count > 0 ||
        ExpectHalted is not null || Window is not null;
}

/// <summary>
/// A deterministic, project-independent simulation test script. Scripts are data,
/// never code, and use the same public engine APIs as the editor and CLI. They are
/// intentionally not stored inside project files: a project describes a circuit, a
/// script describes an experiment over it.
/// </summary>
public sealed class SimulationScript
{
    public const string FormatName = "radix-test";
    public const int CurrentVersion = 1;
    public const string DefaultExtension = ".radixtest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private SimulationScript(
        string name,
        string? description,
        string? definitionId,
        IReadOnlyList<SimulationScriptStep> steps)
    {
        Name = name;
        Description = description;
        DefinitionId = definitionId;
        Steps = steps;
    }

    public string Name { get; }

    public string? Description { get; }

    /// <summary>Optional definition override. The project root is used when absent.</summary>
    public string? DefinitionId { get; }

    public IReadOnlyList<SimulationScriptStep> Steps { get; }

    public static SimulationScript Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadFromString(File.ReadAllText(path, Encoding.UTF8));
    }

    public static SimulationScript LoadFromString(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ScriptDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ScriptDto>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new SimulationScriptException($"Invalid test script JSON: {exception.Message}", exception);
        }

        if (dto is null)
        {
            throw new SimulationScriptException("Test script JSON is empty or null.");
        }

        if (!string.Equals(dto.Format, FormatName, StringComparison.Ordinal))
        {
            throw new SimulationScriptException(
                $"Expected test script format '{FormatName}' but found '{dto.Format ?? "<missing>"}'.");
        }

        if (dto.Version < 1 || dto.Version > CurrentVersion)
        {
            throw new SimulationScriptException(
                $"Test script version {dto.Version} is not supported; this build accepts 1..{CurrentVersion}.");
        }

        if (dto.Steps is null || dto.Steps.Count == 0)
        {
            throw new SimulationScriptException("A test script needs at least one step.");
        }

        var steps = new List<SimulationScriptStep>(dto.Steps.Count);
        long previousAt = 0;
        for (int i = 0; i < dto.Steps.Count; i++)
        {
            StepDto step = dto.Steps[i]
                ?? throw new SimulationScriptException($"Step {i + 1} is null.");
            int stepNumber = i + 1;
            long at = step.At ?? previousAt;
            if (at < 0)
            {
                throw new SimulationScriptException($"Step {stepNumber} has a negative 'at' ({at}).");
            }

            if (at < previousAt)
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} moves backward from t={previousAt} to t={at}; steps must be chronological.");
            }

            previousAt = at;
            var drives = MapDrives(step.Set, stepNumber);
            var pinExpectations = MapExpectations(step.Expect, stepNumber, "expect");
            var laneExpectations = MapLaneExpectations(step.ExpectLane, stepNumber, "expectLane");
            ScriptWindow? window = MapWindow(step.Within, stepNumber, at);

            // A bare step is a deliberate advance-only marker; it is useful for
            // separating phases and lets later steps inherit this tick.
            steps.Add(new SimulationScriptStep(
                at,
                drives,
                pinExpectations,
                laneExpectations,
                step.ExpectHalted,
                step.Description,
                window));
        }

        return new SimulationScript(
            string.IsNullOrWhiteSpace(dto.Name) ? "untitled" : dto.Name.Trim(),
            dto.Description,
            string.IsNullOrWhiteSpace(dto.Definition) ? null : dto.Definition.Trim(),
            steps);
    }

    private static List<ScriptDrive> MapDrives(Dictionary<string, JsonElement>? source, int stepNumber)
    {
        var drives = new List<ScriptDrive>();
        if (source is null)
        {
            return drives;
        }

        if (source.Count == 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} has an empty 'set' object.");
        }

        foreach ((string pinId, JsonElement element) in source)
        {
            drives.Add(new ScriptDrive(
                NormalizeId(pinId, "pin", stepNumber, "set"),
                ReadValue(element, stepNumber, $"set.{pinId}")));
        }

        return drives;
    }

    private static List<ScriptPinExpectation> MapExpectations(
        Dictionary<string, JsonElement>? source,
        int stepNumber,
        string field)
    {
        var expectations = new List<ScriptPinExpectation>();
        if (source is null)
        {
            return expectations;
        }

        if (source.Count == 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} has an empty '{field}' object.");
        }

        foreach ((string pinId, JsonElement element) in source)
        {
            expectations.Add(new ScriptPinExpectation(
                NormalizeId(pinId, "pin", stepNumber, field),
                ReadValue(element, stepNumber, $"{field}.{pinId}")));
        }

        return expectations;
    }

    private static List<ScriptLaneExpectation> MapLaneExpectations(
        Dictionary<string, JsonElement>? source,
        int stepNumber,
        string field)
    {
        var expectations = new List<ScriptLaneExpectation>();
        if (source is null)
        {
            return expectations;
        }

        if (source.Count == 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} has an empty '{field}' object.");
        }

        foreach ((string laneId, JsonElement element) in source)
        {
            string normalizedLane = NormalizeId(laneId, "lane", stepNumber, field);
            if (!TryReadBigInteger(element, out BigInteger numeric))
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{field}.{laneId}' must be an integer value.");
            }

            expectations.Add(new ScriptLaneExpectation(normalizedLane, numeric));
        }

        return expectations;
    }

    private static ScriptWindow? MapWindow(WindowDto? source, int stepNumber, long at)
    {
        if (source is null)
        {
            return null;
        }

        if (source.Ticks is null)
        {
            throw new SimulationScriptException($"Step {stepNumber} 'within' needs an explicit 'ticks' value.");
        }

        long ticks = source.Ticks.Value;
        if (ticks < 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} 'within.ticks' must be non-negative.");
        }

        if (ticks > long.MaxValue - at)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'within.ticks' moves the deadline past the maximum simulated time.");
        }

        var pinExpectations = MapExpectations(source.Expect, stepNumber, "within.expect");
        var laneExpectations = MapLaneExpectations(source.ExpectLane, stepNumber, "within.expectLane");
        if (pinExpectations.Count == 0 && laneExpectations.Count == 0)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'within' needs at least one 'expect' or 'expectLane' entry.");
        }

        return new ScriptWindow(ticks, pinExpectations, laneExpectations);
    }

    private static string NormalizeId(string id, string kind, int stepNumber, string field)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new SimulationScriptException($"Step {stepNumber} '{field}' contains a blank {kind} ID.");
        }

        return id.Trim();
    }

    private static ScriptValue ReadValue(JsonElement element, int stepNumber, string field)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return ScriptValue.Undriven;
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out long numeric) || numeric < -1 || numeric > int.MaxValue)
                {
                    throw new SimulationScriptException(
                        $"Step {stepNumber} '{field}' must be an integer signal index or a domain label.");
                }

                return new ScriptValue(numeric.ToString(CultureInfo.InvariantCulture), (int)numeric);
            case JsonValueKind.String:
                string text = element.GetString()?.Trim() ?? string.Empty;
                if (text.Length == 0)
                {
                    throw new SimulationScriptException($"Step {stepNumber} '{field}' is an empty string.");
                }

                if (string.Equals(text, "undriven", StringComparison.OrdinalIgnoreCase))
                {
                    return ScriptValue.Undriven;
                }

                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                {
                    if (parsed < -1 || parsed > int.MaxValue)
                    {
                        throw new SimulationScriptException(
                            $"Step {stepNumber} '{field}' has out-of-range signal index {parsed}.");
                    }

                    return new ScriptValue(text, (int)parsed);
                }

                return new ScriptValue(text, null);
            default:
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{field}' must be a number, a string, or null.");
        }
    }

    private static bool TryReadBigInteger(JsonElement element, out BigInteger value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return BigInteger.TryParse(
                element.GetRawText(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return BigInteger.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        value = BigInteger.Zero;
        return false;
    }

    private sealed class ScriptDto
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public string? Name { get; set; }

        public string? Description { get; set; }

        public string? Definition { get; set; }

        public List<StepDto>? Steps { get; set; }
    }

    private sealed class StepDto
    {
        public long? At { get; set; }

        public string? Description { get; set; }

        public Dictionary<string, JsonElement>? Set { get; set; }

        public Dictionary<string, JsonElement>? Expect { get; set; }

        public Dictionary<string, JsonElement>? ExpectLane { get; set; }

        public bool? ExpectHalted { get; set; }

        public WindowDto? Within { get; set; }
    }

    private sealed class WindowDto
    {
        public long? Ticks { get; set; }

        public Dictionary<string, JsonElement>? Expect { get; set; }

        public Dictionary<string, JsonElement>? ExpectLane { get; set; }
    }
}
