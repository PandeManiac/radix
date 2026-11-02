using Radix.Core.Simulation;
using Xunit;

namespace Radix.Core.Tests;

public sealed class SimulationOptionsTests
{
    [Fact]
    public void EngineRejectsInvalidRuntimeLimits()
    {
        var project = TestCircuits.ConstantRoot(0);
        SimulationOptions[] invalidOptions =
        [
            new() { MaxEvaluationsPerComponentPerTimestamp = 0 },
            new() { MaxEvaluationsPerComponentPerTimestamp = -1 },
            new() { MaxPendingEvents = -1 },
            new() { MaxEventsProcessed = -1 },
            new() { MaxRuntimeDiagnostics = -1 },
            new() { MaxRecordedChanges = -1, RecordTrace = false },
        ];

        foreach (SimulationOptions options in invalidOptions)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TestFixtures.Simulate(project, options: options));
        }
    }

    [Fact]
    public void ZeroDisablesOptionalRuntimeLimits()
    {
        var project = TestCircuits.ConstantRoot(0);
        var options = new SimulationOptions
        {
            MaxPendingEvents = 0,
            MaxEventsProcessed = 0,
            MaxRuntimeDiagnostics = 0,
            MaxRecordedChanges = 0,
            RecordTrace = false,
        };

        SimulationEngine engine = TestFixtures.Simulate(project, options: options);

        Assert.Equal(0, engine.Options.MaxPendingEvents);
        Assert.Equal(0, engine.Options.MaxEventsProcessed);
        Assert.Equal(0, engine.Options.MaxRuntimeDiagnostics);
        Assert.Equal(0, engine.Trace.Capacity);
    }
}
