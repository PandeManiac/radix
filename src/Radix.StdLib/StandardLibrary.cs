using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.StdLib;

/// <summary>
/// Standard components assembled from kernel primitives. Nothing here is known to
/// the engine: gates are lookup tables, latches and flip-flops are wired from those
/// gates, and arithmetic is hierarchical. Projects keep their own copies, so users can
/// inspect and modify every component.
/// </summary>
public static class StandardLibrary
{
    public const long GateDelay = 1;

    public const string NandId = "std.nand";
    public const string NotId = "std.not";
    public const string DFlipFlopId = "std.dff";
    public const string HalfAdderId = "std.half-adder";
    public const string BalancedTernaryFullAdderId = "std.bt-full-adder";
    public const string BalancedTernaryAdder2Id = "std.bt-adder-2";
    public const string TernaryLatchId = "std.t-latch";
    public const string TernaryDFlipFlopId = "std.t-dff";
    public const string BalancedTernaryLatchId = "std.bt-latch";
    public const string BalancedTernaryDFlipFlopId = "std.bt-dff";
    public const string BalancedTernaryRegister2Id = "std.bt-reg-2";
    public const string QuaternaryLatchId = "std.q4-latch";
    public const string QuaternaryDFlipFlopId = "std.q4-dff";
    public const string QuaternaryLoadMuxId = "std.q4-load-mux";
    public const string QuaternaryRegister2Id = "std.q4-reg-2";
    public const string QuaternaryIncId = "std.q4-inc";
    public const string QuaternaryCarryId = "std.q4-carry";
    public const string QuaternaryAddId = "std.q4-add";
    public const string QuaternaryFullAdderId = "std.q4-full-adder";
    public const string QuaternaryAdder2Id = "std.q4-adder-2";
    public const string QuaternaryMux4Id = "std.q4-mux4";
    public const string QuaternaryRam4x2Id = "std.q4-ram-4x2";
    public const string TernaryFullAdderId = "std.t-full-adder";
    public const string TernaryAdder2Id = "std.t-adder-2";
    public const string TernaryRegister2Id = "std.t-reg-2";
    public const string TernaryMux3Id = "std.t-mux3";
    public const string TernaryRam3x2Id = "std.t-ram-3x2";
    public const string TernaryRam9x2Id = "std.t-ram-9x2";
    public const string Ram16x4Id = "std.ram-16x4";
    public const string Adder8Id = "std.adder-8";
    public const string Multiplier4Id = "std.mult-4";
    public const string Register8Id = "std.reg-8";
    public const string Alu8Id = "std.alu-8";

    private static readonly (string Id, string Name, int Inputs, int[] Table)[] BinaryGates =
    [
        ("std.not", "NOT", 1, [1, 0]),
        ("std.buf", "BUF", 1, [0, 1]),
        ("std.and", "AND", 2, [0, 0, 0, 1]),
        ("std.or", "OR", 2, [0, 1, 1, 1]),
        ("std.nand", "NAND", 2, [1, 1, 1, 0]),
        ("std.nor", "NOR", 2, [1, 0, 0, 0]),
        ("std.xor", "XOR", 2, [0, 1, 1, 0]),
        ("std.xnor", "XNOR", 2, [1, 0, 0, 1]),
    ];

    /// <summary>Adds missing domain definitions used by the standard library.</summary>
    public static void EnsureDomains(CircuitProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (SignalDomain domain in StandardDomains.All())
        {
            if (project.FindDomain(domain.Id) is null)
            {
                project.Domains.Add(domain);
            }
        }
    }

    /// <summary>Installs every standard definition that is not already present.</summary>
    public static IReadOnlyList<string> Install(CircuitProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        EnsureDomains(project);

        var added = new List<string>();

        foreach ((string id, string name, int inputs, int[] table) in BinaryGates)
        {
            if (project.FindDefinition(id) is not null)
            {
                continue;
            }

            string[] pinIds = inputs == 1 ? ["a"] : ["a", "b"];
            string[] pinNames = inputs == 1 ? ["A"] : ["A", "B"];
            AddLookupDefinition(project, id, name, StandardDomains.BinaryId, pinIds, pinNames, table);
            added.Add(id);
        }

        if (project.FindDefinition("std.d-latch") is null)
        {
            AddDLatch(project);
            added.Add("std.d-latch");
        }

        if (project.FindDefinition(DFlipFlopId) is null)
        {
            AddDFlipFlop(project);
            added.Add(DFlipFlopId);
        }

        if (project.FindDefinition(HalfAdderId) is null)
        {
            AddHalfAdder(project);
            added.Add(HalfAdderId);
        }

        if (project.FindDefinition("std.full-adder") is null)
        {
            AddFullAdder(project);
            added.Add("std.full-adder");
        }

        if (project.FindDefinition("std.ripple-adder-4") is null)
        {
            AddRippleAdder4(project);
            added.Add("std.ripple-adder-4");
        }

        if (project.FindDefinition(Adder8Id) is null)
        {
            AddAdder8(project);
            added.Add(Adder8Id);
        }

        if (project.FindDefinition(Multiplier4Id) is null)
        {
            AddMultiplier4(project);
            added.Add(Multiplier4Id);
        }

        if (project.FindDefinition("std.mux2") is null)
        {
            AddLookupDefinition(
                project,
                "std.mux2",
                "2:1 multiplexer",
                StandardDomains.BinaryId,
                ["a", "b", "s"],
                ["A", "B", "Select"],
                [0, 0, 0, 1, 1, 0, 1, 1]);
            added.Add("std.mux2");
        }

        if (project.FindDefinition("std.reg-4") is null)
        {
            AddRegister4(project);
            added.Add("std.reg-4");
        }

        if (project.FindDefinition(Register8Id) is null)
        {
            AddRegister8(project);
            added.Add(Register8Id);
        }

        if (project.FindDefinition(Alu8Id) is null)
        {
            AddAlu8(project);
            added.Add(Alu8Id);
        }

        if (project.FindDefinition(Ram16x4Id) is null)
        {
            AddRam16x4(project);
            added.Add(Ram16x4Id);
        }

        added.AddRange(AddTernaryDefinitions(project));
        added.AddRange(AddQuaternaryDefinitions(project));
        return added;
    }

    /// <summary>Creates a standalone NAND with a configurable delay (useful in tests).</summary>
    public static CircuitDefinition CreateNand(string id = NandId, long delay = GateDelay)
    {
        var project = new CircuitProject { Name = "scratch" };
        EnsureDomains(project);
        AddLookupDefinition(project, id, "NAND", StandardDomains.BinaryId, ["a", "b"], ["A", "B"], [1, 1, 1, 0], delay);
        return project.RequireDefinition(id);
    }

    private static void AddLookupDefinition(
        CircuitProject project,
        string id,
        string name,
        string domainId,
        string[] inputPinIds,
        string[] inputPinNames,
        IReadOnlyList<int> table,
        long delay = GateDelay,
        string? description = null)
    {
        var builder = new DefinitionBuilder(project, id, name);
        builder.Definition.Description = description;

        var inputNets = new Net[inputPinIds.Length];
        for (int i = 0; i < inputPinIds.Length; i++)
        {
            inputNets[i] = builder.AddPin(inputPinIds[i], inputPinNames[i], PinDirection.Input, domainId);
        }

        Net output = builder.AddPin("y", "Y", PinDirection.Output, domainId);
        ComponentInstance gate = builder.AddPrimitive(
            "g",
            new LookupSpec(inputPinIds.Length, table, delay),
            x: 180,
            y: 80);
        for (int i = 0; i < inputNets.Length; i++)
        {
            builder.Connect(inputNets[i], builder.Pin(gate.Id, PrimitiveShapes.InputPinId(i)));
        }

        builder.Connect(output, builder.Pin(gate.Id, PrimitiveShapes.OutputPinId));
    }

    private static void AddDLatch(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, "std.d-latch", "D latch");
        builder.Definition.Description =
            "Level-sensitive D latch built from four NAND gates. Q/QN are seeded to 0/1 at power-on.";

        Net d = builder.AddPin("d", "D", PinDirection.Input, StandardDomains.BinaryId);
        Net e = builder.AddPin("e", "E", PinDirection.Input, StandardDomains.BinaryId);
        Net q = builder.SetInitial(builder.AddPin("q", "Q", PinDirection.Output, StandardDomains.BinaryId), 0);
        Net qn = builder.SetInitial(builder.AddPin("qn", "QN", PinDirection.Output, StandardDomains.BinaryId), 1);

        Net s = builder.AddNet("s", StandardDomains.BinaryId);
        Net r = builder.AddNet("r", StandardDomains.BinaryId);

        builder.AddInstance("g1", NandId, 140, 60);
        builder.AddInstance("g2", NandId, 140, 200);
        builder.AddInstance("g3", NandId, 400, 60);
        builder.AddInstance("g4", NandId, 400, 200);

