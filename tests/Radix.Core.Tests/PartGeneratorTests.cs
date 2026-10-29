using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Simulation;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class PartGeneratorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(32)]
    public void GeneratedRegisterCapturesHoldsAndClears(int width)
    {
        GeneratedPart part = PartGenerator.CreateRegister(width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        uint mask = Mask(width);
        uint value = 0xA5A5_5A5Au & mask;

        engine.Drive("clk", 0);
        engine.Drive("load", 1);
        engine.Drive("clr", 0);
        DriveValue(engine, "d", width, value);
        Pulse(engine);
        Assert.Equal(value, ReadValue(engine, "q", width));

        engine.Drive("load", 0);
        DriveValue(engine, "d", width, ~value & mask);
        Pulse(engine);
        Assert.Equal(value, ReadValue(engine, "q", width));

        engine.Drive("load", 1);
        engine.Drive("clr", 1);
        DriveValue(engine, "d", width, 0x3C3C_3C3Cu & mask);
        Pulse(engine);
        Assert.Equal(0u, ReadValue(engine, "q", width));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void GeneratedAdderMatchesIntegerAddition(int width)
    {
        GeneratedPart part = PartGenerator.CreateAdder(width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        uint mask = Mask(width);
        uint limit = mask;
        (uint A, uint B, uint Cin)[] cases =
        [
            (0, 0, 0),
            (1, 0, 1),
            (limit, limit, 0),
            (limit, 1, 0),
            (limit, limit, 1),
            (0x5555_5555u & mask, 0xAAAA_AAAAu & mask, 0),
            (0x1234_5678u & mask, 0x0FED_CBA9u & mask, 1),
        ];

        foreach ((uint a, uint b, uint cin) in cases)
        {
            DriveValue(engine, "a", width, a);
            DriveValue(engine, "b", width, b);
            engine.Drive("cin", (int)cin);
            engine.Settle((width * 6) + 20);

            ulong expected = (ulong)a + b + cin;
            Assert.Equal((uint)expected & mask, ReadValue(engine, "s", width));
            Assert.Equal((int)(expected >> width) & 1, engine.Read("cout"));
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Theory]
    [InlineData(4, 3)]
    [InlineData(8, 8)]
    [InlineData(16, 4)]
    public void GeneratedRamWritesEveryWordAndReadsItBack(int words, int width)
    {
        GeneratedPart part = PartGenerator.CreateRam(words, width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        int addressBits = System.Numerics.BitOperations.Log2((uint)words);
        uint mask = Mask(width);

        engine.Drive("clk", 0);
        engine.Drive("we", 0);
        for (int word = 0; word < words; word++)
        {
            uint value = (uint)((word * 37) + 5) & mask;
            DriveValue(engine, "addr", addressBits, (uint)word);
            DriveValue(engine, "d", width, value);
            engine.Drive("we", 1);
            Pulse(engine);
            engine.Drive("we", 0);
            engine.Settle(40);
            Assert.Equal(value, ReadValue(engine, "q", width));
        }

        for (int word = 0; word < words; word++)
        {
            DriveValue(engine, "addr", addressBits, (uint)word);
            engine.Settle(40);
            uint expected = (uint)((word * 37) + 5) & mask;
            Assert.Equal(expected, ReadValue(engine, "q", width));
        }

        uint stored = (uint)5 & mask;
        DriveValue(engine, "addr", addressBits, 0);
        DriveValue(engine, "d", width, ~stored & mask);
        engine.Drive("we", 0);
        Pulse(engine);
        engine.Settle(40);
        Assert.Equal(stored, ReadValue(engine, "q", width));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void GeneratedAluComputesAllOperationsAndFlags(int width)
    {
        GeneratedPart part = PartGenerator.CreateAlu(width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        uint mask = Mask(width);
        (uint A, uint B)[] cases =
        [
            (0, 0),
            (mask, mask),
            (mask, 1),
            (0x5555_5555u & mask, 0xAAAA_AAAAu & mask),
            (0x1234_5678u & mask, 0x0FED_CBA9u & mask),
        ];

        for (int operation = 0; operation < 4; operation++)
        {
            foreach ((uint a, uint b) in cases)
            {
                DriveValue(engine, "a", width, a);
                DriveValue(engine, "b", width, b);
                engine.Drive("op0", operation & 1);
                engine.Drive("op1", (operation >> 1) & 1);
                engine.Settle((width * 5) + 40);

                ulong arithmetic = (ulong)a + b;
                uint expected = operation switch
                {
                    0 => (uint)arithmetic & mask,
                    1 => a & b,
                    2 => a | b,
                    _ => a ^ b,
                };
                int expectedCarry = operation == 0 ? (int)(arithmetic >> width) & 1 : 0;

                Assert.Equal(expected, ReadValue(engine, "y", width));
                Assert.Equal(expectedCarry, engine.Read("carry"));
                Assert.Equal(expected == 0 ? 1 : 0, engine.Read("zero"));
            }
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void GeneratedMuxSelectsWholeWords(int width)
    {
        GeneratedPart part = PartGenerator.CreateMux(width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        uint mask = Mask(width);
        uint a = 0x1234_5678u & mask;
        uint b = 0x0FED_CBA9u & mask;
        DriveValue(engine, "a", width, a);
        DriveValue(engine, "b", width, b);

        engine.Drive("s", 0);
        engine.Settle((width * 2) + 20);
        Assert.Equal(a, ReadValue(engine, "y", width));

        engine.Drive("s", 1);
        engine.Settle((width * 2) + 20);
        Assert.Equal(b, ReadValue(engine, "y", width));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Theory]
    [InlineData(4, 8)]
    [InlineData(16, 4)]
    [InlineData(8, 1)]
    [InlineData(2, 16)]
    public void GeneratedRomReturnsItsProgrammedContents(int words, int width)
    {
        uint mask = Mask(width);
        ulong[] contents = new ulong[words];
        for (int word = 0; word < words; word++)
        {
            contents[word] = (uint)((word * 61) + 7) & mask;
        }

        GeneratedPart part = PartGenerator.CreateRom(words, width, contents);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        int addressBits = System.Numerics.BitOperations.Log2((uint)words);

        for (int word = 0; word < words; word++)
        {
            DriveValue(engine, "addr", addressBits, (uint)word);
            engine.Settle(30);
            Assert.Equal((uint)contents[word], ReadValue(engine, "d", width));
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void UnprogrammedRomWordsReadAsZero()
    {
        GeneratedPart part = PartGenerator.CreateRom(4, 4, [3, 12]);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);

        DriveValue(engine, "addr", 2, 0);
        engine.Settle(20);
        Assert.Equal(3u, ReadValue(engine, "d", 4));

        DriveValue(engine, "addr", 2, 1);
        engine.Settle(20);
        Assert.Equal(12u, ReadValue(engine, "d", 4));

        DriveValue(engine, "addr", 2, 2);
        engine.Settle(20);
        Assert.Equal(0u, ReadValue(engine, "d", 4));
    }

    [Fact]
    public void RomCarriesAddressAndDataLanes()
    {
        GeneratedPart part = PartGenerator.CreateRom(8, 6);
        CircuitDefinition root = part.Project.RequireDefinition(part.RootDefinitionId);

        Assert.Equal(["address", "data"], root.NumericLanes.Select(lane => lane.Id));
        Assert.Equal(3, root.FindNumericLane("address")!.PinIds.Count);
        Assert.Equal(6, root.FindNumericLane("data")!.PinIds.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void GeneratedComparatorReportsUnsignedOrder(int width)
    {
        GeneratedPart part = PartGenerator.CreateComparator(width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        uint mask = Mask(width);
        (uint A, uint B)[] cases =
        [
            (0, 0),
            (0, mask),
            (mask, 0),
            (mask, mask),
            (1, 2),
            (2, 1),
            (0x5555_5555u & mask, 0xAAAA_AAAAu & mask),
            (0x1234_5678u & mask, 0x1234_5678u & mask),
        ];

        foreach ((uint caseA, uint caseB) in cases)
        {
            uint a = caseA & mask;
            uint b = caseB & mask;
            DriveValue(engine, "a", width, a);
            DriveValue(engine, "b", width, b);
            engine.Settle((width * 6) + 40);

            int eq = engine.Read("eq");
            int lt = engine.Read("lt");
            int gt = engine.Read("gt");
            Assert.True(
                eq == (a == b ? 1 : 0) &&
                lt == (a < b ? 1 : 0) &&
                gt == (a > b ? 1 : 0),
                $"width {width}, a {a}, b {b}: eq={eq}, lt={lt}, gt={gt}");
        }

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void GeneratedCounterLoadsHoldsIncrementsAndWraps(int width)
    {
        GeneratedPart part = PartGenerator.CreateCounter(width);
        SimulationEngine engine = TestFixtures.Simulate(part.Project, part.RootDefinitionId);
        uint mask = Mask(width);
        engine.Drive("clk", 0);
        engine.Drive("clr", 0);
        engine.Drive("load", 0);
        engine.Drive("en", 0);
        DriveValue(engine, "d", width, 0);

        Assert.Equal(0u, ReadValue(engine, "q", width));
        engine.Drive("en", 1);
        Pulse(engine);
        Assert.Equal(1u, ReadValue(engine, "q", width));
        Pulse(engine);
        Assert.Equal(2u & mask, ReadValue(engine, "q", width));

        engine.Drive("en", 0);
        uint loaded = 0x5Au & mask;
        engine.Drive("load", 1);
        DriveValue(engine, "d", width, loaded);
        Pulse(engine);
        Assert.Equal(loaded, ReadValue(engine, "q", width));

        engine.Drive("load", 0);
        DriveValue(engine, "d", width, ~loaded & mask);
        Pulse(engine);
        Assert.Equal(loaded, ReadValue(engine, "q", width));

        engine.Drive("load", 1);
        DriveValue(engine, "d", width, mask);
        Pulse(engine);
        engine.Drive("en", 1);
        engine.Settle(20);
        Assert.Equal(0, engine.Read("carry"));
        engine.Drive("load", 0);
        engine.Settle(20);
        Assert.Equal(1, engine.Read("carry"));
        Pulse(engine);
        Assert.Equal(0u, ReadValue(engine, "q", width));
        Assert.Equal(0, engine.Read("carry"));

        engine.Drive("clr", 1);
        engine.Drive("load", 1);
        engine.Drive("en", 1);
        DriveValue(engine, "d", width, 1);
        Pulse(engine);
        Assert.Equal(0u, ReadValue(engine, "q", width));

        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void ExportedGeneratedPartIsAValidStandaloneLibrary()
    {
        GeneratedPart part = PartGenerator.CreateRegister(12);
        string json = ProjectLibrary.Export(part.Project, part.RootDefinitionId);
        CircuitProject library = ProjectSerializer.LoadFromString(json);

        Assert.Equal(part.RootDefinitionId, library.RootDefinitionId);
        Assert.True(library.Definitions.Count < part.Project.Definitions.Count);
        Assert.Contains(library.Definitions, definition => definition.Id == part.RootDefinitionId);
        Assert.DoesNotContain(library.Definitions, definition => definition.Id == StandardLibrary.Alu8Id);
        Assert.True(
            CircuitCompiler.TryCompile(library, library.RootDefinitionId, out _, out var diagnostics),
            string.Join(Environment.NewLine, diagnostics));
        Assert.Equal(2, library.Definitions.Single(d => d.Id == part.RootDefinitionId).NumericLanes.Count);
    }

    [Fact]
    public void RamCarriesAddressDataAndReadLanes()
    {
        GeneratedPart part = PartGenerator.CreateRam(8, 6);
        CircuitDefinition root = part.Project.RequireDefinition(part.RootDefinitionId);

        Assert.Equal(["address", "data", "read"], root.NumericLanes.Select(lane => lane.Id));
        Assert.Equal(3, root.FindNumericLane("address")!.PinIds.Count);
        Assert.Equal(6, root.FindNumericLane("data")!.PinIds.Count);
        Assert.Equal(6, root.FindNumericLane("read")!.PinIds.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PartGenerator.MaxWidth + 1)]
    public void RegisterRejectsInvalidWidth(int width)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartGenerator.CreateRegister(width));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(3, 8)]
    [InlineData(256, 8)]
    [InlineData(8, 0)]
    [InlineData(8, PartGenerator.MaxRamWidth + 1)]
    [InlineData(128, 64)]
    public void RamRejectsInvalidShape(int words, int width)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartGenerator.CreateRam(words, width));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(3, 8)]
    [InlineData(512, 8)]
    [InlineData(8, 0)]
    [InlineData(8, PartGenerator.MaxRomWidth + 1)]
    [InlineData(256, 128)]
    public void RomRejectsInvalidShape(int words, int width)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartGenerator.CreateRom(words, width));
    }

    [Fact]
    public void RomRejectsOverlongOrOversizedContents()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartGenerator.CreateRom(4, 4, [1, 2, 3, 4, 5]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartGenerator.CreateRom(4, 4, [16]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartGenerator.CreateRom(4, 1, [2]));
    }

    private static void Pulse(SimulationEngine engine)
    {
        engine.Drive("clk", 0);
        engine.Settle(10);
        engine.Drive("clk", 1);
        engine.Settle(30);
        engine.Drive("clk", 0);
        engine.Settle(10);
    }

    private static void DriveValue(SimulationEngine engine, string prefix, int width, uint value)
    {
        for (int bit = 0; bit < width; bit++)
        {
            engine.Drive($"{prefix}{bit}", (int)((value >> bit) & 1));
        }
    }

    private static uint ReadValue(SimulationEngine engine, string prefix, int width)
    {
        uint value = 0;
        for (int bit = 0; bit < width; bit++)
        {
            value |= (uint)engine.Read($"{prefix}{bit}") << bit;
        }

        return value;
    }

    private static uint Mask(int width) => width == 32 ? uint.MaxValue : (1u << width) - 1;
}
