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
    private readonly int _outputPinOffset;

    public LookupRuntime(LookupSpec spec)
    {
        _delay = spec.DelayTicks;
        _table = spec.Table.ToArray();
        _radices = new int[spec.InputCount];
        _outputPinOffset = spec.InputCount;
    }

    public override void Initialize(SimulationEngine engine, CompiledComponent component)
    {
        for (int i = 0; i < _radices.Length; i++)
        {
            CompiledPin pin = engine.Circuit.Pins[component.FirstPin + i];
            _radices[i] = pin.Net >= 0 ? engine.Circuit.Nets[pin.Net].Domain.ValueCount : 0;
        }
    }

    public override void Evaluate(SimulationEngine engine, CompiledComponent component)
    {
        int index = 0;
        for (int i = 0; i < _radices.Length; i++)
        {
            int value = engine.ReadPinValue(component.FirstPin + i);
            if (value < 0)
            {
                // Undriven is not a logical value. Hold the previous output until the
                // input becomes defined; this also preserves explicit net seed values
                // during start-up propagation.
                return;
            }

            int radix = _radices[i];
            if (radix <= 1)
            {
                // A one-valued input contributes nothing to the index; a zero-valued
                // (unconnected) input was already handled by the undriven check.
                index *= Math.Max(radix, 1);
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

        if ((uint)index >= (uint)_table.Length)
        {
            engine.ReportRuntimeWarning(
                "RAD141",
                $"Lookup index {index} is outside the table ({_table.Length} entries).",
                component);
            return;
        }

        engine.ScheduleOutput(component.FirstPin + _outputPinOffset, _table[index], _delay);
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
