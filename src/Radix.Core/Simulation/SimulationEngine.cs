using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.Core.Simulation;

/// <summary>
/// Single-threaded, deterministic event-driven simulator over a
/// <see cref="CompiledCircuit"/>.
/// </summary>
/// <remarks>
/// <para>Events are ordered by simulated time and then by insertion sequence, so
/// simultaneous events are processed FIFO. The engine never consults wall-clock time.</para>
/// <para>All components are evaluated once at time zero after source primitives have
/// seeded their outputs. Nets begin undriven unless they declare an initial value.</para>
/// <para>Outputs commit after the primitive's transport delay. Zero-delay feedback
/// that fails to settle is detected and reported as non-convergence.</para>
/// </remarks>
public sealed class SimulationEngine
{
    public const string NonConvergenceCode = "RAD100";
    public const string DriverConflictCode = "RAD101";
    public const string TimeOverflowCode = "RAD142";
    public const string RuntimeWarningCode = "RAD140";

    /// <summary>
    /// Reported when the configured pending-event cap is reached. The run halts rather
    /// than letting event production outpace draining until memory is exhausted.
    /// </summary>
    public const string EventLimitCode = "RAD150";

    /// <summary>
    /// Emitted by the editor when runtime warnings were dropped after
    /// <see cref="SimulationOptions.MaxRuntimeDiagnostics"/> was reached.
    /// </summary>
    public const string SuppressedDiagnosticsCode = "RAD151";

    /// <summary>
    /// Reported when the cumulative event budget (<see cref="SimulationOptions.MaxEventsProcessed"/>)
    /// is exhausted. Unlike <see cref="EventLimitCode"/> this bounds total work, not
    /// queue occupancy, and leaves the partial simulation state inspectable.
    /// </summary>
    public const string EventBudgetCode = "RAD152";

    private readonly CompiledCircuit _circuit;
    private readonly SimulationOptions _options;
    private readonly int[] _pinValues;
    private readonly int[] _netValues;
    private readonly bool[] _pendingEvaluation;
    private readonly int[] _evaluationCounts;
    private readonly bool[] _conflictReported;
    private readonly int[] _driverScratch;
    private readonly SimEventHeap _queue = new();
    private readonly DiagnosticBag _runtimeDiagnostics = new();
    private readonly SimulationTrace _trace;
    private readonly Dictionary<int, int> _externalValues = [];
    private int _runtimeWarningCount;
    private int _suppressedRuntimeDiagnostics;

    private PrimitiveRuntime[] _runtimes = [];
    private long _now;
    private long _sequence;
    private long _evaluationTimestamp = -1;
    private bool _halted;
    private string? _haltReason;

    public SimulationEngine(CompiledCircuit circuit, SimulationOptions? options = null)
    {
        _circuit = circuit ?? throw new ArgumentNullException(nameof(circuit));
        _options = options ?? new SimulationOptions();
        _pinValues = new int[circuit.Pins.Count];
        _netValues = new int[circuit.Nets.Count];
        _pendingEvaluation = new bool[circuit.Components.Count];
        _evaluationCounts = new int[circuit.Components.Count];
        _conflictReported = new bool[circuit.Nets.Count];

        int maxDrivers = circuit.Nets.Count == 0 ? 1 : circuit.Nets.Max(n => n.DriverPins.Length);
        _driverScratch = new int[Math.Max(1, maxDrivers + 1)];

        _trace = new SimulationTrace(_options.RecordTrace ? _options.MaxRecordedChanges : 0);
        Reset();
    }

    public CompiledCircuit Circuit => _circuit;

    public SimulationOptions Options => _options;

    /// <summary>Current simulated time.</summary>
    public long Now => _now;

    public bool IsHalted => _halted;

    public string? HaltReason => _haltReason;

    public bool HasPendingEvents => _queue.Count > 0;

    public int PendingEventCount => _queue.Count;

    public long EventsProcessed { get; private set; }

    public long ComponentEvaluations { get; private set; }

    public long NetTransitions { get; private set; }

    /// <summary>Bounded trace of all recorded net transitions since reset.</summary>
    public SimulationTrace Trace => _trace;

    /// <summary>
    /// Raised synchronously after a net's resolved value changes. Observers run inside
    /// the event loop, so they must be fast and must not mutate the engine.
    /// </summary>
    public event Action<NetChange>? NetChanged;

    /// <summary>Runtime warnings/errors (driver conflicts, non-convergence).</summary>
    public IReadOnlyList<Diagnostic> RuntimeDiagnostics => _runtimeDiagnostics;

    /// <summary>Runtime warnings dropped after the configured diagnostic cap.</summary>
    public int SuppressedRuntimeDiagnostics => _suppressedRuntimeDiagnostics;

