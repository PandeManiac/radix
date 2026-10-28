using Radix.Core.Compilation;
using Radix.Core.Interpretation;
using Radix.Core.Serialization;
using Radix.Core.Simulation;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class ExampleTests
{
    public static TheoryData<string> ExampleFiles()
    {
        var data = new TheoryData<string>();
        foreach (ExampleProject example in ExampleProjects.All())
        {
            data.Add(example.FileName);
        }

        return data;
    }

    [Fact]
    public void ExamplesHaveUniqueNamesAndFiles()
    {
        IReadOnlyList<ExampleProject> examples = ExampleProjects.All();
        Assert.NotEmpty(examples);
        Assert.Equal(examples.Count, examples.Select(e => e.FileName).Distinct().Count());
        Assert.All(examples, example =>
        {
            Assert.EndsWith(".radix", example.FileName, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(example.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(example.Description));
        });
    }

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void ExampleRoundTripsAndCompiles(string fileName)
    {
        ExampleProject example = ExampleProjects.All().Single(e => e.FileName == fileName);
        string json = ProjectSerializer.SaveToString(example.Project);
        Core.Model.CircuitProject loaded = ProjectSerializer.LoadFromString(json);
        CompiledCircuit circuit = CircuitCompiler.Compile(loaded);

        Assert.NotEmpty(circuit.Components);
        Assert.DoesNotContain(circuit.Diagnostics, d => d.Severity == Core.DiagnosticSeverity.Error);
        Assert.Equal(example.Project.RootDefinitionId, loaded.RootDefinitionId);
    }

    [Fact]
    public void DFlipFlopExampleSamplesData()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateDFlipFlopDemo(), "demo.dff");
        engine.Drive("clk", 0);
        engine.Drive("d", 1);
        engine.AdvanceTo(20);
        Assert.Equal(0, engine.Read("q"));

        engine.Drive("clk", 1);
        engine.AdvanceTo(40);
        Assert.Equal(1, engine.Read("q"));
        Assert.Equal(0, engine.Read("qn"));
    }

    [Fact]
    public void FullAdderExampleAddsThreeOnes()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateFullAdderDemo(), "demo.full-adder");
        engine.Drive("a", 1);
        engine.Drive("b", 1);
        engine.Drive("cin", 1);
        engine.AdvanceTo(20);
        Assert.Equal(1, engine.Read("s"));
        Assert.Equal(1, engine.Read("cout"));
    }

    [Fact]
    public void TernaryExampleCoversAllDigitPairs()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateBalancedTernaryHalfAdderDemo(), "demo.bt-half-adder");
        for (int a = 0; a < 3; a++)
        {
            for (int b = 0; b < 3; b++)
            {
                engine.Drive("a", a);
                engine.Drive("b", b);
                engine.AdvanceTo(engine.Now + 20);
                NumericDecodeResult result = NumericFormats.BalancedTernary.Decode([engine.Read("s"), engine.Read("c")]);
                Assert.True(result.IsValid);
                Assert.Equal((a - 1) + (b - 1), (int)result.Value);
            }
        }
    }

    [Fact]
    public void CounterExampleIsFreeRunning()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateCounterDemo(), "demo.counter");
        engine.AdvanceTo(320);
        int value = 0;
        for (int bit = 0; bit < 4; bit++)
        {
            value |= engine.Read($"q{bit}") << bit;
        }

        Assert.Equal(0, value);
        Assert.False(engine.IsHalted, engine.HaltReason);
    }
}
