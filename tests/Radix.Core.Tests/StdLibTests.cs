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
