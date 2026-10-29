using System.Globalization;
using System.Numerics;
using Radix.Core.Compilation;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;

namespace Radix.Core.Testing;

/// <summary>A single failed script assertion or runtime error.</summary>
public sealed record ScriptFailure(int Step, long At, string Message);

/// <summary>Outcome of one scripted simulation run.</summary>
public sealed class SimulationScriptResult
{
    public SimulationScriptResult(
        string scriptName,
        string definitionId,
        IReadOnlyList<ScriptFailure> failures,
        IReadOnlyList<Diagnostic> diagnostics,
        int stepsExecuted,
        long finalTime,
        long eventsProcessed,
        bool halted,
        string? haltReason)
    {
        ScriptName = scriptName;
        DefinitionId = definitionId;
        Failures = failures;
        Diagnostics = diagnostics;
        StepsExecuted = stepsExecuted;
        FinalTime = finalTime;
        EventsProcessed = eventsProcessed;
        Halted = halted;
        HaltReason = haltReason;
    }

    public string ScriptName { get; }

    public string DefinitionId { get; }

    public IReadOnlyList<ScriptFailure> Failures { get; }

    /// <summary>Compile warnings and runtime diagnostics. Not every entry is a failure.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public int StepsExecuted { get; }

    public long FinalTime { get; }

    public long EventsProcessed { get; }

    public bool Halted { get; }

    public string? HaltReason { get; }

    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// Executes <see cref="SimulationScript"/> files against a project. The runner only
/// uses the public compiler/engine API, so a passing script proves the same behavior
/// the editor and CLI exercise; it does not bypass the kernel with a reference model.
/// </summary>
public static class SimulationScriptRunner
{
    public static SimulationScriptResult Run(
        CircuitProject project,
        SimulationScript script,
        string? definitionOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(script);
        cancellationToken.ThrowIfCancellationRequested();

        string definitionId = definitionOverride ?? script.DefinitionId ?? project.RootDefinitionId;
        if (project.FindDefinition(definitionId) is null)
        {
            throw new SimulationScriptException(
                $"Test script targets unknown definition '{definitionId}'.");
        }

        CompiledCircuit circuit = CircuitCompiler.Compile(project, definitionId);
        var options = new SimulationOptions
        {
            MaxEvaluationsPerComponentPerTimestamp =
                Math.Max(1, project.Settings.MaxEvaluationsPerComponentPerTimestamp),

            // Scripts assert values, not waveforms. Skipping trace storage keeps long
            // sweeps cheap and avoids a hidden dependence on the trace ring capacity.
            MaxRecordedChanges = 0,
            RecordTrace = false,
        };
        var engine = new SimulationEngine(circuit, options);
        CircuitDefinition definition = project.RequireDefinition(definitionId);

        var transitionCounts = new Dictionary<int, long>();
        var edgeCounts = new Dictionary<(int Net, int OldValue, int NewValue), long>();
        var sequenceChanges = new List<NetChange>();
        bool observesTransitions = script.Steps.Any(step => step.Transitions is not null);
        bool observesEdges = script.Steps.Any(step => step.Edges is not null);
        bool observesSequence = script.Steps.Any(step => step.Sequence is not null);
        bool observesChanges = observesTransitions || observesEdges || observesSequence;
        if (observesChanges)
        {
            engine.NetChanged += change =>
            {
                if (observesTransitions)
                {
                    transitionCounts.TryGetValue(change.Net, out long count);
                    transitionCounts[change.Net] = count + 1;
                }

                if (observesEdges)
                {
                    var key = (change.Net, change.OldValue, change.NewValue);
                    edgeCounts.TryGetValue(key, out long count);
                    edgeCounts[key] = count + 1;
                }

                if (observesSequence)
                {
                    sequenceChanges.Add(change);
                }
            };
        }

        var failures = new List<ScriptFailure>();
        int stepsExecuted = 0;
        for (int i = 0; i < script.Steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SimulationScriptStep step = script.Steps[i];
            int stepNumber = i + 1;
            if (step.At < engine.Now)
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    step.At,
                    $"step time {step.At} is before the current simulation time {engine.Now}."));
                break;
            }

            if (step.Setup is { } setup)
            {
                CheckSetup(project, definition, engine, step, setup, stepNumber, failures, cancellationToken);
            }

            engine.AdvanceTo(step.At, cancellationToken);
            if (observesChanges)
            {
                // The interval starts at this step, including changes caused by its
                // drives at this tick but excluding earlier activity.
                transitionCounts.Clear();
                edgeCounts.Clear();
                sequenceChanges.Clear();
            }

