using Radix.Core.Compilation;
using Radix.Core.Model;
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
}
