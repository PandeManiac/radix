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
/// A held-value assertion: the expected values must match at every tick from the
/// step's tick through <c>At + Ticks</c> inclusive. Used to prove that a value is
/// stable, not merely eventually reached.
/// </summary>
public sealed class ScriptStability
{
    public ScriptStability(
        long ticks,
        IReadOnlyList<ScriptPinExpectation> pinExpectations,
        IReadOnlyList<ScriptLaneExpectation> laneExpectations)
    {
        Ticks = ticks;
        PinExpectations = pinExpectations;
        LaneExpectations = laneExpectations;
    }

    /// <summary>Number of ticks after the step's tick that the values must hold, inclusive.</summary>
    public long Ticks { get; }

    public IReadOnlyList<ScriptPinExpectation> PinExpectations { get; }

    public IReadOnlyList<ScriptLaneExpectation> LaneExpectations { get; }
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
        IReadOnlyList<ScriptLaneExpectation> laneExpectations)
    {
        Ticks = ticks;
        PinExpectations = pinExpectations;
        LaneExpectations = laneExpectations;
    }

    /// <summary>Number of ticks before the step that the values must hold.</summary>
    public long Ticks { get; }

    public IReadOnlyList<ScriptPinExpectation> PinExpectations { get; }

    public IReadOnlyList<ScriptLaneExpectation> LaneExpectations { get; }
}

/// <summary>Expected number of value changes on one interface pin over an interval.</summary>
public sealed record ScriptTransitionExpectation(string PinId, long Count);

/// <summary>
/// A transition-count assertion: every observed interface pin must change value
/// exactly <see cref="ScriptTransitionExpectation.Count"/> times between the step's
/// tick and <c>At + Ticks</c>, inclusive, after this step's drives are applied.
/// </summary>
public sealed class ScriptTransitionWindow
{
    public ScriptTransitionWindow(
        long ticks,
        IReadOnlyList<ScriptTransitionExpectation> pinExpectations)
    {
        Ticks = ticks;
        PinExpectations = pinExpectations;
    }

    public long Ticks { get; }

    public IReadOnlyList<ScriptTransitionExpectation> PinExpectations { get; }
}

/// <summary>
/// One filtered edge count. A missing <see cref="From"/> or <see cref="To"/> matches
/// any value on that side, so a filter can count rising edges (0 to 1), falling
/// edges (1 to 0), or every change into a chosen value.
/// </summary>
public sealed record ScriptEdgeFilter(ScriptValue? From, ScriptValue? To, long Count);

/// <summary>Filtered edge expectations for one interface pin.</summary>
public sealed record ScriptEdgeExpectation(string PinId, IReadOnlyList<ScriptEdgeFilter> Filters);

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
        IReadOnlyList<ScriptEdgeExpectation> pinExpectations)
    {
        Ticks = ticks;
        PinExpectations = pinExpectations;
    }

    public long Ticks { get; }

    public IReadOnlyList<ScriptEdgeExpectation> PinExpectations { get; }
}