            if (!engine.IsHalted)
            {
                foreach (ScriptDrive drive in step.Drives)
                {
                    ApplyDrive(engine, stepNumber, step.At, drive, failures);
                }

                foreach (ScriptLaneDrive drive in step.LaneDrives)
                {
                    ApplyLaneDrive(project, definition, engine, stepNumber, step.At, drive, failures);
                }

                // Settle zero-delay consequences of this step's drives at the same tick.
                // AdvanceTo is deliberate here: StepTimestamp would run the earliest
                // pending event even when it belongs to a later tick, which would make
                // the reported final time depend on the circuit's future schedule.
                engine.AdvanceTo(step.At, cancellationToken);
            }

            if (engine.IsHalted && step.ExpectHalted != true)
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    step.At,
                    $"simulation halted unexpectedly: {engine.HaltReason}"));
                stepsExecuted++;
                break;
            }

            foreach (ScriptPinExpectation expectation in step.PinExpectations)
            {
                if (CheckPin(engine, expectation) is { } message)
                {
                    failures.Add(new ScriptFailure(stepNumber, step.At, message));
                }
            }

            foreach (ScriptLaneExpectation expectation in step.LaneExpectations)
            {
                if (CheckLane(project, definition, engine, expectation) is { } message)
                {
                    failures.Add(new ScriptFailure(stepNumber, step.At, message));
                }
            }

            foreach (ScriptNetExpectation expectation in step.NetExpectations)
            {
                if (CheckNet(engine, expectation) is { } message)
                {
                    failures.Add(new ScriptFailure(stepNumber, step.At, message));
                }
            }

            for (int relationIndex = 0; relationIndex < step.RelationExpectations.Count; relationIndex++)
            {
                if (CheckRelation(engine, step.RelationExpectations[relationIndex], relationIndex + 1) is { } message)
                {
                    failures.Add(new ScriptFailure(stepNumber, step.At, message));
                }
            }

            if (step.Window is { } window)
            {
                long deadline = step.At + window.Ticks;
                var mismatches = new List<string>();
                bool matched = false;
                while (true)
                {
                    CollectMismatches(
                        project,
                        definition,
                        engine,
                        window.PinExpectations,
                        window.LaneExpectations,
                        window.NetExpectations,
                        window.RelationExpectations,
                        mismatches);
                    if (mismatches.Count == 0)
                    {
                        matched = true;
                        break;
                    }

                    // Signals change only at integer ticks, so checking once per tick
                    // after settling is enough to catch every stable state in the window.
                    if (engine.IsHalted || engine.Now >= deadline)
                    {
                        break;
                    }

                    engine.AdvanceTo(engine.Now + 1, cancellationToken);
                }

                if (!matched)
                {
                    string reason = engine.IsHalted
                        ? $"simulation halted at t={engine.Now} during the window: {engine.HaltReason}"
                        : $"no tick up to t={deadline} satisfied the window";
                    failures.Add(new ScriptFailure(
                        stepNumber,
                        engine.Now,
                        $"window assertion failed: {reason}. Last values: {string.Join("; ", mismatches)}."));
                }
            }

            if (step.Stability is { } stability)
            {
                long deadline = step.At + stability.Ticks;
                var mismatches = new List<string>();
                while (true)
                {
                    CollectMismatches(
                        project,
                        definition,
                        engine,
                        stability.PinExpectations,
                        stability.LaneExpectations,
                        stability.NetExpectations,
                        stability.RelationExpectations,
                        mismatches);
                    if (mismatches.Count > 0)
                    {
                        failures.Add(new ScriptFailure(
                            stepNumber,
                            engine.Now,
                            $"stable assertion failed at t={engine.Now}: {string.Join("; ", mismatches)}."));
                        break;
                    }

                    if (engine.Now >= deadline)
                    {
                        break;
                    }

                    if (engine.IsHalted)
                    {
                        failures.Add(new ScriptFailure(
                            stepNumber,
                            engine.Now,
                            $"stable assertion needs the simulation to run to t={deadline}, " +
                            $"but it halted at t={engine.Now}: {engine.HaltReason}"));
                        break;
                    }

                    engine.AdvanceTo(engine.Now + 1, cancellationToken);
                }
            }

            if (step.Transitions is { } transitions)
            {
                long deadline = step.At + transitions.Ticks;
                var observed = new List<(ScriptTransitionExpectation Expectation, int Net)>();
                bool signalsValid = true;
                foreach (ScriptTransitionExpectation expectation in transitions.Expectations)
                {
                    if (!TryResolveSignal(
                            engine,
                            expectation.Signal,
                            out int net,
                            out _,
                            out string error))
                    {
                        failures.Add(new ScriptFailure(stepNumber, step.At, error));
                        signalsValid = false;
                        continue;
                    }

                    observed.Add((expectation, net));
                }

                if (signalsValid)
                {
                    if (engine.Now < deadline)
                    {
                        engine.AdvanceTo(deadline, cancellationToken);
                    }

                    if (engine.IsHalted)
                    {
                        failures.Add(new ScriptFailure(
                            stepNumber,
                            engine.Now,
                            $"transition window needs the simulation to keep running through t={deadline}, " +
                            $"but it halted at t={engine.Now}: {engine.HaltReason}"));
                    }
                    else
                    {
                        foreach ((ScriptTransitionExpectation expectation, int net) in observed)
                        {
                            long actual = transitionCounts.GetValueOrDefault(net);
                            if (actual != expectation.Count)
                            {
                                failures.Add(new ScriptFailure(
                                    stepNumber,
                                    deadline,
                                    $"{expectation.Signal.Kind} '{expectation.Signal.Name}': expected " +
                                    $"{expectation.Count.ToString(CultureInfo.InvariantCulture)} " +
                                    $"transition(s) between t={step.At} and t={deadline}, observed {actual.ToString(CultureInfo.InvariantCulture)}."));
                            }
                        }
                    }
                }
            }

            if (step.Edges is { } edges)
            {
                long deadline = step.At + edges.Ticks;
                var observed = new List<ResolvedEdgeExpectation>();
                bool signalsValid = true;
                foreach (ScriptEdgeExpectation expectation in edges.Expectations)
                {
                    if (!TryResolveSignal(
                            engine,
                            expectation.Signal,
                            out int net,
                            out SignalDomain domain,
                            out string error))
                    {
                        failures.Add(new ScriptFailure(stepNumber, step.At, error));
                        signalsValid = false;
                        continue;
                    }

                    var filters = new List<ResolvedEdgeFilter>(expectation.Filters.Count);
                    foreach (ScriptEdgeFilter filter in expectation.Filters)
                    {
                        if (!TryResolveEdgeValues(
                                expectation.Signal,
                                domain,
                                filter.From,
                                filter.To,
                                stepNumber,
                                step.At,
                                failures,
                                out int? from,
                                out int? to))
                        {
                            signalsValid = false;
                            continue;
                        }

                        filters.Add(new ResolvedEdgeFilter(from, to, filter.Count));
                    }

                    observed.Add(new ResolvedEdgeExpectation(
                        expectation.Signal,
                        net,
                        domain,
                        filters));
                }

                if (signalsValid)
                {
                    if (engine.Now < deadline)
                    {
                        engine.AdvanceTo(deadline, cancellationToken);
                    }

                    if (engine.IsHalted)
                    {
                        failures.Add(new ScriptFailure(
                            stepNumber,
                            engine.Now,
                            $"edge window needs the simulation to keep running through t={deadline}, " +
                            $"but it halted at t={engine.Now}: {engine.HaltReason}"));
                    }
                    else
                    {
                        foreach (ResolvedEdgeExpectation expectation in observed)
                        {
                            foreach (ResolvedEdgeFilter filter in expectation.Filters)
                            {
                                long actual = 0;
                                foreach (((int net, int oldValue, int newValue), long count) in edgeCounts)
                                {
                                    if (net == expectation.Net &&
                                        (filter.From is null || filter.From == oldValue) &&
                                        (filter.To is null || filter.To == newValue))
                                    {
                                        actual += count;
                                    }
                                }

                                if (actual != filter.Count)
                                {
                                    failures.Add(new ScriptFailure(
                                        stepNumber,
                                        deadline,
                                        $"{expectation.Signal.Kind} '{expectation.Signal.Name}': expected " +
                                        $"{filter.Count.ToString(CultureInfo.InvariantCulture)} edge(s) " +
                                        $"{DescribeEdge(expectation.Domain, filter.From, filter.To)} " +
                                        $"between t={step.At} and t={deadline}, observed " +
                                        $"{actual.ToString(CultureInfo.InvariantCulture)}."));
                                }
                            }
                        }
                    }
                }
            }

            if (step.Sequence is { } sequence)
            {
                long deadline = step.At + sequence.Ticks;
                var resolved = new List<ResolvedEdgeSequenceItem>();
                bool itemsValid = true;
                for (int itemIndex = 0; itemIndex < sequence.Items.Count; itemIndex++)
                {
                    ScriptEdgeSequenceItem item = sequence.Items[itemIndex];
                    if (!TryResolveSignal(
                            engine,
                            item.Signal,
                            out int net,
                            out SignalDomain domain,
                            out string error))
                    {
                        failures.Add(new ScriptFailure(stepNumber, step.At, error));
                        itemsValid = false;
                        continue;
                    }

                    if (!TryResolveEdgeValues(
                            item.Signal,
                            domain,
                            item.From,
                            item.To,
                            stepNumber,
                            step.At,
                            failures,
                            out int? from,
                            out int? to))
                    {
                        itemsValid = false;
                        continue;
                    }

                    resolved.Add(new ResolvedEdgeSequenceItem(
                        itemIndex + 1,
                        item.Signal,
                        net,
                        domain,
                        from,
                        to));
                }

                if (itemsValid)
                {
                    if (engine.Now < deadline)
                    {
                        engine.AdvanceTo(deadline, cancellationToken);
                    }

                    if (engine.IsHalted)
                    {
                        failures.Add(new ScriptFailure(
                            stepNumber,
                            engine.Now,
                            $"edge sequence needs the simulation to keep running through t={deadline}, " +
                            $"but it halted at t={engine.Now}: {engine.HaltReason}"));
                    }
                    else
                    {
                        int searchFrom = 0;
                        foreach (ResolvedEdgeSequenceItem item in resolved)
                        {
                            bool found = false;
                            for (int changeIndex = searchFrom; changeIndex < sequenceChanges.Count; changeIndex++)
                            {
                                NetChange change = sequenceChanges[changeIndex];
                                if (change.Net == item.Net &&
                                    (item.From is null || item.From == change.OldValue) &&
                                    (item.To is null || item.To == change.NewValue))
                                {
                                    found = true;
                                    searchFrom = changeIndex + 1;
                                    break;
                                }
                            }

                            if (!found)
                            {
                                failures.Add(new ScriptFailure(
                                    stepNumber,
                                    deadline,
                                    $"sequence item {item.Index} ({item.Signal.Kind} '{item.Signal.Name}': " +
                                    $"{DescribeEdge(item.Domain, item.From, item.To)}) was not observed " +
                                    $"in order between t={step.At} and t={deadline}."));
                                break;
                            }
                        }
                    }
                }
            }

            if (step.ExpectHalted is bool expectedHalt && engine.IsHalted != expectedHalt)
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    step.At,
                    expectedHalt
                        ? "expected the simulation to halt, but it is still running."
                        : $"expected the simulation to keep running, but it halted: {engine.HaltReason}"));
            }

            stepsExecuted++;
            if (engine.IsHalted)
            {
                break;
            }
        }

        var diagnostics = new List<Diagnostic>(circuit.Diagnostics);
        diagnostics.AddRange(engine.RuntimeDiagnostics);
        return new SimulationScriptResult(
            script.Name,
            definitionId,
            failures,
            diagnostics,
            stepsExecuted,
            engine.Now,
            engine.EventsProcessed,
            engine.IsHalted,
            engine.HaltReason);
    }

    /// <summary>
    /// Checks a setup window tick by tick before the step's drives. Values are sampled
    /// after each tick's events have been processed, so an event anywhere in the window
    /// fails the assertion. The window cannot reach back into already-simulated time;
    /// that is a script-spacing error, not a circuit failure.
    /// </summary>
    private static void CheckSetup(
        CircuitProject project,
        CircuitDefinition definition,
        SimulationEngine engine,
        SimulationScriptStep step,
        ScriptSetup setup,
        int stepNumber,
        List<ScriptFailure> failures,
        CancellationToken cancellationToken)
    {
        long start = step.At - setup.Ticks;
        if (start < engine.Now)
        {
            failures.Add(new ScriptFailure(
                stepNumber,
                engine.Now,
                $"setup window starts at t={start}, before the current time t={engine.Now}; " +
                "move the previous step earlier or reduce setup.ticks."));
            return;
        }

        engine.AdvanceTo(start, cancellationToken);
        var mismatches = new List<string>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (engine.IsHalted)
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    engine.Now,
                    $"simulation halted at t={engine.Now} during the setup window: {engine.HaltReason}"));
                return;
            }

            CollectMismatches(
                project,
                definition,
                engine,
                setup.PinExpectations,
                setup.LaneExpectations,
                setup.NetExpectations,
                setup.RelationExpectations,
                mismatches);
            if (mismatches.Count > 0)
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    engine.Now,
                    $"setup assertion failed at t={engine.Now}: {string.Join("; ", mismatches)}."));
                return;
            }

            if (engine.Now >= step.At - 1)
            {
                return;
            }

            engine.AdvanceTo(engine.Now + 1, cancellationToken);
        }
    }

    private static void CollectMismatches(
        CircuitProject project,
        CircuitDefinition definition,
        SimulationEngine engine,
        IReadOnlyList<ScriptPinExpectation> pinExpectations,
        IReadOnlyList<ScriptLaneExpectation> laneExpectations,
        IReadOnlyList<ScriptNetExpectation> netExpectations,
        IReadOnlyList<ScriptRelationExpectation> relationExpectations,
        List<string> mismatches)
    {
        mismatches.Clear();
        foreach (ScriptPinExpectation expectation in pinExpectations)
        {
            if (CheckPin(engine, expectation) is { } message)
            {
                mismatches.Add(message);
            }
        }

        foreach (ScriptLaneExpectation expectation in laneExpectations)
        {
            if (CheckLane(project, definition, engine, expectation) is { } message)
            {
                mismatches.Add(message);
            }
        }

        foreach (ScriptNetExpectation expectation in netExpectations)
        {
            if (CheckNet(engine, expectation) is { } message)
            {
                mismatches.Add(message);
            }
        }

        for (int i = 0; i < relationExpectations.Count; i++)
        {
            if (CheckRelation(engine, relationExpectations[i], i + 1) is { } message)
            {
                mismatches.Add(message);
            }
        }
    }

    private static void ApplyDrive(
        SimulationEngine engine,
        int stepNumber,
        long at,
        ScriptDrive drive,
        List<ScriptFailure> failures)
    {
        if (!TryFindPort(engine, drive.PinId, out int portIndex, out CompiledPort port, out string error))
        {
            failures.Add(new ScriptFailure(stepNumber, at, error));
            return;
        }

        if (port.Direction != PinDirection.Input)
        {
            failures.Add(new ScriptFailure(
                stepNumber,
                at,
                $"pin '{drive.PinId}' is an output and cannot be driven."));
            return;
        }

        if (!TryResolveValue(port.Domain, drive.Value, out int value, out error))
        {
            failures.Add(new ScriptFailure(
                stepNumber,
                at,
                $"cannot drive pin '{drive.PinId}': {error}"));
            return;
        }

        engine.SetPortValue(portIndex, value);
    }

    /// <summary>
    /// Encodes one numeric lane and applies every resulting digit as an external
    /// drive. The lane and every pin are validated before the first drive is applied,
    /// so a malformed lane cannot leave a partially driven value behind.
    /// </summary>
    private static void ApplyLaneDrive(
        CircuitProject project,
        CircuitDefinition definition,
        SimulationEngine engine,
        int stepNumber,
        long at,
        ScriptLaneDrive drive,
        List<ScriptFailure> failures)
    {
        NumericLane? lane = definition.FindNumericLane(drive.LaneId);
        if (lane is null)
        {
            failures.Add(new ScriptFailure(
                stepNumber,
                at,
                $"definition '{definition.Id}' has no numeric lane '{drive.LaneId}'."));
            return;
        }

        INumericFormat? format = NumericFormatCatalog.Find(project, lane.FormatId);
        if (format is null)
        {
            failures.Add(new ScriptFailure(
                stepNumber,
                at,
                $"lane '{lane.Id}' uses unknown numeric format '{lane.FormatId}'."));
            return;
        }

        if (lane.PinIds.Count == 0)
        {
            failures.Add(new ScriptFailure(stepNumber, at, $"lane '{lane.Id}' has no interface pins."));
            return;
        }

        int[] signals = new int[lane.PinIds.Count];
        if (!format.TryEncode(drive.Value, signals, out string? encodeError))
        {
            failures.Add(new ScriptFailure(
                stepNumber,
                at,
                $"cannot drive lane '{lane.Id}' with {drive.Value}: {encodeError}"));
            return;
        }

        if (!format.LeastSignificantFirst)
        {
            Array.Reverse(signals);
        }

        string? domainId = null;
        var targets = new List<(int PortIndex, int Value)>(signals.Length);
        for (int i = 0; i < lane.PinIds.Count; i++)
        {
            string pinId = lane.PinIds[i];
            PinDeclaration? pin = definition.FindPin(pinId);
            if (pin is null)
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    at,
                    $"lane '{lane.Id}' refers to unknown interface pin '{pinId}'."));
                return;
            }

            if (pin.Direction != PinDirection.Input)
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    at,
                    $"lane '{lane.Id}' can only be driven when every pin is an input; '{pinId}' is an output."));
                return;
            }

            if (domainId is null)
            {
                domainId = pin.DomainId;
            }
            else if (!string.Equals(domainId, pin.DomainId, StringComparison.Ordinal))
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    at,
                    $"lane '{lane.Id}' contains pins from more than one domain."));
                return;
            }

            SignalDomain? domain = project.FindDomain(pin.DomainId);
            if (domain is null || !domain.IsValidValue(signals[i]))
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    at,
                    $"lane '{lane.Id}' needs a digit outside domain '{pin.DomainId}' for pin '{pinId}'."));
                return;
            }

            if (!TryFindPort(engine, pinId, out int portIndex, out CompiledPort port, out string portError))
            {
                failures.Add(new ScriptFailure(stepNumber, at, $"lane '{lane.Id}': {portError}"));
                return;
            }

            if (port.Direction != PinDirection.Input)
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    at,
                    $"lane '{lane.Id}': pin '{pinId}' is not an input port."));
                return;
            }

            targets.Add((portIndex, signals[i]));
        }

        foreach ((int portIndex, int value) in targets)
        {
            engine.SetPortValue(portIndex, value);
        }
    }

    /// <summary>Returns a failure message, or null when the pin matches.</summary>
    private static string? CheckPin(SimulationEngine engine, ScriptPinExpectation expectation)
    {
        if (!TryFindPort(engine, expectation.PinId, out int portIndex, out CompiledPort port, out string error))
        {
            return error;
        }

        if (!TryResolveValue(port.Domain, expectation.Expected, out int expected, out error))
        {
            return $"invalid expectation for pin '{expectation.PinId}': {error}";
        }

        int actual = engine.GetPortValue(portIndex);
        return actual == expected
            ? null
            : $"pin '{expectation.PinId}': expected {DescribeValue(port.Domain, expected)}, " +
              $"got {DescribeValue(port.Domain, actual)}";
    }

    /// <summary>Returns a failure message, or null when the internal net matches.</summary>
    private static string? CheckNet(SimulationEngine engine, ScriptNetExpectation expectation)
    {
        if (!TryFindNet(engine, expectation.Reference, out int netIndex, out CompiledNet net, out string error))
        {
            return error;
        }

        if (!TryResolveValue(net.Domain, expectation.Expected, out int expected, out error))
        {
            return $"invalid expectation for net '{expectation.Reference}': {error}";
        }

        int actual = engine.GetNetValue(netIndex);
        return actual == expected
            ? null
            : $"net '{expectation.Reference}': expected {DescribeValue(net.Domain, expected)}, " +
              $"got {DescribeValue(net.Domain, actual)}";
    }

    /// <summary>
    /// Returns a failure message, or null when a cross-signal relation holds. Both
    /// signals are read from their resolved nets, so a pin and an authored net that
    /// the compiler merged are compared by value rather than by reference identity.
    /// </summary>
    private static string? CheckRelation(
        SimulationEngine engine,
        ScriptRelationExpectation expectation,
        int index)
    {
        string prefix = $"relation {index} ({expectation.OperatorText})";
        if (!TryResolveSignal(
                engine,
                expectation.Left,
                out int leftNet,
                out SignalDomain leftDomain,
                out string leftError))
        {
            return $"{prefix}: {leftError}";
        }

        if (!TryResolveSignal(
                engine,
                expectation.Right,
                out int rightNet,
                out SignalDomain rightDomain,
                out string rightError))
        {
            return $"{prefix}: {rightError}";
        }

        if (!string.Equals(leftDomain.Id, rightDomain.Id, StringComparison.Ordinal))
        {
            return $"{prefix}: left {expectation.Left.Kind} '{expectation.Left.Name}' uses domain " +
                   $"'{leftDomain.Id}' but right {expectation.Right.Kind} '{expectation.Right.Name}' " +
                   $"uses domain '{rightDomain.Id}'; a relation needs both signals in the same domain.";
        }

        int leftValue = engine.GetNetValue(leftNet);
        int rightValue = engine.GetNetValue(rightNet);
        if (leftValue == SignalValue.UndrivenIndex)
        {
            return $"{prefix}: left {expectation.Left.Kind} '{expectation.Left.Name}' is undriven.";
        }

        if (rightValue == SignalValue.UndrivenIndex)
        {
            return $"{prefix}: right {expectation.Right.Kind} '{expectation.Right.Name}' is undriven.";
        }

        if (Holds(expectation.Operator, leftValue, rightValue))
        {
            return null;
        }

        return $"{prefix}: left {expectation.Left.Kind} '{expectation.Left.Name}' is " +
               $"{DescribeValue(leftDomain, leftValue)}, right {expectation.Right.Kind} " +
               $"'{expectation.Right.Name}' is {DescribeValue(rightDomain, rightValue)}; " +
               $"the relation does not hold.";
    }

    private static bool Holds(ScriptRelationOperator op, int left, int right) => op switch
    {
        ScriptRelationOperator.Equal => left == right,
        ScriptRelationOperator.NotEqual => left != right,
        ScriptRelationOperator.LessThan => left < right,
        ScriptRelationOperator.LessThanOrEqual => left <= right,
        ScriptRelationOperator.GreaterThan => left > right,
        ScriptRelationOperator.GreaterThanOrEqual => left >= right,
        _ => throw new InvalidOperationException($"Unknown relation operator '{op}'."),
    };

    /// <summary>Returns a failure message, or null when the lane matches.</summary>
    private static string? CheckLane(
        CircuitProject project,
        CircuitDefinition definition,
        SimulationEngine engine,
        ScriptLaneExpectation expectation)
    {
        NumericLane? lane = definition.FindNumericLane(expectation.LaneId);
        if (lane is null)
        {
            return $"definition '{definition.Id}' has no numeric lane '{expectation.LaneId}'.";
        }

        INumericFormat? format = NumericFormatCatalog.Find(project, lane.FormatId);
        if (format is null)
        {
            return $"lane '{lane.Id}' uses unknown numeric format '{lane.FormatId}'.";
        }

        if (lane.PinIds.Count == 0)
        {
            return $"lane '{lane.Id}' has no interface pins.";
        }

        int[] values = new int[lane.PinIds.Count];
        for (int i = 0; i < lane.PinIds.Count; i++)
        {
            string pinId = lane.PinIds[i];
            if (!TryFindPort(engine, pinId, out int portIndex, out _, out string error))
            {
                return $"lane '{lane.Id}': {error}";
            }

            values[i] = engine.GetPortValue(portIndex);
        }

        if (!format.LeastSignificantFirst)
        {
            Array.Reverse(values);
        }

        NumericDecodeResult decoded = format.Decode(values);
        if (!decoded.IsValid)
        {
            return $"lane '{lane.Id}' could not be decoded with '{format.Id}': {decoded.Error}.";
        }

        return decoded.Value == expectation.Expected
            ? null
            : $"lane '{lane.Id}': expected {expectation.Expected.ToString(CultureInfo.InvariantCulture)}, " +
              $"got {decoded.Value.ToString(CultureInfo.InvariantCulture)} " +
              $"({format.Describe(values.Length)} over {string.Join(",", lane.PinIds)})";
    }

    private static bool TryResolveSignal(
        SimulationEngine engine,
        ScriptSignalReference signal,
        out int net,
        out SignalDomain domain,
        out string error)
    {
        if (signal.Net is ScriptNetReference reference)
        {
            if (!TryFindNet(engine, reference, out net, out CompiledNet compiled, out error))
            {
                domain = null!;
                return false;
            }

            domain = compiled.Domain;
            return true;
        }

        if (!TryFindPort(engine, signal.Name, out _, out CompiledPort port, out error))
        {
            net = 0;
            domain = null!;
            return false;
        }

        net = port.Net;
        domain = port.Domain;
        return true;
    }

    private static bool TryFindNet(
        SimulationEngine engine,
        ScriptNetReference reference,
        out int netIndex,
        out CompiledNet net,
        out string error)
    {
        if (engine.Circuit.TryGetNet(new NetLocation(reference.Path, reference.NetId), out netIndex))
        {
            net = engine.Circuit.Nets[netIndex];
            error = string.Empty;
            return true;
        }

        net = null!;
        error = $"unknown internal net '{reference}'. Authored nets available: " +
                $"{DescribeAvailableNets(engine.Circuit)}.";
        return false;
    }

    private static string DescribeAvailableNets(CompiledCircuit circuit)
    {
        const int limit = 8;
        string[] names = circuit.NetIndexByLocation.Keys
            .Select(location => location.Path.Length == 0
                ? location.NetId
                : $"{location.Path}/{location.NetId}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
        {
            return "none";
        }

        string shown = string.Join(", ", names.Take(limit));
        return names.Length > limit
            ? $"{shown}, ... ({names.Length} total)"
            : shown;
    }

    private static bool TryFindPort(
        SimulationEngine engine,
        string pinId,
        out int portIndex,
        out CompiledPort port,
        out string error)
    {
        portIndex = engine.FindPortIndex(pinId);
        if (portIndex < 0)
        {
            string known = engine.Circuit.Ports.Count == 0
                ? "none"
                : string.Join(", ", engine.Circuit.Ports.Select(candidate => candidate.PinId));
            port = null!;
            error = $"unknown interface pin '{pinId}' (available: {known}).";
            return false;
        }

        port = engine.Circuit.Ports[portIndex];
        error = string.Empty;
        return true;
    }

    private static bool TryResolveEdgeValues(
        ScriptSignalReference signal,
        SignalDomain domain,
        ScriptValue? fromValue,
        ScriptValue? toValue,
        int stepNumber,
        long at,
        List<ScriptFailure> failures,
        out int? from,
        out int? to)
    {
        from = null;
        to = null;
        if (fromValue is ScriptValue fromRaw)
        {
            if (!TryResolveValue(domain, fromRaw, out int resolved, out string error))
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    at,
                    $"invalid 'from' for {signal.Kind} '{signal.Name}': {error}"));
                return false;
            }

            from = resolved;
        }

        if (toValue is ScriptValue toRaw)
        {
            if (!TryResolveValue(domain, toRaw, out int resolved, out string error))
            {
                failures.Add(new ScriptFailure(
                    stepNumber,
                    at,
                    $"invalid 'to' for {signal.Kind} '{signal.Name}': {error}"));
                return false;
            }

            to = resolved;
        }

        if (from is not null && to is not null && from == to)
        {
            failures.Add(new ScriptFailure(
                stepNumber,
                at,
                $"edge filter for {signal.Kind} '{signal.Name}' has the same 'from' and 'to' value " +
                $"{DescribeValue(domain, from.Value)}; no transition can match."));
            return false;
        }

        return true;
    }

    private static bool TryResolveValue(
        SignalDomain domain,
        ScriptValue value,
        out int index,
        out string error)
    {
        if (value.Index is int numeric)
        {
            index = numeric;
            if (index == SignalValue.UndrivenIndex)
            {
                error = string.Empty;
                return true;
            }

            if (!domain.IsValidValue(index))
            {
                error = $"value {index} is outside domain '{domain.Id}' (0..{domain.ValueCount - 1}).";
                return false;
            }

            error = string.Empty;
            return true;
        }

        for (int i = 0; i < domain.ValueCount; i++)
        {
            if (string.Equals(domain.ValueName(i), value.Raw, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                error = string.Empty;
                return true;
            }
        }

        string labels = string.Join(", ", Enumerable.Range(0, domain.ValueCount).Select(domain.ValueName));
        index = 0;
        error = $"'{value.Raw}' is not a value of domain '{domain.Id}' " +
                $"(indices 0..{domain.ValueCount - 1}, labels: {labels}, or 'undriven').";
        return false;
    }

    private static string DescribeValue(SignalDomain domain, int value) =>
        value == SignalValue.UndrivenIndex
            ? "undriven"
            : $"{value.ToString(CultureInfo.InvariantCulture)} ({domain.ValueName(value)})";

    private static string DescribeEdge(SignalDomain domain, int? from, int? to) =>
        $"{(from is int start ? DescribeValue(domain, start) : "any")} -> " +
        $"{(to is int end ? DescribeValue(domain, end) : "any")}";

    private sealed record ResolvedEdgeFilter(int? From, int? To, long Count);

    private sealed record ResolvedEdgeExpectation(
        ScriptSignalReference Signal,
        int Net,
        SignalDomain Domain,
        IReadOnlyList<ResolvedEdgeFilter> Filters);

    private sealed record ResolvedEdgeSequenceItem(
        int Index,
        ScriptSignalReference Signal,
        int Net,
        SignalDomain Domain,
        int? From,
        int? To);
}
