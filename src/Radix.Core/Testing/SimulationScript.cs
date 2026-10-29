using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Radix.Core.Model;

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

/// <summary>
/// An authored net at a hierarchy path, relative to the script's simulated
/// definition. The root definition has an empty path; a nested instance uses the
/// instance IDs joined with '/'. Unlike a pin reference, a net reference names the
/// authored wiring itself, including nets that never reach an interface.
/// </summary>
public readonly record struct ScriptNetReference(string Path, string NetId)
{
    public override string ToString() => Path.Length == 0 ? NetId : $"{Path}/{NetId}";
}

/// <summary>An expected internal-net value at a scripted tick.</summary>
public sealed record ScriptNetExpectation(ScriptNetReference Reference, ScriptValue Expected);

/// <summary>
/// A signal observed by a transition, edge, or sequence window: either an
/// interface pin or an authored internal net. <see cref="Name"/> is the pin ID or
/// the display form of the net reference, and <see cref="Net"/> distinguishes them.
/// </summary>
public readonly record struct ScriptSignalReference(string Name, ScriptNetReference? Net)
{
    public bool IsNet => Net.HasValue;

    public string Kind => IsNet ? "net" : "pin";

    public static ScriptSignalReference Pin(string pinId) => new(pinId, null);

    public static ScriptSignalReference InternalNet(ScriptNetReference reference) =>
        new(reference.ToString(), reference);

    public override string ToString() => Name;
}

/// <summary>The operator used by a cross-signal relation assertion.</summary>
public enum ScriptRelationOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}

/// <summary>
/// A typed comparison between two signals at the same tick. Unlike a value
/// expectation, this states a property that must hold without naming either
/// signal's exact value, so it survives a domain relabeling as long as both
/// signals still share that domain.
/// </summary>
public sealed record ScriptRelationExpectation(
    ScriptSignalReference Left,
    ScriptSignalReference Right,
    ScriptRelationOperator Operator)
{
    public string OperatorText => Operator switch
    {
        ScriptRelationOperator.Equal => "eq",
        ScriptRelationOperator.NotEqual => "ne",
        ScriptRelationOperator.LessThan => "lt",
        ScriptRelationOperator.LessThanOrEqual => "le",
        ScriptRelationOperator.GreaterThan => "gt",
        ScriptRelationOperator.GreaterThanOrEqual => "ge",
        _ => throw new InvalidOperationException($"Unknown relation operator '{Operator}'."),
    };
}

/// <summary>
/// A numeric lane written as one value at a scripted tick. The runner encodes it with
/// the lane's format and drives its pins in one batch, so scripts can set multi-pin
/// numbers without duplicating the encoding by hand.
/// </summary>
public sealed class ScriptLaneDrive
{
    public ScriptLaneDrive(string laneId, BigInteger value)
    {
        LaneId = laneId;
        Value = value;
    }

    public string LaneId { get; }

    public BigInteger Value { get; }
}

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
        IReadOnlyList<ScriptLaneExpectation> laneExpectations,
        IReadOnlyList<ScriptNetExpectation> netExpectations,
        IReadOnlyList<ScriptRelationExpectation>? relationExpectations = null)
    {
        Ticks = ticks;
        PinExpectations = pinExpectations;
        LaneExpectations = laneExpectations;
        NetExpectations = netExpectations;
        RelationExpectations = relationExpectations ?? [];
    }

    /// <summary>Maximum ticks after the step's tick, inclusive, to wait for a match.</summary>
    public long Ticks { get; }

    public IReadOnlyList<ScriptPinExpectation> PinExpectations { get; }

    public IReadOnlyList<ScriptLaneExpectation> LaneExpectations { get; }

    public IReadOnlyList<ScriptNetExpectation> NetExpectations { get; }

    public IReadOnlyList<ScriptRelationExpectation> RelationExpectations { get; }
}

/// <summary>
/// A held-value assertion: the expected values must match at every tick from the
/// step's tick through <c>At + Ticks</c> inclusive. Used to prove that a value is
/// stable, not merely eventually reached.
/// </summary>
public sealed class ScriptStability
{
    public ScriptStability(
        long ticks,
        IReadOnlyList<ScriptPinExpectation> pinExpectations,
        IReadOnlyList<ScriptLaneExpectation> laneExpectations,
        IReadOnlyList<ScriptNetExpectation> netExpectations,
        IReadOnlyList<ScriptRelationExpectation>? relationExpectations = null)
    {
        Ticks = ticks;
        PinExpectations = pinExpectations;
        LaneExpectations = laneExpectations;
        NetExpectations = netExpectations;
        RelationExpectations = relationExpectations ?? [];
    }

