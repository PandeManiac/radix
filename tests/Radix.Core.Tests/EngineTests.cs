using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Xunit;

namespace Radix.Core.Tests;

public sealed class EngineTests
{
    private static SimulationEngine Engine(CircuitProject project, SimulationOptions? options = null) =>
        TestFixtures.Simulate(project, project.RootDefinitionId, options);

    [Fact]
    public void ConstantPropagatesAtTimeZero()
    {
        SimulationEngine engine = Engine(TestCircuits.ConstantRoot(1));
        engine.AdvanceTo(0);
        Assert.Equal(1, engine.Read("y"));
        Assert.Equal(0, engine.Now);
    }

    [Fact]
    public void GateTransportDelayIsExact()
    {
        SimulationEngine engine = Engine(TestCircuits.LookupRoot(1, [1, 0], delay: 3));
        engine.Drive("a", 1);

        engine.AdvanceTo(2);
        Assert.Equal(-1, engine.Read("y"));

        engine.AdvanceTo(3);
        Assert.Equal(0, engine.Read("y"));
    }

    [Fact]
    public void ZeroDelayChainSettlesInOneTimestamp()
    {
        SimulationEngine engine = Engine(TestCircuits.BufferChain(100, delay: 0));
        engine.Drive("a", 1);
        engine.AdvanceTo(0);

        Assert.Equal(1, engine.Read("y"));
        Assert.Equal(0, engine.Now);
        Assert.False(engine.IsHalted);
    }

    [Fact]
    public void UndrivenInputMakesGateHoldItsOutput()
    {
        SimulationEngine engine = Engine(TestCircuits.LookupRoot(1, [1, 0]));
        engine.Drive("a", 1);
        engine.AdvanceTo(10);
        Assert.Equal(0, engine.Read("y"));

        engine.Drive("a", -1);
        engine.AdvanceTo(20);
        Assert.Equal(0, engine.Read("y"));
    }

    [Fact]
    public void WaveformFollowsConfiguredPhases()
    {
        SimulationEngine engine = Engine(TestCircuits.ClockRoot([5, 5], [0, 1]));
        engine.AdvanceTo(0);
        Assert.Equal(0, engine.Read("y"));
        engine.AdvanceTo(4);
        Assert.Equal(0, engine.Read("y"));
        engine.AdvanceTo(5);
        Assert.Equal(1, engine.Read("y"));
        engine.AdvanceTo(10);
        Assert.Equal(0, engine.Read("y"));
        engine.AdvanceTo(25);
        Assert.Equal(1, engine.Read("y"));
    }

    [Fact]
    public void NetChangedReportsEachResolvedTransitionInOrder()
    {
        SimulationEngine engine = Engine(TestCircuits.ClockRoot([5, 5], [0, 1]));
        var changes = new List<NetChange>();
        engine.NetChanged += changes.Add;

        engine.AdvanceTo(10);

        Assert.Equal(3, changes.Count);
        Assert.Equal(engine.NetTransitions, changes.Count);
        Assert.Equal([0L, 5L, 10L], changes.Select(change => change.Time).ToArray());
        Assert.Equal([-1, 0, 1], changes.Select(change => change.OldValue).ToArray());
        Assert.Equal([0, 1, 0], changes.Select(change => change.NewValue).ToArray());
    }

    [Fact]
    public void PriorityDomainCombinesDrivers()
    {
        var builder = new ProjectBuilder("multi driver");
        var root = builder.Define("root", "Root");
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.FourStateId);
        root.AddPrimitive("low", new ConstantSpec(0));
        root.AddPrimitive("high", new ConstantSpec(1));
        root.Connect(y, root.Pin("low", "out"), root.Pin("high", "out"));

        CircuitProject project = builder.Build("root");
        SimulationEngine engine = Engine(project);
        engine.AdvanceTo(0);

