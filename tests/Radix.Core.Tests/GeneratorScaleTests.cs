using Radix.Core.Simulation;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Generator checks at widths the editor's 32-bit conveniences cannot represent.
/// These pin the 64/65/128 boundaries and the full-width carry chain, where a
/// truncating mask or an off-by-one lane would silently produce a plausible but
/// wrong generated part.
/// </summary>
public sealed class GeneratorScaleTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(128)]
    public void WideRegisterLoadsHoldsAndClears(int width)
    {
        GeneratedPart part = PartGenerator.CreateRegister(width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        bool[] first = Pattern(width, seed: 1);
        bool[] second = Pattern(width, seed: 2);

        engine.Drive("clk", 0);
        engine.Drive("load", 0);
        engine.Drive("clr", 0);
        engine.Settle(20);
        Assert.All(ReadBits(engine, "q", width), Assert.False);

        engine.Drive("load", 1);
        DriveBits(engine, "d", first);
        Pulse(engine, width);
        Assert.Equal(first, ReadBits(engine, "q", width));

        engine.Drive("load", 0);
        DriveBits(engine, "d", second);
        Pulse(engine, width);
        Assert.Equal(first, ReadBits(engine, "q", width));

        engine.Drive("clr", 1);
        engine.Drive("load", 0);
        Pulse(engine, width);
        Assert.All(ReadBits(engine, "q", width), Assert.False);

        engine.Drive("load", 1);
        engine.Drive("clr", 1);
        DriveBits(engine, "d", second);
        Pulse(engine, width);
        Assert.All(ReadBits(engine, "q", width), Assert.False);

        engine.Drive("clr", 0);
        DriveBits(engine, "d", first);
        engine.Settle(20);
        Assert.All(ReadBits(engine, "q", width), Assert.False);
        Pulse(engine, width);
        Assert.Equal(first, ReadBits(engine, "q", width));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public void WideCounterWrapsWithCarryAtTheFullWidth(int width)
    {
        GeneratedPart part = PartGenerator.CreateCounter(width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);

        // Lookup primitives hold while any input is undriven, and the generated
        // counter's data muxes carry D even when LOAD is low. Drive every input, as
        // the generated part's documented usage requires.
        engine.Drive("clk", 0);
        engine.Drive("clr", 0);
        engine.Drive("load", 0);
        engine.Drive("en", 1);
        DriveBits(engine, "d", new bool[width]);
        engine.Settle(width * 8 + 40);

        Pulse(engine, width);
        bool[] expected = new bool[width];
        expected[0] = true;
        Assert.Equal(expected, ReadBits(engine, "q", width));
        Assert.Equal(0, engine.Read("carry"));

        engine.Drive("en", 0);
        engine.Drive("load", 1);
        DriveBits(engine, "d", Enumerable.Repeat(true, width).ToArray());
        Pulse(engine, width);
        Assert.All(ReadBits(engine, "q", width), Assert.True);

        engine.Drive("load", 0);
        engine.Drive("en", 1);
        engine.Settle(width * 8 + 40);
        Assert.Equal(1, engine.Read("carry"));

        Pulse(engine, width);
        Assert.All(ReadBits(engine, "q", width), Assert.False);
        Assert.Equal(0, engine.Read("carry"));

        engine.Drive("clr", 1);
        engine.Drive("load", 1);
        DriveBits(engine, "d", Enumerable.Repeat(true, width).ToArray());
        Pulse(engine, width);
        Assert.All(ReadBits(engine, "q", width), Assert.False);

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void WideAdderCarriesPastSixtyFourBits()
    {
        const int width = 65;
        GeneratedPart part = PartGenerator.CreateAdder(width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        bool[] allOnes = Enumerable.Repeat(true, width).ToArray();

        DriveBits(engine, "a", allOnes);
        DriveBits(engine, "b", allOnes);
        engine.Drive("cin", 1);
        engine.Settle(width * 8 + 40);

        Assert.All(ReadBits(engine, "s", width), Assert.True);
        Assert.Equal(1, engine.Read("cout"));

        bool[] one = new bool[width];
        one[0] = true;
        DriveBits(engine, "a", one);
        DriveBits(engine, "b", new bool[width]);
        engine.Drive("cin", 0);
        engine.Settle(width * 8 + 40);
        Assert.Equal(one, ReadBits(engine, "s", width));
        Assert.Equal(0, engine.Read("cout"));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void LargeRamKeepsWordsIndependent()
    {
        const int words = 128;
        const int width = 8;
        const int addressBits = 7;
        GeneratedPart part = PartGenerator.CreateRam(words, width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        int[] addresses = [0, 1, 63, 64, 126, 127];

        engine.Drive("clk", 0);
        engine.Drive("we", 0);
        foreach (int word in addresses)
        {
            DriveWord(engine, "addr", addressBits, word);
            DriveWord(engine, "d", width, (word * 37) + 11);
            engine.Drive("we", 1);
            Pulse(engine, width);
            engine.Drive("we", 0);
            engine.Settle(60);
        }

        foreach (int word in addresses)
        {
            DriveWord(engine, "addr", addressBits, word);
            engine.Settle(60);
            Assert.Equal((word * 37) + 11 & 0xFF, ReadWord(engine, "q", width));
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    private static void Pulse(SimulationEngine engine, int width)
    {
        engine.Drive("clk", 0);
        engine.Settle(20);
        engine.Drive("clk", 1);
        engine.Settle(width * 4 + 40);
        engine.Drive("clk", 0);
        engine.Settle(20);
    }

    private static bool[] Pattern(int width, int seed)
    {
        var random = new Random((seed * 7919) + width);
        return Enumerable.Range(0, width).Select(_ => random.Next(2) == 1).ToArray();
    }

    private static void DriveBits(SimulationEngine engine, string prefix, IReadOnlyList<bool> bits)
    {
        for (int bit = 0; bit < bits.Count; bit++)
        {
            engine.Drive($"{prefix}{bit}", bits[bit] ? 1 : 0);
        }
    }

    private static bool[] ReadBits(SimulationEngine engine, string prefix, int width)
    {
        var bits = new bool[width];
        for (int bit = 0; bit < width; bit++)
        {
            bits[bit] = engine.Read($"{prefix}{bit}") == 1;
        }

        return bits;
    }

    private static void DriveWord(SimulationEngine engine, string prefix, int width, int value)
    {
        for (int bit = 0; bit < width; bit++)
        {
            engine.Drive($"{prefix}{bit}", (value >> bit) & 1);
        }
    }

    private static int ReadWord(SimulationEngine engine, string prefix, int width)
    {
        int value = 0;
        for (int bit = 0; bit < width; bit++)
        {
            value |= engine.Read($"{prefix}{bit}") << bit;
        }

        return value;
    }
}
