using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Radix.Core.Testing;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// An output may be written only when its declaration says it is externally supplied. The default is
/// unchanged, so ordinary pins keep the rule the kernel test pins down; this file covers the declared
/// case and the fact that the declaration survives a file. Declaring it is how a solved electrical
/// node is allowed to feed a digital output: the exception is per pin and visible in the file, not a
/// quiet widening of every output.
/// </summary>
public sealed class ExternalSupplyTests
{
    private static CircuitProject Inverter(bool externalSupply)
    {
        var builder = new ProjectBuilder("ternary inverter");
        DefinitionBuilder definition = builder.Define("invert", "Ternary inverter");
        Net a = definition.AddPin("a", "A", PinDirection.Input, StandardDomains.TernaryId);
        Net y = definition.AddPin(
            "y",
            "Y",
            PinDirection.Output,
            StandardDomains.TernaryId,
            externalSupply: externalSupply);
        definition.AddPrimitive("gate", new LookupSpec(1, [2, 1, 0]));
        definition.Connect(a, definition.Pin("gate", "in0"));
        definition.Connect(y, definition.Pin("gate", "out"));
        return builder.Build("invert");
    }

    [Fact]
    public void ADeclaredOutputAcceptsAnExternalValue()
    {
        SimulationEngine engine = TestFixtures.Simulate(Inverter(externalSupply: true));

        engine.SetPortValue(engine.Port("y"), 1);

        Assert.Equal(1, engine.GetPortValue(engine.Port("y")));
    }

    [Fact]
    public void AnUndeclaredOutputStillRefusesOne()
    {
        SimulationEngine engine = TestFixtures.Simulate(Inverter(externalSupply: false));

        // Same circuit, one flag: the refusal follows the declaration, not the direction alone.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => engine.SetPortValue(engine.Port("y"), 1));
        Assert.Contains("cannot be driven externally", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeclarationSurvivesAFileAndDefaultsToFalse()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"radix-external-{Guid.NewGuid():N}.radix");
        try
        {
            ProjectSerializer.Save(path, Inverter(externalSupply: true));
            CircuitProject loaded = ProjectSerializer.Load(path, validate: false);

            CircuitDefinition definition = Assert.IsType<CircuitDefinition>(
                loaded.FindDefinition("invert"));
            Assert.True(Assert.IsType<PinDeclaration>(definition.FindPin("y")).ExternalSupply);

            // Only the declared pin carries it; the input beside it does not inherit the flag.
            Assert.False(Assert.IsType<PinDeclaration>(definition.FindPin("a")).ExternalSupply);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
