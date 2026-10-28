using Radix.Core.Compilation;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class StdLibTests
{
    private static SimulationEngine Engine(string definitionId, CircuitProject? project = null)
    {
        CircuitProject p = project ?? TestFixtures.WithStandardLibrary();
        return TestFixtures.Simulate(p, definitionId);
    }

    private static void Settle(SimulationEngine engine, long ticks = 20) => engine.AdvanceTo(engine.Now + ticks);

    [Theory]
    [InlineData("std.not", 1, new[] { 1, 0 })]
    [InlineData("std.buf", 1, new[] { 0, 1 })]
    [InlineData("std.and", 2, new[] { 0, 0, 0, 1 })]
    [InlineData("std.or", 2, new[] { 0, 1, 1, 1 })]
    [InlineData("std.nand", 2, new[] { 1, 1, 1, 0 })]
    [InlineData("std.nor", 2, new[] { 1, 0, 0, 0 })]
    [InlineData("std.xor", 2, new[] { 0, 1, 1, 0 })]
    [InlineData("std.xnor", 2, new[] { 1, 0, 0, 1 })]
    public void BinaryGateMatchesTruthTable(string definitionId, int inputs, int[] table)
    {
        SimulationEngine engine = Engine(definitionId);
        int combinations = 1 << inputs;
        for (int index = 0; index < combinations; index++)
        {
            int a = (index >> (inputs - 1)) & 1;
            int b = inputs == 2 ? index & 1 : 0;
            engine.Drive("a", a);
            if (inputs == 2)
            {
                engine.Drive("b", b);
            }

            Settle(engine, 10);
            Assert.Equal(table[index], engine.Read("y"));
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
    }

    [Fact]
    public void GatedDLatchIsTransparentThenHolds()
    {
        SimulationEngine engine = Engine("std.d-latch");
        engine.Drive("e", 0);
        Settle(engine);
        Assert.Equal(0, engine.Read("q"));
        Assert.Equal(1, engine.Read("qn"));

        engine.Drive("e", 1);
        engine.Drive("d", 1);
        Settle(engine);
        Assert.Equal(1, engine.Read("q"));

        engine.Drive("d", 0);
        Settle(engine);
        Assert.Equal(0, engine.Read("q"));

        engine.Drive("e", 0);
        engine.Drive("d", 1);
        Settle(engine);
        Assert.Equal(0, engine.Read("q"));

        engine.Drive("e", 1);
        Settle(engine);
        Assert.Equal(1, engine.Read("q"));
        Assert.False(engine.IsHalted, engine.HaltReason);
    }

    [Fact]
    public void DFlipFlopIsStructuralAndNineGatesDeep()
    {
        CircuitProject project = TestFixtures.WithStandardLibrary();
        CircuitDefinition dff = project.RequireDefinition(StandardLibrary.DFlipFlopId);
        Assert.Equal([StandardLibrary.NotId, "std.d-latch", "std.d-latch"], dff.Instances.Select(i => i.DefinitionId));

        CompiledCircuit compiled = CircuitCompiler.Compile(project, StandardLibrary.DFlipFlopId);
        Assert.Equal(9, compiled.Components.Count);
    }

    [Fact]
    public void DFlipFlopIgnoresDataWhileClockHigh()
    {
        SimulationEngine engine = Engine(StandardLibrary.DFlipFlopId);
        engine.Drive("clk", 1);
        engine.Drive("d", 1);
        Settle(engine);
        engine.Drive("d", 0);
        Settle(engine);
        Assert.Equal(0, engine.Read("q"));

        engine.Drive("clk", 0);
        Settle(engine);
        engine.Drive("d", 1);
        Settle(engine);
        engine.Drive("clk", 1);
        Settle(engine);
        Assert.Equal(1, engine.Read("q"));

        engine.Drive("d", 0);
        Settle(engine);
        Assert.Equal(1, engine.Read("q"));
    }

    [Fact]
    public void HalfAdderCoversAllInputs()
    {
        SimulationEngine engine = Engine(StandardLibrary.HalfAdderId);
        for (int index = 0; index < 4; index++)
        {
            int a = index >> 1;
            int b = index & 1;
            engine.Drive("a", a);
            engine.Drive("b", b);
            Settle(engine, 10);
            Assert.Equal(a ^ b, engine.Read("s"));
            Assert.Equal(a & b, engine.Read("c"));
        }
    }

    [Fact]
    public void FullAdderCoversAllInputs()
    {
        SimulationEngine engine = Engine("std.full-adder");
        for (int index = 0; index < 8; index++)
        {
            int a = (index >> 2) & 1;
            int b = (index >> 1) & 1;
            int cin = index & 1;
            engine.Drive("a", a);
            engine.Drive("b", b);
            engine.Drive("cin", cin);
            Settle(engine, 20);
            int sum = a + b + cin;
            Assert.Equal(sum & 1, engine.Read("s"));
            Assert.Equal(sum >> 1, engine.Read("cout"));
        }
    }

    [Fact]
    public void RippleCarryAdderAddsAllFourBitPairs()
    {
        SimulationEngine engine = Engine("std.ripple-adder-4");
        for (int a = 0; a < 16; a++)
        {
            for (int b = 0; b < 16; b++)
            {
                for (int bit = 0; bit < 4; bit++)
                {
                    engine.Drive($"a{bit}", (a >> bit) & 1);
                    engine.Drive($"b{bit}", (b >> bit) & 1);
                }

                Settle(engine, 30);
                int sum = 0;
                for (int bit = 0; bit < 4; bit++)
                {
                    sum |= engine.Read($"s{bit}") << bit;
                }

                Assert.Equal((a + b) & 0xF, sum);
                Assert.Equal((a + b) >> 4, engine.Read("cout"));
            }
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
    }

    [Fact]
    public void FourBitMultiplierComputesEveryProduct()
    {
        SimulationEngine engine = Engine(StandardLibrary.Multiplier4Id);
        for (int a = 0; a < 16; a++)
        {
            for (int b = 0; b < 16; b++)
            {
                for (int bit = 0; bit < 4; bit++)
                {
                    engine.Drive($"a{bit}", (a >> bit) & 1);
                    engine.Drive($"b{bit}", (b >> bit) & 1);
                }

                Settle(engine, 80);
                Assert.Equal(a * b, ReadBinaryByte(engine, "p"));
            }
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
    }

    [Fact]
    public void MultiplexerSelectsInput()
    {
        SimulationEngine engine = Engine("std.mux2");
        for (int select = 0; select < 2; select++)
        {
            for (int a = 0; a < 2; a++)
            {
                for (int b = 0; b < 2; b++)
                {
                    engine.Drive("a", a);
                    engine.Drive("b", b);
                    engine.Drive("s", select);
                    Settle(engine, 10);
                    Assert.Equal(select == 0 ? a : b, engine.Read("y"));
                }
            }
        }
    }

    [Theory]
    [InlineData("std.t-not", new[] { 0, 2, 1 })]
    public void TernaryNotIsThreeValued(string definitionId, int[] mapping)
    {
        SimulationEngine engine = Engine(definitionId);
        for (int value = 0; value < 3; value++)
        {
            engine.Drive("a", value);
            Settle(engine, 10);
            Assert.Equal(mapping[value], engine.Read("y"));
        }
    }

    [Fact]
    public void TernaryMinAndMaxCoverNineCombinations()
    {
        SimulationEngine min = Engine("std.t-min");
        SimulationEngine max = Engine("std.t-max");
        for (int a = 0; a < 3; a++)
        {
            for (int b = 0; b < 3; b++)
            {
                min.Drive("a", a);
                min.Drive("b", b);
                max.Drive("a", a);
                max.Drive("b", b);
                Settle(min, 10);
                Settle(max, 10);
                Assert.Equal(Math.Min(a, b), min.Read("y"));
                Assert.Equal(Math.Max(a, b), max.Read("y"));
            }
        }
    }

    [Fact]
    public void BalancedTernaryNegationUsesThreeValues()
    {
        SimulationEngine engine = Engine("std.bt-not");
        int[] expected = [2, 1, 0];
        for (int value = 0; value < 3; value++)
        {
            engine.Drive("a", value);
            Settle(engine, 10);
            Assert.Equal(expected[value], engine.Read("y"));
        }
    }

    [Fact]
    public void BalancedTernaryHalfAdderProducesCorrectTritSum()
    {
        SimulationEngine engine = Engine("std.bt-half-adder");
        for (int a = 0; a < 3; a++)
        {
            for (int b = 0; b < 3; b++)
            {
                engine.Drive("a", a);
                engine.Drive("b", b);
                Settle(engine, 20);

                int s = engine.Read("s");
                int c = engine.Read("c");
                Assert.InRange(s, 0, 2);
                Assert.InRange(c, 0, 2);

                NumericDecodeResult decoded = NumericFormats.BalancedTernary.Decode([s, c]);
                Assert.True(decoded.IsValid);
                Assert.Equal((a - 1) + (b - 1), (int)decoded.Value);
            }
        }

        Assert.Contains(engine.Trace, change => change.NewValue == 2);
    }

    [Fact]
    public void BalancedTernaryFullAdderCoversAllTritTriples()
    {
        SimulationEngine engine = Engine(StandardLibrary.BalancedTernaryFullAdderId);
        for (int a = 0; a < 3; a++)
        {
            for (int b = 0; b < 3; b++)
            {
                for (int cin = 0; cin < 3; cin++)
                {
                    engine.Drive("a", a);
                    engine.Drive("b", b);
                    engine.Drive("cin", cin);
                    Settle(engine, 20);

                    int s = engine.Read("s");
                    int cout = engine.Read("cout");
                    Assert.InRange(s, 0, 2);
                    Assert.InRange(cout, 0, 2);

                    NumericDecodeResult decoded = NumericFormats.BalancedTernary.Decode([s, cout]);
                    Assert.True(decoded.IsValid);
                    Assert.Equal((a - 1) + (b - 1) + (cin - 1), (int)decoded.Value);
                }
            }
        }
    }

    [Fact]
    public void BalancedTernaryAdder2AddsEveryTwoTritPair()
    {
        SimulationEngine engine = Engine(StandardLibrary.BalancedTernaryAdder2Id);
        for (int ai = 0; ai < 3; ai++)
        {
            for (int ah = 0; ah < 3; ah++)
            {
                for (int bi = 0; bi < 3; bi++)
                {
                    for (int bh = 0; bh < 3; bh++)
                    {
                        engine.Drive("a0", ai);
                        engine.Drive("a1", ah);
                        engine.Drive("b0", bi);
                        engine.Drive("b1", bh);
                        Settle(engine, 30);

                        int expected = ((ai - 1) + (3 * (ah - 1))) + ((bi - 1) + (3 * (bh - 1)));
                        NumericDecodeResult decoded = NumericFormats.BalancedTernary.Decode(
                            [engine.Read("s0"), engine.Read("s1"), engine.Read("cout")]);

                        Assert.True(decoded.IsValid);
                        Assert.Equal(expected, (int)decoded.Value);
                    }
                }
            }
        }
    }

    [Fact]
    public void FourBitCounterCountsAndWraps()
    {
        CircuitProject project = ExampleProjects.CreateCounterDemo();
        SimulationEngine engine = Engine("demo.counter", project);

        engine.AdvanceTo(200);
        Assert.Equal(10, ReadBus(engine, "q0", "q1", "q2", "q3"));

        engine.AdvanceTo(300);
        Assert.Equal(15, ReadBus(engine, "q0", "q1", "q2", "q3"));

        engine.AdvanceTo(320);
        Assert.Equal(0, ReadBus(engine, "q0", "q1", "q2", "q3"));
        Assert.False(engine.IsHalted, engine.HaltReason);
    }

    [Fact]
    public void RamWritesOnClockAndReadsAsynchronously()
    {
        SimulationEngine engine = Engine(StandardLibrary.Ram16x4Id);
        engine.Drive("clk", 0);
        engine.Drive("we", 0);
        DriveWord(engine, "addr", 3);
        DriveWord(engine, "d", 5);
        Settle(engine, 80);
        Assert.Equal(0, ReadBus(engine, "q0", "q1", "q2", "q3"));

        engine.Drive("we", 1);
        Settle(engine, 80);
        Assert.Equal(0, ReadBus(engine, "q0", "q1", "q2", "q3"));

        PulseClock(engine);
        Assert.Equal(5, ReadBus(engine, "q0", "q1", "q2", "q3"));

        DriveWord(engine, "addr", 9);
        DriveWord(engine, "d", 10);
        Settle(engine, 60);
        PulseClock(engine);
        Assert.Equal(10, ReadBus(engine, "q0", "q1", "q2", "q3"));

        // A write to another address leaves the first word intact.
        DriveWord(engine, "addr", 3);
        Settle(engine, 60);
        Assert.Equal(5, ReadBus(engine, "q0", "q1", "q2", "q3"));

        // WE low means the clock edge only advances other state.
        DriveWord(engine, "d", 2);
        engine.Drive("we", 0);
        Settle(engine, 60);
        PulseClock(engine);
        Assert.Equal(5, ReadBus(engine, "q0", "q1", "q2", "q3"));
        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryMuxSelectsOnTheSelectTrit()
    {
        SimulationEngine engine = Engine("std.t-mux");
        for (int a = 0; a < 3; a++)
        {
            for (int b = 0; b < 3; b++)
            {
                for (int s = 0; s < 3; s++)
                {
                    engine.Drive("a", a);
                    engine.Drive("b", b);
                    engine.Drive("s", s);
                    Settle(engine, 20);

                    int expected = s == 0 ? a : s == 2 ? b : 1;
                    Assert.Equal(expected, engine.Read("y"));
                }
            }
        }
    }

    [Fact]
    public void TernaryMux3SelectsEveryInput()
    {
        SimulationEngine engine = Engine(StandardLibrary.TernaryMux3Id);
        for (int a = 0; a < 3; a++)
        {
            for (int b = 0; b < 3; b++)
            {
                for (int c = 0; c < 3; c++)
                {
                    for (int s = 0; s < 3; s++)
                    {
                        engine.Drive("a", a);
                        engine.Drive("b", b);
                        engine.Drive("c", c);
                        engine.Drive("s", s);
                        Settle(engine, 20);

                        int expected = s == 0 ? a : s == 1 ? b : c;
                        Assert.Equal(expected, engine.Read("y"));
                    }
                }
            }
        }
    }

    [Fact]
    public void TernaryRam9x2WritesEveryWordAndHoldsTheOthers()
    {
        SimulationEngine engine = Engine(StandardLibrary.TernaryRam9x2Id);
        engine.Drive("clk", 0);
        engine.Drive("we", 0);
        engine.Drive("addr0", 0);
        engine.Drive("addr1", 0);
        Settle(engine, 80);
        Assert.Equal(0, ReadTernaryPair(engine, "q"));

        int[] values = [1, 5, 0, 4, 8, 3, 7, 2, 6];
        for (int word = 0; word < 9; word++)
        {
            engine.Drive("addr0", word % 3);
            engine.Drive("addr1", word / 3);
            LoadTernaryPair(engine, "d", values[word]);
            engine.Drive("we", 2);
            PulseTernaryClock(engine, settle: 100);
            Assert.Equal(values[word], ReadTernaryPair(engine, "q"));
        }

        engine.Drive("we", 0);
        for (int word = 0; word < 9; word++)
        {
            engine.Drive("addr0", word % 3);
            engine.Drive("addr1", word / 3);
            Settle(engine, 60);
            Assert.Equal(values[word], ReadTernaryPair(engine, "q"));
        }

        // WE=0 and the unknown trit both hold every word while D changes.
        LoadTernaryPair(engine, "d", 4);
        PulseTernaryClock(engine, settle: 100);
        Assert.Equal(values[8], ReadTernaryPair(engine, "q"));

        engine.Drive("we", 1);
        LoadTernaryPair(engine, "d", 7);
        PulseTernaryClock(engine, settle: 100);
        Assert.Equal(values[8], ReadTernaryPair(engine, "q"));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryMachineExecutesItsLookupProgram()
    {
        CircuitProject project = ExampleProjects.CreateTernaryMachineDemo();
        SimulationEngine engine = Engine("demo.t-machine", project);
        engine.Drive("clk", 0);
        Settle(engine, 120);

        Assert.Equal(0, ReadTernaryPair(engine, "pc"));
        Assert.Equal(0, ReadTernaryPair(engine, "q"));
        Assert.Equal(1, engine.Read("op"));
        Assert.Equal(1, engine.Read("arg"));

        (int Pc, int Accumulator)[] expected =
        [
            (1, 1), (2, 3), (3, 5), (4, 5), (5, 2),
            (6, 4), (7, 5), (8, 5), (0, 5), (1, 1),
        ];

        foreach ((int pc, int accumulator) in expected)
        {
            PulseTernaryClock(engine, settle: 120);
            Assert.Equal(pc, ReadTernaryPair(engine, "pc"));
            Assert.Equal(accumulator, ReadTernaryPair(engine, "q"));
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void EightBitRegisterLoadsHoldsAndClears()
    {
        SimulationEngine engine = Engine(StandardLibrary.Register8Id);
        engine.Drive("clk", 0);
        engine.Drive("clr", 0);
        engine.Drive("load", 1);
        DriveBinaryByte(engine, "d", 0xAB);
        Settle(engine, 40);
        engine.Drive("clk", 1);
        Settle(engine, 40);

        Assert.Equal(0xAB, ReadBinaryByte(engine, "q"));

        engine.Drive("clk", 0);
        engine.Drive("load", 0);
        DriveBinaryByte(engine, "d", 0x12);
        Settle(engine, 40);
        engine.Drive("clk", 1);
        Settle(engine, 40);
        Assert.Equal(0xAB, ReadBinaryByte(engine, "q"));

        engine.Drive("clk", 0);
        engine.Drive("clr", 1);
        Settle(engine, 40);
        engine.Drive("clk", 1);
        Settle(engine, 40);
        Assert.Equal(0, ReadBinaryByte(engine, "q"));
    }

    private static void DriveBinaryByte(SimulationEngine engine, string prefix, int value)
    {
        for (int i = 0; i < 8; i++)
        {
            engine.Drive($"{prefix}{i}", (value >> i) & 1);
        }
    }

    private static int ReadBinaryByte(SimulationEngine engine, string prefix)
    {
        int value = 0;
        for (int i = 0; i < 8; i++)
        {
            value |= engine.Read($"{prefix}{i}") << i;
        }

        return value;
    }

    [Fact]
    public void BalancedTernaryRegisterLoadsOnTheEdgeAndHoldsWhenDisabled()
    {
        SimulationEngine engine = Engine(StandardLibrary.BalancedTernaryRegister2Id);
        engine.Drive("clk", 0);
        engine.Drive("load", 2);

        for (int value = 0; value < 9; value++)
        {
            LoadTernaryPair(engine, "d", value);
            PulseTernaryClock(engine, settle: 120);
            Assert.Equal(value, ReadTernaryPair(engine, "q"));
        }

        engine.Drive("load", 0);
        LoadTernaryPair(engine, "d", 4);
        PulseTernaryClock(engine, settle: 120);
        Assert.Equal(8, ReadTernaryPair(engine, "q"));

        engine.Drive("load", 2);
        PulseTernaryClock(engine, settle: 120);
        Assert.Equal(4, ReadTernaryPair(engine, "q"));
    }

    [Fact]
    public void BalancedTernaryAccumulatorLoadsAddsHoldsAndWraps()
    {
        CircuitProject project = ExampleProjects.CreateBalancedTernaryAccumulatorDemo();
        Assert.Contains(
            project.RequireDefinition("demo.bt-accumulator").Instances,
            instance => instance.DefinitionId == StandardLibrary.BalancedTernaryRegister2Id);
        SimulationEngine engine = Engine("demo.bt-accumulator", project);
        engine.Drive("clk", 0);
        engine.Drive("load", 0);
        engine.Drive("add", 0);
        Settle(engine, 120);
        Assert.Equal(0, ReadBalancedPair(engine, "q"));

        // LOAD +4.
        engine.Drive("d0", 2);
        engine.Drive("d1", 2);
        engine.Drive("load", 2);
        PulseTernaryClock(engine, settle: 120);
        Assert.Equal(4, ReadBalancedPair(engine, "q"));

        // HOLD keeps +4 even with new data on the pins.
        engine.Drive("load", 0);
        engine.Drive("d0", 0);
        engine.Drive("d1", 0);
        PulseTernaryClock(engine, settle: 120);
        Assert.Equal(4, ReadBalancedPair(engine, "q"));

        // ADD +4 wraps out of the top of the 2-trit range to -1.
        engine.Drive("add", 2);
        engine.Drive("d0", 2);
        engine.Drive("d1", 2);
        PulseTernaryClock(engine, settle: 120);
        Assert.Equal(-1, ReadBalancedPair(engine, "q"));

        // ADD +2 gives 1.
        engine.Drive("d0", 0);
        engine.Drive("d1", 2);
        PulseTernaryClock(engine, settle: 120);
        Assert.Equal(1, ReadBalancedPair(engine, "q"));

        // HOLD with ADD back at - keeps 1.
        engine.Drive("add", 0);
        engine.Drive("d0", 1);
        engine.Drive("d1", 1);
        PulseTernaryClock(engine, settle: 120);
        Assert.Equal(1, ReadBalancedPair(engine, "q"));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryRamWritesAddressedWordsAndHoldsTheOthers()
    {
        SimulationEngine engine = Engine(StandardLibrary.TernaryRam3x2Id);
        engine.Drive("clk", 0);
        engine.Drive("we", 0);
        engine.Drive("addr", 0);
        Settle(engine, 80);
        Assert.Equal(0, ReadTernaryPair(engine, "q"));

        int[] values = [4, 7, 2];
        for (int word = 0; word < 3; word++)
        {
            engine.Drive("addr", word);
            LoadTernaryPair(engine, "d", values[word]);
            engine.Drive("we", 2);
            PulseTernaryClock(engine, settle: 100);
            Assert.Equal(values[word], ReadTernaryPair(engine, "q"));
        }

        engine.Drive("we", 0);
        for (int word = 0; word < 3; word++)
        {
            engine.Drive("addr", word);
            Settle(engine, 40);
            Assert.Equal(values[word], ReadTernaryPair(engine, "q"));
        }

        // WE=0 holds every word even while the data pins change.
        LoadTernaryPair(engine, "d", 1);
        PulseTernaryClock(engine, settle: 100);
        Assert.Equal(values[2], ReadTernaryPair(engine, "q"));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryBranchMachineBranchesStoresAndReadsMemory()
    {
        CircuitProject project = ExampleProjects.CreateTernaryBranchMachineDemo();
        Assert.Contains(
            project.RequireDefinition("demo.t-machine-2").Instances,
            instance => instance.DefinitionId == StandardLibrary.TernaryRam3x2Id);
        SimulationEngine engine = Engine("demo.t-machine-2", project);
        engine.Drive("clk", 0);
        Settle(engine, 120);

        Assert.Equal(0, ReadTernaryPair(engine, "pc"));
        Assert.Equal(0, ReadTernaryPair(engine, "q"));
        Assert.Equal(1, ReadTernaryPair(engine, "op"));
        Assert.Equal(2, ReadTernaryPair(engine, "arg"));
        Assert.Equal(0, ReadTernaryPair(engine, "read"));

        // Program: LOAD 2, ADD 2, STORE 1, BZ 8 (not taken), LOAD 0, STORE 2,
        // BZ 8 (taken, skipping the LOAD 1 at 7), LOADM 1, then PC wraps to 0.
        // The read port shows the word at the argument address of the fetched
        // instruction, so it exposes the stored value only while LOADM 1 is fetched.
        (int Pc, int Accumulator, int Read)[] expected =
        [
            (1, 2, 0), (2, 4, 0), (3, 4, 0), (4, 4, 0), (5, 0, 0),
            (6, 0, 0), (8, 0, 4), (0, 4, 0), (1, 2, 0),
        ];

        for (int step = 0; step < expected.Length; step++)
        {
            PulseTernaryClock(engine, settle: 120);
            (int pc, int accumulator, int read) = expected[step];
            Assert.Equal(pc, ReadTernaryPair(engine, "pc"));
            Assert.Equal(accumulator, ReadTernaryPair(engine, "q"));
            Assert.Equal(read, ReadTernaryPair(engine, "read"));
        }

        // The fetched instruction tracks the new PC immediately: PC 1 fetches ADD 2.
        Assert.Equal(2, ReadTernaryPair(engine, "op"));
        Assert.Equal(2, ReadTernaryPair(engine, "arg"));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryStackMachinePushesSwapsAddsDuplicatesAndPops()
    {
        CircuitProject project = ExampleProjects.CreateTernaryStackMachineDemo();
        Assert.Contains(
            project.RequireDefinition("demo.t-stack").Instances,
            instance => instance.DefinitionId == StandardLibrary.TernaryRam9x2Id);
        SimulationEngine engine = Engine("demo.t-stack", project);
        engine.Drive("clk", 0);
        Settle(engine, 120);

        Assert.Equal(0, ReadTernaryPair(engine, "pc"));
        Assert.Equal(0, ReadTernaryPair(engine, "top"));
        Assert.Equal(0, ReadTernaryPair(engine, "sp"));
        Assert.Equal(0, ReadTernaryPair(engine, "out"));
        Assert.Equal(1, ReadTernaryPair(engine, "op"));
        Assert.Equal(2, ReadTernaryPair(engine, "arg"));

        // PUSH 2, PUSH 3, SWAP, ADD, DUP, POP, OUT, POP, JMP 0. The stack RAM holds
        // old TOS values below the top register, so SWAP and ADD read the value written
        // by the preceding push, and DUP/POP exercise the same path without changing TOS.
        (int Pc, int Top, int Sp, int Out, int Read)[] expected =
        [
            (1, 2, 1, 0, 0), (2, 3, 2, 0, 2), (3, 2, 2, 0, 3), (4, 5, 1, 0, 3),
            (5, 5, 2, 0, 5), (6, 5, 1, 0, 0), (7, 5, 1, 5, 0), (8, 0, 0, 5, 0),
            (0, 0, 0, 5, 0),
        ];

        foreach ((int pc, int top, int sp, int @out, int read) in expected)
        {
            PulseTernaryClock(engine, settle: 120);
            Assert.Equal(pc, ReadTernaryPair(engine, "pc"));
            Assert.Equal(top, ReadTernaryPair(engine, "top"));
            Assert.Equal(sp, ReadTernaryPair(engine, "sp"));
            Assert.Equal(@out, ReadTernaryPair(engine, "out"));
            Assert.Equal(read, ReadTernaryPair(engine, "read"));
        }

        Assert.Equal(0, engine.Read("stack_error"));
        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryStackMachineBranchesOnlyWhenTheTopIsZero()
    {
        CircuitProject project = ExampleProjects.CreateTernaryStackMachineDemo();
        SetStackMachineProgram(
            project,
            [1, 8, 3, 6, 1, 8, 0, 0, 0],
            [0, 4, 0, 0, 3, 4, 0, 0, 0]);
        SimulationEngine engine = Engine("demo.t-stack", project);
        engine.Drive("clk", 0);
        Settle(engine, 120);

        // PUSH 0; BZ 4 (taken); PUSH 3; BZ 4 (not taken); then three NOPs.
        (int Pc, int Top, int Sp)[] expected =
        [
            (1, 0, 1), (4, 0, 1), (5, 3, 2), (6, 3, 2), (7, 3, 2), (8, 3, 2), (0, 3, 2),
        ];

        foreach ((int pc, int top, int sp) in expected)
        {
            PulseTernaryClock(engine, settle: 120);
            Assert.Equal(pc, ReadTernaryPair(engine, "pc"));
            Assert.Equal(top, ReadTernaryPair(engine, "top"));
            Assert.Equal(sp, ReadTernaryPair(engine, "sp"));
        }

        Assert.Equal(0, engine.Read("stack_error"));
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryStackMachineLatchesOverflowAndUnderflow()
    {
        CircuitProject overflowProject = ExampleProjects.CreateTernaryStackMachineDemo();
        SetStackMachineProgram(
            overflowProject,
            [1, 1, 1, 1, 1, 1, 1, 1, 1],
            [0, 0, 0, 0, 0, 0, 0, 0, 0]);
        SimulationEngine overflow = Engine("demo.t-stack", overflowProject);
        overflow.Drive("clk", 0);
        Settle(overflow, 120);

        for (int depth = 1; depth <= 9; depth++)
        {
            PulseTernaryClock(overflow, settle: 120);
            Assert.Equal(depth % 9, ReadTernaryPair(overflow, "sp"));
            Assert.Equal(depth < 9 ? 0 : 2, overflow.Read("stack_error"));
        }

        PulseTernaryClock(overflow, settle: 120);
        Assert.Equal(1, ReadTernaryPair(overflow, "sp"));
        Assert.Equal(2, overflow.Read("stack_error"));

        CircuitProject underflowProject = ExampleProjects.CreateTernaryStackMachineDemo();
        SetStackMachineProgram(
            underflowProject,
            [2, 2, 0, 0, 0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0, 0, 0, 0, 0]);
        SimulationEngine underflow = Engine("demo.t-stack", underflowProject);
        underflow.Drive("clk", 0);
        Settle(underflow, 120);

        PulseTernaryClock(underflow, settle: 120);
        Assert.Equal(8, ReadTernaryPair(underflow, "sp"));
        Assert.Equal(2, underflow.Read("stack_error"));

        PulseTernaryClock(underflow, settle: 120);
        Assert.Equal(7, ReadTernaryPair(underflow, "sp"));
        Assert.Equal(2, underflow.Read("stack_error"));
        Assert.Empty(underflow.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryMachinesAddBothArgumentTrits()
    {
        // ADD 3 exercises the high argument trit; the earlier wiring added only the low
        // digit, so this would silently produce 5 again instead of 8.
        CircuitProject callProject = ExampleProjects.CreateTernaryCallMachineDemo();
        SetCallMachineProgram(
            callProject,
            [1, 2, 0, 0, 0, 0, 0, 0, 0],
            [5, 3, 0, 0, 0, 0, 0, 0, 0]);
        SimulationEngine call = Engine("demo.t-machine-3", callProject);
        call.Drive("clk", 0);
        Settle(call, 120);
        PulseTernaryClock(call, settle: 120);
        Assert.Equal(5, ReadTernaryPair(call, "q"));
        PulseTernaryClock(call, settle: 120);
        Assert.Equal(8, ReadTernaryPair(call, "q"));
        Assert.Empty(call.RuntimeDiagnostics);

        CircuitProject branchProject = ExampleProjects.CreateTernaryBranchMachineDemo();
        SetMachineProgram(
            branchProject,
            "demo.t-machine-2",
            [1, 2, 0, 0, 0, 0, 0, 0, 0],
            [5, 3, 0, 0, 0, 0, 0, 0, 0]);
        SimulationEngine branch = Engine("demo.t-machine-2", branchProject);
        branch.Drive("clk", 0);
        Settle(branch, 120);
        PulseTernaryClock(branch, settle: 120);
        Assert.Equal(5, ReadTernaryPair(branch, "q"));
        PulseTernaryClock(branch, settle: 120);
        Assert.Equal(8, ReadTernaryPair(branch, "q"));
        Assert.Empty(branch.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryCallMachinePushesAndPopsNestedReturns()
    {
        CircuitProject project = ExampleProjects.CreateTernaryCallMachineDemo();
        Assert.Contains(
            project.RequireDefinition("demo.t-machine-3").Instances,
            instance => instance.DefinitionId == StandardLibrary.TernaryRam9x2Id);
        SimulationEngine engine = Engine("demo.t-machine-3", project);
        engine.Drive("clk", 0);
        Settle(engine, 120);

        Assert.Equal(0, ReadTernaryPair(engine, "pc"));
        Assert.Equal(0, ReadTernaryPair(engine, "q"));
        Assert.Equal(0, ReadTernaryPair(engine, "sp"));
        Assert.Equal(5, ReadTernaryPair(engine, "op"));
        Assert.Equal(4, ReadTernaryPair(engine, "arg"));
        Assert.Equal(0, ReadTernaryPair(engine, "read"));

        // CALL 4 pushes 1; CALL 7 pushes 5; RET returns to 5, LOAD 2, RET returns
        // to 1, then LOAD 1, ADD 2, and the JMP 3 loop. SP goes 0,1,2,1,0 and holds.
        (int Pc, int Accumulator, int StackPointer, int Read)[] expected =
        [
            (4, 0, 1, 0), (7, 0, 2, 0), (8, 0, 2, 5), (5, 0, 1, 5), (6, 2, 1, 1),
            (1, 2, 0, 1), (2, 1, 0, 1), (3, 3, 0, 1), (3, 3, 0, 1),
        ];

        foreach ((int pc, int accumulator, int stackPointer, int read) in expected)
        {
            PulseTernaryClock(engine, settle: 120);
            Assert.Equal(pc, ReadTernaryPair(engine, "pc"));
            Assert.Equal(accumulator, ReadTernaryPair(engine, "q"));
            Assert.Equal(stackPointer, ReadTernaryPair(engine, "sp"));
            Assert.Equal(read, ReadTernaryPair(engine, "read"));
        }

        Assert.Equal(0, engine.Read("stack_error"));
        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryCallMachineLatchesStackOverflowAndUnderflow()
    {
        // CALL 0 recurses forever, pushing PC+1 each time. SP walks 1..8 and the
        // ninth call writes the last word, wraps SP to 0, and raises the fault.
        CircuitProject overflowProject = ExampleProjects.CreateTernaryCallMachineDemo();
        SetCallMachineProgram(overflowProject, [5, 0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0, 0]);
        SimulationEngine overflow = Engine("demo.t-machine-3", overflowProject);
        overflow.Drive("clk", 0);
        Settle(overflow, 120);
        Assert.Equal(0, overflow.Read("stack_error"));

        for (int depth = 1; depth <= 9; depth++)
        {
            PulseTernaryClock(overflow, settle: 120);
            Assert.Equal(depth % 9, ReadTernaryPair(overflow, "sp"));
            Assert.Equal(depth < 9 ? 0 : 2, overflow.Read("stack_error"));
        }

        // The flag is latched: another recursive CALL does not clear it.
        PulseTernaryClock(overflow, settle: 120);
        Assert.Equal(1, ReadTernaryPair(overflow, "sp"));
        Assert.Equal(2, overflow.Read("stack_error"));
        Assert.Empty(overflow.RuntimeDiagnostics);

        CircuitProject underflowProject = ExampleProjects.CreateTernaryCallMachineDemo();
        SetCallMachineProgram(underflowProject, [6, 0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0, 0]);
        SimulationEngine underflow = Engine("demo.t-machine-3", underflowProject);
        underflow.Drive("clk", 0);
        Settle(underflow, 120);
        Assert.Equal(0, underflow.Read("stack_error"));

        PulseTernaryClock(underflow, settle: 120);
        Assert.Equal(2, underflow.Read("stack_error"));
        Assert.Equal(8, ReadTernaryPair(underflow, "sp"));
        Assert.Empty(underflow.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryRegisterLoadsOnTheEdgeAndHoldsWhenDisabled()
    {
        SimulationEngine engine = Engine(StandardLibrary.TernaryRegister2Id);
        engine.Drive("clk", 0);
        engine.Drive("load", 2);

        for (int value = 0; value < 9; value++)
        {
            LoadTernaryPair(engine, "d", value);
            PulseTernaryClock(engine);
            Assert.Equal(value, ReadTernaryPair(engine, "q"));
        }

        engine.Drive("load", 0);
        LoadTernaryPair(engine, "d", 1);
        PulseTernaryClock(engine);
        Assert.Equal(8, ReadTernaryPair(engine, "q"));

        engine.Drive("load", 2);
        PulseTernaryClock(engine);
        Assert.Equal(1, ReadTernaryPair(engine, "q"));
    }

    [Fact]
    public void TernaryAccumulatorLoadsAddsHoldsAndWraps()
    {
        CircuitProject project = ExampleProjects.CreateTernaryAccumulatorDemo();
        Assert.Contains(
            project.RequireDefinition("demo.t-accumulator").Instances,
            instance => instance.DefinitionId == StandardLibrary.TernaryRegister2Id);
        SimulationEngine engine = Engine("demo.t-accumulator", project);
        engine.Drive("clk", 0);
        engine.Drive("load", 0);
        engine.Drive("add", 0);

        LoadTernaryPair(engine, "d", 5);
        engine.Drive("load", 2);
        PulseTernaryClock(engine);
        Assert.Equal(5, ReadTernaryPair(engine, "q"));

        engine.Drive("load", 0);
        engine.Drive("add", 2);
        LoadTernaryPair(engine, "d", 3);
        PulseTernaryClock(engine);
        Assert.Equal(8, ReadTernaryPair(engine, "q"));

        LoadTernaryPair(engine, "d", 4);
        PulseTernaryClock(engine);
        Assert.Equal(3, ReadTernaryPair(engine, "q"));

        engine.Drive("add", 0);
        LoadTernaryPair(engine, "d", 8);
        PulseTernaryClock(engine);
        Assert.Equal(3, ReadTernaryPair(engine, "q"));

        engine.Drive("load", 2);
        PulseTernaryClock(engine);
        Assert.Equal(8, ReadTernaryPair(engine, "q"));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void TernaryFullAdderCoversAllDigitTriples()
    {
        SimulationEngine engine = Engine(StandardLibrary.TernaryFullAdderId);
        for (int a = 0; a < 3; a++)
        {
            for (int b = 0; b < 3; b++)
            {
                for (int cin = 0; cin < 3; cin++)
                {
                    engine.Drive("a", a);
                    engine.Drive("b", b);
                    engine.Drive("cin", cin);
                    Settle(engine, 20);

                    int total = engine.Read("s") + (3 * engine.Read("cout"));
                    Assert.Equal(a + b + cin, total);
                }
            }
        }
    }

    [Fact]
    public void TernaryAdder2AddsEveryTwoTritPair()
    {
        SimulationEngine engine = Engine(StandardLibrary.TernaryAdder2Id);
        for (int ai = 0; ai < 3; ai++)
        {
            for (int ah = 0; ah < 3; ah++)
            {
                for (int bi = 0; bi < 3; bi++)
                {
                    for (int bh = 0; bh < 3; bh++)
                    {
                        engine.Drive("a0", ai);
                        engine.Drive("a1", ah);
                        engine.Drive("b0", bi);
                        engine.Drive("b1", bh);
                        Settle(engine, 30);

                        int expected = (ai + (3 * ah)) + (bi + (3 * bh));
                        int observed = engine.Read("s0") +
                                       (3 * engine.Read("s1")) +
                                       (9 * engine.Read("cout"));
                        Assert.Equal(expected, observed);
                    }
                }
            }
        }
    }

    [Fact]
    public void TernaryLatchCapturesWhenEnabledAndHoldsWhenClosed()
    {
        SimulationEngine engine = Engine(StandardLibrary.TernaryLatchId);
        engine.Drive("e", 2);
        engine.Drive("d", 2);
        Settle(engine, 20);
        Assert.Equal(2, engine.Read("q"));
        Assert.Equal(0, engine.Read("qn"));

        engine.Drive("d", 1);
        Settle(engine, 20);
        Assert.Equal(1, engine.Read("q"));

        engine.Drive("e", 0);
        engine.Drive("d", 0);
        Settle(engine, 20);
        Assert.Equal(1, engine.Read("q"));
        Assert.Equal(1, engine.Read("qn"));

        engine.Drive("e", 2);
        Settle(engine, 20);
        Assert.Equal(0, engine.Read("q"));
        Assert.Equal(2, engine.Read("qn"));
    }

    [Fact]
    public void TernaryFlipFlopSamplesOnRisingEdgeOnly()
    {
        SimulationEngine engine = Engine(StandardLibrary.TernaryDFlipFlopId);
        engine.Drive("clk", 0);
        engine.Drive("d", 2);
        Settle(engine, 40);
        Assert.Equal(0, engine.Read("q"));

        engine.Drive("clk", 2);
        Settle(engine, 40);
        Assert.Equal(2, engine.Read("q"));
        Assert.Equal(0, engine.Read("qn"));

        engine.Drive("clk", 0);
        engine.Drive("d", 1);
        Settle(engine, 40);
        Assert.Equal(2, engine.Read("q"));

        engine.Drive("d", 0);
        Settle(engine, 40);
        Assert.Equal(2, engine.Read("q"));

        engine.Drive("clk", 2);
        Settle(engine, 40);
        Assert.Equal(0, engine.Read("q"));
    }

    [Fact]
    public void TernaryCounterCyclesThroughAllNineStates()
    {
        CircuitProject project = ExampleProjects.CreateTernaryCounterDemo();
        SimulationEngine engine = Engine("demo.ternary-counter", project);
        engine.Drive("clk", 0);
        Settle(engine, 40);
        Assert.Equal(0, ReadTernaryCounter(engine));

        for (int expected = 1; expected <= 9; expected++)
        {
            engine.Drive("clk", 2);
            Settle(engine, 60);
            Assert.Equal(expected % 9, ReadTernaryCounter(engine));

            engine.Drive("clk", 0);
            Settle(engine, 60);
            Assert.Equal(expected % 9, ReadTernaryCounter(engine));
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void InstallIsIdempotent()
    {
        CircuitProject project = TestFixtures.WithStandardLibrary();
        int definitions = project.Definitions.Count;
        int domains = project.Domains.Count;
        IReadOnlyList<string> added = StandardLibrary.Install(project);
        Assert.Empty(added);
        Assert.Equal(definitions, project.Definitions.Count);
        Assert.Equal(domains, project.Domains.Count);
    }

    [Fact]
    public void EveryStandardDefinitionCompiles()
    {
        CircuitProject project = TestFixtures.WithStandardLibrary();
        foreach (CircuitDefinition definition in project.Definitions)
        {
            bool compiled = CircuitCompiler.TryCompile(project, definition.Id, out CompiledCircuit? circuit, out DiagnosticBag report);
            Assert.True(compiled, $"{definition.Id}: {string.Join('\n', report.Where(d => d.Severity == Core.DiagnosticSeverity.Error))}");
            Assert.NotNull(circuit);
        }
    }

    private static int ReadTernaryCounter(SimulationEngine engine) =>
        engine.Read("t0") + (3 * engine.Read("t1"));

    private static int ReadTernaryPair(SimulationEngine engine, string prefix) =>
        engine.Read($"{prefix}0") + (3 * engine.Read($"{prefix}1"));

    /// <summary>Rewrites the stack machine's four instruction ROMs for a scenario test.</summary>
    private static void SetStackMachineProgram(CircuitProject project, int[] program, int[] operands) =>
        SetMachineProgram(project, "demo.t-stack", program, operands);

    /// <summary>Rewrites the call machine's four instruction ROMs for a scenario test.</summary>
    private static void SetCallMachineProgram(CircuitProject project, int[] program, int[] operands) =>
        SetMachineProgram(project, "demo.t-machine-3", program, operands);

    /// <summary>Rewrites the four instruction ROMs of a ternary machine for a scenario test.</summary>
    private static void SetMachineProgram(
        CircuitProject project,
        string definitionId,
        int[] program,
        int[] operands)
    {
        CircuitDefinition root = project.RequireDefinition(definitionId);
        int[] opcodeLow = new int[9];
        int[] opcodeHigh = new int[9];
        int[] argumentLow = new int[9];
        int[] argumentHigh = new int[9];
        for (int address = 0; address < 9; address++)
        {
            opcodeLow[address] = program[address] % 3;
            opcodeHigh[address] = program[address] / 3;
            argumentLow[address] = operands[address] % 3;
            argumentHigh[address] = operands[address] / 3;
        }

        SetRom(root, "rom_op_low", opcodeLow);
        SetRom(root, "rom_op_high", opcodeHigh);
        SetRom(root, "rom_arg_low", argumentLow);
        SetRom(root, "rom_arg_high", argumentHigh);
    }

    private static void SetRom(CircuitDefinition definition, string instanceId, int[] table)
    {
        ComponentInstance instance = definition.Instances.Single(i => string.Equals(i.Id, instanceId, StringComparison.Ordinal));
        instance.Primitive = new LookupSpec(2, table, 1);
    }

    private static int ReadBalancedPair(SimulationEngine engine, string prefix)
    {
        NumericDecodeResult decoded = NumericFormats.BalancedTernary.Decode(
            [engine.Read($"{prefix}0"), engine.Read($"{prefix}1")]);
        Assert.True(decoded.IsValid);
        return (int)decoded.Value;
    }

    private static void LoadTernaryPair(SimulationEngine engine, string prefix, int value)
    {
        engine.Drive($"{prefix}0", value % 3);
        engine.Drive($"{prefix}1", value / 3);
    }

    private static void PulseTernaryClock(SimulationEngine engine, long settle = 80)
    {
        // Setup time: the mux and adder chain must settle while the master latch is open.
        Settle(engine, settle);
        engine.Drive("clk", 2);
        Settle(engine, settle);
        engine.Drive("clk", 0);
        Settle(engine, settle);
    }

    private static int ReadBus(SimulationEngine engine, params string[] pins)
    {
        int value = 0;
        for (int i = 0; i < pins.Length; i++)
        {
            value |= engine.Read(pins[i]) << i;
        }

        return value;
    }

    private static void DriveWord(SimulationEngine engine, string prefix, int value)
    {
        for (int bit = 0; bit < 4; bit++)
        {
            engine.Drive($"{prefix}{bit}", (value >> bit) & 1);
        }
    }

    private static void PulseClock(SimulationEngine engine, long settle = 80)
    {
        engine.Drive("clk", 1);
        Settle(engine, settle);
        engine.Drive("clk", 0);
        Settle(engine, settle);
    }
}
