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
        string root = AddRegister(project, width);
        return Finish(project, root, $"reg-{width}");
    }

    public static GeneratedPart CreateAdder(int width)
    {
        RequireWidth(width);
        CircuitProject project = CreateProject($"{width}-bit ripple-carry adder");
        string root = AddAdder(project, width);
        return Finish(project, root, $"adder-{width}");
    }

    public static GeneratedPart CreateAlu(int width)
    {
        RequireWidth(width);
        CircuitProject project = CreateProject($"{width}-bit ALU");
        string adderId = AddAdder(project, width);
        var builder = new DefinitionBuilder(project, $"gen.alu-{width}", $"{width}-bit ALU");
        builder.Definition.Description =
            $"Generated {width}-bit arithmetic/logic unit. OP1:OP0 selects ADD (00), AND (01), " +
            "OR (10), or XOR (11). CARRY is the adder carry for ADD and 0 for logic operations; " +
            "ZERO is asserted when every result bit is 0. Built from a generated adder, gates, " +
            "and a mux tree.";

        var a = new Net[width];
        var b = new Net[width];
        var y = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            a[bit] = builder.AddPin($"a{bit}", $"A{bit}", PinDirection.Input, StandardDomains.BinaryId);
            b[bit] = builder.AddPin($"b{bit}", $"B{bit}", PinDirection.Input, StandardDomains.BinaryId);
            y[bit] = builder.AddPin($"y{bit}", $"Y{bit}", PinDirection.Output, StandardDomains.BinaryId);
        }

        Net op0 = builder.AddPin("op0", "OP0", PinDirection.Input, StandardDomains.BinaryId);
        Net op1 = builder.AddPin("op1", "OP1", PinDirection.Input, StandardDomains.BinaryId);
        Net carry = builder.AddPin("carry", "CARRY", PinDirection.Output, StandardDomains.BinaryId);
        Net zero = builder.AddPin("zero", "ZERO", PinDirection.Output, StandardDomains.BinaryId);

        builder.AddNumericLane("a", "A", NumericFormats.UnsignedBinary.Id, PinIds("a", width));
        builder.AddNumericLane("b", "B", NumericFormats.UnsignedBinary.Id, PinIds("b", width));
        builder.AddNumericLane("y", "Y", NumericFormats.UnsignedBinary.Id, PinIds("y", width));

        Net zeroConstant = builder.AddNet("zero_constant", StandardDomains.BinaryId);
        builder.AddPrimitive("const0", new ConstantSpec(0), 60, 80);
        builder.Connect(zeroConstant, builder.Pin("const0", PrimitiveShapes.OutputPinId));

        builder.AddInstance("add", adderId, 80, 60);
        var sum = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            sum[bit] = builder.AddNet($"sum{bit}", StandardDomains.BinaryId);
            builder.Connect(a[bit], builder.Pin("add", $"a{bit}"));
            builder.Connect(b[bit], builder.Pin("add", $"b{bit}"));
            builder.Connect(sum[bit], builder.Pin("add", $"s{bit}"));
        }

        Net addCarry = builder.AddNet("add_carry", StandardDomains.BinaryId);
        builder.Connect(zeroConstant, builder.Pin("add", "cin"));
        builder.Connect(addCarry, builder.Pin("add", "cout"));

        var andNets = new Net[width];
        var orNets = new Net[width];
        var xorNets = new Net[width];
        var lowNets = new Net[width];
        var highNets = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            double x = 80 + (bit * 220);
            double yPos = 300 + (bit * 130);
            andNets[bit] = builder.AddNet($"and{bit}", StandardDomains.BinaryId);
            orNets[bit] = builder.AddNet($"or{bit}", StandardDomains.BinaryId);
            xorNets[bit] = builder.AddNet($"xor{bit}", StandardDomains.BinaryId);
            lowNets[bit] = builder.AddNet($"low{bit}", StandardDomains.BinaryId);
            highNets[bit] = builder.AddNet($"high{bit}", StandardDomains.BinaryId);

            builder.AddInstance($"and{bit}", "std.and", x, yPos);
            builder.AddInstance($"or{bit}", "std.or", x, yPos + 100);
            builder.AddInstance($"xor{bit}", "std.xor", x, yPos + 200);
            foreach (string gate in new[] { $"and{bit}", $"or{bit}", $"xor{bit}" })
            {
                builder.Connect(a[bit], builder.Pin(gate, "a"));
                builder.Connect(b[bit], builder.Pin(gate, "b"));
            }

            builder.Connect(andNets[bit], builder.Pin($"and{bit}", "y"));
            builder.Connect(orNets[bit], builder.Pin($"or{bit}", "y"));
            builder.Connect(xorNets[bit], builder.Pin($"xor{bit}", "y"));

            builder.AddInstance($"mux_low{bit}", "std.mux2", x, yPos + 300);
            builder.Connect(sum[bit], builder.Pin($"mux_low{bit}", "a"));
            builder.Connect(andNets[bit], builder.Pin($"mux_low{bit}", "b"));
            builder.Connect(op0, builder.Pin($"mux_low{bit}", "s"));
            builder.Connect(lowNets[bit], builder.Pin($"mux_low{bit}", "y"));

            builder.AddInstance($"mux_high{bit}", "std.mux2", x, yPos + 400);
            builder.Connect(orNets[bit], builder.Pin($"mux_high{bit}", "a"));
            builder.Connect(xorNets[bit], builder.Pin($"mux_high{bit}", "b"));
            builder.Connect(op0, builder.Pin($"mux_high{bit}", "s"));
            builder.Connect(highNets[bit], builder.Pin($"mux_high{bit}", "y"));

            builder.AddInstance($"mux_out{bit}", "std.mux2", x, yPos + 500);
            builder.Connect(lowNets[bit], builder.Pin($"mux_out{bit}", "a"));
            builder.Connect(highNets[bit], builder.Pin($"mux_out{bit}", "b"));
            builder.Connect(op1, builder.Pin($"mux_out{bit}", "s"));
            builder.Connect(y[bit], builder.Pin($"mux_out{bit}", "y"));
        }

        Net addOnly = builder.AddNet("add_only", StandardDomains.BinaryId);
        builder.AddInstance("carry_select", "std.nor", 80 + (width * 220), 900);
        builder.Connect(op0, builder.Pin("carry_select", "a"));
        builder.Connect(op1, builder.Pin("carry_select", "b"));
        builder.Connect(addOnly, builder.Pin("carry_select", "y"));

        builder.AddInstance("carry_and", "std.and", 260 + (width * 220), 900);
        builder.Connect(addCarry, builder.Pin("carry_and", "a"));
        builder.Connect(addOnly, builder.Pin("carry_and", "b"));
        builder.Connect(carry, builder.Pin("carry_and", "y"));

        Net any = ReduceOr(builder, y, "zero", 80, 300 + (width * 130) + 620);
        builder.AddInstance("zero_not", "std.not", 80 + (width * 110), 300 + (width * 130) + 740);
        builder.Connect(any, builder.Pin("zero_not", "a"));
        builder.Connect(zero, builder.Pin("zero_not", "y"));

        return Finish(project, builder.Definition.Id, $"alu-{width}");
    }

    public static GeneratedPart CreateCounter(int width)
    {
        RequireWidth(width);
        CircuitProject project = CreateProject($"{width}-bit counter");
        string registerId = AddRegister(project, width);
        string adderId = AddAdder(project, width);
        var builder = new DefinitionBuilder(project, $"gen.counter-{width}", $"{width}-bit counter");
        builder.Definition.Description =
            $"Generated {width}-bit modulo-2^{width} counter. On a rising CLK edge CLR has " +
            "priority, then LOAD captures D, then EN increments, otherwise the count holds. " +
            "CARRY is asserted when EN is high, LOAD is low, and the current count is the " +
            "maximum value, so it marks the edge that will wrap.";

        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.BinaryId);
        Net clr = builder.AddPin("clr", "CLR", PinDirection.Input, StandardDomains.BinaryId);
        Net load = builder.AddPin("load", "LOAD", PinDirection.Input, StandardDomains.BinaryId);
        Net en = builder.AddPin("en", "EN", PinDirection.Input, StandardDomains.BinaryId);

        var data = new Net[width];
        var count = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            data[bit] = builder.AddPin($"d{bit}", $"D{bit}", PinDirection.Input, StandardDomains.BinaryId);
            count[bit] = builder.SetInitial(
                builder.AddPin($"q{bit}", $"Q{bit}", PinDirection.Output, StandardDomains.BinaryId),
                0);
        }

        Net carry = builder.AddPin("carry", "CARRY", PinDirection.Output, StandardDomains.BinaryId);
        builder.AddNumericLane("data", "Data", NumericFormats.UnsignedBinary.Id, PinIds("d", width));
        builder.AddNumericLane("count", "Count", NumericFormats.UnsignedBinary.Id, PinIds("q", width));

        Net zero = builder.AddNet("zero", StandardDomains.BinaryId);
        Net one = builder.AddNet("one", StandardDomains.BinaryId);
        builder.AddPrimitive("const0", new ConstantSpec(0), 60, 80);
        builder.AddPrimitive("const1", new ConstantSpec(1), 60, 220);
        builder.Connect(zero, builder.Pin("const0", PrimitiveShapes.OutputPinId));
        builder.Connect(one, builder.Pin("const1", PrimitiveShapes.OutputPinId));

        builder.AddInstance("increment", adderId, 280, 80);
        var incremented = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            incremented[bit] = builder.AddNet($"incremented{bit}", StandardDomains.BinaryId);
            builder.Connect(count[bit], builder.Pin("increment", $"a{bit}"));
            builder.Connect(bit == 0 ? one : zero, builder.Pin("increment", $"b{bit}"));
            builder.Connect(incremented[bit], builder.Pin("increment", $"s{bit}"));
        }

        Net incrementCarry = builder.AddNet("increment_carry", StandardDomains.BinaryId);
        builder.Connect(zero, builder.Pin("increment", "cin"));
        builder.Connect(incrementCarry, builder.Pin("increment", "cout"));

        Net notLoad = builder.AddNet("not_load", StandardDomains.BinaryId);
        builder.AddInstance("not_load", "std.not", 300, 80);
        builder.Connect(load, builder.Pin("not_load", "a"));
        builder.Connect(notLoad, builder.Pin("not_load", "y"));

        Net counting = builder.AddNet("count_enabled", StandardDomains.BinaryId);
        builder.AddInstance("count_and", "std.and", 320 + (width * 240), 80);
        builder.Connect(en, builder.Pin("count_and", "a"));
        builder.Connect(notLoad, builder.Pin("count_and", "b"));
        builder.Connect(counting, builder.Pin("count_and", "y"));

        builder.AddInstance("carry_and", "std.and", 520 + (width * 240), 80);
        builder.Connect(incrementCarry, builder.Pin("carry_and", "a"));
        builder.Connect(counting, builder.Pin("carry_and", "b"));
        builder.Connect(carry, builder.Pin("carry_and", "y"));

        Net enabled = builder.AddNet("load_or_enable", StandardDomains.BinaryId);
        builder.AddInstance("load_or_enable", "std.or", 280, 260);
        builder.Connect(load, builder.Pin("load_or_enable", "a"));
        builder.Connect(en, builder.Pin("load_or_enable", "b"));
        builder.Connect(enabled, builder.Pin("load_or_enable", "y"));

        var next = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            next[bit] = builder.AddNet($"next{bit}", StandardDomains.BinaryId);
            builder.AddInstance($"next_mux{bit}", "std.mux2", 520 + (bit * 140), 260);
            builder.Connect(incremented[bit], builder.Pin($"next_mux{bit}", "a"));
            builder.Connect(data[bit], builder.Pin($"next_mux{bit}", "b"));
            builder.Connect(load, builder.Pin($"next_mux{bit}", "s"));
            builder.Connect(next[bit], builder.Pin($"next_mux{bit}", "y"));
        }

        builder.AddInstance("register", registerId, 520, 460);
        for (int bit = 0; bit < width; bit++)
        {
            builder.Connect(next[bit], builder.Pin("register", $"d{bit}"));
            builder.Connect(count[bit], builder.Pin("register", $"q{bit}"));
        }

        builder.Connect(enabled, builder.Pin("register", "load"));
        builder.Connect(clr, builder.Pin("register", "clr"));
        builder.Connect(clk, builder.Pin("register", "clk"));

        return Finish(project, builder.Definition.Id, $"counter-{width}");
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

    public static GeneratedPart CreateMux(int width)
    {
        RequireWidth(width);
        CircuitProject project = CreateProject($"{width}-bit 2:1 multiplexer");
        var builder = new DefinitionBuilder(project, $"gen.mux-{width}", $"{width}-bit 2:1 multiplexer");
        builder.Definition.Description =
            $"Generated {width}-bit 2:1 multiplexer. S=0 selects A, S=1 selects B; " +
            "each bit is an ordinary std.mux2.";

        var a = new Net[width];
        var b = new Net[width];
        var y = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            a[bit] = builder.AddPin($"a{bit}", $"A{bit}", PinDirection.Input, StandardDomains.BinaryId);
            b[bit] = builder.AddPin($"b{bit}", $"B{bit}", PinDirection.Input, StandardDomains.BinaryId);
            y[bit] = builder.AddPin($"y{bit}", $"Y{bit}", PinDirection.Output, StandardDomains.BinaryId);
        }

        Net select = builder.AddPin("s", "S", PinDirection.Input, StandardDomains.BinaryId);
        builder.AddNumericLane("a", "A", NumericFormats.UnsignedBinary.Id, PinIds("a", width));
        builder.AddNumericLane("b", "B", NumericFormats.UnsignedBinary.Id, PinIds("b", width));
        builder.AddNumericLane("y", "Y", NumericFormats.UnsignedBinary.Id, PinIds("y", width));

        for (int bit = 0; bit < width; bit++)
        {
            string id = $"mux{bit}";
            builder.AddInstance(id, "std.mux2", 80 + (bit * 220), 100);
            builder.Connect(a[bit], builder.Pin(id, "a"));
            builder.Connect(b[bit], builder.Pin(id, "b"));
            builder.Connect(select, builder.Pin(id, "s"));
            builder.Connect(y[bit], builder.Pin(id, "y"));
        }

        return Finish(project, builder.Definition.Id, $"mux-{width}");
    }

    public static GeneratedPart CreateComparator(int width)
    {
        RequireWidth(width);
        CircuitProject project = CreateProject($"{width}-bit unsigned comparator");
        var builder = new DefinitionBuilder(project, $"gen.cmp-{width}", $"{width}-bit unsigned comparator");
        builder.Definition.Description =
            $"Generated {width}-bit unsigned comparator. EQ is 1 when A = B, LT when A < B, " +
            "and GT when A > B. It compares from the most-significant bit down with ordinary gates, " +
            "so wider widths need no lookup with 2^(2*width) entries.";

        var a = new Net[width];
        var b = new Net[width];
        for (int bit = 0; bit < width; bit++)
        {
            a[bit] = builder.AddPin($"a{bit}", $"A{bit}", PinDirection.Input, StandardDomains.BinaryId);
            b[bit] = builder.AddPin($"b{bit}", $"B{bit}", PinDirection.Input, StandardDomains.BinaryId);
        }

        Net equal = builder.AddPin("eq", "EQ", PinDirection.Output, StandardDomains.BinaryId);
        Net less = builder.AddPin("lt", "LT", PinDirection.Output, StandardDomains.BinaryId);
        Net greater = builder.AddPin("gt", "GT", PinDirection.Output, StandardDomains.BinaryId);
        builder.AddNumericLane("a", "A", NumericFormats.UnsignedBinary.Id, PinIds("a", width));
        builder.AddNumericLane("b", "B", NumericFormats.UnsignedBinary.Id, PinIds("b", width));

        Net zero = builder.AddNet("zero", StandardDomains.BinaryId);
        Net one = builder.AddNet("one", StandardDomains.BinaryId);
        builder.AddPrimitive("const0", new ConstantSpec(0), 60, 80);
        builder.AddPrimitive("const1", new ConstantSpec(1), 60, 200);
        builder.Connect(zero, builder.Pin("const0", PrimitiveShapes.OutputPinId));
        builder.Connect(one, builder.Pin("const1", PrimitiveShapes.OutputPinId));

        Net equalSoFar = one;
        Net lessSoFar = zero;
        Net greaterSoFar = zero;
        for (int bit = width - 1; bit >= 0; bit--)
        {
            double x = 80 + ((width - 1 - bit) * 460);
            string notA = $"not_a{bit}";
            string notB = $"not_b{bit}";
            string xor = $"xor{bit}";
            string eqBit = $"eq_bit{bit}";
            string aGreater = $"a_gt_b{bit}";
            string aLess = $"a_lt_b{bit}";
            string eqGreater = $"eq_gt{bit}";
            string eqLess = $"eq_lt{bit}";
            string nextGreater = $"next_gt{bit}";
            string nextLess = $"next_lt{bit}";
            string nextEqual = $"next_eq{bit}";

            Net notANet = builder.AddNet($"{notA}_out", StandardDomains.BinaryId);
            Net notBNet = builder.AddNet($"{notB}_out", StandardDomains.BinaryId);
            Net xorNet = builder.AddNet($"{xor}_out", StandardDomains.BinaryId);
            Net eqBitNet = builder.AddNet($"{eqBit}_out", StandardDomains.BinaryId);
            Net greaterHere = builder.AddNet($"{aGreater}_out", StandardDomains.BinaryId);
            Net lessHere = builder.AddNet($"{aLess}_out", StandardDomains.BinaryId);
            Net eqGreaterNet = builder.AddNet($"{eqGreater}_out", StandardDomains.BinaryId);
            Net eqLessNet = builder.AddNet($"{eqLess}_out", StandardDomains.BinaryId);

            builder.AddInstance(notA, "std.not", x, 100);
            builder.Connect(a[bit], builder.Pin(notA, "a"));
            builder.Connect(notANet, builder.Pin(notA, "y"));

            builder.AddInstance(notB, "std.not", x, 220);
            builder.Connect(b[bit], builder.Pin(notB, "a"));
            builder.Connect(notBNet, builder.Pin(notB, "y"));

            builder.AddInstance(xor, "std.xor", x, 340);
            builder.Connect(a[bit], builder.Pin(xor, "a"));
            builder.Connect(b[bit], builder.Pin(xor, "b"));
            builder.Connect(xorNet, builder.Pin(xor, "y"));

            builder.AddInstance(eqBit, "std.not", x + 220, 340);
            builder.Connect(xorNet, builder.Pin(eqBit, "a"));
            builder.Connect(eqBitNet, builder.Pin(eqBit, "y"));

            builder.AddInstance(aGreater, "std.and", x + 220, 100);
            builder.Connect(a[bit], builder.Pin(aGreater, "a"));
            builder.Connect(notBNet, builder.Pin(aGreater, "b"));
            builder.Connect(greaterHere, builder.Pin(aGreater, "y"));

            builder.AddInstance(aLess, "std.and", x + 220, 220);
            builder.Connect(notANet, builder.Pin(aLess, "a"));
            builder.Connect(b[bit], builder.Pin(aLess, "b"));
            builder.Connect(lessHere, builder.Pin(aLess, "y"));

            builder.AddInstance(eqGreater, "std.and", x + 440, 100);
            builder.Connect(equalSoFar, builder.Pin(eqGreater, "a"));
            builder.Connect(greaterHere, builder.Pin(eqGreater, "b"));
            builder.Connect(eqGreaterNet, builder.Pin(eqGreater, "y"));

            builder.AddInstance(eqLess, "std.and", x + 440, 220);
            builder.Connect(equalSoFar, builder.Pin(eqLess, "a"));
            builder.Connect(lessHere, builder.Pin(eqLess, "b"));
            builder.Connect(eqLessNet, builder.Pin(eqLess, "y"));

            Net nextGreaterNet = bit == 0
                ? greater
                : builder.AddNet($"greater{bit}", StandardDomains.BinaryId);
            builder.AddInstance(nextGreater, "std.or", x + 660, 100);
            builder.Connect(greaterSoFar, builder.Pin(nextGreater, "a"));
            builder.Connect(eqGreaterNet, builder.Pin(nextGreater, "b"));
            builder.Connect(nextGreaterNet, builder.Pin(nextGreater, "y"));

            Net nextLessNet = bit == 0
                ? less
                : builder.AddNet($"less{bit}", StandardDomains.BinaryId);
            builder.AddInstance(nextLess, "std.or", x + 660, 220);
            builder.Connect(lessSoFar, builder.Pin(nextLess, "a"));
            builder.Connect(eqLessNet, builder.Pin(nextLess, "b"));
            builder.Connect(nextLessNet, builder.Pin(nextLess, "y"));

            Net nextEqualNet = bit == 0
                ? equal
                : builder.AddNet($"equal{bit}", StandardDomains.BinaryId);
            builder.AddInstance(nextEqual, "std.and", x + 660, 340);
            builder.Connect(equalSoFar, builder.Pin(nextEqual, "a"));
            builder.Connect(eqBitNet, builder.Pin(nextEqual, "b"));
            builder.Connect(nextEqualNet, builder.Pin(nextEqual, "y"));

            equalSoFar = nextEqualNet;
            lessSoFar = nextLessNet;
            greaterSoFar = nextGreaterNet;
        }

        return Finish(project, builder.Definition.Id, $"cmp-{width}");
    }

    private static string AddRegister(CircuitProject project, int width)
    {
        string id = $"gen.reg-{width}";
        if (project.FindDefinition(id) is not null)
        {
            return id;
        }

        var builder = new DefinitionBuilder(project, id, $"{width}-bit register");
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

        return id;
    }

    private static string AddAdder(CircuitProject project, int width)
    {
        string id = $"gen.adder-{width}";
        if (project.FindDefinition(id) is not null)
        {
            return id;
        }

        var builder = new DefinitionBuilder(project, id, $"{width}-bit ripple-carry adder");
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

        return id;
    }

    private static Net ReduceOr(
        DefinitionBuilder builder,
        Net[] inputs,
        string prefix,
        double x,
        double y)
    {
        if (inputs.Length == 1)
        {
            return inputs[0];
        }

        List<Net> level = [.. inputs];
        for (int stage = 0; level.Count > 1; stage++)
        {
            var next = new List<Net>((level.Count + 1) / 2);
            for (int i = 0; i < level.Count; i += 2)
            {
                if (i + 1 == level.Count)
                {
                    next.Add(level[i]);
                    continue;
                }

                string id = $"{prefix}_or{stage}_{i / 2}";
                Net output = builder.AddNet($"{id}_out", StandardDomains.BinaryId);
                builder.AddInstance(id, "std.or", x + (i * 110), y + (stage * 130));
                builder.Connect(level[i], builder.Pin(id, "a"));
                builder.Connect(level[i + 1], builder.Pin(id, "b"));
                builder.Connect(output, builder.Pin(id, "y"));
                next.Add(output);
            }

            level = next;
        }

        return level[0];
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
