using Radix.Core.Analysis;
using Radix.Core.Model;
using Radix.Core.Signals;
using Xunit;

namespace Radix.Core.Tests;

public sealed class InputSweepTests
{
    [Fact]
    public void BinaryAndSweepEnumeratesAllInputsInDeclarationOrder()
    {
        CircuitProject project = BinaryGate([0, 0, 0, 1]);

        InputSweepResult result = InputSweep.Run(project, "and");

        Assert.Equal("and", result.DefinitionId);
        Assert.Equal(["a", "b"], result.Inputs.Select(port => port.PinId).ToArray());
        Assert.Equal(["y"], result.Outputs.Select(port => port.PinId).ToArray());
        Assert.Equal(
            new[]
            {
                new[] { 0, 0 },
                new[] { 0, 1 },
                new[] { 1, 0 },
                new[] { 1, 1 },
            },
            result.Rows.Select(row => row.Inputs.ToArray()).ToArray());
        Assert.Equal([0, 0, 0, 1], result.Rows.Select(row => row.Outputs[0]).ToArray());
        Assert.Equal(0, result.SettleTicks);
    }

    [Fact]
    public void TernaryLookupSweepUsesThePinDomains()
    {
        CircuitProject project = TernaryInverter();

        InputSweepResult result = InputSweep.Run(project, "invert");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal([0, 1, 2], result.Rows.Select(row => row.Inputs[0]).ToArray());
        Assert.Equal([2, 1, 0], result.Rows.Select(row => row.Outputs[0]).ToArray());
        Assert.Equal(3, result.Inputs[0].Domain.ValueCount);
    }

    [Fact]
    public void SweepSettlesTransportDelaysWhenAsked()
    {
        CircuitProject project = DelayedBuffer();

        InputSweepResult immediate = InputSweep.Run(project, "delay", settleTicks: 0);
        Assert.Equal([SignalValue.UndrivenIndex, SignalValue.UndrivenIndex], immediate.Rows.Select(row => row.Outputs[0]).ToArray());

        InputSweepResult settled = InputSweep.Run(project, "delay", settleTicks: 3);
        Assert.Equal([0, 1], settled.Rows.Select(row => row.Outputs[0]).ToArray());
        Assert.Equal(3, settled.SettleTicks);
    }

    [Fact]
    public void TableFormatterAlignsColumnsAndShowsEveryRow()
    {
        InputSweepResult result = InputSweep.Run(BinaryGate([0, 0, 0, 1]), "and");

        string table = InputSweepTable.Format(result).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(
            "a | b | y\n" +
            "--+---+--\n" +
            "0 | 0 | 0\n" +
            "0 | 1 | 0\n" +
            "1 | 0 | 0\n" +
            "1 | 1 | 1",
            table);
        Assert.Equal("-", InputSweepTable.FormatValue(result.Outputs[0], SignalValue.UndrivenIndex));
        Assert.Equal("0", InputSweepTable.FormatValue(result.Outputs[0], 0));
    }