    /// <summary>Number of ticks after the step's tick that the values must hold, inclusive.</summary>
    public long Ticks { get; }

    public IReadOnlyList<ScriptPinExpectation> PinExpectations { get; }

    public IReadOnlyList<ScriptLaneExpectation> LaneExpectations { get; }

    public IReadOnlyList<ScriptNetExpectation> NetExpectations { get; }

    public IReadOnlyList<ScriptRelationExpectation> RelationExpectations { get; }
}

/// <summary>
/// A pre-drive held-value assertion: the expected values must match at every tick
/// from <c>At - Ticks</c> through <c>At - 1</c>, before this step's drives. Used to
/// express setup conditions around a clock edge without sampling history.
/// </summary>
public sealed class ScriptSetup
{
    public ScriptSetup(
        long ticks,
        IReadOnlyList<ScriptPinExpectation> pinExpectations,
        IReadOnlyList<ScriptLaneExpectation> laneExpectations,
        IReadOnlyList<ScriptNetExpectation> netExpectations,
        IReadOnlyList<ScriptRelationExpectation>? relationExpectations = null)
    {
        Ticks = ticks;
        PinExpectations = pinExpectations;
        LaneExpectations = laneExpectations;
        NetExpectations = netExpectations;
        RelationExpectations = relationExpectations ?? [];
    }

    /// <summary>Number of ticks before the step that the values must hold.</summary>
    public long Ticks { get; }

    public IReadOnlyList<ScriptPinExpectation> PinExpectations { get; }

    public IReadOnlyList<ScriptLaneExpectation> LaneExpectations { get; }

    public IReadOnlyList<ScriptNetExpectation> NetExpectations { get; }

    public IReadOnlyList<ScriptRelationExpectation> RelationExpectations { get; }
}

/// <summary>Expected number of value changes on one pin or internal net over an interval.</summary>
public sealed record ScriptTransitionExpectation(ScriptSignalReference Signal, long Count);

/// <summary>
/// A transition-count assertion: every observed signal must change value exactly
/// <see cref="ScriptTransitionExpectation.Count"/> times between the step's tick and
/// <c>At + Ticks</c>, inclusive, after this step's drives are applied.
/// </summary>
public sealed class ScriptTransitionWindow
{
    public ScriptTransitionWindow(
        long ticks,
        IReadOnlyList<ScriptTransitionExpectation> expectations)
    {
        Ticks = ticks;
        Expectations = expectations;
    }

    public long Ticks { get; }

    public IReadOnlyList<ScriptTransitionExpectation> Expectations { get; }
}

/// <summary>
/// One filtered edge count. A missing <see cref="From"/> or <see cref="To"/> matches
/// any value on that side, so a filter can count rising edges (0 to 1), falling
/// edges (1 to 0), or every change into a chosen value.
/// </summary>
public sealed record ScriptEdgeFilter(ScriptValue? From, ScriptValue? To, long Count);

/// <summary>Filtered edge expectations for one pin or internal net.</summary>
public sealed record ScriptEdgeExpectation(ScriptSignalReference Signal, IReadOnlyList<ScriptEdgeFilter> Filters);

/// <summary>
/// A filtered-edge assertion: each filter must match exactly its expected number of
/// resolved net transitions between the step's tick and <c>At + Ticks</c>, inclusive,
/// after this step's drives are applied. Unlike a total transition count, this can
/// distinguish a rising clock edge from a falling one or from a data change.
/// </summary>
public sealed class ScriptEdgeWindow
{
    public ScriptEdgeWindow(
        long ticks,
        IReadOnlyList<ScriptEdgeExpectation> expectations)
    {
        Ticks = ticks;
        Expectations = expectations;
    }

    public long Ticks { get; }

    public IReadOnlyList<ScriptEdgeExpectation> Expectations { get; }
}

/// <summary>One ordered edge in a sequence assertion.</summary>
public sealed record ScriptEdgeSequenceItem(
    ScriptSignalReference Signal,
    ScriptValue? From,
    ScriptValue? To);

