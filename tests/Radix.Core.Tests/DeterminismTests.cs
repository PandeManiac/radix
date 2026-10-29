using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class DeterminismTests
{
    private static SimulationEngine Counter(CircuitProject? project = null)
    {
        CircuitProject p = project ?? ExampleProjects.CreateCounterDemo();
        return TestFixtures.Simulate(p, "demo.counter");
    }

    private static void AssertSameTrace(SimulationTrace left, SimulationTrace right)
    {
        Assert.Equal(left.TotalRecorded, right.TotalRecorded);
        Assert.Equal(left.Count, right.Count);
        for (int i = 0; i < left.Count; i++)
        {
            Assert.Equal(left[i], right[i]);
        }
    }

    [Fact]
    public void RepeatedRunsProduceIdenticalTraces()
    {
        SimulationEngine first = Counter();
        SimulationEngine second = Counter();

        first.AdvanceTo(200);
        second.AdvanceTo(200);

        AssertSameTrace(first.Trace, second.Trace);
        Assert.Equal(first.EventsProcessed, second.EventsProcessed);
        Assert.Equal(first.ComponentEvaluations, second.ComponentEvaluations);
        for (int net = 0; net < first.Circuit.Nets.Count; net++)
        {
            Assert.Equal(first.GetNetValue(net), second.GetNetValue(net));
        }
    }

    [Fact]
    public void ChunkedAdvanceMatchesSingleAdvance()
    {
        SimulationEngine oneShot = Counter();
        SimulationEngine chunked = Counter();

        oneShot.AdvanceTo(200);
        for (long target = 7; target < 200; target += 7)
        {
            chunked.AdvanceTo(target);
        }

        chunked.AdvanceTo(200);

        AssertSameTrace(oneShot.Trace, chunked.Trace);
        Assert.Equal(oneShot.Now, chunked.Now);
    }

    [Fact]
    public void ResetReproducesTheSameRun()
    {
        SimulationEngine engine = Counter();
        engine.AdvanceTo(200);
        int[] values = Enumerable.Range(0, engine.Circuit.Nets.Count).Select(engine.GetNetValue).ToArray();
        int transitions = engine.Trace.Count;
        long total = engine.Trace.TotalRecorded;

        engine.Reset();
        Assert.Equal(0, engine.Now);
        Assert.All(engine.Trace, change => Assert.Equal(0, change.Time));

        engine.AdvanceTo(200);
        Assert.Equal(transitions, engine.Trace.Count);
        Assert.Equal(total, engine.Trace.TotalRecorded);
        Assert.Equal(values, Enumerable.Range(0, engine.Circuit.Nets.Count).Select(engine.GetNetValue).ToArray());
    }

    [Fact]
    public void IndependentCompilationsHaveStableNetOrdering()
    {
        CircuitProject project = ExampleProjects.CreateCounterDemo();
        CompiledCircuit firstCircuit = CircuitCompiler.Compile(project, "demo.counter");
        CompiledCircuit secondCircuit = CircuitCompiler.Compile(project, "demo.counter");

        Assert.Equal(firstCircuit.Nets.Count, secondCircuit.Nets.Count);
        for (int i = 0; i < firstCircuit.Nets.Count; i++)
        {
            Assert.Equal(firstCircuit.Nets[i].RepresentativeNetId, secondCircuit.Nets[i].RepresentativeNetId);
            Assert.Equal(firstCircuit.Nets[i].RepresentativePath, secondCircuit.Nets[i].RepresentativePath);
        }

        var first = new SimulationEngine(firstCircuit);
        var second = new SimulationEngine(secondCircuit);
        first.AdvanceTo(120);
        second.AdvanceTo(120);
        AssertSameTrace(first.Trace, second.Trace);
    }

    [Fact]
    public void PrimitiveInsertionOrderDoesNotChangeSingleDriverLogic()
    {
        SimulationEngine forward = TestFixtures.Simulate(BuildIndependentLogic(reverseInstances: false));
        SimulationEngine reverse = TestFixtures.Simulate(BuildIndependentLogic(reverseInstances: true));

        foreach ((int a, int b) in new[] { (0, 0), (0, 1), (1, 0), (1, 1), (1, 0), (0, 1) })
        {
            forward.Drive("a", a);
            forward.Drive("b", b);
            reverse.Drive("a", a);
            reverse.Drive("b", b);
            forward.AdvanceTo(0);
            reverse.AdvanceTo(0);

            int expectedNotA = 1 - a;
            int expectedAnd = a & b;
            Assert.Equal(expectedNotA, forward.Read("not_a"));
            Assert.Equal(expectedAnd, forward.Read("and"));
            Assert.Equal(forward.Read("not_a"), reverse.Read("not_a"));
            Assert.Equal(forward.Read("and"), reverse.Read("and"));
            Assert.False(forward.IsHalted, forward.HaltReason);
            Assert.False(reverse.IsHalted, reverse.HaltReason);
        }
    }

    [Fact]
    public void HierarchicalAndFlatCircuitsMatchAcrossSerialization()
    {
        CircuitProject hierarchical = BuildHierarchicalInverter();
        CircuitProject flat = BuildFlatInverter();
        CircuitProject roundTripped = ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(hierarchical));
        SimulationEngine hierarchicalEngine = TestFixtures.Simulate(hierarchical);
        SimulationEngine flatEngine = TestFixtures.Simulate(flat);
        SimulationEngine roundTripEngine = TestFixtures.Simulate(roundTripped);

        foreach (int input in new[] { 0, 1, 1, 0, 1, 0 })
        {
            hierarchicalEngine.Drive("a", input);
            flatEngine.Drive("a", input);
            roundTripEngine.Drive("a", input);
            hierarchicalEngine.AdvanceTo(0);
            flatEngine.AdvanceTo(0);
            roundTripEngine.AdvanceTo(0);

            int expected = 1 - input;
            Assert.Equal(expected, hierarchicalEngine.Read("y"));
            Assert.Equal(hierarchicalEngine.Read("y"), flatEngine.Read("y"));
            Assert.Equal(hierarchicalEngine.Read("y"), roundTripEngine.Read("y"));
        }
    }

    private static CircuitProject BuildIndependentLogic(bool reverseInstances)
    {
        var builder = new ProjectBuilder("insertion order");
        var root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net b = root.AddPin("b", "B", PinDirection.Input, StandardDomains.BinaryId);
        Net notA = root.AddPin("not_a", "Not A", PinDirection.Output, StandardDomains.BinaryId);
        Net and = root.AddPin("and", "And", PinDirection.Output, StandardDomains.BinaryId);

        var parts = reverseInstances
            ? new (string Id, PrimitiveSpec Spec)[]
            {
                ("and_gate", new LookupSpec(2, [0, 0, 0, 1])),
                ("inverter", new LookupSpec(1, [1, 0])),
            }
            : new (string Id, PrimitiveSpec Spec)[]
            {
                ("inverter", new LookupSpec(1, [1, 0])),
                ("and_gate", new LookupSpec(2, [0, 0, 0, 1])),
            };

        foreach ((string id, PrimitiveSpec spec) in parts)
        {
            root.AddPrimitive(id, spec);
        }

        root.Connect(a, root.Pin("inverter", "in0"));
        root.Connect(b, root.Pin("and_gate", "in0"));
        root.Connect(a, root.Pin("and_gate", "in1"));
        root.Connect(notA, root.Pin("inverter", "out"));
        root.Connect(and, root.Pin("and_gate", "out"));
        return builder.Build("root");
    }

    private static CircuitProject BuildHierarchicalInverter()
    {
        var builder = new ProjectBuilder("hierarchical inverter");
        var inverter = builder.Define("inverter", "Inverter");
        Net input = inverter.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net output = inverter.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        inverter.AddPrimitive("gate", new LookupSpec(1, [1, 0]));
        inverter.Connect(input, inverter.Pin("gate", "in0"));
        inverter.Connect(output, inverter.Pin("gate", "out"));

        var root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddInstance("u", "inverter");
        root.Connect(a, root.Pin("u", "a"));
        root.Connect(y, root.Pin("u", "y"));
        return builder.Build("root");
    }

    private static CircuitProject BuildFlatInverter()
    {
        var builder = new ProjectBuilder("flat inverter");
        var root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("gate", new LookupSpec(1, [1, 0]));
        root.Connect(a, root.Pin("gate", "in0"));
        root.Connect(y, root.Pin("gate", "out"));
        return builder.Build("root");
    }
}