    [Fact]
    public void SweepRejectsMoreCombinationsThanTheLimit()
    {
        CircuitProject project = TernaryPair();

        InputSweepException exception = Assert.Throws<InputSweepException>(() =>
            InputSweep.Run(project, "pair", maxRows: 8));

        Assert.Contains("at least 9 rows", exception.Message, StringComparison.Ordinal);
        Assert.Contains("limit of 8", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--max-rows", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SweepWithNoInputsProducesOneRow()
    {
        var builder = new ProjectBuilder("constant");
        DefinitionBuilder definition = builder.Define("constant", "Constant");
        Net output = definition.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        definition.AddPrimitive("one", new ConstantSpec(1));
        definition.Connect(output, definition.Pin("one", "out"));

        InputSweepResult result = InputSweep.Run(builder.Build("constant"));

        Assert.Empty(result.Inputs);
        SweepRow row = Assert.Single(result.Rows);
        Assert.Empty(row.Inputs);
        Assert.Equal(1, row.Outputs[0]);
    }

    [Fact]
    public void SweepReportsProgressAndHonorsCancellation()
    {
        CircuitProject project = BinaryGate([0, 0, 0, 1]);
        var reports = new List<SweepProgress>();

        InputSweepResult result = InputSweep.Run(
            project,
            "and",
            progress: new InlineProgress<SweepProgress>(reports.Add));

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(0, reports[0].CompletedRows);
        Assert.Equal(4, reports[^1].CompletedRows);
        Assert.Equal(4, reports[^1].TotalRows);
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.Equal([1, 2, 3, 4], reports.Skip(1).Select(report => report.CompletedRows).ToArray());

        using var cancellation = new CancellationTokenSource();
        var cancelling = new InlineProgress<SweepProgress>(report =>
        {
            if (report.CompletedRows >= 1)
            {
                cancellation.Cancel();
            }
        });
        Assert.Throws<OperationCanceledException>(() => InputSweep.Run(
            project,
            "and",
            progress: cancelling,
            cancellationToken: cancellation.Token));

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => InputSweep.Run(
            project,
            "and",
            cancellationToken: alreadyCancelled.Token));
    }

    [Fact]
    public void SweepHonorsCancellationInsideASingleSettleRow()
    {
        var builder = new ProjectBuilder("cancel sweep");
        DefinitionBuilder definition = builder.Define("cancel", "Cancel");
        Net output = definition.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        long[] durations = Enumerable.Repeat(1L, 500).ToArray();
        int[] values = Enumerable.Range(0, 500).Select(index => index % 2).ToArray();
        definition.AddPrimitive("clock", new WaveformSpec(durations, values));
        Net current = definition.AddNet("clock_net", StandardDomains.BinaryId);
        definition.Connect(current, definition.Pin("clock", PrimitiveShapes.OutputPinId));
        for (int i = 0; i < 2_000; i++)
        {
            string id = $"buffer{i}";
            definition.AddPrimitive(id, new LookupSpec(1, [0, 1]));
            definition.Connect(current, definition.Pin(id, "in0"));
            Net next = i == 1_999
                ? output
                : definition.AddNet($"stage{i}", StandardDomains.BinaryId);
            definition.Connect(next, definition.Pin(id, "out"));
            current = next;
        }

        CircuitProject project = builder.Build("cancel");
        using var cancellation = new CancellationTokenSource();
        int observedChanges = 0;

        // Cancel from a net observer instead of a wall-clock deadline. The engine checks
        // the token every 1,024 processed events, and this circuit produces hundreds of
        // thousands of them during a single settle, so the check is guaranteed to run and
        // the test cannot flake on a slow or loaded machine.
        Assert.Throws<OperationCanceledException>(() => InputSweep.Run(
            project,
            "cancel",
            settleTicks: 1_000,
            engineObserver: engine => engine.NetChanged += _ =>
            {
                if (++observedChanges == 64)
                {
                    cancellation.Cancel();
                }
            },
            cancellationToken: cancellation.Token));
        Assert.True(observedChanges >= 64, $"observed {observedChanges} net changes");
    }

    [Fact]
    public void SweepRejectsUnknownDefinitionsAndInvalidLimits()
    {
        CircuitProject project = BinaryGate([0, 1]);

        Assert.Contains(
            "Unknown definition 'missing'",
            Assert.Throws<InputSweepException>(() => InputSweep.Run(project, "missing")).Message,
            StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => InputSweep.Run(project, "and", settleTicks: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => InputSweep.Run(project, "and", maxRows: 0));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static CircuitProject BinaryGate(IEnumerable<int> table)
    {
        var builder = new ProjectBuilder("and");
        DefinitionBuilder definition = builder.Define("and", "AND");
        Net a = definition.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net b = definition.AddPin("b", "B", PinDirection.Input, StandardDomains.BinaryId);
        Net y = definition.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        definition.AddPrimitive("gate", new LookupSpec(2, table));
        definition.Connect(a, definition.Pin("gate", "in0"));
        definition.Connect(b, definition.Pin("gate", "in1"));
        definition.Connect(y, definition.Pin("gate", "out"));
        return builder.Build("and");
    }

    private static CircuitProject TernaryInverter()
    {
        var builder = new ProjectBuilder("ternary inverter");
        DefinitionBuilder definition = builder.Define("invert", "Ternary inverter");
        Net a = definition.AddPin("a", "A", PinDirection.Input, StandardDomains.TernaryId);
        Net y = definition.AddPin("y", "Y", PinDirection.Output, StandardDomains.TernaryId);
        definition.AddPrimitive("gate", new LookupSpec(1, [2, 1, 0]));
        definition.Connect(a, definition.Pin("gate", "in0"));
        definition.Connect(y, definition.Pin("gate", "out"));
        return builder.Build("invert");
    }

    private static CircuitProject DelayedBuffer()
    {
        var builder = new ProjectBuilder("delayed buffer");
        DefinitionBuilder definition = builder.Define("delay", "Delay");
        Net a = definition.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y = definition.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        definition.AddPrimitive("gate", new LookupSpec(1, [0, 1], delayTicks: 3));
        definition.Connect(a, definition.Pin("gate", "in0"));
        definition.Connect(y, definition.Pin("gate", "out"));
        return builder.Build("delay");
    }

    private static CircuitProject TernaryPair()
    {
        var builder = new ProjectBuilder("ternary pair");
        DefinitionBuilder definition = builder.Define("pair", "Pair");
        Net a = definition.AddPin("a", "A", PinDirection.Input, StandardDomains.TernaryId);
        Net b = definition.AddPin("b", "B", PinDirection.Input, StandardDomains.TernaryId);
        Net y = definition.AddPin("y", "Y", PinDirection.Output, StandardDomains.TernaryId);
        definition.AddPrimitive("gate", new LookupSpec(2, Enumerable.Repeat(0, 9)));
        definition.Connect(a, definition.Pin("gate", "in0"));
        definition.Connect(b, definition.Pin("gate", "in1"));
        definition.Connect(y, definition.Pin("gate", "out"));
        return builder.Build("pair");
    }
}
