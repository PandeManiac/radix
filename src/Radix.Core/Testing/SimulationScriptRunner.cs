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
        string? definitionOverride = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(script);

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
        bool observesTransitions = script.Steps.Any(step => step.Transitions is not null);
        if (observesTransitions)
        {
            engine.NetChanged += change =>
            {
                transitionCounts.TryGetValue(change.Net, out long count);
                transitionCounts[change.Net] = count + 1;
            };
        }

        var failures = new List<ScriptFailure>();
        int stepsExecuted = 0;
        for (int i = 0; i < script.Steps.Count; i++)
        {
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

            engine.AdvanceTo(step.At);
            if (observesTransitions)
            {
                // The interval starts at this step, including changes caused by its
                // drives at this tick but excluding earlier activity.
                transitionCounts.Clear();
            }

            if (!engine.IsHalted)
            {
                foreach (ScriptDrive drive in step.Drives)
                {
                    ApplyDrive(engine, stepNumber, step.At, drive, failures);
                }

                // Settle zero-delay consequences of this step's drives at the same tick.
                // AdvanceTo is deliberate here: StepTimestamp would run the earliest
                // pending event even when it belongs to a later tick, which would make
                // the reported final time depend on the circuit's future schedule.
                engine.AdvanceTo(step.At);
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

                    engine.AdvanceTo(engine.Now + 1);
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

                    engine.AdvanceTo(engine.Now + 1);
                }
            }

            if (step.Transitions is { } transitions)
            {
                long deadline = step.At + transitions.Ticks;
                var observed = new List<(ScriptTransitionExpectation Expectation, int Net)>();
                bool pinsValid = true;
                foreach (ScriptTransitionExpectation expectation in transitions.PinExpectations)
                {
                    if (!TryFindPort(engine, expectation.PinId, out _, out CompiledPort port, out string error))
                    {
                        failures.Add(new ScriptFailure(stepNumber, step.At, error));
                        pinsValid = false;
                        continue;
                    }

                    observed.Add((expectation, port.Net));
                }

                if (pinsValid)
                {
                    if (engine.Now < deadline)
                    {
                        engine.AdvanceTo(deadline);
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
                                    $"pin '{expectation.PinId}': expected {expectation.Count.ToString(CultureInfo.InvariantCulture)} " +
                                    $"transition(s) between t={step.At} and t={deadline}, observed {actual.ToString(CultureInfo.InvariantCulture)}."));
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

    private static void CollectMismatches(
        CircuitProject project,
        CircuitDefinition definition,
        SimulationEngine engine,
        IReadOnlyList<ScriptPinExpectation> pinExpectations,
        IReadOnlyList<ScriptLaneExpectation> laneExpectations,
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
}
