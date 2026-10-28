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

        return added;
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
}
