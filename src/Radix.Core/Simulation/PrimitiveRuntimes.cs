using Radix.Core.Compilation;
using Radix.Core.Model;

namespace Radix.Core.Simulation;

/// <summary>
/// Mutable runtime state for one leaf component. Runtimes are recreated on reset so
/// no state leaks between runs.
/// </summary>
internal abstract class PrimitiveRuntime
{
    public virtual void Initialize(SimulationEngine engine, CompiledComponent component)
    {
    }

    public abstract void Evaluate(SimulationEngine engine, CompiledComponent component);

    public virtual void OnTimer(SimulationEngine engine, CompiledComponent component, int payload)
    {
    }
}

internal sealed class ConstantRuntime(ConstantSpec spec) : PrimitiveRuntime
{
    public override void Initialize(SimulationEngine engine, CompiledComponent component) =>
        engine.ScheduleOutput(OutputPin(component), spec.Value, 0);

    public override void Evaluate(SimulationEngine engine, CompiledComponent component)
    {
    }

    private static int OutputPin(CompiledComponent component) => component.FirstPin + component.PinCount - 1;
}

internal sealed class LookupRuntime : PrimitiveRuntime
{
    private readonly long _delay;
    private readonly int[] _table;
    private readonly int[] _radices;
    private readonly int[] _strides;
    private readonly int[] _wildcardInputs;
    private readonly int[] _wildcardDigits;
    private readonly int _outputPinOffset;

    public LookupRuntime(LookupSpec spec)
    {
        _delay = spec.DelayTicks;
        _table = spec.Table.ToArray();
        _radices = new int[spec.InputCount];
        _strides = new int[spec.InputCount];
        _wildcardInputs = new int[spec.InputCount];
        _wildcardDigits = new int[spec.InputCount];
        _outputPinOffset = spec.InputCount;
    }

    public override void Initialize(SimulationEngine engine, CompiledComponent component)
    {
        for (int i = 0; i < _radices.Length; i++)
        {
            CompiledPin pin = engine.Circuit.Pins[component.FirstPin + i];
            _radices[i] = pin.Net >= 0 ? engine.Circuit.Nets[pin.Net].Domain.ValueCount : 0;
        }

        // Compiler validation guarantees that a fully connected lookup's table is
        // exactly the product of its input-domain sizes and is capped at 1,048,576
        // entries, so these strides cannot overflow for a compiled circuit.
        int stride = 1;
        for (int i = _radices.Length - 1; i >= 0; i--)
        {
            _strides[i] = stride;
            stride *= Math.Max(_radices[i], 1);
        }
    }

    public override void Evaluate(SimulationEngine engine, CompiledComponent component)
    {
        int index = 0;
        int wildcardCount = 0;
        for (int i = 0; i < _radices.Length; i++)
        {
            int radix = _radices[i];
            if (radix <= 0)
            {
                // The pin has no compiled net, so there is no domain to complete.
                // Hold the previous output, as the pre-conservative evaluator did.
                return;
            }

            int value = engine.ReadPinValue(component.FirstPin + i);
            if (value < 0)
            {
                if (radix == 1)
                {
                    // A one-valued domain has exactly one completion; its digit is 0.
                    continue;
                }

                _wildcardInputs[wildcardCount++] = i;
                index *= radix;
                continue;
            }

            if (value >= radix)
            {
                engine.ReportRuntimeWarning(
                    "RAD140",
                    $"Input value {value} (pin '{engine.Circuit.Pins[component.FirstPin + i].PinId}') exceeds domain size {radix}.",
                    component);
                return;
            }

            index = (index * radix) + value;
        }

        if (wildcardCount == 0)
        {
            if (TryReadTable(engine, component, index, out int output))
            {
                engine.ScheduleOutput(component.FirstPin + _outputPinOffset, output, _delay);
            }

            return;
        }

        if (!TryDetermineOutput(engine, component, index, wildcardCount, out int determined))
        {
            return;
        }

        engine.ScheduleOutput(component.FirstPin + _outputPinOffset, determined, _delay);
    }

