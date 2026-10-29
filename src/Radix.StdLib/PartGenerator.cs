using Radix.Core;
using Radix.Core.Compilation;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;

namespace Radix.StdLib;

/// <summary>
/// A generated part and the metadata needed to write it as a portable library.
/// The project contains the generated root plus only the ordinary standard-library
/// dependencies it uses; <see cref="ProjectLibrary.Export"/> prunes the rest.
/// </summary>
public sealed record GeneratedPart(
    CircuitProject Project,
    string RootDefinitionId,
    string SuggestedFileName);

/// <summary>
/// Builds ordinary, editable RADIX definitions at a requested width. This is a
/// generator boundary, not a new engine concept: the result contains the same
/// DFFs, muxes, adders, and lookups a user would wire by hand, and it is validated
/// by the normal compiler before it is returned. Widening a datapath therefore does
/// not require another hand-built standard-library entry.
/// </summary>
public static class PartGenerator
{
    public const int MinWidth = 1;
    public const int MaxWidth = 256;
    public const int MinRamWords = 2;
    public const int MaxRamWords = 128;
    public const int MaxRamWidth = 64;
    public const int MaxRamBits = 4096;

    public static GeneratedPart CreateRegister(int width)
    {
        RequireWidth(width);
        CircuitProject project = CreateProject($"{width}-bit register");
        var builder = new DefinitionBuilder(project, $"gen.reg-{width}", $"{width}-bit register");
        builder.Definition.Description =
            $"Generated {width}-bit register. LOAD captures D on a rising CLK edge; CLR has " +
            "priority over LOAD; every bit powers up at 0. Built from D flip-flops and muxes.";

        Net load = builder.AddPin("load", "LOAD", PinDirection.Input, StandardDomains.BinaryId);
        Net clr = builder.AddPin("clr", "CLR", PinDirection.Input, StandardDomains.BinaryId);
        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.BinaryId);

