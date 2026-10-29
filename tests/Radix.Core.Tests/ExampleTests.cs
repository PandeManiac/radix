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
    public void EightBitAluAddsAndSelectsLogicOperations()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateEightBitAluDemo(), "demo.alu-8");
        engine.Drive("op0", 0);
        engine.Drive("op1", 0);
        DriveByte(engine, "a", 200);
        DriveByte(engine, "b", 100);
        engine.Settle(40);

        Assert.Equal(44, ReadByte(engine, "y"));
        Assert.Equal(1, engine.Read("carry"));
        Assert.Equal(0, engine.Read("zero"));

        engine.Drive("op0", 1);
        engine.Settle(40);
        Assert.Equal(64, ReadByte(engine, "y"));
        Assert.Equal(0, engine.Read("carry"));

        engine.Drive("op0", 0);
        engine.Drive("op1", 1);
        engine.Settle(40);
        Assert.Equal(236, ReadByte(engine, "y"));

        engine.Drive("op0", 1);
        engine.Settle(40);
        Assert.Equal(172, ReadByte(engine, "y"));

        engine.Drive("op0", 0);
        engine.Drive("op1", 0);
        DriveByte(engine, "a", 0);
        DriveByte(engine, "b", 0);
        engine.Settle(40);
        Assert.Equal(0, ReadByte(engine, "y"));
        Assert.Equal(0, engine.Read("carry"));
        Assert.Equal(1, engine.Read("zero"));
    }

    [Fact]
    public void MultiplierExampleCarriesNumericLanesAndComputes()
    {
        CircuitProject project = ExampleProjects.CreateMultiplierDemo();
        CircuitDefinition root = project.RequireDefinition("demo.mult-4");
        Assert.Equal(["A", "B", "P"], root.NumericLanes.Select(lane => lane.Name).ToArray());
        Assert.Equal(4, root.NumericLanes[0].PinIds.Count);
        Assert.Equal(8, root.NumericLanes[2].PinIds.Count);

        var engine = TestFixtures.Simulate(project, "demo.mult-4");
        for (int bit = 0; bit < 4; bit++)
        {
            engine.Drive($"a{bit}", (9 >> bit) & 1);
            engine.Drive($"b{bit}", (7 >> bit) & 1);
        }

        engine.Settle(80);
        Assert.Equal(63, ReadByte(engine, "p"));
        Assert.False(engine.IsHalted, engine.HaltReason);
    }

    private static void DriveByte(SimulationEngine engine, string prefix, int value)
    {
        for (int i = 0; i < 8; i++)
        {
            engine.Drive($"{prefix}{i}", (value >> i) & 1);
        }
    }

    private static int ReadByte(SimulationEngine engine, string prefix)
    {
        int value = 0;
        for (int i = 0; i < 8; i++)
        {
            value |= engine.Read($"{prefix}{i}") << i;
        }

        return value;
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
    public void TernaryMemoryExampleWritesAndReadsAllNineWords()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateTernaryMemoryDemo(), "demo.t-memory");
        engine.Drive("clk", 0);
        engine.Drive("we", 0);
        engine.AdvanceTo(40);
        Assert.Equal(0, engine.Read("q0") + engine.Read("q1"));

        int[] values = [1, 5, 0, 4, 8, 3, 7, 2, 6];
        for (int word = 0; word < 9; word++)
        {
            engine.Drive("addr0", word % 3);
            engine.Drive("addr1", word / 3);
            engine.Drive("d0", values[word] % 3);
            engine.Drive("d1", values[word] / 3);
            engine.Drive("we", 2);
            engine.AdvanceTo(engine.Now + 40);
            engine.Drive("clk", 2);
            engine.AdvanceTo(engine.Now + 40);
            engine.Drive("clk", 0);
            engine.AdvanceTo(engine.Now + 40);
        }

        engine.Drive("we", 0);
        for (int word = 0; word < 9; word++)
        {
            engine.Drive("addr0", word % 3);
            engine.Drive("addr1", word / 3);
            engine.AdvanceTo(engine.Now + 40);
            int actual = engine.Read("q0") + (engine.Read("q1") * 3);
            Assert.Equal(values[word], actual);
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void FourStateBusResolvesZValuesAndConflicts()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateFourStateBusDemo(), "demo.four-state-bus");
        engine.Drive("ena", 0);
        engine.Drive("enb", 0);
        engine.AdvanceTo(40);
        Assert.Equal(3, engine.Read("bus"));
        Assert.Equal(0, engine.Read("alert"));
        Assert.Empty(engine.RuntimeDiagnostics);

        engine.Drive("a", 0);
        engine.Drive("ena", 1);
        engine.AdvanceTo(engine.Now + 20);
        Assert.Equal(0, engine.Read("bus"));
        Assert.Equal(0, engine.Read("alert"));

        // Two different driven values tie on priority and resolve to X.
        engine.Drive("b", 1);
        engine.Drive("enb", 1);
        engine.AdvanceTo(engine.Now + 20);
        Assert.Equal(2, engine.Read("bus"));
        Assert.Equal(1, engine.Read("alert"));
        Assert.Contains(
            engine.RuntimeDiagnostics,
            diagnostic => diagnostic.Code == SimulationEngine.DriverConflictCode);

        // Agreement is not a conflict.
        engine.Drive("b", 0);
        engine.AdvanceTo(engine.Now + 20);
        Assert.Equal(0, engine.Read("bus"));
        Assert.Equal(0, engine.Read("alert"));

        // Z yields to the other driver.
        engine.Drive("enb", 0);
        engine.Drive("b", 2);
        engine.Drive("a", 2);
        engine.AdvanceTo(engine.Now + 20);
        Assert.Equal(2, engine.Read("bus"));
        Assert.Equal(1, engine.Read("alert"));

        engine.Drive("ena", 0);
        engine.Drive("enb", 1);
        engine.Drive("b", 1);
        engine.AdvanceTo(engine.Now + 20);
        Assert.Equal(1, engine.Read("bus"));
        Assert.Equal(0, engine.Read("alert"));
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
    public void QuaternaryAccumulatorLoadsAddsHoldsAndWraps()
    {
        CircuitProject project = ExampleProjects.CreateQuaternaryAccumulatorDemo();
        CircuitDefinition root = project.RequireDefinition("demo.q4-accumulator");
        Assert.Equal(["Data", "Result"], root.NumericLanes.Select(lane => lane.Name).ToArray());

        var engine = TestFixtures.Simulate(project, "demo.q4-accumulator");
        engine.Drive("clk", 0);
        engine.Drive("load", 0);
        engine.Drive("add", 0);
        engine.Drive("d0", 0);
        engine.Drive("d1", 0);
        engine.Settle(100);
        Assert.Equal(0, ReadQuaternaryPair(engine, "q"));

        engine.Drive("d0", 1);
        engine.Drive("d1", 2);
        engine.Drive("load", 3);
        PulseQuaternaryClock(engine);
        Assert.Equal(9, ReadQuaternaryPair(engine, "q"));

        engine.Drive("load", 0);
        engine.Drive("d0", 3);
        engine.Drive("d1", 3);
        engine.Drive("add", 3);
        engine.Settle(40);
        Assert.Equal(9, ReadQuaternaryPair(engine, "q"));
        Assert.Equal(1, engine.Read("carry"));
        PulseQuaternaryClock(engine);
        Assert.Equal(8, ReadQuaternaryPair(engine, "q"));

        engine.Drive("add", 0);
        engine.Drive("d0", 0);
        engine.Drive("d1", 0);
        PulseQuaternaryClock(engine);
        Assert.Equal(8, ReadQuaternaryPair(engine, "q"));

        engine.Drive("d0", 0);
        engine.Drive("d1", 0);
        engine.Drive("load", 3);
        PulseQuaternaryClock(engine);
        Assert.Equal(0, ReadQuaternaryPair(engine, "q"));

        engine.Drive("load", 0);
        engine.Drive("d0", 2);
        engine.Drive("d1", 1);
        engine.Drive("add", 3);
        PulseQuaternaryClock(engine);
        Assert.Equal(6, ReadQuaternaryPair(engine, "q"));

        engine.Drive("d0", 0);
        engine.Drive("d1", 3);
        engine.Settle(40);
        Assert.Equal(1, engine.Read("carry"));
        PulseQuaternaryClock(engine);
        Assert.Equal(2, ReadQuaternaryPair(engine, "q"));

        engine.Drive("load", 3);
        engine.Drive("d0", 1);
        engine.Drive("d1", 1);
        PulseQuaternaryClock(engine);
        Assert.Equal(5, ReadQuaternaryPair(engine, "q"));
        Assert.True(engine.Read("q0") == 1 && engine.Read("q1") == 1);

        engine.Drive("load", 0);
        engine.Drive("add", 3);
        engine.Drive("d0", 0);
        engine.Drive("d1", 0);
        engine.Settle(40);
        Assert.Equal(5, ReadQuaternaryPair(engine, "q"));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void QuaternaryMemoryWritesReadsAndHoldsEveryWord()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateQuaternaryMemoryDemo(), "demo.q4-memory");
        engine.Drive("clk", 0);
        engine.Drive("we", 0);
        engine.Drive("addr", 0);
        engine.Drive("d0", 0);
        engine.Drive("d1", 0);
        engine.Settle(60);
        Assert.Equal(0, ReadQuaternaryPair(engine, "q"));

        int[] values = [10, 3, 15, 7];
        for (int word = 0; word < 4; word++)
        {
            engine.Drive("addr", word);
            engine.Drive("d0", values[word] % 4);
            engine.Drive("d1", values[word] / 4);
            engine.Drive("we", 3);
            engine.Settle(60);
            engine.Drive("clk", 3);
            engine.Settle(80);
            engine.Drive("clk", 0);
            engine.Settle(60);
        }

        engine.Drive("we", 0);
        for (int word = 0; word < 4; word++)
        {
            engine.Drive("addr", word);
            engine.Settle(30);
            Assert.Equal(values[word], ReadQuaternaryPair(engine, "q"));
        }

        engine.Drive("addr", 0);
        engine.Drive("we", 2);
        engine.Drive("d0", 0);
        engine.Drive("d1", 0);
        engine.Settle(60);
        engine.Drive("clk", 3);
        engine.Settle(80);
        engine.Drive("clk", 0);
        engine.Settle(60);
        Assert.Equal(10, ReadQuaternaryPair(engine, "q"));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void QuaternaryMachineExecutesLoadAddStoreLoadmAndJump()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateQuaternaryMachineDemo(), "demo.q4-machine");
        engine.Drive("clk", 0);
        engine.Settle(120);
        Assert.Equal(0, ReadQuaternaryPair(engine, "pc"));
        Assert.Equal(0, ReadQuaternaryPair(engine, "q"));
        Assert.Equal(1, ReadQuaternaryPair(engine, "op"));
        Assert.Equal(5, ReadQuaternaryPair(engine, "arg"));
        Assert.Equal(0, ReadQuaternaryPair(engine, "read"));

        PulseQuaternaryClock(engine);
        Assert.Equal(1, ReadQuaternaryPair(engine, "pc"));
        Assert.Equal(5, ReadQuaternaryPair(engine, "q"));
        Assert.Equal(2, ReadQuaternaryPair(engine, "op"));
        Assert.Equal(3, ReadQuaternaryPair(engine, "arg"));

        PulseQuaternaryClock(engine);
        Assert.Equal(2, ReadQuaternaryPair(engine, "pc"));
        Assert.Equal(8, ReadQuaternaryPair(engine, "q"));
        Assert.Equal(4, ReadQuaternaryPair(engine, "op"));
        Assert.Equal(0, ReadQuaternaryPair(engine, "arg"));
        Assert.Equal(0, ReadQuaternaryPair(engine, "read"));

        PulseQuaternaryClock(engine);
        Assert.Equal(3, ReadQuaternaryPair(engine, "pc"));
        Assert.Equal(8, ReadQuaternaryPair(engine, "q"));
        Assert.Equal(5, ReadQuaternaryPair(engine, "op"));
        Assert.Equal(8, ReadQuaternaryPair(engine, "read"));

        PulseQuaternaryClock(engine);
        Assert.Equal(4, ReadQuaternaryPair(engine, "pc"));
        Assert.Equal(8, ReadQuaternaryPair(engine, "q"));
        Assert.Equal(3, ReadQuaternaryPair(engine, "op"));
        Assert.Equal(8, ReadQuaternaryPair(engine, "read"));

        PulseQuaternaryClock(engine);
        Assert.Equal(0, ReadQuaternaryPair(engine, "pc"));
        Assert.Equal(8, ReadQuaternaryPair(engine, "q"));
        Assert.Equal(1, ReadQuaternaryPair(engine, "op"));
        Assert.Equal(0, ReadQuaternaryPair(engine, "read"));

        PulseQuaternaryClock(engine);
        Assert.Equal(1, ReadQuaternaryPair(engine, "pc"));
        Assert.Equal(5, ReadQuaternaryPair(engine, "q"));
        Assert.Equal(2, ReadQuaternaryPair(engine, "op"));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TinyCpuFetchesAndExecutesItsShippedProgram()
    {
        var engine = TestFixtures.Simulate(ExampleProjects.CreateTinyCpuDemo(), "demo.tiny-cpu");
        engine.Drive("clk", 0);
        engine.Drive("reset", 0);
        engine.Settle(80);

        Assert.Equal(0, ReadLane(engine, "pc", 4));
        Assert.Equal(0x15, ReadLane(engine, "ir", 8));
        Assert.Equal(0, ReadLane(engine, "sp", 4));
        Assert.Equal(0, ReadLane(engine, "mem", 4));
        Assert.Equal(1, engine.Read("zero"));
        Assert.Equal(0, engine.Read("stack_error"));

        (int Pc, int Ir, int Acc, int Sp, int Mem)[] states =
        [
            (1, 0x38, 5, 0, 0),    // LDA 5 executed; STA 8 fetched
            (2, 0x19, 5, 0, 0),    // STA 8 stored 5
            (3, 0xA0, 9, 0, 0),    // LDA 9 executed; PUSH fetched
            (4, 0x10, 9, 1, 9),    // PUSH wrote 9 and advanced SP
            (5, 0xB0, 0, 1, 9),    // LDA 0 executed; stack top visible
            (6, 0x39, 9, 0, 0),    // POP restored 9 and rewound SP
            (7, 0xCC, 9, 0, 9),    // STA 9 stored 9; CALL 12 fetched
            (12, 0xE8, 9, 1, 5),   // CALL pushed return address 8
            (13, 0x99, 5, 1, 9),   // LDA 8 loaded RAM[8] = 5
            (14, 0xD0, 14, 1, 8),  // ADD@ 9 added 9; stack top is return address
            (8, 0x68, 14, 0, 5),   // RET returned to PC 8
            (8, 0x68, 14, 0, 5),   // JMP 8 loops
        ];

        foreach ((int pc, int instruction, int accumulator, int sp, int memory) in states)
        {
            PulseClock(engine);
            Assert.Equal(pc, ReadLane(engine, "pc", 4));
            Assert.Equal(instruction, ReadLane(engine, "ir", 8));
            Assert.Equal(accumulator, ReadLane(engine, "acc", 4));
            Assert.Equal(sp, ReadLane(engine, "sp", 4));
            Assert.Equal(memory, ReadLane(engine, "mem", 4));
            Assert.Equal(0, engine.Read("carry"));
            Assert.False(engine.IsHalted, engine.HaltReason);
        }

        Assert.Empty(engine.RuntimeDiagnostics);
        Assert.Equal(0, engine.Read("stack_error"));
    }

    [Fact]
    public void TinyCpuLoadsAndAddsMemoryDirectly()
    {
        int[] program =
        [
            0x15, // 0: LDA 5
            0x38, // 1: STA 8 (RAM[8] = 5)
            0xE8, // 2: LDA 8 direct -> 5
            0xF8, // 3: ADD 8 direct -> 10
            0x44, // 4: JMP 4
            0x44, // 5: halt loop
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        var engine = TestFixtures.Simulate(ExampleProjects.CreateTinyCpuDemo(program), "demo.tiny-cpu");
        engine.Drive("clk", 0);
        engine.Drive("reset", 0);
        engine.Settle(80);

        PulseClock(engine);
        PulseClock(engine);
        Assert.Equal(2, ReadLane(engine, "pc", 4));

        PulseClock(engine);
        Assert.Equal(5, ReadLane(engine, "acc", 4));
        Assert.Equal(0xF8, ReadLane(engine, "ir", 8));

        PulseClock(engine);
        Assert.Equal(10, ReadLane(engine, "acc", 4));
        Assert.Equal(4, ReadLane(engine, "pc", 4));
        Assert.Equal(0, engine.Read("stack_error"));
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TinyCpuFlagsStackUnderflowAndClearsItWithReset()
    {
        int[] program = Enumerable.Repeat(0xB0, 16).ToArray(); // POP at SP=0
        var engine = TestFixtures.Simulate(ExampleProjects.CreateTinyCpuDemo(program), "demo.tiny-cpu");
        engine.Drive("clk", 0);
        engine.Drive("reset", 0);
        engine.Settle(80);
        Assert.Equal(0, engine.Read("stack_error"));

        PulseClock(engine);
        Assert.Equal(15, ReadLane(engine, "sp", 4));
        Assert.Equal(1, engine.Read("stack_error"));

        engine.Drive("reset", 1);
        engine.Settle(40);
        PulseClock(engine);
        Assert.Equal(0, ReadLane(engine, "sp", 4));
        Assert.Equal(0, engine.Read("stack_error"));
    }

    [Fact]
    public void TinyCpuFlagsStackOverflowAfterSixteenPushes()
    {
        int[] program = Enumerable.Repeat(0xA0, 16).ToArray(); // PUSH repeatedly
        var engine = TestFixtures.Simulate(ExampleProjects.CreateTinyCpuDemo(program), "demo.tiny-cpu");
        engine.Drive("clk", 0);
        engine.Drive("reset", 0);
        engine.Settle(80);

        for (int i = 0; i < 15; i++)
        {
            PulseClock(engine);
        }

        Assert.Equal(15, ReadLane(engine, "sp", 4));
        Assert.Equal(0, engine.Read("stack_error"));

        PulseClock(engine);
        Assert.Equal(0, ReadLane(engine, "sp", 4));
        Assert.Equal(1, engine.Read("stack_error"));
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
        Assert.Equal(0, ReadLane(engine, "sp", 4));
        Assert.Equal(0, engine.Read("carry"));

        engine.Drive("reset", 0);
        engine.Drive("clk", 0);
        engine.Settle(80);
        Assert.Equal(0, ReadLane(engine, "pc", 4));
        Assert.Equal(0x10, ReadLane(engine, "ir", 8));
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TinyCpuNestedCallsUseTheStack()
    {
        int[] program =
        [
            0xC8, // 0: CALL 8 (push 1)
            0x61, // 1: JMP 1 (halt)
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xCC, // 8: CALL 12 (push 9)
            0xD0, // 9: RET to 1
            0x00, 0x00,
            0xD0, // 12: RET to 9
            0x00, 0x00, 0x00,
        ];
        var engine = TestFixtures.Simulate(ExampleProjects.CreateTinyCpuDemo(program), "demo.tiny-cpu");
        engine.Drive("clk", 0);
        engine.Drive("reset", 0);
        engine.Settle(80);

        PulseClock(engine);
        Assert.Equal(8, ReadLane(engine, "pc", 4));
        Assert.Equal(1, ReadLane(engine, "sp", 4));

        PulseClock(engine);
        Assert.Equal(12, ReadLane(engine, "pc", 4));
        Assert.Equal(2, ReadLane(engine, "sp", 4));

        PulseClock(engine);
        Assert.Equal(9, ReadLane(engine, "pc", 4));
        Assert.Equal(1, ReadLane(engine, "sp", 4));

        PulseClock(engine);
        Assert.Equal(1, ReadLane(engine, "pc", 4));
        Assert.Equal(0, ReadLane(engine, "sp", 4));

        PulseClock(engine);
        Assert.Equal(1, ReadLane(engine, "pc", 4));
        Assert.Equal(0, ReadLane(engine, "sp", 4));
        Assert.False(engine.IsHalted, engine.HaltReason);
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

    private static void PulseQuaternaryClock(SimulationEngine engine)
    {
        // Let the selected data and the write-enable decoder settle while the clock is
        // low; the flip-flop's master latch must see the new D before the rising edge.
        engine.Settle(80);
        engine.Drive("clk", 3);
        engine.Settle(100);
        engine.Drive("clk", 0);
        engine.Settle(100);
    }

    private static int ReadQuaternaryPair(SimulationEngine engine, string prefix) =>
        engine.Read($"{prefix}0") + (4 * engine.Read($"{prefix}1"));

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