        builder.Connect(d, builder.Pin("g1", "a"));
        builder.Connect(e, builder.Pin("g1", "b"), builder.Pin("g2", "b"));
        builder.Connect(s, builder.Pin("g1", "y"), builder.Pin("g2", "a"), builder.Pin("g3", "a"));
        builder.Connect(r, builder.Pin("g2", "y"), builder.Pin("g4", "a"));
        builder.Connect(qn, builder.Pin("g3", "b"));
        builder.Connect(q, builder.Pin("g4", "b"));
        builder.Connect(q, builder.Pin("g3", "y"));
        builder.Connect(qn, builder.Pin("g4", "y"));
    }

    private static void AddDFlipFlop(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, DFlipFlopId, "D flip-flop");
        builder.Definition.Description =
            "Positive-edge-triggered D flip-flop built from two gated D latches (master/slave) and an inverter. " +
            "Requires a driven clock; Q/QN power up at 0/1.";

        Net d = builder.AddPin("d", "D", PinDirection.Input, StandardDomains.BinaryId);
        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.BinaryId);
        Net q = builder.SetInitial(builder.AddPin("q", "Q", PinDirection.Output, StandardDomains.BinaryId), 0);
        Net qn = builder.SetInitial(builder.AddPin("qn", "QN", PinDirection.Output, StandardDomains.BinaryId), 1);

        Net clkN = builder.AddNet("clk_n", StandardDomains.BinaryId);
        Net masterQ = builder.SetInitial(builder.AddNet("master_q", StandardDomains.BinaryId), 0);
        Net masterQn = builder.SetInitial(builder.AddNet("master_qn", StandardDomains.BinaryId), 1);

        builder.AddInstance("inv", NotId, 80, 40);
        builder.AddInstance("master", "std.d-latch", 340, 40);
        builder.AddInstance("slave", "std.d-latch", 600, 160);

        builder.Connect(clk, builder.Pin("inv", "a"));
        builder.Connect(clkN, builder.Pin("inv", "y"), builder.Pin("master", "e"));
        builder.Connect(d, builder.Pin("master", "d"));
        builder.Connect(masterQ, builder.Pin("master", "q"), builder.Pin("slave", "d"));
        builder.Connect(masterQn, builder.Pin("master", "qn"));
        builder.Connect(clk, builder.Pin("slave", "e"));
        builder.Connect(q, builder.Pin("slave", "q"));
        builder.Connect(qn, builder.Pin("slave", "qn"));
    }

    private static void AddHalfAdder(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, HalfAdderId, "Half adder");
        Net a = builder.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net b = builder.AddPin("b", "B", PinDirection.Input, StandardDomains.BinaryId);
        Net s = builder.AddPin("s", "S", PinDirection.Output, StandardDomains.BinaryId);
        Net c = builder.AddPin("c", "C", PinDirection.Output, StandardDomains.BinaryId);

        builder.AddInstance("xor", "std.xor", 180, 60);
        builder.AddInstance("and", "std.and", 180, 180);
        builder.Connect(a, builder.Pin("xor", "a"), builder.Pin("and", "a"));
        builder.Connect(b, builder.Pin("xor", "b"), builder.Pin("and", "b"));
        builder.Connect(s, builder.Pin("xor", "y"));
        builder.Connect(c, builder.Pin("and", "y"));
    }

    private static void AddFullAdder(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, "std.full-adder", "Full adder");
        Net a = builder.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net b = builder.AddPin("b", "B", PinDirection.Input, StandardDomains.BinaryId);
        Net cin = builder.AddPin("cin", "CIN", PinDirection.Input, StandardDomains.BinaryId);
        Net s = builder.AddPin("s", "S", PinDirection.Output, StandardDomains.BinaryId);
        Net cout = builder.AddPin("cout", "COUT", PinDirection.Output, StandardDomains.BinaryId);

        Net sum1 = builder.AddNet("sum1", StandardDomains.BinaryId);
        Net carry1 = builder.AddNet("carry1", StandardDomains.BinaryId);
        Net carry2 = builder.AddNet("carry2", StandardDomains.BinaryId);

        builder.AddInstance("h1", HalfAdderId, 80, 60);
        builder.AddInstance("h2", HalfAdderId, 340, 60);
        builder.AddInstance("or", "std.or", 600, 200);

        builder.Connect(a, builder.Pin("h1", "a"));
        builder.Connect(b, builder.Pin("h1", "b"));
        builder.Connect(sum1, builder.Pin("h1", "s"), builder.Pin("h2", "a"));
        builder.Connect(cin, builder.Pin("h2", "b"));
        builder.Connect(carry1, builder.Pin("h1", "c"), builder.Pin("or", "a"));
        builder.Connect(carry2, builder.Pin("h2", "c"), builder.Pin("or", "b"));
        builder.Connect(s, builder.Pin("h2", "s"));
        builder.Connect(cout, builder.Pin("or", "y"));
    }

    private static void AddRippleAdder4(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, "std.ripple-adder-4", "4-bit ripple-carry adder");
        var aNets = new Net[4];
        var bNets = new Net[4];
        var sNets = new Net[4];

        for (int i = 0; i < 4; i++)
        {
            aNets[i] = builder.AddPin($"a{i}", $"A{i}", PinDirection.Input, StandardDomains.BinaryId);
            bNets[i] = builder.AddPin($"b{i}", $"B{i}", PinDirection.Input, StandardDomains.BinaryId);
            sNets[i] = builder.AddPin($"s{i}", $"S{i}", PinDirection.Output, StandardDomains.BinaryId);
        }

        Net cout = builder.AddPin("cout", "COUT", PinDirection.Output, StandardDomains.BinaryId);
        var carries = new Net[5];
        carries[0] = builder.AddNet("c0", StandardDomains.BinaryId);
        carries[0].InitialValue = 0;
        for (int i = 1; i < 4; i++)
        {
            carries[i] = builder.AddNet($"c{i}", StandardDomains.BinaryId);
        }

        carries[4] = cout;

        for (int i = 0; i < 4; i++)
        {
            builder.AddInstance($"fa{i}", "std.full-adder", 80 + (i * 260), 100);
            builder.Connect(aNets[i], builder.Pin($"fa{i}", "a"));
            builder.Connect(bNets[i], builder.Pin($"fa{i}", "b"));
            builder.Connect(carries[i], builder.Pin($"fa{i}", "cin"));
            builder.Connect(sNets[i], builder.Pin($"fa{i}", "s"));
            builder.Connect(carries[i + 1], builder.Pin($"fa{i}", "cout"));
        }
    }

    /// <summary>Eight full adders chained by carry, with an exposed carry-in and carry-out.</summary>
    private static void AddAdder8(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, Adder8Id, "8-bit ripple-carry adder");
        builder.Definition.Description =
            "Eight full adders in a carry chain, exposing CIN and COUT so wider adders can be " +
            "composed without changing the engine.";

        var a = new Net[8];
        var b = new Net[8];
        var s = new Net[8];
        for (int i = 0; i < 8; i++)
        {
            a[i] = builder.AddPin($"a{i}", $"A{i}", PinDirection.Input, StandardDomains.BinaryId);
            b[i] = builder.AddPin($"b{i}", $"B{i}", PinDirection.Input, StandardDomains.BinaryId);
            s[i] = builder.AddPin($"s{i}", $"S{i}", PinDirection.Output, StandardDomains.BinaryId);
        }

        Net cin = builder.AddPin("cin", "CIN", PinDirection.Input, StandardDomains.BinaryId);
        Net cout = builder.AddPin("cout", "COUT", PinDirection.Output, StandardDomains.BinaryId);
        var carries = new Net[9];
        carries[0] = cin;
        for (int i = 1; i < 8; i++)
        {
            carries[i] = builder.AddNet($"c{i}", StandardDomains.BinaryId);
        }

        carries[8] = cout;

        for (int i = 0; i < 8; i++)
        {
            builder.AddInstance($"fa{i}", "std.full-adder", 80 + (i * 240), 100);
            builder.Connect(a[i], builder.Pin($"fa{i}", "a"));
            builder.Connect(b[i], builder.Pin($"fa{i}", "b"));
            builder.Connect(carries[i], builder.Pin($"fa{i}", "cin"));
            builder.Connect(s[i], builder.Pin($"fa{i}", "s"));
            builder.Connect(carries[i + 1], builder.Pin($"fa{i}", "cout"));
        }
    }

    /// <summary>
    /// Four-by-four array multiplier. Sixteen AND gates form the partial products,
    /// and three 8-bit adders accumulate them already shifted by their row. The
    /// product of two four-bit values fits in eight bits, so no overflow output is
    /// exposed and the top bit stays 0 for every input pair.
    /// </summary>
    private static void AddMultiplier4(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, Multiplier4Id, "4 × 4 multiplier");
        builder.Definition.Description =
            "An array multiplier built from sixteen AND gates and three 8-bit adders. " +
            "Combinational; the 8-bit product of two 4-bit operands always fits.";

        var a = new Net[4];
        var b = new Net[4];
        var p = new Net[8];
        for (int i = 0; i < 4; i++)
        {
            a[i] = builder.AddPin($"a{i}", $"A{i}", PinDirection.Input, StandardDomains.BinaryId);
        }

        for (int i = 0; i < 4; i++)
        {
            b[i] = builder.AddPin($"b{i}", $"B{i}", PinDirection.Input, StandardDomains.BinaryId);
        }

        for (int i = 0; i < 8; i++)
        {
            p[i] = builder.AddPin($"p{i}", $"P{i}", PinDirection.Output, StandardDomains.BinaryId);
        }

        Net zero = builder.AddNet("zero", StandardDomains.BinaryId);
        builder.AddPrimitive("zero_source", new ConstantSpec(0), 40, 40);
        builder.Connect(zero, builder.Pin("zero_source", "out"));

        var partial = new Net[4, 4];
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                string id = $"and{i}{j}";
                partial[i, j] = builder.AddNet($"{id}_out", StandardDomains.BinaryId);
                builder.AddPrimitive(id, new LookupSpec(2, [0, 0, 0, 1]), 80 + (j * 240), 80 + (i * 120));
                builder.Connect(a[i], builder.Pin(id, "in0"));
                builder.Connect(b[j], builder.Pin(id, "in1"));
                builder.Connect(partial[i, j], builder.Pin(id, "out"));
            }
        }

        Net[] Row(int shiftedBy)
        {
            var row = new Net[8];
            for (int bit = 0; bit < 8; bit++)
            {
                int multiplicandBit = bit - shiftedBy;
                row[bit] = multiplicandBit is >= 0 and < 4 ? partial[multiplicandBit, shiftedBy] : zero;
            }

            return row;
        }

        Net[] AddEightBits(string instanceId, Net[] left, Net[] right, Net[]? sum, double y)
        {
            Net[] outputs = sum ?? new Net[8];
            if (sum is null)
            {
                for (int i = 0; i < 8; i++)
                {
                    outputs[i] = builder.AddNet($"{instanceId}_s{i}", StandardDomains.BinaryId);
                }
            }

            Net cout = builder.AddNet($"{instanceId}_cout", StandardDomains.BinaryId);
            builder.AddInstance(instanceId, Adder8Id, 120, y);
            for (int i = 0; i < 8; i++)
            {
                builder.Connect(left[i], builder.Pin(instanceId, $"a{i}"));
                builder.Connect(right[i], builder.Pin(instanceId, $"b{i}"));
                builder.Connect(outputs[i], builder.Pin(instanceId, $"s{i}"));
            }

            builder.Connect(zero, builder.Pin(instanceId, "cin"));
            builder.Connect(cout, builder.Pin(instanceId, "cout"));
            return outputs;
        }

        Net[] low = AddEightBits("add_low", Row(0), Row(1), sum: null, 620);
        Net[] mid = AddEightBits("add_mid", low, Row(2), sum: null, 820);
        AddEightBits("add_high", mid, Row(3), p, 1020);
    }

    private static void AddRegister4(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, "std.reg-4", "4-bit register");
        builder.Definition.Description =
            "Four D flip-flops with synchronous load and clear. CLR has priority over LOAD; " +
            "all bits update on the rising edge of CLK and power up at 0.";

        Net load = builder.AddPin("load", "LOAD", PinDirection.Input, StandardDomains.BinaryId);
        Net clr = builder.AddPin("clr", "CLR", PinDirection.Input, StandardDomains.BinaryId);
        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.BinaryId);
        var data = new Net[4];
        var output = new Net[4];
        for (int i = 0; i < 4; i++)
        {
            data[i] = builder.AddPin($"d{i}", $"D{i}", PinDirection.Input, StandardDomains.BinaryId);
            output[i] = builder.SetInitial(
                builder.AddPin($"q{i}", $"Q{i}", PinDirection.Output, StandardDomains.BinaryId),
                0);
        }

        Net zero = builder.AddNet("zero", StandardDomains.BinaryId);
        builder.AddPrimitive("const0", new ConstantSpec(0), 60, 360);
        builder.Connect(zero, builder.Pin("const0", PrimitiveShapes.OutputPinId));

        for (int i = 0; i < 4; i++)
        {
            Net held = builder.AddNet($"held{i}", StandardDomains.BinaryId);
            Net next = builder.AddNet($"next{i}", StandardDomains.BinaryId);
            double y = 60 + (i * 120);

            builder.AddInstance($"mux_hold{i}", "std.mux2", 300, y);
            builder.Connect(output[i], builder.Pin($"mux_hold{i}", "a"));
            builder.Connect(data[i], builder.Pin($"mux_hold{i}", "b"));
            builder.Connect(load, builder.Pin($"mux_hold{i}", "s"));
            builder.Connect(held, builder.Pin($"mux_hold{i}", "y"));

            builder.AddInstance($"mux_clear{i}", "std.mux2", 560, y);
            builder.Connect(held, builder.Pin($"mux_clear{i}", "a"));
            builder.Connect(zero, builder.Pin($"mux_clear{i}", "b"));
            builder.Connect(clr, builder.Pin($"mux_clear{i}", "s"));
            builder.Connect(next, builder.Pin($"mux_clear{i}", "y"));

            builder.AddInstance($"ff{i}", DFlipFlopId, 820, y);
            builder.Connect(next, builder.Pin($"ff{i}", "d"));
            builder.Connect(clk, builder.Pin($"ff{i}", "clk"));
            builder.Connect(output[i], builder.Pin($"ff{i}", "q"));
        }
    }

    /// <summary>Two 4-bit registers sharing clock, load, and clear.</summary>
    private static void AddRegister8(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, Register8Id, "8-bit register");
        builder.Definition.Description =
            "Two 4-bit registers sharing LOAD, CLR, and CLK. CLR wins over LOAD; all bits " +
            "update on the rising clock edge and power up at 0.";

        Net load = builder.AddPin("load", "LOAD", PinDirection.Input, StandardDomains.BinaryId);
        Net clr = builder.AddPin("clr", "CLR", PinDirection.Input, StandardDomains.BinaryId);
        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.BinaryId);
        var data = new Net[8];
        var output = new Net[8];
        for (int i = 0; i < 8; i++)
        {
            data[i] = builder.AddPin($"d{i}", $"D{i}", PinDirection.Input, StandardDomains.BinaryId);
            output[i] = builder.SetInitial(
                builder.AddPin($"q{i}", $"Q{i}", PinDirection.Output, StandardDomains.BinaryId),
                0);
        }

        builder.AddInstance("lo", "std.reg-4", 80, 100);
        builder.AddInstance("hi", "std.reg-4", 460, 100);
        foreach (string half in new[] { "lo", "hi" })
        {
            builder.Connect(load, builder.Pin(half, "load"));
            builder.Connect(clr, builder.Pin(half, "clr"));
            builder.Connect(clk, builder.Pin(half, "clk"));
        }

        for (int i = 0; i < 8; i++)
        {
            string half = i < 4 ? "lo" : "hi";
            int offset = i < 4 ? i : i - 4;
            builder.Connect(data[i], builder.Pin(half, $"d{offset}"));
            builder.Connect(output[i], builder.Pin(half, $"q{offset}"));
        }
    }

    /// <summary>
    /// Eight-bit ALU: add, AND, OR, and XOR selected by OP1:OP0, with carry and zero flags.
    /// </summary>
    private static void AddAlu8(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, Alu8Id, "8-bit ALU");
        builder.Definition.Description =
            "Eight-bit arithmetic/logic unit. OP1:OP0 selects ADD (00), AND (01), OR (10), " +
            "XOR (11). CARRY is the adder carry for ADD and 0 for logic operations; ZERO is " +
            "asserted when every result bit is 0. Built from the 8-bit adder, gates, and muxes.";

        var a = new Net[8];
        var b = new Net[8];
        var y = new Net[8];
        for (int i = 0; i < 8; i++)
        {
            a[i] = builder.AddPin($"a{i}", $"A{i}", PinDirection.Input, StandardDomains.BinaryId);
            b[i] = builder.AddPin($"b{i}", $"B{i}", PinDirection.Input, StandardDomains.BinaryId);
            y[i] = builder.AddPin($"y{i}", $"Y{i}", PinDirection.Output, StandardDomains.BinaryId);
        }

        Net op0 = builder.AddPin("op0", "OP0", PinDirection.Input, StandardDomains.BinaryId);
        Net op1 = builder.AddPin("op1", "OP1", PinDirection.Input, StandardDomains.BinaryId);
        Net carry = builder.AddPin("carry", "CARRY", PinDirection.Output, StandardDomains.BinaryId);
        Net zero = builder.AddPin("zero", "ZERO", PinDirection.Output, StandardDomains.BinaryId);

        Net constZero = builder.AddNet("const_zero", StandardDomains.BinaryId);
        builder.AddPrimitive("const0", new ConstantSpec(0), 60, 1560);
        builder.Connect(constZero, builder.Pin("const0", PrimitiveShapes.OutputPinId));

        builder.AddInstance("add", Adder8Id, 80, 60);
        var sum = new Net[8];
        for (int i = 0; i < 8; i++)
        {
            sum[i] = builder.AddNet($"sum{i}", StandardDomains.BinaryId);
            builder.Connect(a[i], builder.Pin("add", $"a{i}"));
            builder.Connect(b[i], builder.Pin("add", $"b{i}"));
            builder.Connect(sum[i], builder.Pin("add", $"s{i}"));
        }

        Net addCarry = builder.AddNet("add_carry", StandardDomains.BinaryId);
        builder.Connect(constZero, builder.Pin("add", "cin"));
        builder.Connect(addCarry, builder.Pin("add", "cout"));

        var andNets = new Net[8];
        var orNets = new Net[8];
        var xorNets = new Net[8];
        var lowNets = new Net[8];
        var highNets = new Net[8];
        for (int i = 0; i < 8; i++)
        {
            double x = 80 + (i * 220);
            andNets[i] = builder.AddNet($"and{i}", StandardDomains.BinaryId);
            orNets[i] = builder.AddNet($"or{i}", StandardDomains.BinaryId);
            xorNets[i] = builder.AddNet($"xor{i}", StandardDomains.BinaryId);
            lowNets[i] = builder.AddNet($"low{i}", StandardDomains.BinaryId);
            highNets[i] = builder.AddNet($"high{i}", StandardDomains.BinaryId);

            builder.AddInstance($"and{i}", "std.and", x, 380);
            builder.AddInstance($"or{i}", "std.or", x, 520);
            builder.AddInstance($"xor{i}", "std.xor", x, 660);
            foreach (string gate in new[] { $"and{i}", $"or{i}", $"xor{i}" })
            {
                builder.Connect(a[i], builder.Pin(gate, "a"));
                builder.Connect(b[i], builder.Pin(gate, "b"));
            }

            builder.Connect(andNets[i], builder.Pin($"and{i}", "y"));
            builder.Connect(orNets[i], builder.Pin($"or{i}", "y"));
            builder.Connect(xorNets[i], builder.Pin($"xor{i}", "y"));

            builder.AddInstance($"mux_low{i}", "std.mux2", x, 800);
            builder.Connect(sum[i], builder.Pin($"mux_low{i}", "a"));
            builder.Connect(andNets[i], builder.Pin($"mux_low{i}", "b"));
            builder.Connect(op0, builder.Pin($"mux_low{i}", "s"));
            builder.Connect(lowNets[i], builder.Pin($"mux_low{i}", "y"));

            builder.AddInstance($"mux_high{i}", "std.mux2", x, 940);
            builder.Connect(orNets[i], builder.Pin($"mux_high{i}", "a"));
            builder.Connect(xorNets[i], builder.Pin($"mux_high{i}", "b"));
            builder.Connect(op0, builder.Pin($"mux_high{i}", "s"));
            builder.Connect(highNets[i], builder.Pin($"mux_high{i}", "y"));

            builder.AddInstance($"mux_out{i}", "std.mux2", x, 1080);
            builder.Connect(lowNets[i], builder.Pin($"mux_out{i}", "a"));
            builder.Connect(highNets[i], builder.Pin($"mux_out{i}", "b"));
            builder.Connect(op1, builder.Pin($"mux_out{i}", "s"));
            builder.Connect(y[i], builder.Pin($"mux_out{i}", "y"));
        }

        Net addOnly = builder.AddNet("add_only", StandardDomains.BinaryId);
        builder.AddInstance("carry_select", "std.nor", 1880, 800);
        builder.Connect(op0, builder.Pin("carry_select", "a"));
        builder.Connect(op1, builder.Pin("carry_select", "b"));
        builder.Connect(addOnly, builder.Pin("carry_select", "y"));

        builder.AddInstance("carry_and", "std.and", 2140, 800);
        builder.Connect(addCarry, builder.Pin("carry_and", "a"));
        builder.Connect(addOnly, builder.Pin("carry_and", "b"));
        builder.Connect(carry, builder.Pin("carry_and", "y"));

        var or01 = builder.AddNet("zero_n01", StandardDomains.BinaryId);
        var or23 = builder.AddNet("zero_n23", StandardDomains.BinaryId);
        var or45 = builder.AddNet("zero_n45", StandardDomains.BinaryId);
        var or67 = builder.AddNet("zero_n67", StandardDomains.BinaryId);
        var or0123 = builder.AddNet("zero_n0123", StandardDomains.BinaryId);
        var or4567 = builder.AddNet("zero_n4567", StandardDomains.BinaryId);
        var any = builder.AddNet("zero_nany", StandardDomains.BinaryId);

        builder.AddInstance("zero_or01", "std.or", 80, 1240);
        builder.Connect(y[0], builder.Pin("zero_or01", "a"));
        builder.Connect(y[1], builder.Pin("zero_or01", "b"));
        builder.Connect(or01, builder.Pin("zero_or01", "y"));

        builder.AddInstance("zero_or23", "std.or", 320, 1240);
        builder.Connect(y[2], builder.Pin("zero_or23", "a"));
        builder.Connect(y[3], builder.Pin("zero_or23", "b"));
        builder.Connect(or23, builder.Pin("zero_or23", "y"));

        builder.AddInstance("zero_or45", "std.or", 560, 1240);
        builder.Connect(y[4], builder.Pin("zero_or45", "a"));
        builder.Connect(y[5], builder.Pin("zero_or45", "b"));
        builder.Connect(or45, builder.Pin("zero_or45", "y"));

        builder.AddInstance("zero_or67", "std.or", 800, 1240);
        builder.Connect(y[6], builder.Pin("zero_or67", "a"));
        builder.Connect(y[7], builder.Pin("zero_or67", "b"));
        builder.Connect(or67, builder.Pin("zero_or67", "y"));

        builder.AddInstance("zero_or0123", "std.or", 200, 1380);
        builder.Connect(or01, builder.Pin("zero_or0123", "a"));
        builder.Connect(or23, builder.Pin("zero_or0123", "b"));
        builder.Connect(or0123, builder.Pin("zero_or0123", "y"));

        builder.AddInstance("zero_or4567", "std.or", 680, 1380);
        builder.Connect(or45, builder.Pin("zero_or4567", "a"));
        builder.Connect(or67, builder.Pin("zero_or4567", "b"));
        builder.Connect(or4567, builder.Pin("zero_or4567", "y"));

        builder.AddInstance("zero_or_any", "std.or", 440, 1520);
        builder.Connect(or0123, builder.Pin("zero_or_any", "a"));
        builder.Connect(or4567, builder.Pin("zero_or_any", "b"));
        builder.Connect(any, builder.Pin("zero_or_any", "y"));

        builder.AddInstance("zero_not", "std.not", 700, 1520);
        builder.Connect(any, builder.Pin("zero_not", "a"));
        builder.Connect(zero, builder.Pin("zero_not", "y"));
    }

    /// <summary>
    /// A 16-word by 4-bit asynchronous-read, synchronous-write RAM. Every storage
    /// cell is a D flip-flop; address decoding and the read mux tree are gates. This
    /// is deliberately a project definition so the engine has no memory concept.
    /// </summary>
    private static void AddRam16x4(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, Ram16x4Id, "16 × 4 RAM");
        builder.Definition.Description =
            "Sixteen 4-bit words with asynchronous read and synchronous write. WE selects a " +
            "write on the rising CLK edge. Every cell is a DFF and the read path is a mux tree.";

        var address = new Net[4];
        var data = new Net[4];
        var output = new Net[4];
        for (int i = 0; i < 4; i++)
        {
            address[i] = builder.AddPin($"addr{i}", $"ADDR{i}", PinDirection.Input, StandardDomains.BinaryId);
            data[i] = builder.AddPin($"d{i}", $"D{i}", PinDirection.Input, StandardDomains.BinaryId);
            output[i] = builder.AddPin($"q{i}", $"Q{i}", PinDirection.Output, StandardDomains.BinaryId);
        }

        Net writeEnable = builder.AddPin("we", "WE", PinDirection.Input, StandardDomains.BinaryId);
        Net clock = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.BinaryId);

        var invertedAddress = new Net[4];
        for (int bit = 0; bit < 4; bit++)
        {
            invertedAddress[bit] = builder.AddNet($"addr{bit}_n", StandardDomains.BinaryId);
            builder.AddInstance($"not_addr{bit}", NotId, 80, 80 + (bit * 120));
            builder.Connect(address[bit], builder.Pin($"not_addr{bit}", "a"));
            builder.Connect(invertedAddress[bit], builder.Pin($"not_addr{bit}", "y"));
        }

        var writeSelect = new Net[16];
        for (int word = 0; word < 16; word++)
        {
            Net match = (word & 1) != 0 ? address[0] : invertedAddress[0];
            for (int bit = 1; bit < 4; bit++)
            {
                Net literal = (word & (1 << bit)) != 0 ? address[bit] : invertedAddress[bit];
                Net joined = builder.AddNet($"decode{word}_{bit}", StandardDomains.BinaryId);
                builder.AddInstance($"and_addr{word}_{bit}", "std.and", 300 + (bit * 180), 60 + (word * 90));
                builder.Connect(match, builder.Pin($"and_addr{word}_{bit}", "a"));
                builder.Connect(literal, builder.Pin($"and_addr{word}_{bit}", "b"));
                builder.Connect(joined, builder.Pin($"and_addr{word}_{bit}", "y"));
                match = joined;
            }

            writeSelect[word] = builder.AddNet($"write{word}", StandardDomains.BinaryId);
            builder.AddInstance($"and_write{word}", "std.and", 1080, 60 + (word * 90));
            builder.Connect(writeEnable, builder.Pin($"and_write{word}", "a"));
            builder.Connect(match, builder.Pin($"and_write{word}", "b"));
            builder.Connect(writeSelect[word], builder.Pin($"and_write{word}", "y"));
        }

        for (int bit = 0; bit < 4; bit++)
        {
            var cells = new Net[16];
            for (int word = 0; word < 16; word++)
            {
                cells[word] = builder.AddNet($"cell{bit}_{word}", StandardDomains.BinaryId);
                Net next = builder.AddNet($"next{bit}_{word}", StandardDomains.BinaryId);

                builder.AddInstance($"mux_write{bit}_{word}", "std.mux2", 1300, 60 + (word * 110));
                builder.Connect(cells[word], builder.Pin($"mux_write{bit}_{word}", "a"));
                builder.Connect(data[bit], builder.Pin($"mux_write{bit}_{word}", "b"));
                builder.Connect(writeSelect[word], builder.Pin($"mux_write{bit}_{word}", "s"));
                builder.Connect(next, builder.Pin($"mux_write{bit}_{word}", "y"));

                builder.AddInstance($"ff{bit}_{word}", DFlipFlopId, 1580, 60 + (word * 110));
                builder.Connect(next, builder.Pin($"ff{bit}_{word}", "d"));
                builder.Connect(clock, builder.Pin($"ff{bit}_{word}", "clk"));
                builder.Connect(cells[word], builder.Pin($"ff{bit}_{word}", "q"));
            }

            Net[] level = cells;
            for (int stage = 0; stage < 4; stage++)
            {
                var reduced = new Net[level.Length / 2];
                for (int pair = 0; pair < reduced.Length; pair++)
                {
                    reduced[pair] = stage == 3
                        ? output[bit]
                        : builder.AddNet($"read{bit}_{stage}_{pair}", StandardDomains.BinaryId);
                    builder.AddInstance(
                        $"mux_read{bit}_{stage}_{pair}",
                        "std.mux2",
                        1900 + (stage * 260),
                        60 + (pair * 120));
                    builder.Connect(level[pair * 2], builder.Pin($"mux_read{bit}_{stage}_{pair}", "a"));
                    builder.Connect(level[(pair * 2) + 1], builder.Pin($"mux_read{bit}_{stage}_{pair}", "b"));
                    builder.Connect(address[stage], builder.Pin($"mux_read{bit}_{stage}_{pair}", "s"));
                    builder.Connect(reduced[pair], builder.Pin($"mux_read{bit}_{stage}_{pair}", "y"));
                }

                level = reduced;
            }
        }
    }

    /// <summary>
    /// Radix-4 parts. A quaternary latch is one feedback lookup instead of the
    /// cross-coupled gate pair used for three-valued domains: the table holds D when
    /// enabled and Q otherwise, which needs only values, not a complement operator.
    /// The flip-flop is that latch twice with clock-phase enables.
    /// </summary>
    private static IEnumerable<string> AddQuaternaryDefinitions(CircuitProject project)
    {
        var added = new List<string>();

        if (project.FindDefinition(QuaternaryLatchId) is null)
        {
            AddQuaternaryLatch(project);
            added.Add(QuaternaryLatchId);
        }

        if (project.FindDefinition(QuaternaryDFlipFlopId) is null)
        {
            AddQuaternaryDFlipFlop(project);
            added.Add(QuaternaryDFlipFlopId);
        }

        if (project.FindDefinition(QuaternaryLoadMuxId) is null)
        {
            int[] selectTable = new int[64];
            for (int a = 0; a < 4; a++)
            {
                for (int b = 0; b < 4; b++)
                {
                    for (int select = 0; select < 4; select++)
                    {
                        selectTable[(a * 16) + (b * 4) + select] = select == 3 ? b : a;
                    }
                }
            }

            AddLookupDefinition(
                project,
                QuaternaryLoadMuxId,
                "Quaternary load select",
                StandardDomains.QuaternaryId,
                ["a", "b", "s"],
                ["A", "B", "LOAD"],
                selectTable,
                description: "Selects B when LOAD=3 and A for every other LOAD value. " +
                             "This is the explicit load-control selector used by the quaternary register, not a general 2:1 mux.");
            added.Add(QuaternaryLoadMuxId);
        }

        if (project.FindDefinition(QuaternaryRegister2Id) is null)
        {
            AddQuaternaryRegister2(project);
            added.Add(QuaternaryRegister2Id);
        }

        if (project.FindDefinition(QuaternaryIncId) is null)
        {
            AddLookupDefinition(
                project,
                QuaternaryIncId,
                "Quaternary increment",
                StandardDomains.QuaternaryId,
                ["a"],
                ["A"],
                [1, 2, 3, 0],
                description: "Adds one to a radix-4 digit modulo 4.");
            added.Add(QuaternaryIncId);
        }

        if (project.FindDefinition(QuaternaryCarryId) is null)
        {
            AddLookupDefinition(
                project,
                QuaternaryCarryId,
                "Quaternary carry",
                StandardDomains.QuaternaryId,
                ["a"],
                ["A"],
                [0, 0, 0, 1],
                description: "Carry out of adding one: 1 when the digit is 3, otherwise 0.");
            added.Add(QuaternaryCarryId);
        }

        if (project.FindDefinition(QuaternaryAddId) is null)
        {
            int[] table = new int[16];
            for (int a = 0; a < 4; a++)
            {
                for (int b = 0; b < 4; b++)
                {
                    table[(a * 4) + b] = (a + b) % 4;
                }
            }

            AddLookupDefinition(
                project,
                QuaternaryAddId,
                "Quaternary digit adder",
                StandardDomains.QuaternaryId,
                ["a", "b"],
                ["A", "B"],
                table,
                description: "Adds two radix-4 digits modulo 4; carry is a separate lookup such as std.q4-carry.");
            added.Add(QuaternaryAddId);
        }

        if (project.FindDefinition(QuaternaryFullAdderId) is null)
        {
            AddQuaternaryFullAdder(project);
            added.Add(QuaternaryFullAdderId);
        }

        if (project.FindDefinition(QuaternaryAdder2Id) is null)
        {
            AddQuaternaryAdder2(project);
            added.Add(QuaternaryAdder2Id);
        }

        if (project.FindDefinition(QuaternaryMux4Id) is null)
        {
            int[] table = new int[1024];
            for (int a = 0; a < 4; a++)
            {
                for (int b = 0; b < 4; b++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        for (int d = 0; d < 4; d++)
                        {
                            for (int select = 0; select < 4; select++)
                            {
                                int index = ((((a * 4) + b) * 4 + c) * 4 + d) * 4 + select;
                                table[index] = select switch
                                {
                                    0 => a,
                                    1 => b,
                                    2 => c,
                                    _ => d,
                                };
                            }
                        }
                    }
                }
            }

            AddLookupDefinition(
                project,
                QuaternaryMux4Id,
                "Quaternary 4:1 selector",
                StandardDomains.QuaternaryId,
                ["a", "b", "c", "d", "s"],
                ["A", "B", "C", "D", "SELECT"],
                table,
                description: "Selects A, B, C, or D according to the radix-4 SELECT digit.");
            added.Add(QuaternaryMux4Id);
        }

        if (project.FindDefinition(QuaternaryRam4x2Id) is null)
        {
            AddQuaternaryRam4x2(project);
            added.Add(QuaternaryRam4x2Id);
        }

        return added;
    }

    private static void AddQuaternaryLatch(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, QuaternaryLatchId, "Quaternary D latch");
        builder.Definition.Description =
            "Level-sensitive radix-4 latch. E=1 captures D, E=0 holds, and other enable values hold. " +
            "One lookup reads D, E, and the fed-back Q; Q powers up at 0.";

        Net d = builder.AddPin("d", "D", PinDirection.Input, StandardDomains.QuaternaryId);
        Net e = builder.AddPin("e", "E", PinDirection.Input, StandardDomains.QuaternaryId);
        Net q = builder.SetInitial(
            builder.AddPin("q", "Q", PinDirection.Output, StandardDomains.QuaternaryId),
            0);

        int[] table = new int[64];
        for (int digit = 0; digit < 4; digit++)
        {
            for (int enable = 0; enable < 4; enable++)
            {
                for (int held = 0; held < 4; held++)
                {
                    table[(digit * 16) + (enable * 4) + held] = enable == 1 ? digit : held;
                }
            }
        }

        builder.AddPrimitive("cell", new LookupSpec(3, table, 1), 300, 120);
        builder.Connect(d, builder.Pin("cell", "in0"));
        builder.Connect(e, builder.Pin("cell", "in1"));
        builder.Connect(q, builder.Pin("cell", "in2"));
        builder.Connect(q, builder.Pin("cell", PrimitiveShapes.OutputPinId));
    }

    private static void AddQuaternaryDFlipFlop(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, QuaternaryDFlipFlopId, "Quaternary D flip-flop");
        builder.Definition.Description =
            "Positive-edge-triggered radix-4 flip-flop. The master latch captures while CLK=0 and the " +
            "slave captures while CLK=3, so a 0 -> 3 transition samples D once. Q powers up at 0.";

        Net d = builder.AddPin("d", "D", PinDirection.Input, StandardDomains.QuaternaryId);
        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.QuaternaryId);
        Net q = builder.SetInitial(
            builder.AddPin("q", "Q", PinDirection.Output, StandardDomains.QuaternaryId),
            0);

        Net clockLow = builder.Rename(builder.AddNet("clk_low", StandardDomains.QuaternaryId), "clock is 0");
        Net clockHigh = builder.Rename(builder.AddNet("clk_high", StandardDomains.QuaternaryId), "clock is 3");
        Net masterQ = builder.SetInitial(
            builder.Rename(builder.AddNet("master_q", StandardDomains.QuaternaryId), "master Q"),
            0);

        builder.AddPrimitive("low_select", new LookupSpec(1, [1, 0, 0, 0]), 60, 240);
        builder.AddPrimitive("high_select", new LookupSpec(1, [0, 0, 0, 1]), 60, 420);
        builder.AddInstance("master", QuaternaryLatchId, 300, 40);
        builder.AddInstance("slave", QuaternaryLatchId, 600, 40);

        builder.Connect(clk, builder.Pin("low_select", "in0"), builder.Pin("high_select", "in0"));
        builder.Connect(clockLow, builder.Pin("low_select", PrimitiveShapes.OutputPinId), builder.Pin("master", "e"));
        builder.Connect(clockHigh, builder.Pin("high_select", PrimitiveShapes.OutputPinId), builder.Pin("slave", "e"));
        builder.Connect(d, builder.Pin("master", "d"));
        builder.Connect(masterQ, builder.Pin("master", "q"), builder.Pin("slave", "d"));
        builder.Connect(q, builder.Pin("slave", "q"));
    }

    private static void AddQuaternaryRegister2(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, QuaternaryRegister2Id, "Quaternary 2-digit register");
        builder.Definition.Description =
            "Two radix-4 flip-flops sharing LOAD and CLK. LOAD=3 captures D on the rising CLK edge " +
            "(0 -> 3); every other LOAD value holds the current digit. Q powers up at 0.";

        var data = new Net[2];
        var output = new Net[2];
        for (int lane = 0; lane < 2; lane++)
        {
            data[lane] = builder.AddPin($"d{lane}", $"D{lane}", PinDirection.Input, StandardDomains.QuaternaryId);
            output[lane] = builder.SetInitial(
                builder.AddPin($"q{lane}", $"Q{lane}", PinDirection.Output, StandardDomains.QuaternaryId),
                0);
        }

        Net load = builder.AddPin("load", "LOAD", PinDirection.Input, StandardDomains.QuaternaryId);
        Net clock = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.QuaternaryId);

        builder.AddNumericLane("data", "Data", NumericFormats.UnsignedQuaternary.Id, "d0", "d1");
        builder.AddNumericLane("result", "Result", NumericFormats.UnsignedQuaternary.Id, "q0", "q1");

        for (int lane = 0; lane < 2; lane++)
        {
            Net selected = builder.Rename(
                builder.AddNet($"selected{lane}", StandardDomains.QuaternaryId),
                $"next Q{lane}");
            builder.AddInstance($"select{lane}", QuaternaryLoadMuxId, 260, 60 + (lane * 200));
            builder.AddInstance($"ff{lane}", QuaternaryDFlipFlopId, 600, 60 + (lane * 200));

            builder.Connect(output[lane], builder.Pin($"select{lane}", "a"), builder.Pin($"ff{lane}", "q"));
            builder.Connect(data[lane], builder.Pin($"select{lane}", "b"));
            builder.Connect(load, builder.Pin($"select{lane}", "s"));
            builder.Connect(selected, builder.Pin($"select{lane}", "y"), builder.Pin($"ff{lane}", "d"));
            builder.Connect(clock, builder.Pin($"ff{lane}", "clk"));
        }
    }

    private static void AddQuaternaryFullAdder(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, QuaternaryFullAdderId, "Quaternary full adder");
        builder.Definition.Description =
            "Adds three radix-4 digits: A+B+CIN = 4*COUT + S, with both S and COUT in 0..3. " +
            "COUT is a full carry digit (0..2 for valid digit inputs), not a Boolean flag.";

        Net a = builder.AddPin("a", "A", PinDirection.Input, StandardDomains.QuaternaryId);
        Net b = builder.AddPin("b", "B", PinDirection.Input, StandardDomains.QuaternaryId);
        Net cin = builder.AddPin("cin", "CIN", PinDirection.Input, StandardDomains.QuaternaryId);
        Net s = builder.AddPin("s", "S", PinDirection.Output, StandardDomains.QuaternaryId);
        Net cout = builder.AddPin("cout", "COUT", PinDirection.Output, StandardDomains.QuaternaryId);

        int[] sumTable = new int[64];
        int[] carryTable = new int[64];
        for (int ai = 0; ai < 4; ai++)
        {
            for (int bi = 0; bi < 4; bi++)
            {
                for (int ci = 0; ci < 4; ci++)
                {
                    int total = ai + bi + ci;
                    int index = ((ai * 4) + bi) * 4 + ci;
                    sumTable[index] = total % 4;
                    carryTable[index] = total / 4;
                }
            }
        }

        builder.AddPrimitive("sum", new LookupSpec(3, sumTable, GateDelay), 160, 60);
        builder.AddPrimitive("carry", new LookupSpec(3, carryTable, GateDelay), 160, 240);
        builder.Connect(a, builder.Pin("sum", "in0"), builder.Pin("carry", "in0"));
        builder.Connect(b, builder.Pin("sum", "in1"), builder.Pin("carry", "in1"));
        builder.Connect(cin, builder.Pin("sum", "in2"), builder.Pin("carry", "in2"));
        builder.Connect(s, builder.Pin("sum", "out"));
        builder.Connect(cout, builder.Pin("carry", "out"));
    }

    private static void AddQuaternaryAdder2(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, QuaternaryAdder2Id, "Quaternary 2-digit adder");
        builder.Definition.Description =
            "Adds two radix-4 2-digit numbers into a three-digit result: S0 + 4*S1 + 16*COUT = A+B. " +
            "The low full adder seeds a zero digit; its carry feeds the high digit.";

        var aNets = new Net[2];
        var bNets = new Net[2];
        var sNets = new Net[2];
        for (int i = 0; i < 2; i++)
        {
            aNets[i] = builder.AddPin($"a{i}", $"A{i}", PinDirection.Input, StandardDomains.QuaternaryId);
            bNets[i] = builder.AddPin($"b{i}", $"B{i}", PinDirection.Input, StandardDomains.QuaternaryId);
            sNets[i] = builder.AddPin($"s{i}", $"S{i}", PinDirection.Output, StandardDomains.QuaternaryId);
        }

        Net cout = builder.AddPin("cout", "COUT", PinDirection.Output, StandardDomains.QuaternaryId);
        Net zero = builder.AddNet("zero", StandardDomains.QuaternaryId);
        builder.AddPrimitive("const0", new ConstantSpec(0), 60, 340);
        builder.Connect(zero, builder.Pin("const0", PrimitiveShapes.OutputPinId));
        Net carry = builder.AddNet("carry", StandardDomains.QuaternaryId);

        builder.AddInstance("low", QuaternaryFullAdderId, 300, 100);
        builder.Connect(aNets[0], builder.Pin("low", "a"));
        builder.Connect(bNets[0], builder.Pin("low", "b"));
        builder.Connect(zero, builder.Pin("low", "cin"));
        builder.Connect(sNets[0], builder.Pin("low", "s"));
        builder.Connect(carry, builder.Pin("low", "cout"));

        builder.AddInstance("high", QuaternaryFullAdderId, 680, 100);
        builder.Connect(aNets[1], builder.Pin("high", "a"));
        builder.Connect(bNets[1], builder.Pin("high", "b"));
        builder.Connect(carry, builder.Pin("high", "cin"));
        builder.Connect(sNets[1], builder.Pin("high", "s"));
        builder.Connect(cout, builder.Pin("high", "cout"));

        builder.AddNumericLane("a", "A", NumericFormats.UnsignedQuaternary.Id, "a0", "a1");
        builder.AddNumericLane("b", "B", NumericFormats.UnsignedQuaternary.Id, "b0", "b1");
        builder.AddNumericLane("s", "A + B", NumericFormats.UnsignedQuaternary.Id, "s0", "s1", "cout");
    }

    private static void AddQuaternaryRam4x2(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, QuaternaryRam4x2Id, "Quaternary 4 × 2 RAM");
        builder.Definition.Description =
            "Four two-digit radix-4 words with asynchronous read and synchronous write. ADDR selects 0..3; " +
            "WE=3 writes D on the rising CLK edge (0 -> 3), while WE=0, 1, or 2 holds every word. " +
            "The read port is one quaternary 4:1 selector per digit.";

        Net address = builder.AddPin("addr", "ADDR", PinDirection.Input, StandardDomains.QuaternaryId);
        var data = new Net[2];
        var output = new Net[2];
        for (int lane = 0; lane < 2; lane++)
        {
            data[lane] = builder.AddPin($"d{lane}", $"D{lane}", PinDirection.Input, StandardDomains.QuaternaryId);
            output[lane] = builder.SetInitial(
                builder.AddPin($"q{lane}", $"Q{lane}", PinDirection.Output, StandardDomains.QuaternaryId),
                0);
        }

        Net writeEnable = builder.AddPin("we", "WE", PinDirection.Input, StandardDomains.QuaternaryId);
        Net clock = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.QuaternaryId);

        builder.AddNumericLane("address", "Address", NumericFormats.UnsignedQuaternary.Id, "addr");
        builder.AddNumericLane("data", "Data", NumericFormats.UnsignedQuaternary.Id, "d0", "d1");
        builder.AddNumericLane("result", "Read", NumericFormats.UnsignedQuaternary.Id, "q0", "q1");

        var words = new Net[4][];
        for (int word = 0; word < 4; word++)
        {
            int[] writeTable = new int[16];
            for (int enable = 0; enable < 4; enable++)
            {
                for (int selected = 0; selected < 4; selected++)
                {
                    writeTable[(enable * 4) + selected] = enable == 3 && selected == word ? 3 : 0;
                }
            }

            Net write = builder.Rename(
                builder.AddNet($"write{word}", StandardDomains.QuaternaryId),
                $"write word {word}");
            builder.AddPrimitive(
                $"decode_write{word}",
                new LookupSpec(2, writeTable, GateDelay),
                240,
                60 + (word * 190));
            builder.Connect(writeEnable, builder.Pin($"decode_write{word}", "in0"));
            builder.Connect(address, builder.Pin($"decode_write{word}", "in1"));
            builder.Connect(write, builder.Pin($"decode_write{word}", PrimitiveShapes.OutputPinId));

            words[word] = new Net[2];
            builder.AddInstance($"word{word}", QuaternaryRegister2Id, 580, 60 + (word * 190));
            for (int lane = 0; lane < 2; lane++)
            {
                words[word][lane] = builder.Rename(
                    builder.AddNet($"word{word}_{lane}", StandardDomains.QuaternaryId),
                    $"word {word} digit {lane}");
                builder.Connect(data[lane], builder.Pin($"word{word}", $"d{lane}"));
                builder.Connect(words[word][lane], builder.Pin($"word{word}", $"q{lane}"));
            }

            builder.Connect(write, builder.Pin($"word{word}", "load"));
            builder.Connect(clock, builder.Pin($"word{word}", "clk"));
        }

        for (int lane = 0; lane < 2; lane++)
        {
            builder.AddInstance($"read{lane}", QuaternaryMux4Id, 1000, 140 + (lane * 300));
            builder.Connect(words[0][lane], builder.Pin($"read{lane}", "a"));
            builder.Connect(words[1][lane], builder.Pin($"read{lane}", "b"));
            builder.Connect(words[2][lane], builder.Pin($"read{lane}", "c"));
            builder.Connect(words[3][lane], builder.Pin($"read{lane}", "d"));
            builder.Connect(address, builder.Pin($"read{lane}", "s"));
            builder.Connect(output[lane], builder.Pin($"read{lane}", "y"));
        }
    }

    private static IEnumerable<string> AddTernaryDefinitions(CircuitProject project)
    {
        var added = new List<string>();

        if (project.FindDefinition("std.t-not") is null)
        {
            AddLookupDefinition(
                project,
                "std.t-not",
                "Ternary NOT (mod 3)",
                StandardDomains.TernaryId,
                ["a"],
                ["A"],
                [0, 2, 1],
                description: "Digit-wise negation on the unsaturated 0/1/2 ternary domain.");
            added.Add("std.t-not");
        }

        if (project.FindDefinition("std.t-lognot") is null)
        {
            AddLookupDefinition(
                project,
                "std.t-lognot",
                "Ternary NOT (Kleene)",
                StandardDomains.TernaryId,
                ["a"],
                ["A"],
                [2, 1, 0],
                description: "Logic negation used by the ternary latches: 0 <-> 2 and 1 stays unknown. " +
                             "Contrast std.t-not, which is digit-wise negation modulo 3.");
            added.Add("std.t-lognot");
        }

        if (project.FindDefinition("std.t-min") is null)
        {
            AddLookupDefinition(project, "std.t-min", "Ternary minimum", StandardDomains.TernaryId, ["a", "b"], ["A", "B"], [0, 0, 0, 0, 1, 1, 0, 1, 2]);
            added.Add("std.t-min");
        }

        if (project.FindDefinition("std.t-max") is null)
        {
            AddLookupDefinition(project, "std.t-max", "Ternary maximum", StandardDomains.TernaryId, ["a", "b"], ["A", "B"], [0, 1, 2, 1, 1, 2, 2, 2, 2]);
            added.Add("std.t-max");
        }

        if (project.FindDefinition("std.bt-not") is null)
        {
            AddLookupDefinition(
                project,
                "std.bt-not",
                "Balanced ternary negation",
                StandardDomains.BalancedTernaryId,
                ["a"],
                ["A"],
                [2, 1, 0],
                description: "Maps -,0,+ to +,0,-. Three-valued; not representable by a binary signal.");
            added.Add("std.bt-not");
        }

        if (project.FindDefinition("std.bt-min") is null)
        {
            AddLookupDefinition(project, "std.bt-min", "Balanced ternary minimum", StandardDomains.BalancedTernaryId, ["a", "b"], ["A", "B"], [0, 0, 0, 0, 1, 1, 0, 1, 2]);
            added.Add("std.bt-min");
        }

        if (project.FindDefinition("std.bt-max") is null)
        {
            AddLookupDefinition(project, "std.bt-max", "Balanced ternary maximum", StandardDomains.BalancedTernaryId, ["a", "b"], ["A", "B"], [0, 1, 2, 1, 1, 2, 2, 2, 2]);
            added.Add("std.bt-max");
        }

        if (project.FindDefinition("std.bt-mux") is null)
        {
            int[] muxTable = new int[27];
            for (int a = 0; a < 3; a++)
            {
                for (int b = 0; b < 3; b++)
                {
                    muxTable[((a * 3) + b) * 3] = a;
                    muxTable[(((a * 3) + b) * 3) + 1] = 1;
                    muxTable[(((a * 3) + b) * 3) + 2] = b;
                }
            }

            AddLookupDefinition(
                project,
                "std.bt-mux",
                "Balanced ternary 2:1 multiplexer",
                StandardDomains.BalancedTernaryId,
                ["a", "b", "s"],
                ["A", "B", "Select"],
                muxTable,
                description: "Select - returns A, + returns B, and 0 returns the middle value 0. " +
                             "The balanced-ternary mirror of std.t-mux, where 0 is the unknown trit.");
            added.Add("std.bt-mux");
        }

        if (project.FindDefinition("std.bt-half-adder") is null)
        {
            AddBalancedTernaryHalfAdder(project);
            added.Add("std.bt-half-adder");
        }

        if (project.FindDefinition(BalancedTernaryFullAdderId) is null)
        {
            AddBalancedTernaryFullAdder(project);
            added.Add(BalancedTernaryFullAdderId);
        }

        if (project.FindDefinition(BalancedTernaryAdder2Id) is null)
        {
            AddBalancedTernaryAdder2(project);
            added.Add(BalancedTernaryAdder2Id);
        }

        if (project.FindDefinition("std.t-inc") is null)
        {
            AddLookupDefinition(
                project,
                "std.t-inc",
                "Ternary increment",
                StandardDomains.TernaryId,
                ["a"],
                ["A"],
                [1, 2, 0],
                description: "Adds one to a ternary digit modulo 3.");
            added.Add("std.t-inc");
        }

        if (project.FindDefinition("std.t-carry") is null)
        {
            AddLookupDefinition(
                project,
                "std.t-carry",
                "Ternary carry",
                StandardDomains.TernaryId,
                ["a"],
                ["A"],
                [0, 0, 1],
                description: "Carry out of adding one: 1 when the digit is 2, otherwise 0.");
            added.Add("std.t-carry");
        }

        if (project.FindDefinition("std.t-add") is null)
        {
            AddLookupDefinition(
                project,
                "std.t-add",
                "Ternary digit adder",
                StandardDomains.TernaryId,
                ["a", "b"],
                ["A", "B"],
                [0, 1, 2, 1, 2, 0, 2, 0, 1],
                description: "Adds two ternary digits modulo 3; carry is a separate lookup such as std.t-carry.");
            added.Add("std.t-add");
        }

        if (project.FindDefinition("std.t-mux") is null)
        {
            int[] muxTable = new int[27];
            for (int a = 0; a < 3; a++)
            {
                for (int b = 0; b < 3; b++)
                {
                    muxTable[((a * 3) + b) * 3] = a;
                    muxTable[(((a * 3) + b) * 3) + 1] = 1;
                    muxTable[(((a * 3) + b) * 3) + 2] = b;
                }
            }

            AddLookupDefinition(
                project,
                "std.t-mux",
                "Ternary 2:1 multiplexer",
                StandardDomains.TernaryId,
                ["a", "b", "s"],
                ["A", "B", "Select"],
                muxTable,
                description: "Select 0 returns A, select 2 returns B, and select 1 returns the unknown trit.");
            added.Add("std.t-mux");
        }

        if (project.FindDefinition(TernaryFullAdderId) is null)
        {
            AddTernaryFullAdder(project);
            added.Add(TernaryFullAdderId);
        }

        if (project.FindDefinition(TernaryAdder2Id) is null)
        {
            AddTernaryAdder2(project);
            added.Add(TernaryAdder2Id);
        }

        added.AddRange(AddTernarySequential(project));

        if (project.FindDefinition(TernaryRegister2Id) is null)
        {
            AddTernaryRegister2(project);
            added.Add(TernaryRegister2Id);
        }

        if (project.FindDefinition(TernaryMux3Id) is null)
        {
            AddTernaryMux3(project);
            added.Add(TernaryMux3Id);
        }

        if (project.FindDefinition(TernaryRam3x2Id) is null)
        {
            AddTernaryRam3x2(project);
            added.Add(TernaryRam3x2Id);
        }

        if (project.FindDefinition(TernaryRam9x2Id) is null)
        {
            AddTernaryRam9x2(project);
            added.Add(TernaryRam9x2Id);
        }

        if (project.FindDefinition(BalancedTernaryLatchId) is null)
        {
            AddBalancedTernaryLatch(project);
            added.Add(BalancedTernaryLatchId);
        }

        if (project.FindDefinition(BalancedTernaryDFlipFlopId) is null)
        {
            AddBalancedTernaryDFlipFlop(project);
            added.Add(BalancedTernaryDFlipFlopId);
        }

        if (project.FindDefinition(BalancedTernaryRegister2Id) is null)
        {
            AddBalancedTernaryRegister2(project);
            added.Add(BalancedTernaryRegister2Id);
        }

        return added;
    }

    private static void AddTernaryRegister2(CircuitProject project) =>
        AddThreeValuedRegister2(
            project,
            TernaryRegister2Id,
            "Ternary 2-trit register",
            StandardDomains.TernaryId,
            "std.t-mux",
            TernaryDFlipFlopId,
            NumericFormats.UnsignedTernary.Id,
            0,
            "Two trit flip-flops with a shared level load. LOAD=2 captures D on the rising CLK edge (0 -> 2); " +
            "LOAD=0 holds. Q/QN power up at 0.");

    private static void AddBalancedTernaryRegister2(CircuitProject project) =>
        AddThreeValuedRegister2(
            project,
            BalancedTernaryRegister2Id,
            "Balanced ternary 2-trit register",
            StandardDomains.BalancedTernaryId,
            "std.bt-mux",
            BalancedTernaryDFlipFlopId,
            NumericFormats.BalancedTernary.Id,
            1,
            "Two balanced-ternary flip-flops with a shared level load. LOAD=+ captures D on the rising CLK edge " +
            "(- -> +); LOAD=- holds and LOAD=0 seeds the unknown middle value. Q/QN power up at 0. " +
            "Data and Result lanes decode as balanced ternary.");

    /// <summary>
    /// Three words of two trits with an asynchronous read port. Every cell is part of a
    /// reusable register, write enables are decoded from WE and the address trit, and the
    /// read port is one lookup per lane over the address and the three word trits.
    /// </summary>
    private static void AddTernaryRam3x2(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, TernaryRam3x2Id, "Ternary 3 × 2 RAM");
        builder.Definition.Description =
            "Three 2-trit words with asynchronous read and synchronous write. WE=2 writes D into the " +
            "addressed word on the rising CLK edge (0 -> 2); WE=0 holds every word and WE=1, the unknown " +
            "trit, also holds rather than writing an unknown. The address is one trit, so ADDR 0..2 selects " +
            "exactly one of the three words.";

        Net address = builder.AddPin("addr", "ADDR", PinDirection.Input, StandardDomains.TernaryId);
        var data = new Net[2];
        var output = new Net[2];
        for (int lane = 0; lane < 2; lane++)
        {
            data[lane] = builder.AddPin($"d{lane}", $"D{lane}", PinDirection.Input, StandardDomains.TernaryId);
            output[lane] = builder.SetInitial(
                builder.AddPin($"q{lane}", $"Q{lane}", PinDirection.Output, StandardDomains.TernaryId),
                0);
        }

        Net writeEnable = builder.AddPin("we", "WE", PinDirection.Input, StandardDomains.TernaryId);
        Net clock = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.TernaryId);

        builder.AddNumericLane("address", "Address", NumericFormats.UnsignedTernary.Id, "addr");
        builder.AddNumericLane("data", "Data", NumericFormats.UnsignedTernary.Id, "d0", "d1");
        builder.AddNumericLane("result", "Read", NumericFormats.UnsignedTernary.Id, "q0", "q1");

        var words = new Net[3][];
        for (int word = 0; word < 3; word++)
        {
            int[] writeTable = new int[9];
            for (int enable = 0; enable < 3; enable++)
            {
                for (int target = 0; target < 3; target++)
                {
                    writeTable[(enable * 3) + target] = enable == 2 && target == word ? 2 : 0;
                }
            }

            Net write = builder.Rename(
                builder.AddNet($"write{word}", StandardDomains.TernaryId),
                $"write word {word}");
            builder.AddPrimitive($"decode_write{word}", new LookupSpec(2, writeTable, GateDelay), 240, 80 + (word * 220));
            builder.Connect(writeEnable, builder.Pin($"decode_write{word}", "in0"));
            builder.Connect(address, builder.Pin($"decode_write{word}", "in1"));
            builder.Connect(write, builder.Pin($"decode_write{word}", PrimitiveShapes.OutputPinId));

            words[word] = new Net[2];
            builder.AddInstance($"word{word}", TernaryRegister2Id, 560, 80 + (word * 220));
            for (int lane = 0; lane < 2; lane++)
            {
                words[word][lane] = builder.Rename(
                    builder.AddNet($"word{word}_{lane}", StandardDomains.TernaryId),
                    $"word {word} trit {lane}");
                builder.Connect(data[lane], builder.Pin($"word{word}", $"d{lane}"));
                builder.Connect(words[word][lane], builder.Pin($"word{word}", $"q{lane}"));
            }

            builder.Connect(write, builder.Pin($"word{word}", "load"));
            builder.Connect(clock, builder.Pin($"word{word}", "clk"));
        }

        for (int lane = 0; lane < 2; lane++)
        {
            int[] readTable = new int[81];
            for (int selected = 0; selected < 3; selected++)
            {
                for (int word0 = 0; word0 < 3; word0++)
                {
                    for (int word1 = 0; word1 < 3; word1++)
                    {
                        for (int word2 = 0; word2 < 3; word2++)
                        {
                            int index = (((selected * 3) + word0) * 3 + word1) * 3 + word2;
                            readTable[index] = selected == 0 ? word0 : selected == 1 ? word1 : word2;
                        }
                    }
                }
            }

            builder.AddPrimitive($"read{lane}", new LookupSpec(4, readTable, GateDelay), 840, 80 + (lane * 220));
            builder.Connect(address, builder.Pin($"read{lane}", "in0"));
            builder.Connect(words[0][lane], builder.Pin($"read{lane}", "in1"));
            builder.Connect(words[1][lane], builder.Pin($"read{lane}", "in2"));
            builder.Connect(words[2][lane], builder.Pin($"read{lane}", "in3"));
            builder.Connect(output[lane], builder.Pin($"read{lane}", PrimitiveShapes.OutputPinId));
        }
    }

    private static void AddTernaryMux3(CircuitProject project)
    {
        int[] table = new int[81];
        for (int a = 0; a < 3; a++)
        {
            for (int b = 0; b < 3; b++)
            {
                for (int c = 0; c < 3; c++)
                {
                    for (int s = 0; s < 3; s++)
                    {
                        table[((((a * 3) + b) * 3) + c) * 3 + s] = s == 0 ? a : s == 1 ? b : c;
                    }
                }
            }
        }

        AddLookupDefinition(
            project,
            TernaryMux3Id,
            "Ternary 3:1 multiplexer",
            StandardDomains.TernaryId,
            ["a", "b", "c", "s"],
            ["A", "B", "C", "Select"],
            table,
            description: "Select 0 returns A, 1 returns B, and 2 returns C. This is the 3-way selector " +
                         "used by memory read ports; std.t-mux is the 2:1 gate with an unknown middle select.");
    }

    /// <summary>
    /// Nine words of two trits with a two-trit address. Write enables are decoded
    /// per word, and the read port is a two-level tree of 3:1 selectors, so the read
    /// path scales by adding levels rather than growing one enormous lookup.
    /// </summary>
    private static void AddTernaryRam9x2(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, TernaryRam9x2Id, "Ternary 9 × 2 RAM");
        builder.Definition.Description =
            "Nine 2-trit words with asynchronous read and synchronous write. The two-trit ADDR selects " +
            "0..8; WE=2 writes D on the rising CLK edge (0 -> 2) while WE=0 and WE=1 hold every word. " +
            "The read port is two levels of ternary 3:1 selectors, so any addressed word appears after " +
            "two gate delays.";

        var address = new Net[2];
        for (int lane = 0; lane < 2; lane++)
        {
            address[lane] = builder.AddPin($"addr{lane}", $"ADDR{lane}", PinDirection.Input, StandardDomains.TernaryId);
        }

        var data = new Net[2];
        var output = new Net[2];
        for (int lane = 0; lane < 2; lane++)
        {
            data[lane] = builder.AddPin($"d{lane}", $"D{lane}", PinDirection.Input, StandardDomains.TernaryId);
            output[lane] = builder.SetInitial(
                builder.AddPin($"q{lane}", $"Q{lane}", PinDirection.Output, StandardDomains.TernaryId),
                0);
        }

        Net writeEnable = builder.AddPin("we", "WE", PinDirection.Input, StandardDomains.TernaryId);
        Net clock = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.TernaryId);

        builder.AddNumericLane("address", "Address", NumericFormats.UnsignedTernary.Id, "addr0", "addr1");
        builder.AddNumericLane("data", "Data", NumericFormats.UnsignedTernary.Id, "d0", "d1");
        builder.AddNumericLane("result", "Read", NumericFormats.UnsignedTernary.Id, "q0", "q1");

        var words = new Net[9][];
        for (int word = 0; word < 9; word++)
        {
            int[] writeTable = new int[27];
            for (int enable = 0; enable < 3; enable++)
            {
                for (int high = 0; high < 3; high++)
                {
                    for (int low = 0; low < 3; low++)
                    {
                        int index = (enable * 9) + (high * 3) + low;
                        writeTable[index] = enable == 2 && (high * 3) + low == word ? 2 : 0;
                    }
                }
            }

            Net write = builder.Rename(
                builder.AddNet($"write{word}", StandardDomains.TernaryId),
                $"write word {word}");
            builder.AddPrimitive(
                $"decode_write{word}",
                new LookupSpec(3, writeTable, GateDelay),
                240,
                60 + (word * 150));
            builder.Connect(writeEnable, builder.Pin($"decode_write{word}", "in0"));
            builder.Connect(address[1], builder.Pin($"decode_write{word}", "in1"));
            builder.Connect(address[0], builder.Pin($"decode_write{word}", "in2"));
            builder.Connect(write, builder.Pin($"decode_write{word}", PrimitiveShapes.OutputPinId));

            words[word] = new Net[2];
            builder.AddInstance($"word{word}", TernaryRegister2Id, 580, 60 + (word * 150));
            for (int lane = 0; lane < 2; lane++)
            {
                words[word][lane] = builder.Rename(
                    builder.AddNet($"word{word}_{lane}", StandardDomains.TernaryId),
                    $"word {word} trit {lane}");
                builder.Connect(data[lane], builder.Pin($"word{word}", $"d{lane}"));
                builder.Connect(words[word][lane], builder.Pin($"word{word}", $"q{lane}"));
            }

            builder.Connect(write, builder.Pin($"word{word}", "load"));
            builder.Connect(clock, builder.Pin($"word{word}", "clk"));
        }

        // ADDR0 is the low digit: it selects inside each group of three words, and
        // ADDR1 selects one of the three resulting group values.
        for (int lane = 0; lane < 2; lane++)
        {
            var groups = new Net[3];
            for (int group = 0; group < 3; group++)
            {
                groups[group] = builder.Rename(
                    builder.AddNet($"read{lane}_group{group}", StandardDomains.TernaryId),
                    $"read lane {lane} group {group}");
                builder.AddInstance(
                    $"read{lane}_low{group}",
                    TernaryMux3Id,
                    940,
                    60 + (group * 170) + (lane * 560));
                builder.Connect(words[(group * 3) + 0][lane], builder.Pin($"read{lane}_low{group}", "a"));
                builder.Connect(words[(group * 3) + 1][lane], builder.Pin($"read{lane}_low{group}", "b"));
                builder.Connect(words[(group * 3) + 2][lane], builder.Pin($"read{lane}_low{group}", "c"));
                builder.Connect(address[0], builder.Pin($"read{lane}_low{group}", "s"));
                builder.Connect(groups[group], builder.Pin($"read{lane}_low{group}", "y"));
            }

            builder.AddInstance($"read{lane}_high", TernaryMux3Id, 1200, 180 + (lane * 560));
            builder.Connect(groups[0], builder.Pin($"read{lane}_high", "a"));
            builder.Connect(groups[1], builder.Pin($"read{lane}_high", "b"));
            builder.Connect(groups[2], builder.Pin($"read{lane}_high", "c"));
            builder.Connect(address[1], builder.Pin($"read{lane}_high", "s"));
            builder.Connect(output[lane], builder.Pin($"read{lane}_high", "y"));
        }
    }

    private static void AddThreeValuedRegister2(
        CircuitProject project,
        string id,
        string name,
        string domainId,
        string muxId,
        string flipFlopId,
        string formatId,
        int initialValue,
        string description)
    {
        var builder = new DefinitionBuilder(project, id, name);
        builder.Definition.Description = description;

        var data = new Net[2];
        var q = new Net[2];
        for (int i = 0; i < 2; i++)
        {
            data[i] = builder.AddPin($"d{i}", $"D{i}", PinDirection.Input, domainId);
            q[i] = builder.SetInitial(builder.AddPin($"q{i}", $"Q{i}", PinDirection.Output, domainId), initialValue);
        }

        Net load = builder.AddPin("load", "LOAD", PinDirection.Input, domainId);
        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, domainId);

        builder.AddNumericLane("data", "Data", formatId, "d0", "d1");
        builder.AddNumericLane("result", "Result", formatId, "q0", "q1");

        for (int i = 0; i < 2; i++)
        {
            Net selected = builder.Rename(builder.AddNet($"selected{i}", domainId), $"next Q{i}");
            builder.AddInstance($"mux{i}", muxId, 220, 60 + (i * 200));
            builder.AddInstance($"ff{i}", flipFlopId, 500, 60 + (i * 200));

            builder.Connect(q[i], builder.Pin($"mux{i}", "a"), builder.Pin($"ff{i}", "q"));
            builder.Connect(data[i], builder.Pin($"mux{i}", "b"));
            builder.Connect(load, builder.Pin($"mux{i}", "s"));
            builder.Connect(selected, builder.Pin($"mux{i}", "y"), builder.Pin($"ff{i}", "d"));
            builder.Connect(clk, builder.Pin($"ff{i}", "clk"));
        }
    }

    private static void AddTernaryFullAdder(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, TernaryFullAdderId, "Ternary full adder");
        builder.Definition.Description =
            "Adds three 0/1/2 digits: A+B+CIN = 3*COUT + S. COUT is a full trit (0..2), not a Boolean flag.";

        Net a = builder.AddPin("a", "A", PinDirection.Input, StandardDomains.TernaryId);
        Net b = builder.AddPin("b", "B", PinDirection.Input, StandardDomains.TernaryId);
        Net cin = builder.AddPin("cin", "CIN", PinDirection.Input, StandardDomains.TernaryId);
        Net s = builder.AddPin("s", "S", PinDirection.Output, StandardDomains.TernaryId);
        Net cout = builder.AddPin("cout", "COUT", PinDirection.Output, StandardDomains.TernaryId);

        int[] sumTable = new int[27];
        int[] carryTable = new int[27];
        for (int ai = 0; ai < 3; ai++)
        {
            for (int bi = 0; bi < 3; bi++)
            {
                for (int ci = 0; ci < 3; ci++)
                {
                    int total = ai + bi + ci;
                    int index = ((ai * 3) + bi) * 3 + ci;
                    sumTable[index] = total % 3;
                    carryTable[index] = total / 3;
                }
            }
        }

        builder.AddPrimitive("sum", new LookupSpec(3, sumTable, GateDelay), 160, 60);
        builder.AddPrimitive("carry", new LookupSpec(3, carryTable, GateDelay), 160, 240);
        builder.Connect(a, builder.Pin("sum", "in0"), builder.Pin("carry", "in0"));
        builder.Connect(b, builder.Pin("sum", "in1"), builder.Pin("carry", "in1"));
        builder.Connect(cin, builder.Pin("sum", "in2"), builder.Pin("carry", "in2"));
        builder.Connect(s, builder.Pin("sum", "out"));
        builder.Connect(cout, builder.Pin("carry", "out"));
    }

    private static void AddTernaryAdder2(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, TernaryAdder2Id, "Ternary 2-trit adder");
        builder.Definition.Description =
            "Adds two unsigned ternary 2-trit numbers into a three-trit result: " +
            "S0 + 3*S1 + 9*COUT = A+B. The low full adder seeds a zero digit; its carry feeds the high trit.";

        var aNets = new Net[2];
        var bNets = new Net[2];
        var sNets = new Net[2];
        for (int i = 0; i < 2; i++)
        {
            aNets[i] = builder.AddPin($"a{i}", $"A{i}", PinDirection.Input, StandardDomains.TernaryId);
            bNets[i] = builder.AddPin($"b{i}", $"B{i}", PinDirection.Input, StandardDomains.TernaryId);
            sNets[i] = builder.AddPin($"s{i}", $"S{i}", PinDirection.Output, StandardDomains.TernaryId);
        }

        Net cout = builder.AddPin("cout", "COUT", PinDirection.Output, StandardDomains.TernaryId);
        Net zero = builder.AddNet("zero", StandardDomains.TernaryId);
        builder.AddPrimitive("const0", new ConstantSpec(0), 60, 340);
        builder.Connect(zero, builder.Pin("const0", PrimitiveShapes.OutputPinId));
        Net carry = builder.AddNet("carry", StandardDomains.TernaryId);

        builder.AddInstance("low", TernaryFullAdderId, 260, 100);
        builder.Connect(aNets[0], builder.Pin("low", "a"));
        builder.Connect(bNets[0], builder.Pin("low", "b"));
        builder.Connect(zero, builder.Pin("low", "cin"));
        builder.Connect(sNets[0], builder.Pin("low", "s"));
        builder.Connect(carry, builder.Pin("low", "cout"));

        builder.AddInstance("high", TernaryFullAdderId, 620, 100);
        builder.Connect(aNets[1], builder.Pin("high", "a"));
        builder.Connect(bNets[1], builder.Pin("high", "b"));
        builder.Connect(carry, builder.Pin("high", "cin"));
        builder.Connect(sNets[1], builder.Pin("high", "s"));
        builder.Connect(cout, builder.Pin("high", "cout"));

        builder.AddNumericLane("a", "A", NumericFormats.UnsignedTernary.Id, "a0", "a1");
        builder.AddNumericLane("b", "B", NumericFormats.UnsignedTernary.Id, "b0", "b1");
        builder.AddNumericLane("s", "A + B", NumericFormats.UnsignedTernary.Id, "s0", "s1", "cout");
    }

    private static IEnumerable<string> AddTernarySequential(CircuitProject project)
    {
        var added = new List<string>();
        if (project.FindDefinition(TernaryLatchId) is null)
        {
            AddTernaryLatch(project);
            added.Add(TernaryLatchId);
        }

        if (project.FindDefinition(TernaryDFlipFlopId) is null)
        {
            AddTernaryDFlipFlop(project);
            added.Add(TernaryDFlipFlopId);
        }

        return added;
    }

    private static void AddTernaryLatch(CircuitProject project) =>
        AddThreeValuedLatch(
            project,
            TernaryLatchId,
            "Ternary D latch",
            StandardDomains.TernaryId,
            "std.t-lognot",
            "std.t-min",
            0,
            2,
            "Level-sensitive trit latch on the 0/1/2 domain. E=2 captures D, E=0 holds, and E=1 gives an unknown trit. " +
            "Two cross-coupled Kleene NANDs store the trit, so the state survives the enable transition; Q/QN power up at 0/2.");

    private static void AddBalancedTernaryLatch(CircuitProject project) =>
        AddThreeValuedLatch(
            project,
            BalancedTernaryLatchId,
            "Balanced ternary D latch",
            StandardDomains.BalancedTernaryId,
            "std.bt-not",
            "std.bt-min",
            1,
            1,
            "Level-sensitive trit latch on the balanced -/0/+ domain. E=+ captures D, E=- holds, and E=0 gives the " +
            "unknown middle value. The structure is the same Kleene latch used for 0/1/2, wired from balanced gates; " +
            "Q/QN power up at 0/0, the self-complementary middle value.");

    /// <summary>
    /// The latch and flip-flop construction only assumes a three-value domain whose
    /// gates are minimum, maximum, and complement. Both shipped ternary domains qualify,
    /// so the same wiring serves them with different constituent definitions.
    /// </summary>
    private static void AddThreeValuedLatch(
        CircuitProject project,
        string id,
        string name,
        string domainId,
        string notId,
        string minId,
        int initialQ,
        int initialQn,
        string description)
    {
        var builder = new DefinitionBuilder(project, id, name);
        builder.Definition.Description = description;

        Net d = builder.AddPin("d", "D", PinDirection.Input, domainId);
        Net e = builder.AddPin("e", "E", PinDirection.Input, domainId);
        Net q = builder.SetInitial(builder.AddPin("q", "Q", PinDirection.Output, domainId), initialQ);
        Net qn = builder.SetInitial(builder.AddPin("qn", "QN", PinDirection.Output, domainId), initialQn);

        Net notD = builder.Rename(builder.AddNet("not_d", domainId), "not D");
        Net setIn = builder.Rename(builder.AddNet("set_in", domainId), "D and E");
        Net resetIn = builder.Rename(builder.AddNet("reset_in", domainId), "not D and E");
        Net setBar = builder.Rename(builder.AddNet("set_bar", domainId), "set");
        Net resetBar = builder.Rename(builder.AddNet("reset_bar", domainId), "reset");
        Net qHold = builder.Rename(builder.AddNet("q_hold", domainId), "set and QN");
        Net qnHold = builder.Rename(builder.AddNet("qn_hold", domainId), "reset and Q");

        builder.AddInstance("inv_d", notId, 60, 200);
        builder.AddInstance("capture", minId, 240, 40);
        builder.AddInstance("release", minId, 240, 240);
        builder.AddInstance("nand_set", notId, 440, 40);
        builder.AddInstance("nand_reset", notId, 440, 240);
        builder.AddInstance("hold_q", minId, 620, 40);
        builder.AddInstance("hold_qn", minId, 620, 240);
        builder.AddInstance("q_bar", notId, 800, 40);
        builder.AddInstance("qn_bar", notId, 800, 240);

        builder.Connect(d, builder.Pin("inv_d", "a"), builder.Pin("capture", "a"));
        builder.Connect(notD, builder.Pin("inv_d", "y"), builder.Pin("release", "a"));
        builder.Connect(e, builder.Pin("capture", "b"), builder.Pin("release", "b"));
        builder.Connect(setIn, builder.Pin("capture", "y"), builder.Pin("nand_set", "a"));
        builder.Connect(resetIn, builder.Pin("release", "y"), builder.Pin("nand_reset", "a"));
        builder.Connect(setBar, builder.Pin("nand_set", "y"), builder.Pin("hold_q", "a"));
        builder.Connect(resetBar, builder.Pin("nand_reset", "y"), builder.Pin("hold_qn", "a"));
        builder.Connect(qn, builder.Pin("hold_q", "b"));
        builder.Connect(q, builder.Pin("hold_qn", "b"));
        builder.Connect(qHold, builder.Pin("hold_q", "y"), builder.Pin("q_bar", "a"));
        builder.Connect(qnHold, builder.Pin("hold_qn", "y"), builder.Pin("qn_bar", "a"));
        builder.Connect(q, builder.Pin("q_bar", "y"));
        builder.Connect(qn, builder.Pin("qn_bar", "y"));
    }

    private static void AddTernaryDFlipFlop(CircuitProject project) =>
        AddThreeValuedDFlipFlop(
            project,
            TernaryDFlipFlopId,
            "Ternary D flip-flop",
            StandardDomains.TernaryId,
            TernaryLatchId,
            "std.t-lognot",
            0,
            2,
            "Positive-edge-triggered trit flip-flop built from two ternary latches and an inverter. " +
            "Drive CLK between 0 and 2; the slave captures on 2. Q/QN power up at 0/2.");

    private static void AddBalancedTernaryDFlipFlop(CircuitProject project) =>
        AddThreeValuedDFlipFlop(
            project,
            BalancedTernaryDFlipFlopId,
            "Balanced ternary D flip-flop",
            StandardDomains.BalancedTernaryId,
            BalancedTernaryLatchId,
            "std.bt-not",
            1,
            1,
            "Positive-edge-triggered balanced-ternary flip-flop built from two balanced latches and a complement " +
            "gate. Drive CLK between - and +; the slave captures on +. Q/QN power up at 0/0.");

    private static void AddThreeValuedDFlipFlop(
        CircuitProject project,
        string id,
        string name,
        string domainId,
        string latchId,
        string notId,
        int initialQ,
        int initialQn,
        string description)
    {
        var builder = new DefinitionBuilder(project, id, name);
        builder.Definition.Description = description;

        Net d = builder.AddPin("d", "D", PinDirection.Input, domainId);
        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, domainId);
        Net q = builder.SetInitial(builder.AddPin("q", "Q", PinDirection.Output, domainId), initialQ);
        Net qn = builder.SetInitial(builder.AddPin("qn", "QN", PinDirection.Output, domainId), initialQn);

        Net clkN = builder.Rename(builder.AddNet("clk_n", domainId), "not clock");
        Net masterQ = builder.SetInitial(
            builder.Rename(builder.AddNet("master_q", domainId), "master Q"),
            initialQ);
        Net masterQn = builder.SetInitial(
            builder.Rename(builder.AddNet("master_qn", domainId), "master QN"),
            initialQn);

        builder.AddInstance("inv", notId, 80, 60);
        builder.AddInstance("master", latchId, 340, 60);
        builder.AddInstance("slave", latchId, 640, 220);

        builder.Connect(clk, builder.Pin("inv", "a"));
        builder.Connect(clkN, builder.Pin("inv", "y"), builder.Pin("master", "e"));
        builder.Connect(d, builder.Pin("master", "d"));
        builder.Connect(masterQ, builder.Pin("master", "q"), builder.Pin("slave", "d"));
        builder.Connect(masterQn, builder.Pin("master", "qn"));
        builder.Connect(clk, builder.Pin("slave", "e"));
        builder.Connect(q, builder.Pin("slave", "q"));
        builder.Connect(qn, builder.Pin("slave", "qn"));
    }

    private static void AddBalancedTernaryHalfAdder(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, "std.bt-half-adder", "Balanced ternary half adder");
        builder.Definition.Description =
            "Adds two balanced-ternary digits, producing a sum trit and carry trit: A+B = 3*C + S.";

        Net a = builder.AddPin("a", "A", PinDirection.Input, StandardDomains.BalancedTernaryId);
        Net b = builder.AddPin("b", "B", PinDirection.Input, StandardDomains.BalancedTernaryId);
        Net s = builder.AddPin("s", "S", PinDirection.Output, StandardDomains.BalancedTernaryId);
        Net c = builder.AddPin("c", "C", PinDirection.Output, StandardDomains.BalancedTernaryId);

        int[] sumTable = [2, 0, 1, 0, 1, 2, 1, 2, 0];
        int[] carryTable = [0, 1, 1, 1, 1, 1, 1, 1, 2];

        builder.AddPrimitive("sum", new LookupSpec(2, sumTable, GateDelay), 160, 60);
        builder.AddPrimitive("carry", new LookupSpec(2, carryTable, GateDelay), 160, 200);

        builder.Connect(a, builder.Pin("sum", "in0"), builder.Pin("carry", "in0"));
        builder.Connect(b, builder.Pin("sum", "in1"), builder.Pin("carry", "in1"));
        builder.Connect(s, builder.Pin("sum", "out"));
        builder.Connect(c, builder.Pin("carry", "out"));
    }

    private static void AddBalancedTernaryFullAdder(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, BalancedTernaryFullAdderId, "Balanced ternary full adder");
        builder.Definition.Description =
            "Adds three balanced-ternary trits: A+B+CIN = 3*COUT + S. Both outputs are trits.";

        Net a = builder.AddPin("a", "A", PinDirection.Input, StandardDomains.BalancedTernaryId);
        Net b = builder.AddPin("b", "B", PinDirection.Input, StandardDomains.BalancedTernaryId);
        Net cin = builder.AddPin("cin", "CIN", PinDirection.Input, StandardDomains.BalancedTernaryId);
        Net s = builder.AddPin("s", "S", PinDirection.Output, StandardDomains.BalancedTernaryId);
        Net cout = builder.AddPin("cout", "COUT", PinDirection.Output, StandardDomains.BalancedTernaryId);

        int[] sumTable = new int[27];
        int[] carryTable = new int[27];
        for (int ai = 0; ai < 3; ai++)
        {
            for (int bi = 0; bi < 3; bi++)
            {
                for (int ci = 0; ci < 3; ci++)
                {
                    int sum = (ai - 1) + (bi - 1) + (ci - 1);
                    int carry = sum >= 2 ? 1 : sum <= -2 ? -1 : 0;
                    int result = sum - (3 * carry);
                    int index = ((ai * 3) + bi) * 3 + ci;
                    sumTable[index] = result + 1;
                    carryTable[index] = carry + 1;
                }
            }
        }

        builder.AddPrimitive("sum", new LookupSpec(3, sumTable, GateDelay), 160, 60);
        builder.AddPrimitive("carry", new LookupSpec(3, carryTable, GateDelay), 160, 240);
        builder.Connect(a, builder.Pin("sum", "in0"), builder.Pin("carry", "in0"));
        builder.Connect(b, builder.Pin("sum", "in1"), builder.Pin("carry", "in1"));
        builder.Connect(cin, builder.Pin("sum", "in2"), builder.Pin("carry", "in2"));
        builder.Connect(s, builder.Pin("sum", "out"));
        builder.Connect(cout, builder.Pin("carry", "out"));
    }

    private static void AddBalancedTernaryAdder2(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, BalancedTernaryAdder2Id, "Balanced ternary 2-trit adder");
        builder.Definition.Description =
            "Adds two balanced-ternary 2-trit numbers: A+B = 9*COUT + S. " +
            "The low full adder seeds a zero trit; its carry feeds the high trit.";

        var aNets = new Net[2];
        var bNets = new Net[2];
        var sNets = new Net[2];
        for (int i = 0; i < 2; i++)
        {
            aNets[i] = builder.AddPin($"a{i}", $"A{i}", PinDirection.Input, StandardDomains.BalancedTernaryId);
            bNets[i] = builder.AddPin($"b{i}", $"B{i}", PinDirection.Input, StandardDomains.BalancedTernaryId);
            sNets[i] = builder.AddPin($"s{i}", $"S{i}", PinDirection.Output, StandardDomains.BalancedTernaryId);
        }

        Net cout = builder.AddPin("cout", "COUT", PinDirection.Output, StandardDomains.BalancedTernaryId);
        Net zero = builder.AddNet("zero", StandardDomains.BalancedTernaryId);
        builder.AddPrimitive("const0", new ConstantSpec(1), 60, 340);
        builder.Connect(zero, builder.Pin("const0", PrimitiveShapes.OutputPinId));
        Net carry = builder.AddNet("carry", StandardDomains.BalancedTernaryId);

        builder.AddInstance("low", BalancedTernaryFullAdderId, 260, 100);
        builder.Connect(aNets[0], builder.Pin("low", "a"));
        builder.Connect(bNets[0], builder.Pin("low", "b"));
        builder.Connect(zero, builder.Pin("low", "cin"));
        builder.Connect(sNets[0], builder.Pin("low", "s"));
        builder.Connect(carry, builder.Pin("low", "cout"));

        builder.AddInstance("high", BalancedTernaryFullAdderId, 620, 100);
        builder.Connect(aNets[1], builder.Pin("high", "a"));
        builder.Connect(bNets[1], builder.Pin("high", "b"));
        builder.Connect(carry, builder.Pin("high", "cin"));
        builder.Connect(sNets[1], builder.Pin("high", "s"));
        builder.Connect(cout, builder.Pin("high", "cout"));

        builder.AddNumericLane("a", "A", NumericFormats.BalancedTernary.Id, "a0", "a1");
        builder.AddNumericLane("b", "B", NumericFormats.BalancedTernary.Id, "b0", "b1");
        builder.AddNumericLane("s", "S", NumericFormats.BalancedTernary.Id, "s0", "s1");
    }
}