    /// <summary>
    /// Conservative evaluation over undriven inputs: the output is usable only when
    /// every completion of those inputs selects the same table entry. Otherwise the
    /// lookup holds its previous output instead of fabricating a value.
    /// </summary>
    private bool TryDetermineOutput(
        SimulationEngine engine,
        CompiledComponent component,
        int baseIndex,
        int wildcardCount,
        out int output)
    {
        output = 0;
        long combinations = 1;
        for (int i = 0; i < wildcardCount; i++)
        {
            int radix = _radices[_wildcardInputs[i]];
            if (combinations > _table.Length / radix)
            {
                engine.ReportRuntimeWarning(
                    "RAD141",
                    $"Lookup has {wildcardCount} undriven inputs whose value completions exceed the {_table.Length}-entry table.",
                    component);
                return false;
            }

            combinations *= radix;
        }

        if (!TryReadTable(engine, component, baseIndex, out output))
        {
            return false;
        }

        int count = (int)combinations;
        int current = baseIndex;
        Array.Clear(_wildcardDigits, 0, wildcardCount);
        for (int combination = 1; combination < count; combination++)
        {
            for (int i = wildcardCount - 1; i >= 0; i--)
            {
                int input = _wildcardInputs[i];
                current += _strides[input];
                _wildcardDigits[i]++;
                if (_wildcardDigits[i] < _radices[input])
                {
                    break;
                }

                current -= _wildcardDigits[i] * _strides[input];
                _wildcardDigits[i] = 0;
            }

            if (!TryReadTable(engine, component, current, out int value))
            {
                return false;
            }

            if (value != output)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryReadTable(
        SimulationEngine engine,
        CompiledComponent component,
        int index,
        out int value)
    {
        if ((uint)index >= (uint)_table.Length)
        {
            engine.ReportRuntimeWarning(
                "RAD141",
                $"Lookup index {index} is outside the table ({_table.Length} entries).",
                component);
            value = 0;
            return false;
        }

        value = _table[index];
        return true;
    }
}

internal sealed class DelayRuntime(DelaySpec spec) : PrimitiveRuntime
{
    private int OutputPin(CompiledComponent component) => component.FirstPin + 1;

    public override void Initialize(SimulationEngine engine, CompiledComponent component) =>
        engine.ScheduleOutput(OutputPin(component), spec.InitialValue, 0);

    public override void Evaluate(SimulationEngine engine, CompiledComponent component)
    {
        int input = engine.ReadPinValue(component.FirstPin);
        if (input < 0)
        {
            // Hold the current output; a transport delay propagates only defined values.
            return;
        }

        engine.ScheduleOutput(OutputPin(component), input, spec.DelayTicks);
    }
}

internal sealed class WaveformRuntime : PrimitiveRuntime
{
    private readonly long[] _durations;
    private readonly int[] _values;
    private readonly int _outputPinOffset;

    public WaveformRuntime(WaveformSpec spec)
    {
        _durations = spec.Durations.ToArray();
        _values = spec.Values.ToArray();
        _outputPinOffset = spec.InputCount;
    }

    public override void Initialize(SimulationEngine engine, CompiledComponent component) =>
        engine.ScheduleTimer(component.Index, 0, 0);

    public override void Evaluate(SimulationEngine engine, CompiledComponent component)
    {
    }

    public override void OnTimer(SimulationEngine engine, CompiledComponent component, int payload)
    {
        if ((uint)payload >= (uint)_values.Length)
        {
            return;
        }

        engine.ScheduleOutput(component.FirstPin + _outputPinOffset, _values[payload], 0);
        int next = (payload + 1) % _values.Length;
        engine.ScheduleTimer(component.Index, _durations[payload], next);
    }
}

internal sealed class ProbeRuntime : PrimitiveRuntime
{
    public override void Initialize(SimulationEngine engine, CompiledComponent component)
    {
    }

    public override void Evaluate(SimulationEngine engine, CompiledComponent component)
    {
    }
}
