using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Simulation;
using Radix.StdLib;

namespace Radix.Core.Tests;

internal static class TestFixtures
{
    public static CircuitProject WithStandardLibrary(string name = "test")
    {
        var project = new CircuitProject { Name = name };
        StandardLibrary.Install(project);
        return project;
    }

    public static SimulationEngine Simulate(
        CircuitProject project,
        string? rootDefinitionId = null,
        SimulationOptions? options = null)
    {
        CompiledCircuit compiled = CircuitCompiler.Compile(project, rootDefinitionId);
        return new SimulationEngine(compiled, options);
    }

    public static int Port(this SimulationEngine engine, string pinId)
    {
        int index = engine.FindPortIndex(pinId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Port '{pinId}' was not found.");
        }

        return index;
    }

    public static void Drive(this SimulationEngine engine, string pinId, int value) =>
        engine.SetPortValue(engine.Port(pinId), value);

    public static int Read(this SimulationEngine engine, string pinId) =>
        engine.GetPortValue(engine.Port(pinId));

    public static void Settle(this SimulationEngine engine, long ticks = 20) =>
        engine.AdvanceTo(engine.Now + ticks);

    public static DefinitionBuilder Define(
        this ProjectBuilder builder,
        string id,
        string name,
        params (string PinId, string PinName, Core.Signals.PinDirection Direction, string Domain)[] pins)
    {
        DefinitionBuilder definition = builder.Define(id, name);
        foreach (var pin in pins)
        {
            definition.AddPin(pin.PinId, pin.PinName, pin.Direction, pin.Domain);
        }

        return definition;
    }
}