        var data = new Net[width];
        var output = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            data[bit] = builder.AddPin($"d{bit}", $"D{bit}", PinDirection.Input, StandardDomains.BinaryId);
            output[bit] = builder.SetInitial(
                builder.AddPin($"q{bit}", $"Q{bit}", PinDirection.Output, StandardDomains.BinaryId),
                0);
        }

        builder.AddNumericLane("data", "Data", NumericFormats.UnsignedBinary.Id, PinIds("d", width));
        builder.AddNumericLane("value", "Q", NumericFormats.UnsignedBinary.Id, PinIds("q", width));

        Net zero = builder.AddNet("zero", StandardDomains.BinaryId);
        builder.AddPrimitive("const0", new ConstantSpec(0), 60, 80);
        builder.Connect(zero, builder.Pin("const0", PrimitiveShapes.OutputPinId));

        for (int bit = 0; bit < width; bit++)
        {
            double y = 80 + (bit * 130);
            Net held = builder.AddNet($"held{bit}", StandardDomains.BinaryId);
            Net next = builder.AddNet($"next{bit}", StandardDomains.BinaryId);

            builder.AddInstance($"mux_hold{bit}", "std.mux2", 300, y);
            builder.Connect(output[bit], builder.Pin($"mux_hold{bit}", "a"));
            builder.Connect(data[bit], builder.Pin($"mux_hold{bit}", "b"));
            builder.Connect(load, builder.Pin($"mux_hold{bit}", "s"));
            builder.Connect(held, builder.Pin($"mux_hold{bit}", "y"));

            builder.AddInstance($"mux_clear{bit}", "std.mux2", 580, y);
            builder.Connect(held, builder.Pin($"mux_clear{bit}", "a"));
            builder.Connect(zero, builder.Pin($"mux_clear{bit}", "b"));
            builder.Connect(clr, builder.Pin($"mux_clear{bit}", "s"));
            builder.Connect(next, builder.Pin($"mux_clear{bit}", "y"));

            builder.AddInstance($"ff{bit}", StandardLibrary.DFlipFlopId, 860, y);
            builder.Connect(next, builder.Pin($"ff{bit}", "d"));
            builder.Connect(clk, builder.Pin($"ff{bit}", "clk"));
            builder.Connect(output[bit], builder.Pin($"ff{bit}", "q"));
        }

        return Finish(project, builder.Definition.Id, $"reg-{width}");
    }

    public static GeneratedPart CreateAdder(int width)
    {
        RequireWidth(width);
        CircuitProject project = CreateProject($"{width}-bit ripple-carry adder");
        var builder = new DefinitionBuilder(project, $"gen.adder-{width}", $"{width}-bit ripple-carry adder");
        builder.Definition.Description =
            $"Generated {width}-bit ripple-carry adder. A + B + CIN produces SUM and COUT; " +
            "every stage is an ordinary std.full-adder.";

        var a = new Net[width];
        var b = new Net[width];
        var sum = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            a[bit] = builder.AddPin($"a{bit}", $"A{bit}", PinDirection.Input, StandardDomains.BinaryId);
            b[bit] = builder.AddPin($"b{bit}", $"B{bit}", PinDirection.Input, StandardDomains.BinaryId);
            sum[bit] = builder.AddPin($"s{bit}", $"S{bit}", PinDirection.Output, StandardDomains.BinaryId);
        }

        Net cin = builder.AddPin("cin", "CIN", PinDirection.Input, StandardDomains.BinaryId);
        Net cout = builder.AddPin("cout", "COUT", PinDirection.Output, StandardDomains.BinaryId);

        builder.AddNumericLane("a", "A", NumericFormats.UnsignedBinary.Id, PinIds("a", width));
        builder.AddNumericLane("b", "B", NumericFormats.UnsignedBinary.Id, PinIds("b", width));
        builder.AddNumericLane(
            "sum",
            "SUM",
            NumericFormats.UnsignedBinary.Id,
            [.. PinIds("s", width), "cout"]);

        var carries = new Net[width + 1];
        carries[0] = cin;
        for (int bit = 1; bit < width; bit++)
        {
            carries[bit] = builder.AddNet($"c{bit}", StandardDomains.BinaryId);
        }

        carries[width] = cout;

        for (int bit = 0; bit < width; bit++)
        {
            builder.AddInstance($"fa{bit}", "std.full-adder", 80 + (bit * 260), 100);
            builder.Connect(a[bit], builder.Pin($"fa{bit}", "a"));
            builder.Connect(b[bit], builder.Pin($"fa{bit}", "b"));
            builder.Connect(carries[bit], builder.Pin($"fa{bit}", "cin"));
            builder.Connect(sum[bit], builder.Pin($"fa{bit}", "s"));
            builder.Connect(carries[bit + 1], builder.Pin($"fa{bit}", "cout"));
        }

        return Finish(project, builder.Definition.Id, $"adder-{width}");
    }

    public static GeneratedPart CreateRam(int words, int width)
    {
        RequireRamShape(words, width);
        int addressBits = System.Numerics.BitOperations.Log2((uint)words);
        CircuitProject project = CreateProject($"{words} × {width} binary RAM");
        string wordRegisterId = $"gen.word-reg-{width}";
        if (project.FindDefinition(wordRegisterId) is null)
        {
            AddWordRegister(project, width, wordRegisterId);
        }

        var builder = new DefinitionBuilder(project, $"gen.ram-{words}x{width}", $"{words} × {width} binary RAM");
        builder.Definition.Description =
            $"Generated {words}-word × {width}-bit RAM. WE=1 writes D into the word selected by " +
            "ADDR on a rising CLK edge; Q is a combinational read of the addressed word. " +
            "Word registers, write decoders, and the read mux tree are ordinary definitions.";

        var address = new Net[addressBits];
        for (int bit = 0; bit < addressBits; bit++)
        {
            address[bit] = builder.AddPin($"addr{bit}", $"ADDR{bit}", PinDirection.Input, StandardDomains.BinaryId);
        }

        var data = new Net[width];
        var read = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            data[bit] = builder.AddPin($"d{bit}", $"D{bit}", PinDirection.Input, StandardDomains.BinaryId);
            read[bit] = builder.SetInitial(
                builder.AddPin($"q{bit}", $"Q{bit}", PinDirection.Output, StandardDomains.BinaryId),
                0);
        }

        Net we = builder.AddPin("we", "WE", PinDirection.Input, StandardDomains.BinaryId);
        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.BinaryId);

        builder.AddNumericLane("address", "Address", NumericFormats.UnsignedBinary.Id, PinIds("addr", addressBits));
        builder.AddNumericLane("data", "Data", NumericFormats.UnsignedBinary.Id, PinIds("d", width));
        builder.AddNumericLane("read", "Read", NumericFormats.UnsignedBinary.Id, PinIds("q", width));

        var wordOutputs = new Net[words][];
        for (int word = 0; word < words; word++)
        {
            int column = word % 8;
            int row = word / 8;
            double x = 80 + (column * 300);
            double y = 200 + (row * 260);
            string wordInstance = $"word{word}";
            builder.AddInstance(wordInstance, wordRegisterId, x, y);

            wordOutputs[word] = new Net[width];
            for (int bit = 0; bit < width; bit++)
            {
                wordOutputs[word][bit] = builder.AddNet($"word{word}_q{bit}", StandardDomains.BinaryId);
                builder.Connect(data[bit], builder.Pin(wordInstance, $"d{bit}"));
                builder.Connect(wordOutputs[word][bit], builder.Pin(wordInstance, $"q{bit}"));
            }

            // Address-only decoder followed by AND with WE. Connecting the address
            // inputs high bit first makes the lookup index equal the lane value.
            int[] table = new int[words];
            table[word] = 1;
            string decoder = $"decode{word}";
            builder.AddPrimitive(decoder, new LookupSpec(addressBits, table, 1), x, y + 90);
            for (int bit = 0; bit < addressBits; bit++)
            {
                builder.Connect(address[addressBits - 1 - bit], builder.Pin(decoder, $"in{bit}"));
            }

            Net selected = builder.AddNet($"selected{word}", StandardDomains.BinaryId);
            builder.Connect(selected, builder.Pin(decoder, PrimitiveShapes.OutputPinId));

            Net wordLoad = builder.AddNet($"load{word}", StandardDomains.BinaryId);
            builder.AddInstance($"write{word}", "std.and", x + 300, y + 90);
            builder.Connect(selected, builder.Pin($"write{word}", "a"));
            builder.Connect(we, builder.Pin($"write{word}", "b"));
            builder.Connect(wordLoad, builder.Pin($"write{word}", "y"));

            builder.Connect(clk, builder.Pin(wordInstance, "clk"));
            builder.Connect(wordLoad, builder.Pin(wordInstance, "load"));
        }

        // Pair adjacent words at each level; address bit 0 selects within a pair.
        for (int bit = 0; bit < width; bit++)
        {
            List<Net> level = [.. wordOutputs.Select(word => word[bit])];
            for (int levelIndex = 0; level.Count > 1; levelIndex++)
            {
                var next = new List<Net>(level.Count / 2);
                for (int i = 0; i < level.Count; i += 2)
                {
                    string mux = $"read{levelIndex}_{i / 2}_{bit}";
                    Net output = level.Count == 2
                        ? read[bit]
                        : builder.AddNet($"{mux}_out", StandardDomains.BinaryId);
                    builder.AddInstance(mux, "std.mux2", 120 + (levelIndex * 220), 80 + (bit * 110));
                    builder.Connect(level[i], builder.Pin(mux, "a"));
                    builder.Connect(level[i + 1], builder.Pin(mux, "b"));
                    builder.Connect(address[levelIndex], builder.Pin(mux, "s"));
                    builder.Connect(output, builder.Pin(mux, "y"));
                    next.Add(output);
                }

                level = next;
            }
        }

        return Finish(project, builder.Definition.Id, $"ram-{words}x{width}");
    }

    private static void AddWordRegister(CircuitProject project, int width, string id)
    {
        var builder = new DefinitionBuilder(project, id, $"{width}-bit word register");
        builder.Definition.Description =
            $"Generated {width}-bit load/hold word register used by generated RAMs. " +
            "It has no clear input so a RAM needs no per-word constant tie-off.";

        Net load = builder.AddPin("load", "LOAD", PinDirection.Input, StandardDomains.BinaryId);
        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.BinaryId);
        var data = new Net[width];
        var output = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            data[bit] = builder.AddPin($"d{bit}", $"D{bit}", PinDirection.Input, StandardDomains.BinaryId);
            output[bit] = builder.SetInitial(
                builder.AddPin($"q{bit}", $"Q{bit}", PinDirection.Output, StandardDomains.BinaryId),
                0);
        }

        builder.AddNumericLane("data", "Data", NumericFormats.UnsignedBinary.Id, PinIds("d", width));
        builder.AddNumericLane("value", "Q", NumericFormats.UnsignedBinary.Id, PinIds("q", width));

        for (int bit = 0; bit < width; bit++)
        {
            double y = 80 + (bit * 130);
            Net next = builder.AddNet($"next{bit}", StandardDomains.BinaryId);
            builder.AddInstance($"mux{bit}", "std.mux2", 300, y);
            builder.Connect(output[bit], builder.Pin($"mux{bit}", "a"));
            builder.Connect(data[bit], builder.Pin($"mux{bit}", "b"));
            builder.Connect(load, builder.Pin($"mux{bit}", "s"));
            builder.Connect(next, builder.Pin($"mux{bit}", "y"));

            builder.AddInstance($"ff{bit}", StandardLibrary.DFlipFlopId, 580, y);
            builder.Connect(next, builder.Pin($"ff{bit}", "d"));
            builder.Connect(clk, builder.Pin($"ff{bit}", "clk"));
            builder.Connect(output[bit], builder.Pin($"ff{bit}", "q"));
        }
    }

    private static CircuitProject CreateProject(string name)
    {
        var project = new CircuitProject { Name = name };
        StandardLibrary.Install(project);
        return project;
    }

    private static GeneratedPart Finish(CircuitProject project, string rootId, string fileBase)
    {
        if (!CircuitCompiler.TryCompile(project, rootId, out _, out DiagnosticBag diagnostics))
        {
            string detail = string.Join(
                Environment.NewLine,
                diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"Generated part '{rootId}' failed validation:{Environment.NewLine}{detail}");
        }

        return new GeneratedPart(project, rootId, fileBase + ProjectLibrary.Extension);
    }

    private static string[] PinIds(string prefix, int count)
    {
        var ids = new string[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = $"{prefix}{i}";
        }

        return ids;
    }

    private static void RequireWidth(int width)
    {
        if (width is < MinWidth or > MaxWidth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"Width must be in {MinWidth}..{MaxWidth}.");
        }
    }

    private static void RequireRamShape(int words, int width)
    {
        if (words is < MinRamWords or > MaxRamWords || (words & (words - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(words),
                words,
                $"Word count must be a power of two in {MinRamWords}..{MaxRamWords}.");
        }

        if (width is < MinWidth or > MaxRamWidth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"RAM width must be in {MinWidth}..{MaxRamWidth}.");
        }

        if ((long)words * width > MaxRamBits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"A generated RAM may store at most {MaxRamBits} bits ({words} words × {width} bits requested).");
        }
    }
}
