using System.Globalization;
using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;

namespace Radix.Core.Analysis;

/// <summary>Thrown when an input sweep cannot be run as requested.</summary>
public sealed class InputSweepException : Exception
{
    public InputSweepException(string message)
        : base(message)
    {
    }
}

/// <summary>One interface port included in a sweep, in declaration order.</summary>
public sealed record SweepPort(string PinId, string Name, SignalDomain Domain);

/// <summary>One input combination and the outputs observed after settling.</summary>
public sealed record SweepRow(IReadOnlyList<int> Inputs, IReadOnlyList<int> Outputs);

/// <summary>How many sweep rows have finished, for a progress display.</summary>
public readonly record struct SweepProgress(int CompletedRows, int TotalRows)
{
    public double Fraction => TotalRows <= 0 ? 0 : (double)CompletedRows / TotalRows;
}

/// <summary>Rows are ordered by input declaration: the first input changes slowest.</summary>
public sealed class InputSweepResult
{
    public InputSweepResult(
        string definitionId,
        IReadOnlyList<SweepPort> inputs,
        IReadOnlyList<SweepPort> outputs,
        IReadOnlyList<SweepRow> rows,
        long settleTicks)
    {
        DefinitionId = definitionId;
        Inputs = inputs;
        Outputs = outputs;
        Rows = rows;
        SettleTicks = settleTicks;
    }

    public string DefinitionId { get; }

    public IReadOnlyList<SweepPort> Inputs { get; }

    public IReadOnlyList<SweepPort> Outputs { get; }

    public IReadOnlyList<SweepRow> Rows { get; }

    /// <summary>Ticks advanced after driving each row.</summary>
    public long SettleTicks { get; }
}

/// <summary>
/// Exhaustively drives every combination of a definition's input port values and
/// reads its output ports after settling. This is an observation tool, not a second
/// simulator: it uses the public compiler and engine and makes no assumptions about
/// domain sizes, arithmetic, or combinational behavior. Each row starts from reset,
/// so sequential circuits are swept from their declared power-on state rather than
/// from a previous row's state.
/// </summary>
public static class InputSweep
{
    public const int DefaultMaxRows = 4096;

    public static InputSweepResult Run(
        CircuitProject project,
        string? definitionId = null,
        long settleTicks = 0,
        int maxRows = DefaultMaxRows,
        IProgress<SweepProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        if (settleTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settleTicks), "Settle ticks must be non-negative.");
        }

        if (maxRows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRows), "Max rows must be at least 1.");
        }

        string targetId = definitionId ?? project.RootDefinitionId;
        if (project.FindDefinition(targetId) is null)
        {
            throw new InputSweepException($"Unknown definition '{targetId}'.");
        }

        CompiledCircuit circuit = CircuitCompiler.Compile(project, targetId);
        var inputs = new List<(SweepPort Port, int Index)>();
        var outputs = new List<(SweepPort Port, int Index)>();
        for (int i = 0; i < circuit.Ports.Count; i++)
        {
            CompiledPort port = circuit.Ports[i];
            var sweepPort = new SweepPort(port.PinId, port.Name, port.Domain);
            if (port.Direction == PinDirection.Input)
            {
                inputs.Add((sweepPort, i));
            }
            else
            {
                outputs.Add((sweepPort, i));
            }
        }

        long rowCount = 1;
        foreach ((SweepPort port, _) in inputs)
        {
            int values = Math.Max(1, port.Domain.ValueCount);
            rowCount = rowCount > long.MaxValue / values
                ? long.MaxValue
                : rowCount * values;
            if (rowCount > maxRows)
            {
                throw new InputSweepException(
                    $"Sweep needs at least {rowCount.ToString(CultureInfo.InvariantCulture)} rows, " +
                    $"above the limit of {maxRows.ToString(CultureInfo.InvariantCulture)}. " +
                    "Reduce the input set or raise --max-rows.");
            }
        }

        var options = new SimulationOptions
        {
            MaxEvaluationsPerComponentPerTimestamp =
                Math.Max(1, project.Settings.MaxEvaluationsPerComponentPerTimestamp),
            MaxPendingEvents = Math.Max(0, project.Settings.MaxPendingEvents),
            MaxRecordedChanges = 0,
            RecordTrace = false,
        };
        var engine = new SimulationEngine(circuit, options);
        int totalRows = (int)rowCount;
        int reportEvery = Math.Max(1, totalRows / 100);
        var rows = new List<SweepRow>(totalRows);
        var combination = new int[inputs.Count];
        progress?.Report(new SweepProgress(0, totalRows));

        for (long row = 0; row < rowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            engine.Reset();
            engine.AdvanceTo(0);
            for (int i = 0; i < inputs.Count; i++)
            {
                engine.SetPortValue(inputs[i].Index, combination[i]);
            }

            engine.AdvanceTo(settleTicks, cancellationToken);
            if (engine.IsHalted)
            {
                throw new InputSweepException(
                    $"Sweep halted for inputs {DescribeInputs(inputs, combination)} " +
                    $"at t={engine.Now.ToString(CultureInfo.InvariantCulture)}: {engine.HaltReason}");
            }

            int[] inputValues = new int[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                inputValues[i] = combination[i];
            }

            int[] outputValues = new int[outputs.Count];
            for (int i = 0; i < outputs.Count; i++)
            {
                outputValues[i] = engine.GetPortValue(outputs[i].Index);
            }

            rows.Add(new SweepRow(inputValues, outputValues));

            long completed = row + 1;
            if (progress is not null &&
                (completed == rowCount || completed % reportEvery == 0))
            {
                progress.Report(new SweepProgress((int)completed, totalRows));
            }

            for (int i = inputs.Count - 1; i >= 0; i--)
            {
                combination[i]++;
                if (combination[i] < inputs[i].Port.Domain.ValueCount)
                {
                    break;
                }

                combination[i] = 0;
            }
        }

        return new InputSweepResult(
            targetId,
            inputs.Select(entry => entry.Port).ToArray(),
            outputs.Select(entry => entry.Port).ToArray(),
            rows,
            settleTicks);
    }

    private static string DescribeInputs(
        IReadOnlyList<(SweepPort Port, int Index)> inputs,
        IReadOnlyList<int> values)
    {
        var parts = new List<string>(inputs.Count);
        for (int i = 0; i < inputs.Count; i++)
        {
            SweepPort port = inputs[i].Port;
            int value = values[i];
            parts.Add($"{port.PinId}={value.ToString(CultureInfo.InvariantCulture)} ({port.Domain.ValueName(value)})");
        }

        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }
}
