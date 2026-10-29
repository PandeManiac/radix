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
