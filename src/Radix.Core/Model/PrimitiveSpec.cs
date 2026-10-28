using System.Globalization;

namespace Radix.Core.Model;

/// <summary>
/// Parameters and pin shape of a leaf component. Primitives are the only built-in
/// behaviours; everything above them is constructed in projects. All primitives are
/// domain-agnostic and are validated against the domains of their connected nets.
/// </summary>
public abstract record PrimitiveSpec
{
    /// <summary>Stable kind identifier used by the project format.</summary>
    public abstract string Kind { get; }

    /// <summary>Number of input pins.</summary>
    public abstract int InputCount { get; }

    /// <summary>Number of output pins (currently zero or one for every primitive).</summary>
    public abstract int OutputCount { get; }

    /// <summary>Short description for inspectors and tooltips.</summary>
    public abstract string Summary { get; }
}

/// <summary>Drives a constant value onto one output. The value is a domain index.</summary>
public sealed record ConstantSpec : PrimitiveSpec
{
    public ConstantSpec(int value) => Value = value;

    public int Value { get; }

    public override string Kind => "constant";

    public override int InputCount => 0;

    public override int OutputCount => 1;

    public override string Summary => $"constant {Value.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Combinational truth table. Inputs are indexed in declaration order, first input
/// most significant, using each connected net's domain size as the radix.
/// </summary>
public sealed record LookupSpec : PrimitiveSpec
{
    public LookupSpec(int inputCount, IEnumerable<int> table, long delayTicks = 0)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (inputCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputCount), "Input count must be non-negative.");
        }

        if (delayTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delayTicks), "Delay must be non-negative.");
        }

        InputCount = inputCount;
        Table = table.ToArray();
        DelayTicks = delayTicks;
    }

    public override string Kind => "lookup";

    public override int InputCount { get; }

    public override int OutputCount => 1;

    /// <summary>Flat truth table, mixed-radix indexed by input pin order.</summary>
    public IReadOnlyList<int> Table { get; }

    /// <summary>Transport delay from an input change to an output change.</summary>
    public long DelayTicks { get; }

    public override string Summary =>
        $"lookup {InputCount}→1, {Table.Count} entries, delay {DelayTicks.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Single-input transport delay with an explicit initial output. Useful for
/// registers, start-up state, and deliberate delay lines.
/// </summary>
public sealed record DelaySpec : PrimitiveSpec
{
    public DelaySpec(long delayTicks, int initialValue = 0)
    {
        if (delayTicks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(delayTicks), "A delay component needs at least one tick.");
        }

        if (initialValue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialValue), "Initial value must be a non-negative index.");
        }

        DelayTicks = delayTicks;
        InitialValue = initialValue;
    }

    public override string Kind => "delay";

    public override int InputCount => 1;

    public override int OutputCount => 1;

    public long DelayTicks { get; }

    public int InitialValue { get; }

    public override string Summary =>
        $"delay {DelayTicks.ToString(CultureInfo.InvariantCulture)}, init {InitialValue.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Cyclic multi-valued waveform source. Phase <c>i</c> holds <c>Values[i]</c> for
/// <c>Durations[i]</c> ticks; after the final phase it restarts at phase 0.
/// </summary>
public sealed record WaveformSpec : PrimitiveSpec
{
    public WaveformSpec(IEnumerable<long> durations, IEnumerable<int> values)
    {
        ArgumentNullException.ThrowIfNull(durations);
        ArgumentNullException.ThrowIfNull(values);
        Durations = durations.ToArray();
        Values = values.ToArray();

        if (Durations.Count == 0 || Durations.Count != Values.Count)
        {
            throw new ArgumentException("Waveform durations and values must have the same non-zero length.");
        }

        if (Durations.Any(d => d < 1))
        {
            throw new ArgumentException("Every waveform phase must last at least one tick.");
        }

        if (Values.Any(v => v < 0))
        {
            throw new ArgumentException("Waveform values must be non-negative domain indices.");
        }
    }

    public override string Kind => "waveform";

    public override int InputCount => 0;

    public override int OutputCount => 1;

    public IReadOnlyList<long> Durations { get; }

    public IReadOnlyList<int> Values { get; }

    public override string Summary =>
        $"waveform {Values.Count} phases, period {Durations.Sum().ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Named observation point. Has no effect on circuit semantics.</summary>
public sealed record ProbeSpec : PrimitiveSpec
{
    public ProbeSpec(string label = "") => Label = label;

    public override string Kind => "probe";

    public override int InputCount => 1;

    public override int OutputCount => 0;

    public string Label { get; }

    public override string Summary => Label.Length == 0 ? "probe" : $"probe '{Label}'";
}

/// <summary>Pin shape helpers for primitives, shared by the editor, validator, and compiler.</summary>
public static class PrimitiveShapes
{
    public const string OutputPinId = "out";

    public const int MaxInputs = 16;

    public static string InputPinId(int index) => $"in{index.ToString(CultureInfo.InvariantCulture)}";

    public static IReadOnlyList<PinDescriptor> GetPins(PrimitiveSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var pins = new List<PinDescriptor>(spec.InputCount + spec.OutputCount);
        for (int i = 0; i < spec.InputCount; i++)
        {
            pins.Add(new PinDescriptor(InputPinId(i), $"In{i.ToString(CultureInfo.InvariantCulture)}", Signals.PinDirection.Input, null));
        }

        if (spec.OutputCount > 0)
        {
            pins.Add(new PinDescriptor(OutputPinId, "Out", Signals.PinDirection.Output, null));
        }

        return pins;
    }

    public static bool IsInputPin(PrimitiveSpec spec, string pinId)
    {
        for (int i = 0; i < spec.InputCount; i++)
        {
            if (InputPinId(i) == pinId)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsOutputPin(PrimitiveSpec spec, string pinId) =>
        spec.OutputCount > 0 && pinId == OutputPinId;
}