        Assert.Equal(2, engine.Read("y")); // X
        Assert.Single(engine.RuntimeDiagnostics, d => d.Code == SimulationEngine.DriverConflictCode);
    }

    [Fact]
    public void SingleDriverConflictIsReportedOnceAndUsesFirstDriver()
    {
        var builder = new ProjectBuilder("conflict");
        var root = builder.Define("root", "Root");
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("first", new ConstantSpec(0));
        root.AddPrimitive("second", new ConstantSpec(1));
        root.Connect(y, root.Pin("first", "out"), root.Pin("second", "out"));

        SimulationEngine engine = Engine(builder.Build("root"));
        engine.AdvanceTo(0);
        engine.AdvanceTo(10);

        Assert.Equal(0, engine.Read("y"));
        Assert.Single(engine.RuntimeDiagnostics, d => d.Code == SimulationEngine.DriverConflictCode);
    }

    [Fact]
    public void SingleDriverPolicyReportsDuplicateEqualDriversAsContention()
    {
        var builder = new ProjectBuilder("equal drivers");
        var root = builder.Define("root", "Root");
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("first", new ConstantSpec(1));
        root.AddPrimitive("second", new ConstantSpec(1));
        root.Connect(y, root.Pin("first", "out"), root.Pin("second", "out"));

        SimulationEngine engine = Engine(builder.Build("root"));
        engine.AdvanceTo(0);

        Assert.Equal(1, engine.Read("y"));
        Diagnostic conflict = Assert.Single(
            engine.RuntimeDiagnostics,
            diagnostic => diagnostic.Code == SimulationEngine.DriverConflictCode);
        Assert.Contains("active drivers", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("single", conflict.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("disagree", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetRestoresTimeValuesAndEvents()
    {
        SimulationEngine engine = Engine(TestCircuits.ClockRoot([5, 5], [0, 1]));
        engine.AdvanceTo(7);
        Assert.Equal(1, engine.Read("y"));
        Assert.True(engine.EventsProcessed > 0);

        engine.Reset();
        Assert.Equal(0, engine.Now);
        Assert.Equal(0, engine.EventsProcessed);
        Assert.False(engine.IsHalted);
        Assert.Empty(engine.Trace);

        engine.AdvanceTo(0);
        Assert.Equal(0, engine.Read("y"));
        engine.AdvanceTo(5);
        Assert.Equal(1, engine.Read("y"));
    }

    [Fact]
    public void StepTimestampProcessesAllEventsAtCurrentTime()
    {
        SimulationEngine engine = Engine(TestCircuits.BufferChain(20, delay: 0));
        engine.Drive("a", 1);
        int processed = engine.StepTimestamp();

        Assert.True(processed > 0);
        Assert.Equal(1, engine.Read("y"));
        Assert.Equal(0, engine.Now);
        Assert.False(engine.HasPendingEvents);
    }

    [Fact]
    public void AdvanceByMovesClockEvenWithoutEvents()
    {
        SimulationEngine engine = Engine(TestCircuits.ConstantRoot(0));
        engine.AdvanceTo(0);
        engine.AdvanceBy(42);
        Assert.Equal(42, engine.Now);
    }

    [Fact]
    public void AdvanceBackwardsIsRejected()
    {
        SimulationEngine engine = Engine(TestCircuits.ConstantRoot(0));
        engine.AdvanceTo(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.AdvanceTo(9));
    }

    [Fact]
    public void TraceRecordsInitialNetValue()
    {
        CircuitProject project = TestCircuits.ConstantRoot(0);
        project.Definitions[0].Nets[0].InitialValue = 1;

        SimulationEngine engine = Engine(project);
        Assert.Contains(engine.Trace, change => change.Time == 0 && change.OldValue == -1 && change.NewValue == 1);

        engine.AdvanceTo(0);
        Assert.Equal(0, engine.Read("y"));
    }

    [Fact]
    public void TraceCapacityIsBoundedAndReportsTruncation()
    {
        var options = new SimulationOptions
        {
            MaxRecordedChanges = 3,
            RecordTrace = true,
            MaxEvaluationsPerComponentPerTimestamp = 64,
        };
        SimulationEngine engine = Engine(TestCircuits.ClockRoot([1, 1], [0, 1]), options);
        engine.AdvanceTo(30);

        Assert.Equal(3, engine.Trace.Count);
        Assert.True(engine.Trace.TotalRecorded > 3);
        Assert.True(engine.Trace.IsTruncated);
    }

    [Fact]
    public void ZeroInputLookupActsAsConstant()
    {
        var builder = new ProjectBuilder("zero lookup");
        var root = builder.Define("root", "Root");
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("g", new LookupSpec(0, [1]));
        root.Connect(y, root.Pin("g", "out"));

        SimulationEngine engine = Engine(builder.Build("root"));
        engine.AdvanceTo(0);
        Assert.Equal(1, engine.Read("y"));
    }

    [Fact]
    public void OutputPortsCannotBeDriven()
    {
        SimulationEngine engine = Engine(TestCircuits.ConstantRoot(0));
        int port = engine.Port("y");
        Assert.Throws<InvalidOperationException>(() => engine.SetPortValue(port, 1));
    }

    [Fact]
    public void ExternalValuesMustBelongToDomain()
    {
        SimulationEngine engine = Engine(TestCircuits.ValidProject());
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Drive("a", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetExternalInput(9999, 0));
    }

    [Fact]
    public void FindPortReturnsMinusOneForUnknownPin()
    {
        SimulationEngine engine = Engine(TestCircuits.ValidProject());
        Assert.Equal(-1, engine.FindPortIndex("nope"));
        Assert.True(engine.Port("a") >= 0);
    }

    [Fact]
    public void ComponentStatsAreCollected()
    {
        SimulationEngine engine = Engine(TestCircuits.BufferChain(10, delay: 1));
        engine.Drive("a", 1);
        engine.AdvanceTo(30);

        Assert.True(engine.ComponentEvaluations > 0);
        Assert.True(engine.EventsProcessed > engine.ComponentEvaluations);
        Assert.True(engine.NetTransitions > 0);
    }

    [Fact]
    public void RuntimeDiagnosticsAreReset()
    {
        var builder = new ProjectBuilder("conflict reset");
        var root = builder.Define("root", "Root");
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("a", new ConstantSpec(0));
        root.AddPrimitive("b", new ConstantSpec(1));
        root.Connect(y, root.Pin("a", "out"), root.Pin("b", "out"));

        SimulationEngine engine = Engine(builder.Build("root"));
        engine.AdvanceTo(0);
        Assert.NotEmpty(engine.RuntimeDiagnostics);

        engine.Reset();
        Assert.Empty(engine.RuntimeDiagnostics);
    }
}
