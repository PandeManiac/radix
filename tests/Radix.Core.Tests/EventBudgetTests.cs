using Radix.Core.Simulation;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class EventBudgetTests
{
    private static SimulationEngine Counter(long budget)
    {
        var options = new SimulationOptions
        {
            MaxEventsProcessed = budget,
            MaxRecordedChanges = 0,
            RecordTrace = false,
        };

        return TestFixtures.Simulate(ExampleProjects.CreateCounterDemo(), "demo.counter", options);
    }

    [Fact]
    public void BudgetHaltsAtTheExactCountWithRad152()
    {
        SimulationEngine engine = Counter(500);
        engine.AdvanceTo(1_000);

        Assert.True(engine.IsHalted);
        Assert.Equal(500, engine.EventsProcessed);
        Assert.Equal(0, engine.PendingEventCount);
        Assert.Contains(engine.RuntimeDiagnostics, diagnostic => diagnostic.Code == SimulationEngine.EventBudgetCode);
        Assert.NotNull(engine.HaltReason);
        Assert.Contains("event budget", engine.HaltReason!, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(engine.Now, 0, 1_000);
    }

    [Fact]
    public void HaltedBudgetStopsFurtherAdvance()
    {
        SimulationEngine engine = Counter(100);
        engine.AdvanceTo(1_000);
        long time = engine.Now;
        long events = engine.EventsProcessed;

        engine.AdvanceTo(2_000);
        engine.RunUntilIdle();

        Assert.Equal(events, engine.EventsProcessed);
        Assert.Equal(time, engine.Now);
        Assert.True(engine.IsHalted);
    }

    [Fact]
    public void ZeroBudgetDisablesTheCap()
    {
        SimulationEngine engine = Counter(0);
        engine.AdvanceTo(200);

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.True(engine.EventsProcessed > 0);
    }

    [Fact]
    public void BudgetAlsoAppliesToRunUntilIdle()
    {
        SimulationEngine engine = Counter(250);
        engine.RunUntilIdle();

        Assert.True(engine.IsHalted);
        Assert.Equal(250, engine.EventsProcessed);
        Assert.Contains(engine.RuntimeDiagnostics, diagnostic => diagnostic.Code == SimulationEngine.EventBudgetCode);
    }
}