    /// <summary>Restores time zero, all values, traces, and runtime state.</summary>
    public void Reset()
    {
        _now = 0;
        _sequence = 0;
        _evaluationTimestamp = -1;
        _halted = false;
        _haltReason = null;
        EventsProcessed = 0;
        ComponentEvaluations = 0;
        NetTransitions = 0;
        _runtimeWarningCount = 0;
        _suppressedRuntimeDiagnostics = 0;

        _queue.Clear();
        _runtimeDiagnostics.Clear();
        _trace.Clear();
        _externalValues.Clear();
        Array.Fill(_pinValues, SignalValue.UndrivenIndex);
        Array.Fill(_pendingEvaluation, false);
        Array.Fill(_evaluationCounts, 0);
        Array.Fill(_conflictReported, false);

        for (int i = 0; i < _circuit.Nets.Count; i++)
        {
            CompiledNet net = _circuit.Nets[i];
            _netValues[i] = net.InitialValue;
            if (net.InitialValue >= 0 && _options.RecordTrace)
            {
                _trace.Add(new NetChange(0, i, SignalValue.UndrivenIndex, net.InitialValue));
            }
        }

        _runtimes = new PrimitiveRuntime[_circuit.Components.Count];
        for (int i = 0; i < _circuit.Components.Count; i++)
        {
            CompiledComponent component = _circuit.Components[i];
            PrimitiveRuntime runtime = CreateRuntime(component.Spec);
            _runtimes[i] = runtime;
            runtime.Initialize(this, component);
        }

        for (int i = 0; i < _circuit.Components.Count; i++)
        {
            ScheduleEvaluation(i);
        }
    }

    /// <summary>Processes exactly one scheduled event. Returns false when nothing remains.</summary>
    public bool Step()
    {
        if (_halted || !_queue.TryPeek(out _))
        {
            return false;
        }

        ProcessNextEvent();
        return true;
    }

    /// <summary>Processes every event at the earliest pending timestamp.</summary>
    /// <returns>Number of events processed.</returns>
    public int StepTimestamp()
    {
        if (_halted || !_queue.TryPeek(out SimEvent first))
        {
            return 0;
        }

        long timestamp = first.Time;
        int processed = 0;
        while (!_halted && _queue.TryPeek(out SimEvent next) && next.Time == timestamp)
        {
            ProcessNextEvent();
            processed++;
        }

        return processed;
    }

    /// <summary>
    /// Processes all events with time less than or equal to <paramref name="targetTime"/>,
    /// then moves the clock to the target if the engine did not halt. A caller running
    /// offline analysis can pass a token to stop a long advance without touching a
    /// live editor simulation.
    /// </summary>
    public void AdvanceTo(long targetTime, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetTime, _now);
        cancellationToken.ThrowIfCancellationRequested();
        if (_halted)
        {
            return;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            // The ordinary editor and CLI path keeps the original tight loop.
            while (!_halted && _queue.TryPeek(out SimEvent next) && next.Time <= targetTime)
            {
                ProcessNextEvent();
            }

            if (!_halted)
            {
                _now = targetTime;
            }

            return;
        }

