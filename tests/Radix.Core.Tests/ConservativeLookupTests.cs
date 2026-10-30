using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Lookup inputs without a driver are completed over their domains. An output is
/// usable only when every completion agrees; otherwise the lookup holds its previous
/// value. These tests pin both sides of that rule and the distinctions from explicit
/// domain values and delayed lookups.
/// </summary>
public sealed class ConservativeLookupTests
{
    private static readonly int[] MuxTable = [0, 0, 0, 1, 1, 0, 1, 1];

    [Fact]
    public void UnselectedUndrivenMuxDataDoesNotBlockOutput()
    {
        SimulationEngine engine = Lookup(
            [("a", StandardDomains.BinaryId), ("b", StandardDomains.BinaryId), ("s", StandardDomains.BinaryId)],
            StandardDomains.BinaryId,
            MuxTable);

        engine.Drive("a", 1);
        engine.Drive("s", 0);
        engine.AdvanceTo(0);
        Assert.Equal(1, engine.Read("y"));

        // With S=1 the result now depends on the undriven B input, so the previous
        // output is held rather than replaced by an arbitrary completion.
        engine.AdvanceTo(1);
        engine.Drive("s", 1);
        engine.AdvanceTo(2);
        Assert.Equal(1, engine.Read("y"));

        engine.Drive("b", 0);
        engine.AdvanceTo(3);
        Assert.Equal(0, engine.Read("y"));
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void ControllingValueDeterminesGateOutput()
    {
        SimulationEngine and = Lookup(
            [("a", StandardDomains.BinaryId), ("b", StandardDomains.BinaryId)],
            StandardDomains.BinaryId,
            [0, 0, 0, 1]);
        and.Drive("a", 0);
        and.AdvanceTo(0);
        Assert.Equal(0, and.Read("y"));

        and.AdvanceTo(1);
        and.Drive("a", 1);
        and.AdvanceTo(2);
        Assert.Equal(0, and.Read("y"));

        and.Drive("b", 1);
        and.AdvanceTo(3);
        Assert.Equal(1, and.Read("y"));

        SimulationEngine or = Lookup(
            [("a", StandardDomains.BinaryId), ("b", StandardDomains.BinaryId)],
            StandardDomains.BinaryId,
            [0, 1, 1, 1]);
        or.Drive("a", 1);
        or.AdvanceTo(0);
        Assert.Equal(1, or.Read("y"));

        or.AdvanceTo(1);
        or.Drive("a", 0);
        or.AdvanceTo(2);
        Assert.Equal(1, or.Read("y"));

        or.Drive("b", 0);
        or.AdvanceTo(3);
        Assert.Equal(0, or.Read("y"));
    }

    [Fact]
    public void UndrivenInputsThatCannotChangeTheTableStillProduceOutput()
    {
        SimulationEngine engine = Lookup(
            [("a", StandardDomains.BinaryId), ("b", StandardDomains.BinaryId)],
            StandardDomains.BinaryId,
            [1, 1, 1, 1]);

        engine.AdvanceTo(10);
        Assert.Equal(1, engine.Read("y"));
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void AmbiguousFullyUndrivenLookupLeavesOutputUndriven()
    {
        SimulationEngine engine = Lookup(
            [("a", StandardDomains.BinaryId)],
            StandardDomains.BinaryId,
            [0, 1]);

        engine.AdvanceTo(10);
        Assert.Equal(SignalValue.UndrivenIndex, engine.Read("y"));
    }

    [Fact]
    public void MixedRadixCompletionUsesEveryDomainValue()
    {
        SimulationEngine engine = Lookup(
            [("a", StandardDomains.TernaryId), ("b", StandardDomains.BinaryId)],
            StandardDomains.TernaryId,
            [0, 0, 1, 1, 2, 2]);

        engine.Drive("a", 2);
        engine.AdvanceTo(0);
        Assert.Equal(2, engine.Read("y"));
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void ExplicitUnknownDomainValueIsAnOrdinaryTableIndex()
    {
        SimulationEngine engine = Lookup(
            [("a", StandardDomains.FourStateId)],
            StandardDomains.FourStateId,
            [1, 0, 2, 3]);

        engine.Drive("a", 2); // explicit X, not the kernel's undriven marker
        engine.AdvanceTo(0);
        Assert.Equal(2, engine.Read("y"));

        // Removing the driver makes every 0/1/X/Z completion possible; the table is a
        // permutation, so the previous output must be held.
        engine.AdvanceTo(1);
        engine.Drive("a", SignalValue.UndrivenIndex);
        engine.AdvanceTo(2);
        Assert.Equal(2, engine.Read("y"));
    }

    [Fact]
    public void DeterminedOutputStillHonorsTransportDelay()
    {
        SimulationEngine engine = Lookup(
            [("a", StandardDomains.BinaryId), ("b", StandardDomains.BinaryId), ("s", StandardDomains.BinaryId)],
            StandardDomains.BinaryId,
            MuxTable,
            delay: 3);

        engine.Drive("a", 1);
        engine.Drive("s", 0);
        engine.AdvanceTo(2);
        Assert.Equal(SignalValue.UndrivenIndex, engine.Read("y"));

        engine.AdvanceTo(3);
        Assert.Equal(1, engine.Read("y"));
    }

    private static SimulationEngine Lookup(
        (string Pin, string Domain)[] inputs,
        string outputDomain,
        int[] table,
        long delay = 0)
    {
        var builder = new ProjectBuilder("conservative lookup");
        DefinitionBuilder root = builder.Define("root", "Root");
        var inputNets = new Net[inputs.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputNets[i] = root.AddPin(
                inputs[i].Pin,
                inputs[i].Pin.ToUpperInvariant(),
                PinDirection.Input,
                inputs[i].Domain);
        }

        Net output = root.AddPin("y", "Y", PinDirection.Output, outputDomain);
        root.AddPrimitive("g", new LookupSpec(inputs.Length, table, delay));
        for (int i = 0; i < inputs.Length; i++)
        {
            root.Connect(inputNets[i], root.Pin("g", $"in{i}"));
        }

        root.Connect(output, root.Pin("g", "out"));
        return TestFixtures.Simulate(builder.Build("root"), "root");
    }
}
