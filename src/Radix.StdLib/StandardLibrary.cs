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
    public const string TernaryFullAdderId = "std.t-full-adder";
    public const string TernaryAdder2Id = "std.t-adder-2";
    public const string Ram16x4Id = "std.ram-16x4";

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

        if (project.FindDefinition(Ram16x4Id) is null)
        {
            AddRam16x4(project);
            added.Add(Ram16x4Id);
        }

        added.AddRange(AddTernaryDefinitions(project));
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
        return added;
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

    private static void AddTernaryLatch(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, TernaryLatchId, "Ternary D latch");
        builder.Definition.Description =
            "Level-sensitive trit latch on the 0/1/2 domain. E=2 captures D, E=0 holds, and E=1 gives an unknown trit. " +
            "Two cross-coupled Kleene NANDs store the trit, so the state survives the enable transition; Q/QN power up at 0/2.";

        Net d = builder.AddPin("d", "D", PinDirection.Input, StandardDomains.TernaryId);
        Net e = builder.AddPin("e", "E", PinDirection.Input, StandardDomains.TernaryId);
        Net q = builder.SetInitial(builder.AddPin("q", "Q", PinDirection.Output, StandardDomains.TernaryId), 0);
        Net qn = builder.SetInitial(builder.AddPin("qn", "QN", PinDirection.Output, StandardDomains.TernaryId), 2);

        Net notD = builder.Rename(builder.AddNet("not_d", StandardDomains.TernaryId), "not D");
        Net setIn = builder.Rename(builder.AddNet("set_in", StandardDomains.TernaryId), "D and E");
        Net resetIn = builder.Rename(builder.AddNet("reset_in", StandardDomains.TernaryId), "not D and E");
        Net setBar = builder.Rename(builder.AddNet("set_bar", StandardDomains.TernaryId), "set");
        Net resetBar = builder.Rename(builder.AddNet("reset_bar", StandardDomains.TernaryId), "reset");
        Net qHold = builder.Rename(builder.AddNet("q_hold", StandardDomains.TernaryId), "set and QN");
        Net qnHold = builder.Rename(builder.AddNet("qn_hold", StandardDomains.TernaryId), "reset and Q");

        builder.AddInstance("inv_d", "std.t-lognot", 60, 200);
        builder.AddInstance("capture", "std.t-min", 240, 40);
        builder.AddInstance("release", "std.t-min", 240, 240);
        builder.AddInstance("nand_set", "std.t-lognot", 440, 40);
        builder.AddInstance("nand_reset", "std.t-lognot", 440, 240);
        builder.AddInstance("hold_q", "std.t-min", 620, 40);
        builder.AddInstance("hold_qn", "std.t-min", 620, 240);
        builder.AddInstance("q_bar", "std.t-lognot", 800, 40);
        builder.AddInstance("qn_bar", "std.t-lognot", 800, 240);

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

    private static void AddTernaryDFlipFlop(CircuitProject project)
    {
        var builder = new DefinitionBuilder(project, TernaryDFlipFlopId, "Ternary D flip-flop");
        builder.Definition.Description =
            "Positive-edge-triggered trit flip-flop built from two ternary latches and an inverter. " +
            "Drive CLK between 0 and 2; the slave captures on 2. Q/QN power up at 0/2.";

        Net d = builder.AddPin("d", "D", PinDirection.Input, StandardDomains.TernaryId);
        Net clk = builder.AddPin("clk", "CLK", PinDirection.Input, StandardDomains.TernaryId);
        Net q = builder.SetInitial(builder.AddPin("q", "Q", PinDirection.Output, StandardDomains.TernaryId), 0);
        Net qn = builder.SetInitial(builder.AddPin("qn", "QN", PinDirection.Output, StandardDomains.TernaryId), 2);

        Net clkN = builder.Rename(builder.AddNet("clk_n", StandardDomains.TernaryId), "not clock");
        Net masterQ = builder.SetInitial(
            builder.Rename(builder.AddNet("master_q", StandardDomains.TernaryId), "master Q"),
            0);
        Net masterQn = builder.SetInitial(
            builder.Rename(builder.AddNet("master_qn", StandardDomains.TernaryId), "master QN"),
            2);

        builder.AddInstance("inv", "std.t-lognot", 80, 60);
        builder.AddInstance("master", TernaryLatchId, 340, 60);
        builder.AddInstance("slave", TernaryLatchId, 640, 220);

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
