using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Radix.Core.Testing;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The electrical side can feed an input and check an output, but it cannot answer for one unless
/// the pin says so: the kernel refuses to drive an output port, and a pin whose declaration opts in
/// with <see cref="PinDeclaration.ExternalSupply"/> is the one exception. That default is a
/// deliberate invariant, not a gap in the CLI, so it is pinned here. A future change that opens the
/// direction more widely has to change this test knowingly rather than discover the behaviour by
/// accident.
/// </summary>
public sealed class ElectricalOutputDrivenTests
{
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

    [Fact]
    public void TheKernelRefusesToDriveAnOutputPort()
    {
        SimulationEngine engine = TestFixtures.Simulate(TernaryInverter());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => engine.SetPortValue(engine.Port("y"), 0));
        Assert.Contains("cannot be driven externally", error.Message, StringComparison.Ordinal);

        // The supported direction still works: an input takes the value and reads back.
        engine.SetPortValue(engine.Port("a"), 2);
        Assert.Equal(2, engine.GetPortValue(engine.Port("a")));
    }
}
