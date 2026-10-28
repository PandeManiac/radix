using Radix.Core.Compilation;
using Radix.Core.Interpretation;
using Radix.Core.Model;
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
    public void AccumulatorExecutesLoadAddClearAndHold()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateAccumulatorCpuDemo(), "demo.accumulator");
        engine.Drive("clk", 0);
        engine.Settle(40);
        DriveOperation(engine, 0, 0, 0);
        Assert.Equal(0, ReadAccumulator(engine));

        DriveOperation(engine, 1, 0, 5);       // LOAD 5
        PulseClock(engine);
        Assert.Equal(5, ReadAccumulator(engine));

        DriveOperation(engine, 0, 1, 3);       // ADD 3
        PulseClock(engine);
        Assert.Equal(8, ReadAccumulator(engine));

        DriveOperation(engine, 0, 0, 9);       // NOP keeps data irrelevant
        PulseClock(engine);
        Assert.Equal(8, ReadAccumulator(engine));

        DriveOperation(engine, 1, 0, 15);      // LOAD 15
        PulseClock(engine);
        DriveOperation(engine, 0, 1, 2);       // ADD 2 -> 17 wraps to 1, carry 1
        PulseClock(engine);
        Assert.Equal(1, ReadAccumulator(engine));
        Assert.Equal(1, engine.Read("carry"));

        DriveOperation(engine, 1, 1, 0);       // CLEAR
        PulseClock(engine);
        Assert.Equal(0, ReadAccumulator(engine));
        Assert.False(engine.IsHalted, engine.HaltReason);
    }

    [Fact]
    public void AccumulatorExampleShipsSignedResultLane()
    {
        var project = ExampleProjects.CreateAccumulatorCpuDemo();
        NumericFormatDefinition format = Assert.Single(project.CustomNumericFormats);
        Assert.Equal("signed-4bit", format.Id);

        NumericLane lane = project.RequireDefinition("demo.accumulator").NumericLanes
            .Single(candidate => candidate.Id == "result");
        Assert.Equal(format.Id, lane.FormatId);
        INumericFormat resolved = NumericFormatCatalog.Find(project, lane.FormatId)!;
        Assert.Equal(-8, (int)resolved.MinimumValue(4));
        Assert.Equal(-2, (int)resolved.Decode([0, 1, 1, 1]).Value);
    }

    [Fact]
    public void TinyCpuFetchesAndExecutesItsShippedProgram()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateTinyCpuDemo(), "demo.tiny-cpu");
        engine.Drive("clk", 0);
        engine.Drive("reset", 0);
        engine.Settle(80);

        Assert.Equal(0, ReadLane(engine, "pc", 4));
        Assert.Equal(0x17, ReadLane(engine, "ir", 8));
        Assert.Equal(0, ReadLane(engine, "mem", 4));
        Assert.Equal(1, engine.Read("zero"));

        (int Pc, int Ir, int Acc, int Mem)[] states =
        [
            (1, 0x33, 7, 0),    // LDA 7 executed; STA 3 fetched
            (2, 0x10, 7, 0),    // STA 3 stored 7
            (3, 0x83, 0, 7),    // LDA 0 executed; no store yet at address 3
            (4, 0x21, 7, 0),    // LDA@ 3 loaded 7
            (5, 0x34, 8, 0),    // ADD 1 executed; STA 4 fetched
            (6, 0x10, 8, 0),    // STA 4 stored 8
            (7, 0x84, 0, 8),    // LDA 0 executed
            (8, 0x93, 8, 7),    // LDA@ 4 loaded 8
            (9, 0x35, 15, 0),   // ADD@ 3 added 7
            (10, 0x10, 15, 0),  // STA 5 stored 15
            (11, 0x85, 0, 15),  // LDA 0 executed
            (12, 0x6C, 15, 0),  // LDA@ 5 loaded 15
            (12, 0x6C, 15, 0),  // JMP 12 loops
        ];

        foreach ((int pc, int instruction, int accumulator, int memory) in states)
        {
            PulseClock(engine);
            Assert.Equal(pc, ReadLane(engine, "pc", 4));
            Assert.Equal(instruction, ReadLane(engine, "ir", 8));
            Assert.Equal(accumulator, ReadLane(engine, "acc", 4));
            Assert.Equal(memory, ReadLane(engine, "mem", 4));
            Assert.Equal(0, engine.Read("carry"));
            Assert.False(engine.IsHalted, engine.HaltReason);
        }

        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TinyCpuJumpsWhenZeroFlagIsSetAndResets()
    {
        int[] program =
        [
            0x10, // LDA 0
            0x74, // JZ 4
            0x11, // skipped
            0x11, // skipped
            0x22, // ADD 2 -> 2
            0x66, // JMP 6
            0x66, // halt loop
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        var engine = TestFixtures.Simulate(ExampleProjects.CreateTinyCpuDemo(program), "demo.tiny-cpu");
        engine.Drive("clk", 0);
        engine.Drive("reset", 0);
        engine.Settle(80);

        PulseClock(engine);                    // LDA 0, ZERO asserted
        Assert.Equal(1, engine.Read("zero"));
        PulseClock(engine);                    // JZ 4
        Assert.Equal(4, ReadLane(engine, "pc", 4));

        PulseClock(engine);                    // ADD 2
        Assert.Equal(5, ReadLane(engine, "pc", 4));
        Assert.Equal(2, ReadLane(engine, "acc", 4));

        engine.Drive("reset", 1);
        engine.Settle(40);
        engine.Drive("clk", 1);
        engine.Settle(80);
        Assert.Equal(0, ReadLane(engine, "pc", 4));
        Assert.Equal(0, ReadLane(engine, "acc", 4));
        Assert.Equal(0, engine.Read("carry"));

        engine.Drive("reset", 0);
        engine.Drive("clk", 0);
        engine.Settle(80);
        Assert.Equal(0, ReadLane(engine, "pc", 4));
        Assert.Equal(0x10, ReadLane(engine, "ir", 8));
        Assert.Empty(engine.RuntimeDiagnostics);
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

    private static void DriveOperation(SimulationEngine engine, int op0, int op1, int value)
    {
        engine.Drive("op0", op0);
        engine.Drive("op1", op1);
        for (int bit = 0; bit < 4; bit++)
        {
            engine.Drive($"d{bit}", (value >> bit) & 1);
        }

        engine.Settle(40);
    }

    private static void PulseClock(SimulationEngine engine)
    {
        engine.Drive("clk", 1);
        engine.Settle(40);
        engine.Drive("clk", 0);
        engine.Settle(40);
    }

    private static int ReadAccumulator(SimulationEngine engine)
    {
        int value = 0;
        for (int bit = 0; bit < 4; bit++)
        {
            value |= engine.Read($"q{bit}") << bit;
        }

        return value;
    }

    private static int ReadLane(SimulationEngine engine, string prefix, int bits)
    {
        int value = 0;
        for (int bit = 0; bit < bits; bit++)
        {
            value |= engine.Read($"{prefix}{bit}") << bit;
        }

        return value;
    }
}