/// <summary>
/// An ordered-edge assertion: the listed transitions must appear within the
/// interval in the given order. Matches need not be adjacent, so unrelated
/// activity may occur between them. Each item uses the same filters as a
/// filtered-edge count, but describes a single occurrence rather than a count.
/// </summary>
public sealed class ScriptEdgeSequenceWindow
{
    public ScriptEdgeSequenceWindow(
        long ticks,
        IReadOnlyList<ScriptEdgeSequenceItem> items)
    {
        Ticks = ticks;
        Items = items;
    }

    public long Ticks { get; }

    public IReadOnlyList<ScriptEdgeSequenceItem> Items { get; }
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
        IReadOnlyList<ScriptLaneDrive> laneDrives,
        IReadOnlyList<ScriptPinExpectation> pinExpectations,
        IReadOnlyList<ScriptLaneExpectation> laneExpectations,
        IReadOnlyList<ScriptNetExpectation> netExpectations,
        bool? expectHalted,
        string? description,
        ScriptWindow? window = null,
        ScriptStability? stability = null,
        ScriptTransitionWindow? transitions = null,
        ScriptEdgeWindow? edges = null,
        ScriptEdgeSequenceWindow? sequence = null,
        ScriptSetup? setup = null,
        IReadOnlyList<ScriptRelationExpectation>? relationExpectations = null)
    {
        At = at;
        Drives = drives;
        LaneDrives = laneDrives;
        PinExpectations = pinExpectations;
        LaneExpectations = laneExpectations;
        NetExpectations = netExpectations;
        ExpectHalted = expectHalted;
        Description = description;
        Window = window;
        Stability = stability;
        Transitions = transitions;
        Edges = edges;
        Sequence = sequence;
        Setup = setup;
        RelationExpectations = relationExpectations ?? [];
    }

    /// <summary>Absolute simulated tick. Missing values default to the previous step's tick.</summary>
    public long At { get; }

    public IReadOnlyList<ScriptDrive> Drives { get; }

    /// <summary>Numeric-lane values applied after the individual pin drives.</summary>
    public IReadOnlyList<ScriptLaneDrive> LaneDrives { get; }

    public IReadOnlyList<ScriptPinExpectation> PinExpectations { get; }

    public IReadOnlyList<ScriptLaneExpectation> LaneExpectations { get; }

    /// <summary>Expected values on authored nets, addressed by hierarchy path.</summary>
    public IReadOnlyList<ScriptNetExpectation> NetExpectations { get; }

    /// <summary>Optional typed comparisons between two signals at this step's tick.</summary>
    public IReadOnlyList<ScriptRelationExpectation> RelationExpectations { get; }

    /// <summary>Optional assertion about the engine's halt state after this step settles.</summary>
    public bool? ExpectHalted { get; }

    public string? Description { get; }

    /// <summary>Optional pre-drive held-value assertion in the window ending at At.</summary>
    public ScriptSetup? Setup { get; }

    /// <summary>Optional eventual assertion that may advance simulated time.</summary>
    public ScriptWindow? Window { get; }

    /// <summary>Optional held-value assertion over an inclusive interval of ticks.</summary>
    public ScriptStability? Stability { get; }

    /// <summary>Optional transition-count assertion over an inclusive interval of ticks.</summary>
    public ScriptTransitionWindow? Transitions { get; }

    /// <summary>Optional filtered-edge assertion over an inclusive interval of ticks.</summary>
    public ScriptEdgeWindow? Edges { get; }

    /// <summary>Optional ordered-edge assertion over an inclusive interval of ticks.</summary>
    public ScriptEdgeSequenceWindow? Sequence { get; }

    public bool HasActions =>
        Drives.Count > 0 || LaneDrives.Count > 0 || PinExpectations.Count > 0 || LaneExpectations.Count > 0 ||
        NetExpectations.Count > 0 || RelationExpectations.Count > 0 || ExpectHalted is not null ||
        Window is not null || Stability is not null ||
        Transitions is not null || Edges is not null || Sequence is not null ||
        Setup is not null;
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
            var laneDrives = MapLaneDrives(step.SetLane, stepNumber);
            var pinExpectations = MapExpectations(step.Expect, stepNumber, "expect");
            var laneExpectations = MapLaneExpectations(step.ExpectLane, stepNumber, "expectLane");
            var netExpectations = MapNetExpectations(step.ExpectNet, stepNumber, "expectNet");
            var relationExpectations = MapRelations(step.ExpectRelation, stepNumber, "expectRelation");
            if (step.Within is not null && step.Stable is not null)
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} has both 'within' and 'stable'; a step can wait for a value or require it to hold, not both.");
            }

            if (step.Edges is not null &&
                (step.Within is not null || step.Stable is not null || step.Transitions is not null ||
                 step.Sequence is not null))
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} cannot combine 'edges' with 'within', 'stable', 'transitions', or 'sequence'; only one timing block may advance the step.");
            }

            if (step.Sequence is not null &&
                (step.Within is not null || step.Stable is not null || step.Transitions is not null))
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} cannot combine 'sequence' with 'within', 'stable', or 'transitions'; only one timing block may advance the step.");
            }

            if (step.Transitions is not null && (step.Within is not null || step.Stable is not null))
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} cannot combine 'transitions' with 'within' or 'stable'; only one timing block may advance the step.");
            }

            if ((step.Stable is not null || step.Transitions is not null || step.Edges is not null ||
                 step.Sequence is not null) &&
                step.ExpectHalted == true)
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} cannot combine a timing window with 'expectHalted: true'; the window needs the simulation to run to its deadline.");
            }

            ScriptSetup? setup = MapSetup(step.Setup, stepNumber, at);
            ScriptWindow? window = MapWindow(step.Within, stepNumber, at);
            ScriptStability? stability = MapStability(step.Stable, stepNumber, at);
            ScriptTransitionWindow? transitions = MapTransitions(step.Transitions, stepNumber, at);
            ScriptEdgeWindow? edges = MapEdges(step.Edges, stepNumber, at);
            ScriptEdgeSequenceWindow? sequence = MapSequence(step.Sequence, stepNumber, at);

            // A bare step is a deliberate advance-only marker; it is useful for
            // separating phases and lets later steps inherit this tick.
            steps.Add(new SimulationScriptStep(
                at,
                drives,
                laneDrives,
                pinExpectations,
                laneExpectations,
                netExpectations,
                step.ExpectHalted,
                step.Description,
                window,
                stability,
                transitions,
                edges,
                sequence,
                setup,
                relationExpectations));
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

    private static List<ScriptLaneDrive> MapLaneDrives(
        Dictionary<string, JsonElement>? source,
        int stepNumber)
    {
        var drives = new List<ScriptLaneDrive>();
        if (source is null)
        {
            return drives;
        }

        if (source.Count == 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} has an empty 'setLane' object.");
        }

        foreach ((string laneId, JsonElement element) in source)
        {
            string normalizedLane = NormalizeId(laneId, "lane", stepNumber, "setLane");
            if (!TryReadBigInteger(element, out BigInteger numeric))
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} 'setLane.{laneId}' must be an integer value.");
            }

            drives.Add(new ScriptLaneDrive(normalizedLane, numeric));
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

    private static List<ScriptNetExpectation> MapNetExpectations(
        Dictionary<string, JsonElement>? source,
        int stepNumber,
        string field)
    {
        var expectations = new List<ScriptNetExpectation>();
        if (source is null)
        {
            return expectations;
        }

        if (source.Count == 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} has an empty '{field}' object.");
        }

        foreach ((string reference, JsonElement element) in source)
        {
            expectations.Add(new ScriptNetExpectation(
                ParseNetReference(reference, stepNumber, field),
                ReadValue(element, stepNumber, $"{field}.{reference}")));
        }

        return expectations;
    }

    private static ScriptNetReference ParseNetReference(string reference, int stepNumber, string field)
    {
        if (!TryParseNetReference(reference, out ScriptNetReference parsed, out string error))
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} '{field}' has an invalid net reference '{reference}': {error}");
        }

        return parsed;
    }

    private static ScriptSignalReference MapPinSignal(string pinId, int stepNumber, string field) =>
        ScriptSignalReference.Pin(NormalizeId(pinId, "pin", stepNumber, field));

    private static ScriptSignalReference MapNetSignal(string reference, int stepNumber, string field) =>
        ScriptSignalReference.InternalNet(ParseNetReference(reference, stepNumber, field));

    private static List<ScriptRelationExpectation> MapRelations(
        List<RelationDto?>? source,
        int stepNumber,
        string field)
    {
        var relations = new List<ScriptRelationExpectation>();
        if (source is null)
        {
            return relations;
        }

        if (source.Count == 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} has an empty '{field}' array.");
        }

        for (int i = 0; i < source.Count; i++)
        {
            RelationDto relation = source[i]
                ?? throw new SimulationScriptException($"Step {stepNumber} '{field}[{i}]' is null.");
            string itemField = $"{field}[{i}]";
            if (string.IsNullOrWhiteSpace(relation.Left))
            {
                throw new SimulationScriptException($"Step {stepNumber} '{itemField}' needs a non-blank 'left' signal.");
            }

            if (string.IsNullOrWhiteSpace(relation.Right))
            {
                throw new SimulationScriptException($"Step {stepNumber} '{itemField}' needs a non-blank 'right' signal.");
            }

            if (!TryParseSignalReference(relation.Left, out ScriptSignalReference left, out string leftError))
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{itemField}.left' is invalid: {leftError}");
            }

            if (!TryParseSignalReference(relation.Right, out ScriptSignalReference right, out string rightError))
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{itemField}.right' is invalid: {rightError}");
            }

            if (string.IsNullOrWhiteSpace(relation.Op))
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{itemField}' needs an 'op' (eq, ne, lt, le, gt, or ge).");
            }

            if (!TryParseRelationOperator(relation.Op, out ScriptRelationOperator op))
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{itemField}.op' must be eq, ne, lt, le, gt, or ge, not '{relation.Op}'.");
            }

            relations.Add(new ScriptRelationExpectation(left, right, op));
        }

        return relations;
    }

    private static bool TryParseRelationOperator(string value, out ScriptRelationOperator op)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "eq":
                op = ScriptRelationOperator.Equal;
                return true;
            case "ne":
                op = ScriptRelationOperator.NotEqual;
                return true;
            case "lt":
                op = ScriptRelationOperator.LessThan;
                return true;
            case "le":
                op = ScriptRelationOperator.LessThanOrEqual;
                return true;
            case "gt":
                op = ScriptRelationOperator.GreaterThan;
                return true;
            case "ge":
                op = ScriptRelationOperator.GreaterThanOrEqual;
                return true;
            default:
                op = default;
                return false;
        }
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

    private static ScriptSetup? MapSetup(WindowDto? source, int stepNumber, long at)
    {
        if (source is null)
        {
            return null;
        }

        (long ticks, List<ScriptPinExpectation> pins, List<ScriptLaneExpectation> lanes, List<ScriptNetExpectation> nets, List<ScriptRelationExpectation> relations) =
            MapWindowContents(source, stepNumber, at, "setup");
        if (ticks > at)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'setup.ticks' ({ticks}) cannot start before t=0 for a step at t={at}.");
        }

        return new ScriptSetup(ticks, pins, lanes, nets, relations);
    }

    private static ScriptWindow? MapWindow(WindowDto? source, int stepNumber, long at)
    {
        if (source is null)
        {
            return null;
        }

        (long ticks, List<ScriptPinExpectation> pins, List<ScriptLaneExpectation> lanes, List<ScriptNetExpectation> nets, List<ScriptRelationExpectation> relations) =
            MapWindowContents(source, stepNumber, at, "within");
        return new ScriptWindow(ticks, pins, lanes, nets, relations);
    }

    private static ScriptStability? MapStability(WindowDto? source, int stepNumber, long at)
    {
        if (source is null)
        {
            return null;
        }

        (long ticks, List<ScriptPinExpectation> pins, List<ScriptLaneExpectation> lanes, List<ScriptNetExpectation> nets, List<ScriptRelationExpectation> relations) =
            MapWindowContents(source, stepNumber, at, "stable");
        return new ScriptStability(ticks, pins, lanes, nets, relations);
    }

    private static ScriptTransitionWindow? MapTransitions(TransitionsDto? source, int stepNumber, long at)
    {
        if (source is null)
        {
            return null;
        }

        if (source.Ticks is null)
        {
            throw new SimulationScriptException($"Step {stepNumber} 'transitions' needs an explicit 'ticks' value.");
        }

        long ticks = source.Ticks.Value;
        if (ticks < 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} 'transitions.ticks' must be non-negative.");
        }

        if (ticks > long.MaxValue - at)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'transitions.ticks' moves the deadline past the maximum simulated time.");
        }

        bool hasPins = source.Expect is { Count: > 0 };
        bool hasNets = source.ExpectNet is { Count: > 0 };
        if (!hasPins && !hasNets)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'transitions' needs at least one 'expect' or 'expectNet' entry.");
        }

        var expectations = new List<ScriptTransitionExpectation>(
            (source.Expect?.Count ?? 0) + (source.ExpectNet?.Count ?? 0));
        if (source.Expect is not null)
        {
            foreach ((string pinId, JsonElement element) in source.Expect)
            {
                expectations.Add(new ScriptTransitionExpectation(
                    MapPinSignal(pinId, stepNumber, "transitions.expect"),
                    ReadTransitionCount(element, stepNumber, $"transitions.expect.{pinId}")));
            }
        }

        if (source.ExpectNet is not null)
        {
            foreach ((string reference, JsonElement element) in source.ExpectNet)
            {
                expectations.Add(new ScriptTransitionExpectation(
                    MapNetSignal(reference, stepNumber, "transitions.expectNet"),
                    ReadTransitionCount(element, stepNumber, $"transitions.expectNet.{reference}")));
            }
        }

        return new ScriptTransitionWindow(ticks, expectations);
    }

    private static long ReadTransitionCount(JsonElement element, int stepNumber, string field)
    {
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt64(out long count) ||
            count < 0)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} '{field}' must be a non-negative integer count.");
        }

        return count;
    }

    private static ScriptEdgeWindow? MapEdges(EdgesDto? source, int stepNumber, long at)
    {
        if (source is null)
        {
            return null;
        }

        if (source.Ticks is null)
        {
            throw new SimulationScriptException($"Step {stepNumber} 'edges' needs an explicit 'ticks' value.");
        }

        long ticks = source.Ticks.Value;
        if (ticks < 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} 'edges.ticks' must be non-negative.");
        }

        if (ticks > long.MaxValue - at)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'edges.ticks' moves the deadline past the maximum simulated time.");
        }

        bool hasPins = source.Expect is { Count: > 0 };
        bool hasNets = source.ExpectNet is { Count: > 0 };
        if (!hasPins && !hasNets)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'edges' needs at least one 'expect' or 'expectNet' entry.");
        }

        var expectations = new List<ScriptEdgeExpectation>(
            (source.Expect?.Count ?? 0) + (source.ExpectNet?.Count ?? 0));
        if (source.Expect is not null)
        {
            foreach ((string pinId, List<EdgeFilterDto>? filters) in source.Expect)
            {
                expectations.Add(new ScriptEdgeExpectation(
                    MapPinSignal(pinId, stepNumber, "edges.expect"),
                    MapEdgeFilters(filters, stepNumber, $"edges.expect.{pinId}")));
            }
        }

        if (source.ExpectNet is not null)
        {
            foreach ((string reference, List<EdgeFilterDto>? filters) in source.ExpectNet)
            {
                expectations.Add(new ScriptEdgeExpectation(
                    MapNetSignal(reference, stepNumber, "edges.expectNet"),
                    MapEdgeFilters(filters, stepNumber, $"edges.expectNet.{reference}")));
            }
        }

        return new ScriptEdgeWindow(ticks, expectations);
    }

    private static List<ScriptEdgeFilter> MapEdgeFilters(
        List<EdgeFilterDto>? filters,
        int stepNumber,
        string field)
    {
        if (filters is null || filters.Count == 0)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} '{field}' needs at least one edge filter.");
        }

        var mapped = new List<ScriptEdgeFilter>(filters.Count);
        foreach (EdgeFilterDto filter in filters)
        {
            if (filter is null)
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{field}' contains a null filter.");
            }

            bool hasFrom = filter.From.ValueKind != JsonValueKind.Undefined;
            bool hasTo = filter.To.ValueKind != JsonValueKind.Undefined;
            if (!hasFrom && !hasTo)
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{field}' filter needs 'from', 'to', or both.");
            }

            if (filter.Count is null || filter.Count < 0)
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{field}' filter needs a non-negative 'count'.");
            }

            ScriptValue? from = hasFrom
                ? ReadValue(filter.From, stepNumber, $"{field}.from")
                : null;
            ScriptValue? to = hasTo
                ? ReadValue(filter.To, stepNumber, $"{field}.to")
                : null;
            mapped.Add(new ScriptEdgeFilter(from, to, filter.Count.Value));
        }

        return mapped;
    }

    private static ScriptEdgeSequenceWindow? MapSequence(SequenceDto? source, int stepNumber, long at)
    {
        if (source is null)
        {
            return null;
        }

        if (source.Ticks is null)
        {
            throw new SimulationScriptException($"Step {stepNumber} 'sequence' needs an explicit 'ticks' value.");
        }

        long ticks = source.Ticks.Value;
        if (ticks < 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} 'sequence.ticks' must be non-negative.");
        }

        if (ticks > long.MaxValue - at)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'sequence.ticks' moves the deadline past the maximum simulated time.");
        }

        if (source.Expect is null || source.Expect.Count == 0)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'sequence' needs at least one 'expect' item.");
        }

        var items = new List<ScriptEdgeSequenceItem>(source.Expect.Count);
        for (int i = 0; i < source.Expect.Count; i++)
        {
            SequenceItemDto item = source.Expect[i]
                ?? throw new SimulationScriptException(
                    $"Step {stepNumber} 'sequence.expect[{i}]' is null.");
            string field = $"sequence.expect[{i}]";
            bool hasPin = !string.IsNullOrWhiteSpace(item.Pin);
            bool hasNet = !string.IsNullOrWhiteSpace(item.Net);
            if (hasPin == hasNet)
            {
                throw new SimulationScriptException(hasPin
                    ? $"Step {stepNumber} '{field}' cannot have both 'pin' and 'net'."
                    : $"Step {stepNumber} '{field}' needs a non-blank 'pin' or 'net'.");
            }

            ScriptSignalReference signal = hasPin
                ? MapPinSignal(item.Pin!, stepNumber, field)
                : MapNetSignal(item.Net!, stepNumber, field);

            bool hasFrom = item.From.ValueKind != JsonValueKind.Undefined;
            bool hasTo = item.To.ValueKind != JsonValueKind.Undefined;
            if (!hasFrom && !hasTo)
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{field}' needs 'from', 'to', or both.");
            }

            ScriptValue? from = hasFrom
                ? ReadValue(item.From, stepNumber, $"{field}.from")
                : null;
            ScriptValue? to = hasTo
                ? ReadValue(item.To, stepNumber, $"{field}.to")
                : null;
            items.Add(new ScriptEdgeSequenceItem(
                signal,
                from,
                to));
        }

        return new ScriptEdgeSequenceWindow(ticks, items);
    }

    private static (long Ticks, List<ScriptPinExpectation> Pins, List<ScriptLaneExpectation> Lanes, List<ScriptNetExpectation> Nets, List<ScriptRelationExpectation> Relations)
        MapWindowContents(WindowDto source, int stepNumber, long at, string field)
    {
        if (source.Ticks is null)
        {
            throw new SimulationScriptException($"Step {stepNumber} '{field}' needs an explicit 'ticks' value.");
        }

        long ticks = source.Ticks.Value;
        if (ticks < 0)
        {
            throw new SimulationScriptException($"Step {stepNumber} '{field}.ticks' must be non-negative.");
        }

        if (ticks > long.MaxValue - at)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} '{field}.ticks' moves the deadline past the maximum simulated time.");
        }

        var pinExpectations = MapExpectations(source.Expect, stepNumber, $"{field}.expect");
        var laneExpectations = MapLaneExpectations(source.ExpectLane, stepNumber, $"{field}.expectLane");
        var netExpectations = MapNetExpectations(source.ExpectNet, stepNumber, $"{field}.expectNet");
        var relationExpectations = MapRelations(source.ExpectRelation, stepNumber, $"{field}.expectRelation");
        if (pinExpectations.Count == 0 && laneExpectations.Count == 0 && netExpectations.Count == 0 &&
            relationExpectations.Count == 0)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} '{field}' needs at least one 'expect', 'expectLane', 'expectNet', or 'expectRelation' entry.");
        }

        return (ticks, pinExpectations, laneExpectations, netExpectations, relationExpectations);
    }

    /// <summary>
    /// Parses a signal reference used by relation assertions. A bare name or a
    /// <c>pin:</c> prefix names an interface pin; a <c>net:</c> prefix names an
    /// authored net, including a hierarchy path such as <c>net:adder/c1</c>.
    /// </summary>
    public static bool TryParseSignalReference(
        string? reference,
        out ScriptSignalReference result,
        out string error)
    {
        result = default;
        string text = reference?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "a signal reference cannot be blank.";
            return false;
        }

        if (text.StartsWith("net:", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseNetReference(text[4..], out ScriptNetReference net, out string netError))
            {
                error = $"invalid net reference: {netError}";
                return false;
            }

            result = ScriptSignalReference.InternalNet(net);
            error = string.Empty;
            return true;
        }

        string pinId = text.StartsWith("pin:", StringComparison.OrdinalIgnoreCase)
            ? text[4..].Trim()
            : text;
        if (pinId.Length == 0)
        {
            error = "a pin reference cannot be blank.";
            return false;
        }

        result = ScriptSignalReference.Pin(pinId);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Parses an authored-net reference relative to the script's simulated definition.
    /// A bare id names a net in the root definition; <c>instance/netId</c> names a net
    /// under a hierarchy path, whose segments are instance IDs. IDs never contain '/',
    /// so the last segment is always the net id.
    /// </summary>
    public static bool TryParseNetReference(string? reference, out ScriptNetReference result, out string error)
    {
        result = default;
        string text = reference?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "a net reference cannot be blank.";
            return false;
        }

        int slash = text.LastIndexOf('/');
        string path = slash < 0 ? string.Empty : text[..slash];
        string netId = slash < 0 ? text : text[(slash + 1)..];
        if (netId.Length == 0 || !Identifiers.IsValid(netId))
        {
            error = $"net id '{netId}' is not valid. {Identifiers.Rule}";
            return false;
        }

        if (slash == 0)
        {
            error = "a net reference cannot start with '/'.";
            return false;
        }

        if (path.Length > 0)
        {
            foreach (string segment in path.Split('/'))
            {
                if (!Identifiers.IsValid(segment))
                {
                    error = $"path segment '{segment}' is not valid. {Identifiers.Rule}";
                    return false;
                }
            }
        }

        result = new ScriptNetReference(path, netId);
        error = string.Empty;
        return true;
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

        public Dictionary<string, JsonElement>? SetLane { get; set; }

        public Dictionary<string, JsonElement>? Expect { get; set; }

        public Dictionary<string, JsonElement>? ExpectLane { get; set; }

        public Dictionary<string, JsonElement>? ExpectNet { get; set; }

        public List<RelationDto?>? ExpectRelation { get; set; }

        public bool? ExpectHalted { get; set; }

        public WindowDto? Setup { get; set; }

        public WindowDto? Within { get; set; }

        public WindowDto? Stable { get; set; }

        public TransitionsDto? Transitions { get; set; }

        public EdgesDto? Edges { get; set; }

        public SequenceDto? Sequence { get; set; }
    }

    private sealed class TransitionsDto
    {
        public long? Ticks { get; set; }

        public Dictionary<string, JsonElement>? Expect { get; set; }

        public Dictionary<string, JsonElement>? ExpectNet { get; set; }
    }

    private sealed class EdgesDto
    {
        public long? Ticks { get; set; }

        public Dictionary<string, List<EdgeFilterDto>?>? Expect { get; set; }

        public Dictionary<string, List<EdgeFilterDto>?>? ExpectNet { get; set; }
    }

    private sealed class EdgeFilterDto
    {
        public JsonElement From { get; set; }

        public JsonElement To { get; set; }

        public long? Count { get; set; }
    }

    private sealed class SequenceDto
    {
        public long? Ticks { get; set; }

        public List<SequenceItemDto?>? Expect { get; set; }
    }

    private sealed class SequenceItemDto
    {
        public string? Pin { get; set; }

        public string? Net { get; set; }

        public JsonElement From { get; set; }

        public JsonElement To { get; set; }
    }

    private sealed class WindowDto
    {
        public long? Ticks { get; set; }

        public Dictionary<string, JsonElement>? Expect { get; set; }

        public Dictionary<string, JsonElement>? ExpectLane { get; set; }

        public Dictionary<string, JsonElement>? ExpectNet { get; set; }

        public List<RelationDto?>? ExpectRelation { get; set; }
    }

    private sealed class RelationDto
    {
        public string? Left { get; set; }

        public string? Right { get; set; }

        public string? Op { get; set; }
    }
}
