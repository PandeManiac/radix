using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Xunit;

namespace Radix.Core.Tests;

public sealed class KernelSmokeTests
{
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void StandardNandUsesTruthTablePrimitive(int a, int b, int expected)
    {
        var project = TestFixtures.WithStandardLibrary("nand");
        SimulationEngine engine = TestFixtures.Simulate(project, "std.nand");

        engine.Drive("a", a);
        engine.Drive("b", b);
        engine.Settle(10);

        Assert.Equal(expected, engine.Read("y"));
        Assert.False(engine.IsHalted);
    }

    [Fact]
    public void DFlipFlopSamplesOnRisingEdgeAndHolds()
    {
        var project = TestFixtures.WithStandardLibrary("dff");
        SimulationEngine engine = TestFixtures.Simulate(project, "std.dff");

        engine.Drive("clk", 0);
        engine.Drive("d", 0);
        engine.Settle(20);
        Assert.Equal(0, engine.Read("q"));
        Assert.Equal(1, engine.Read("qn"));

        engine.Drive("d", 1);
        engine.Settle(20);
        Assert.Equal(0, engine.Read("q"));

        engine.Drive("clk", 1);
        engine.Settle(20);
        Assert.Equal(1, engine.Read("q"));
        Assert.Equal(0, engine.Read("qn"));

        engine.Drive("d", 0);
        engine.Settle(20);
        Assert.Equal(1, engine.Read("q"));

        engine.Drive("clk", 0);
        engine.Settle(20);
        Assert.Equal(1, engine.Read("q"));

        engine.Drive("clk", 1);
        engine.Settle(20);
        Assert.Equal(0, engine.Read("q"));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void ZeroDelayFeedbackIsDetected()
    {
        var builder = new ProjectBuilder("inverter loop");
        var definition = builder.Define("loop", "Inverter loop");
        definition.AddPin("a", "A", PinDirection.Input, "binary");
        definition.AddPin("y", "Y", PinDirection.Output, "binary");
        definition.AddPrimitive("inv", new LookupSpec(1, [1, 0], delayTicks: 0));
        definition.Connect("y_net", definition.Pin("inv", "out"), definition.Pin("inv", "in0"));
        definition.Definition.FindNet("y_net")!.InitialValue = 0;

        SimulationEngine engine = TestFixtures.Simulate(builder.Build(), "loop");
        engine.AdvanceTo(10);

        Assert.True(engine.IsHalted);
        Assert.Contains(engine.RuntimeDiagnostics, d => d.Code == SimulationEngine.NonConvergenceCode);
    }

    [Fact]
    public void UnitDelayTakesConfiguredTime()
    {
        var builder = new ProjectBuilder("delay");
        var definition = builder.Define("delay", "Delay");
        definition.AddPin("a", "A", PinDirection.Input, "binary");
        definition.AddPin("y", "Y", PinDirection.Output, "binary");
        definition.AddPrimitive("buf", new LookupSpec(1, [0, 1], delayTicks: 0));
        definition.AddPrimitive("delay", new DelaySpec(5, initialValue: 0));
        definition.Connect("a_net", definition.Pin("buf", "in0"));
        definition.AddNet("mid", "binary", new PinRef("buf", "out"), new PinRef("delay", "in0"));
        definition.Connect("y_net", new PinRef("delay", "out"));

        SimulationEngine engine = TestFixtures.Simulate(builder.Build(), "delay");
        engine.Drive("a", 1);
        engine.AdvanceTo(4);
        Assert.Equal(0, engine.Read("y"));
        engine.AdvanceTo(5);
        Assert.Equal(1, engine.Read("y"));
    }
}