        int processed = 0;
        while (!_halted && _queue.TryPeek(out SimEvent next) && next.Time <= targetTime)
        {
            if ((++processed & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ProcessNextEvent();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_halted)
        {
            _now = targetTime;
        }
    }

    public void AdvanceBy(long ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        AdvanceTo(checked(_now + ticks));
    }

    /// <summary>
    /// Processes events until the queue is empty or the optional time limit is reached.
    /// Unlike <see cref="AdvanceTo"/> the clock stops at the last processed event.
    /// </summary>
    public void RunUntilIdle(long maxTime = long.MaxValue)
    {
        if (_halted)
        {
            return;
        }

        while (!_halted && _queue.TryPeek(out SimEvent next) && next.Time <= maxTime)
        {
            ProcessNextEvent();
        }
    }

    private void ProcessNextEvent()
    {
        if (_options.MaxEventsProcessed > 0 && EventsProcessed >= _options.MaxEventsProcessed)
        {
            Halt(
                EventBudgetCode,
                $"The event budget of {_options.MaxEventsProcessed} was exhausted after {EventsProcessed} events at time {_now}; increase the budget or set it to 0 to disable it.");
            return;
        }

        SimEvent ev = _queue.Pop();
        _now = ev.Time;
        EventsProcessed++;

        if (ev.Time != _evaluationTimestamp)
        {
            Array.Clear(_evaluationCounts);
            _evaluationTimestamp = ev.Time;
        }

        CompiledComponent? component = null;
        if (ev.Kind is SimEventKind.WakeComponent or SimEventKind.PrimitiveTimer)
        {
            component = _circuit.Components[ev.Target];
        }

        switch (ev.Kind)
        {
            case SimEventKind.WakeComponent:
                if (!_pendingEvaluation[ev.Target])
                {
                    return;
                }

                _pendingEvaluation[ev.Target] = false;
                if (++_evaluationCounts[ev.Target] > _options.MaxEvaluationsPerComponentPerTimestamp)
                {
                    Halt(
                        NonConvergenceCode,
                        $"Component '{component!.InstanceId}' evaluated more than {_options.MaxEvaluationsPerComponentPerTimestamp} times at time {_now}; zero-delay feedback did not converge.");
                    return;
                }

                ComponentEvaluations++;
                _runtimes[ev.Target].Evaluate(this, component!);
                break;

            case SimEventKind.CommitOutput:
                CommitOutput(ev.Target, ev.Value);
                break;

            case SimEventKind.PrimitiveTimer:
                _runtimes[ev.Target].OnTimer(this, component!, ev.Value);
                break;
        }

        if (_queue.Count == 0)
        {
            // An empty queue means the current timestamp has settled. Starting the
            // convergence counters afresh keeps a later stimulus at the same simulated
            // time from inheriting the previous settle's evaluation counts and being
            // misreported as zero-delay feedback that never converged.
            _evaluationTimestamp = -1;
        }
    }

    private void CommitOutput(int pinIndex, int value)
    {
        if (_pinValues[pinIndex] == value)
        {
            return;
        }

        _pinValues[pinIndex] = value;
        CompiledPin pin = _circuit.Pins[pinIndex];
        if (pin.Net < 0)
        {
            return;
        }

        ResolveNet(pin.Net);
    }

    private void ResolveNet(int netIndex)
    {
        CompiledNet net = _circuit.Nets[netIndex];
        int count = 0;
        foreach (int driverPin in net.DriverPins)
        {
            int value = _pinValues[driverPin];
            if (value >= 0)
            {
                _driverScratch[count++] = value;
            }
        }

        if (_externalValues.TryGetValue(netIndex, out int external))
        {
            _driverScratch[count++] = external;
        }

        DriverResolution resolution = net.Domain.Drivers.Resolve(net.Domain, _driverScratch.AsSpan(0, count));
        if (resolution.Conflict && !_conflictReported[netIndex])
        {
            _conflictReported[netIndex] = true;
            AddRuntimeWarning(
                DriverConflictCode,
                $"Multiple active drivers on net '{net.RepresentativeNetId}' violate the '{net.Domain.Drivers.Kind}' driver policy for domain '{net.Domain.Id}'.",
                net.RepresentativeDefinitionId,
                instanceId: null,
                netId: net.RepresentativeNetId,
                path: net.RepresentativePath);
        }

        int oldValue = _netValues[netIndex];
        if (oldValue == resolution.Value)
        {
            return;
        }

        _netValues[netIndex] = resolution.Value;
        NetTransitions++;
        if (_options.RecordTrace)
        {
            _trace.Add(new NetChange(_now, netIndex, oldValue, resolution.Value));
        }

        NetChanged?.Invoke(new NetChange(_now, netIndex, oldValue, resolution.Value));

        foreach (int componentIndex in net.SinkComponents)
        {
            ScheduleEvaluation(componentIndex);
        }
    }

    private void ScheduleEvaluation(int componentIndex)
    {
        if (_pendingEvaluation[componentIndex] || _halted)
        {
            return;
        }

        if (!TryEnqueue(new SimEvent(_now, _sequence++, SimEventKind.WakeComponent, componentIndex, 0)))
        {
            return;
        }

        _pendingEvaluation[componentIndex] = true;
    }

    internal int ReadPinValue(int pinIndex)
    {
        int net = _circuit.Pins[pinIndex].Net;
        return net < 0 ? SignalValue.UndrivenIndex : _netValues[net];
    }

    internal void ScheduleOutput(int pinIndex, int value, long delayTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delayTicks);
        if (!TryScheduleTime(delayTicks, out long eventTime))
        {
            return;
        }

        TryEnqueue(new SimEvent(eventTime, _sequence++, SimEventKind.CommitOutput, pinIndex, value));
    }

    internal void ScheduleTimer(int componentIndex, long delayTicks, int payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delayTicks);
        if (!TryScheduleTime(delayTicks, out long eventTime))
        {
            return;
        }