/// <summary>One ordered edge in a sequence assertion.</summary>
public sealed record ScriptEdgeSequenceItem(
    string PinId,
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
        IReadOnlyList<ScriptPinExpectation> pinExpectations,
        IReadOnlyList<ScriptLaneExpectation> laneExpectations,
        bool? expectHalted,
        string? description,
        ScriptWindow? window = null,
        ScriptStability? stability = null,
        ScriptTransitionWindow? transitions = null,
        ScriptEdgeWindow? edges = null,
        ScriptEdgeSequenceWindow? sequence = null,
        ScriptSetup? setup = null)
    {
        At = at;
        Drives = drives;
        PinExpectations = pinExpectations;
        LaneExpectations = laneExpectations;
        ExpectHalted = expectHalted;
        Description = description;
        Window = window;
        Stability = stability;
        Transitions = transitions;
        Edges = edges;
        Sequence = sequence;
        Setup = setup;
    }

    /// <summary>Absolute simulated tick. Missing values default to the previous step's tick.</summary>
    public long At { get; }

    public IReadOnlyList<ScriptDrive> Drives { get; }

    public IReadOnlyList<ScriptPinExpectation> PinExpectations { get; }

    public IReadOnlyList<ScriptLaneExpectation> LaneExpectations { get; }

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
        Drives.Count > 0 || PinExpectations.Count > 0 || LaneExpectations.Count > 0 ||
        ExpectHalted is not null || Window is not null || Stability is not null ||
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
            var pinExpectations = MapExpectations(step.Expect, stepNumber, "expect");
            var laneExpectations = MapLaneExpectations(step.ExpectLane, stepNumber, "expectLane");
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
                pinExpectations,
                laneExpectations,
                step.ExpectHalted,
                step.Description,
                window,
                stability,
                transitions,
                edges,
                sequence,
                setup));
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

    private static ScriptSetup? MapSetup(WindowDto? source, int stepNumber, long at)
    {
        if (source is null)
        {
            return null;
        }

        (long ticks, List<ScriptPinExpectation> pins, List<ScriptLaneExpectation> lanes) =
            MapWindowContents(source, stepNumber, at, "setup");
        if (ticks > at)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'setup.ticks' ({ticks}) cannot start before t=0 for a step at t={at}.");
        }

        return new ScriptSetup(ticks, pins, lanes);
    }

    private static ScriptWindow? MapWindow(WindowDto? source, int stepNumber, long at)
    {
        if (source is null)
        {
            return null;
        }

        (long ticks, List<ScriptPinExpectation> pins, List<ScriptLaneExpectation> lanes) =
            MapWindowContents(source, stepNumber, at, "within");
        return new ScriptWindow(ticks, pins, lanes);
    }

    private static ScriptStability? MapStability(WindowDto? source, int stepNumber, long at)
    {
        if (source is null)
        {
            return null;
        }

        (long ticks, List<ScriptPinExpectation> pins, List<ScriptLaneExpectation> lanes) =
            MapWindowContents(source, stepNumber, at, "stable");
        return new ScriptStability(ticks, pins, lanes);
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

        if (source.Expect is null || source.Expect.Count == 0)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'transitions' needs at least one 'expect' entry.");
        }

        var expectations = new List<ScriptTransitionExpectation>(source.Expect.Count);
        foreach ((string pinId, JsonElement element) in source.Expect)
        {
            if (element.ValueKind != JsonValueKind.Number ||
                !element.TryGetInt64(out long count) ||
                count < 0)
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} 'transitions.expect.{pinId}' must be a non-negative integer count.");
            }

            expectations.Add(new ScriptTransitionExpectation(
                NormalizeId(pinId, "pin", stepNumber, "transitions.expect"),
                count));
        }

        return new ScriptTransitionWindow(ticks, expectations);
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

        if (source.Expect is null || source.Expect.Count == 0)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} 'edges' needs at least one 'expect' entry.");
        }

        var expectations = new List<ScriptEdgeExpectation>(source.Expect.Count);
        foreach ((string pinId, List<EdgeFilterDto>? filters) in source.Expect)
        {
            string normalizedPin = NormalizeId(pinId, "pin", stepNumber, "edges.expect");
            if (filters is null || filters.Count == 0)
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} 'edges.expect.{pinId}' needs at least one edge filter.");
            }

            var mapped = new List<ScriptEdgeFilter>(filters.Count);
            foreach (EdgeFilterDto filter in filters)
            {
                if (filter is null)
                {
                    throw new SimulationScriptException(
                        $"Step {stepNumber} 'edges.expect.{pinId}' contains a null filter.");
                }

                bool hasFrom = filter.From.ValueKind != JsonValueKind.Undefined;
                bool hasTo = filter.To.ValueKind != JsonValueKind.Undefined;
                if (!hasFrom && !hasTo)
                {
                    throw new SimulationScriptException(
                        $"Step {stepNumber} 'edges.expect.{pinId}' filter needs 'from', 'to', or both.");
                }

                if (filter.Count is null || filter.Count < 0)
                {
                    throw new SimulationScriptException(
                        $"Step {stepNumber} 'edges.expect.{pinId}' filter needs a non-negative 'count'.");
                }

                ScriptValue? from = hasFrom
                    ? ReadValue(filter.From, stepNumber, $"edges.expect.{pinId}.from")
                    : null;
                ScriptValue? to = hasTo
                    ? ReadValue(filter.To, stepNumber, $"edges.expect.{pinId}.to")
                    : null;
                mapped.Add(new ScriptEdgeFilter(from, to, filter.Count.Value));
            }

            expectations.Add(new ScriptEdgeExpectation(normalizedPin, mapped));
        }

        return new ScriptEdgeWindow(ticks, expectations);
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
            if (string.IsNullOrWhiteSpace(item.Pin))
            {
                throw new SimulationScriptException(
                    $"Step {stepNumber} '{field}' needs a non-blank 'pin'.");
            }

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
                item.Pin.Trim(),
                from,
                to));
        }

        return new ScriptEdgeSequenceWindow(ticks, items);
    }

    private static (long Ticks, List<ScriptPinExpectation> Pins, List<ScriptLaneExpectation> Lanes)
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
        if (pinExpectations.Count == 0 && laneExpectations.Count == 0)
        {
            throw new SimulationScriptException(
                $"Step {stepNumber} '{field}' needs at least one 'expect' or 'expectLane' entry.");
        }

        return (ticks, pinExpectations, laneExpectations);
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
    }

    private sealed class EdgesDto
    {
        public long? Ticks { get; set; }

        public Dictionary<string, List<EdgeFilterDto>?>? Expect { get; set; }
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

        public JsonElement From { get; set; }

        public JsonElement To { get; set; }
    }

    private sealed class WindowDto
    {
        public long? Ticks { get; set; }

        public Dictionary<string, JsonElement>? Expect { get; set; }

        public Dictionary<string, JsonElement>? ExpectLane { get; set; }
    }
}