        TryEnqueue(new SimEvent(eventTime, _sequence++, SimEventKind.PrimitiveTimer, componentIndex, payload));
    }

    private bool TryScheduleTime(long delayTicks, out long eventTime)
    {
        try
        {
            eventTime = checked(_now + delayTicks);
            return true;
        }
        catch (OverflowException)
        {
            eventTime = 0;
            _halted = true;
            _haltReason = $"Scheduling an event {delayTicks} ticks after time {_now} exceeds the supported simulation-time range.";
            _queue.Clear();
            _runtimeDiagnostics.Error(TimeOverflowCode, $"{_haltReason} Simulation halted at time {_now}.");
            return false;
        }
    }

    internal void ReportRuntimeWarning(string code, string message, CompiledComponent component)
    {
        AddRuntimeWarning(
            code,
            message,
            component.DefinitionId,
            component.InstanceId,
            netId: null,
            path: component.Path);
    }

    /// <summary>
    /// Retains a runtime warning unless the configured cap is reached. Suppressed
    /// warnings are counted so callers can report how many were dropped.
    /// </summary>
    private void AddRuntimeWarning(
        string code,
        string message,
        string? definitionId,
        string? instanceId,
        string? netId,
        string? path)
    {
        if (_options.MaxRuntimeDiagnostics > 0 && _runtimeWarningCount >= _options.MaxRuntimeDiagnostics)
        {
            _suppressedRuntimeDiagnostics++;
            return;
        }

        _runtimeWarningCount++;
        _runtimeDiagnostics.Warning(code, message, definitionId, instanceId, netId, path);
    }

    /// <summary>
    /// Enqueues one event unless the configured pending-event cap is reached. A circuit
    /// that keeps producing events turns into a diagnostic instead of unbounded growth.
    /// </summary>
    private bool TryEnqueue(SimEvent simEvent)
    {
        if (_halted)
        {
            return false;
        }

        if (_options.MaxPendingEvents > 0 && _queue.Count >= _options.MaxPendingEvents)
        {
            Halt(
                EventLimitCode,
                $"Pending event limit of {_options.MaxPendingEvents} was reached; the circuit is scheduling events faster than they can be processed.");
            return false;
        }

        _queue.Push(simEvent);
        return true;
    }

    private void Halt(string code, string reason)
    {
        _halted = true;
        _haltReason = reason;
        _queue.Clear();
        _runtimeDiagnostics.Error(
            code,
            $"{reason} Simulation halted at time {_now}.");
    }

    /// <summary>
    /// Drives a net from outside the circuit (switches, test harnesses, interactive
    /// input). One external value is allowed per net; -1 clears it. The net re-resolves
    /// immediately and downstream components are scheduled at the current time.
    /// </summary>
    public void SetExternalInput(int netIndex, int value)
    {
        if ((uint)netIndex >= (uint)_circuit.Nets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(netIndex));
        }

        CompiledNet net = _circuit.Nets[netIndex];
        if (value == SignalValue.UndrivenIndex)
        {
            _externalValues.Remove(netIndex);
        }
        else
        {
            if (!net.Domain.IsValidValue(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} is outside domain '{net.Domain.Id}'.");
            }

            _externalValues[netIndex] = value;
        }

        ResolveNet(netIndex);
    }

    public int? GetExternalInput(int netIndex)
    {
        if ((uint)netIndex >= (uint)_circuit.Nets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(netIndex));
        }

        return _externalValues.TryGetValue(netIndex, out int value) ? value : null;
    }

    /// <summary>Sets an input port's external value. Output ports cannot be driven.</summary>
    public void SetPortValue(int portIndex, int value)
    {
        if ((uint)portIndex >= (uint)_circuit.Ports.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(portIndex));
        }

        CompiledPort port = _circuit.Ports[portIndex];
        if (port.Direction != PinDirection.Input)
        {
            throw new InvalidOperationException($"Port '{port.PinId}' is an output and cannot be driven externally.");
        }

        SetExternalInput(port.Net, value);
    }

    public int FindPortIndex(string pinId)
    {
        for (int i = 0; i < _circuit.Ports.Count; i++)
        {
            if (string.Equals(_circuit.Ports[i].PinId, pinId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public int GetPortValue(int portIndex)
    {
        if ((uint)portIndex >= (uint)_circuit.Ports.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(portIndex));
        }

        return GetNetValue(_circuit.Ports[portIndex].Net);
    }

    public int GetNetValue(int netIndex)
    {
        if ((uint)netIndex >= (uint)_circuit.Nets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(netIndex));
        }

        return _netValues[netIndex];
    }

    public int GetPinValue(int pinIndex)
    {
        if ((uint)pinIndex >= (uint)_circuit.Pins.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pinIndex));
        }

        return _pinValues[pinIndex];
    }

    private static PrimitiveRuntime CreateRuntime(PrimitiveSpec spec) => spec switch
    {
        ConstantSpec constant => new ConstantRuntime(constant),
        LookupSpec lookup => new LookupRuntime(lookup),
        DelaySpec delay => new DelayRuntime(delay),
        WaveformSpec waveform => new WaveformRuntime(waveform),
        ProbeSpec => new ProbeRuntime(),
        _ => throw new NotSupportedException($"Primitive kind '{spec.Kind}' is not supported by this build."),
    };
}
