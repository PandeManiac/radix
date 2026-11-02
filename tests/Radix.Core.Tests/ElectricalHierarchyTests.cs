using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

public sealed class ElectricalHierarchyTests
{
    [Fact]
    public void SubcircuitExpandsIntoTheParentSolveWithPrefixedInternalNodes()
    {
        ElectricalCircuit divider = CreateDividerChild();
        ElectricalCircuit parent = CreateParent(divider.Id, 10);

        DcNetlist netlist = parent.ToDcNetlist(Resolve(divider));
        DcSolution solution = netlist.Solve();

        // The child is in--R1--mid--R2--out--R3--gnd, so the unloaded output is 10/3 V.
        Assert.Equal(10.0 / 3.0, solution.NodeVoltages["out"], 12);
        Assert.Equal(20.0 / 3.0, solution.NodeVoltages["X1/mid"], 12);
        Assert.Equal(
            ["V1", "X1/R1", "X1/R2", "X1/R3"],
            netlist.Elements.Select(element => element.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("mid", netlist.Nodes);
        Assert.Equal(
            0,
            solution.ElementPowers.Values.Sum(),
            10);
    }

    [Fact]
    public void TwoInstancesOfOneChildKeepIndependentInternalNodes()
    {
        ElectricalCircuit divider = CreateDividerChild();
        var parent = new ElectricalCircuit
        {
            Id = "two",
            Name = "Two loads",
            GroundNodeId = "gnd",
        };
        parent.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        parent.Nodes.Add(new ElectricalNode { Id = "in1", Name = "Input one" });
        parent.Nodes.Add(new ElectricalNode { Id = "in2", Name = "Input two" });
        parent.Nodes.Add(new ElectricalNode { Id = "out1", Name = "Output one" });
        parent.Nodes.Add(new ElectricalNode { Id = "out2", Name = "Output two" });
        parent.Components.Add(VoltageSource("V1", "in1", "gnd", 10));
        parent.Components.Add(VoltageSource("V2", "in2", "gnd", 20));
        parent.Components.Add(Subcircuit("X1", divider, "in1", "out1", "gnd"));
        parent.Components.Add(Subcircuit("X2", divider, "in2", "out2", "gnd"));

        DcSolution solution = parent.ToDcNetlist(Resolve(divider)).Solve();

        Assert.Equal(20.0 / 3.0, solution.NodeVoltages["X1/mid"], 12);
        Assert.Equal(40.0 / 3.0, solution.NodeVoltages["X2/mid"], 12);
        Assert.Equal(10.0 / 3.0, solution.NodeVoltages["out1"], 12);
        Assert.Equal(20.0 / 3.0, solution.NodeVoltages["out2"], 12);
    }

    [Fact]
    public void NestedSubcircuitsExpandRecursively()
    {
        ElectricalCircuit divider = CreateDividerChild();
        ElectricalCircuit stage = CreateWrapper("stage", divider);
        ElectricalCircuit root = CreateWrapper("root", stage);
        root.Components.Add(VoltageSource("V1", "in", "gnd", 10));

        DcNetlist netlist = root.ToDcNetlist(Resolve(divider, stage));
        DcSolution solution = netlist.Solve();

        Assert.Contains("X1/X1/mid", netlist.Nodes);
        Assert.Equal(10.0 / 3.0, solution.NodeVoltages["out"], 12);
        Assert.Equal(
            ["V1", "X1/X1/R1", "X1/X1/R2", "X1/X1/R3"],
            netlist.Elements.Select(element => element.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SubcircuitTransientRunMatchesTheFlatRcClosedForm()
    {
        var child = new ElectricalCircuit
        {
            Id = "rc",
            Name = "RC load",
            GroundNodeId = "gnd",
        };
        child.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        child.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        child.Ports.Add(new ElectricalPort { Id = "out", Name = "Output", NodeId = "out" });
        child.Ports.Add(new ElectricalPort { Id = "gnd", Name = "Ground", NodeId = "gnd" });
        child.Components.Add(Resistor("R1", "out", "gnd", 1_000));
        ElectricalComponent capacitor = Capacitor("C1", "out", "gnd", 1e-6);
        capacitor.InitialValue = 5;
        child.Components.Add(capacitor);

        var parent = new ElectricalCircuit
        {
            Id = "parent",
            Name = "Parent",
            GroundNodeId = "gnd",
        };
        parent.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        parent.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        parent.Components.Add(Subcircuit("X1", child, outNode: "out", groundNode: "gnd"));

        var options = new TransientOptions { StopTime = 1e-3, Steps = 50 };
        TransientResult result = parent
            .ToTransientNetlist(Resolve(child))
            .Run(options);

        Assert.True(result.Completed, result.Message);
        double expected = 5;
        double h = options.TimeStep;
        for (int step = 1; step <= options.Steps; step++)
        {
            expected /= 1 + (h / 1e-3);
            Assert.Equal(expected, result.Points[step].NodeVoltages["out"], 9);
        }

        Assert.Contains("X1/C1", result.Points[0].ElementCurrents.Keys);
    }

    [Fact]
    public void HierarchyRoundTripsThroughTheProjectFormat()
    {
        ElectricalCircuit divider = CreateDividerChild();
        ElectricalCircuit parent = CreateParent(divider.Id, 10);
        CircuitProject project = CreateProject();
        project.ElectricalCircuits.Add(divider);
        project.ElectricalCircuits.Add(parent);

        string json = ProjectSerializer.SaveToString(project);
        Assert.Contains("\"ports\"", json, StringComparison.Ordinal);
        Assert.Contains("\"circuit\": \"divider\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"value\": 0", json, StringComparison.Ordinal);

        CircuitProject loaded = ProjectSerializer.LoadFromString(json, validate: false);
        ElectricalCircuit reloadedChild = loaded.FindElectricalCircuit("divider")!;
        ElectricalCircuit reloadedParent = loaded.FindElectricalCircuit("parent")!;
        ElectricalComponent instance = reloadedParent.FindComponent("X1")!;

        Assert.Equal(3, reloadedChild.Ports.Count);
        Assert.Equal(ElectricalComponentKind.Subcircuit, instance.Kind);
        Assert.Equal("divider", instance.CircuitId);
        Assert.Equal(0, instance.Value);
        Assert.Equal(
            ["in", "out", "gnd"],
            instance.Terminals.Select(terminal => terminal.Id));
        Assert.Equal(
            10.0 / 3.0,
            reloadedParent.ToDcNetlist(loaded.FindElectricalCircuit)
                .Solve().NodeVoltages["out"],
            12);
    }

    [Fact]
    public void MissingOrRecursiveReferencesAreValidationErrors()
    {
        ElectricalCircuit divider = CreateDividerChild();
        ElectricalCircuit parent = CreateParent("missing", 10);
        DiagnosticBag missing = ElectricalCircuitValidator.Validate(
            parent,
            Resolve(divider));
        Assert.Contains(missing, diagnostic => diagnostic.Code == "RAD-ELEC-029");

        ElectricalCircuit a = CreateCycleCircuit("a", "b");
        ElectricalCircuit b = CreateCycleCircuit("b", "a");

        DiagnosticBag recursive = ElectricalCircuitValidator.Validate(a, Resolve(a, b));
        Assert.Contains(recursive, diagnostic => diagnostic.Code == "RAD-ELEC-033");
        Assert.Throws<ElectricalCircuitValidationException>(
            () => a.ToDcNetlist(Resolve(a, b)));
    }

    [Fact]
    public void SubcircuitNeedsPortsAndAnExplicitGroundPort()
    {
        ElectricalCircuit divider = CreateDividerChild();
        ElectricalCircuit noPorts = CreateWrapper("no-ports", divider);
        noPorts.Ports.Clear();
        DiagnosticBag noPortDiagnostics = ElectricalCircuitValidator.Validate(
            CreateParent("no-ports", 10),
            Resolve(noPorts));
        Assert.Contains(
            noPortDiagnostics,
            diagnostic => diagnostic.Code == "RAD-ELEC-030");

        ElectricalCircuit noGround = CreateDividerChild();
        noGround.Ports.RemoveAll(port => port.Id == "gnd");
        DiagnosticBag noGroundDiagnostics = ElectricalCircuitValidator.Validate(
            CreateParent(noGround.Id, 10),
            Resolve(noGround));
        Assert.Contains(
            noGroundDiagnostics,
            diagnostic => diagnostic.Code == "RAD-ELEC-031");
    }

    [Fact]
    public void SubcircuitTerminalsMustMatchTheReferencedPortsExactly()
    {
        ElectricalCircuit divider = CreateDividerChild();
        ElectricalCircuit parent = CreateParent(divider.Id, 10);
        ElectricalComponent instance = parent.FindComponent("X1")!;
        instance.Terminals.Remove(instance.FindTerminal("out")!);

        DiagnosticBag missing = ElectricalCircuitValidator.Validate(
            parent,
            Resolve(divider));
        Assert.Contains(missing, diagnostic => diagnostic.Code == "RAD-ELEC-032");

        parent = CreateParent(divider.Id, 10);
        instance = parent.FindComponent("X1")!;
        instance.Terminals.Add(new ElectricalTerminal
        {
            Id = "extra",
            Name = "Extra",
            Polarity = ElectricalTerminalPolarity.Passive,
            NodeId = "gnd",
        });
        DiagnosticBag unexpected = ElectricalCircuitValidator.Validate(
            parent,
            Resolve(divider));
        Assert.Contains(unexpected, diagnostic => diagnostic.Code == "RAD-ELEC-032");

        parent = CreateParent(divider.Id, 10);
        instance = parent.FindComponent("X1")!;
        instance.FindTerminal("out")!.NodeId = "nowhere";
        DiagnosticBag unknownNode = ElectricalCircuitValidator.Validate(
            parent,
            Resolve(divider));
        Assert.Contains(unknownNode, diagnostic => diagnostic.Code == "RAD-ELEC-011");
    }

    [Fact]
    public void PortIdentityAndNodeBindingsAreValidated()
    {
        ElectricalCircuit divider = CreateDividerChild();
        divider.Ports.Add(new ElectricalPort { Id = "out", Name = "Duplicate", NodeId = "in" });
        Assert.Contains(
            ElectricalCircuitValidator.Validate(divider),
            diagnostic => diagnostic.Code == "RAD-ELEC-026");

        ElectricalCircuit duplicateNode = CreateDividerChild();
        duplicateNode.Ports.Add(new ElectricalPort { Id = "in2", Name = "Another input", NodeId = "in" });
        Assert.Contains(
            ElectricalCircuitValidator.Validate(duplicateNode),
            diagnostic => diagnostic.Code == "RAD-ELEC-028");

        ElectricalCircuit unknownNode = CreateDividerChild();
        unknownNode.Ports.Add(new ElectricalPort { Id = "ghost", Name = "Ghost", NodeId = "nowhere" });
        Assert.Contains(
            ElectricalCircuitValidator.Validate(unknownNode),
            diagnostic => diagnostic.Code == "RAD-ELEC-027");
    }

    [Fact]
    public void AFlatCircuitStillValidatesWithoutAProjectResolver()
    {
        ElectricalCircuit flat = CreateDividerChild();
        flat.Components.Add(VoltageSource("V1", "in", "gnd", 10));
        Assert.False(ElectricalCircuitValidator.Validate(flat).HasErrors);
        Assert.Equal(
            10.0 / 3.0,
            flat.ToDcNetlist().Solve().NodeVoltages["out"],
            12);

        ElectricalCircuit parent = CreateParent(flat.Id, 10);
        Assert.Contains(
            ElectricalCircuitValidator.Validate(parent),
            diagnostic => diagnostic.Code == "RAD-ELEC-029");
    }

    [Fact]
    public void BuiltInKindsRejectAReferencedCircuit()
    {
        ElectricalCircuit divider = CreateDividerChild();
        divider.FindComponent("R1")!.CircuitId = "other";
        Assert.Contains(
            ElectricalCircuitValidator.Validate(divider),
            diagnostic => diagnostic.Code == "RAD-ELEC-035");
    }

    [Fact]
    public void ShippedCascadedTernaryInverterIsIdentityAtAllThreeLevels()
    {
        CircuitProject project = ProjectSerializer.Load(ExamplePath("electrical-ternary-hierarchy.radix"));
        ElectricalCircuit root = project.FindElectricalCircuit("chain")!;
        ElectricalCircuit child = project.FindElectricalCircuit("sti")!;
        Assert.Equal(4, child.Ports.Count);
        Assert.Equal(2, root.Components.Count(component =>
            component.Kind == ElectricalComponentKind.Subcircuit));

        DcNetlist netlist = root.ToDcNetlist(project.FindElectricalCircuit);
        // The ported output is the parent's 'mid' node, so the private internal nodes are the
        // two inverter outputs. They keep the instance prefix rather than leaking into the parent.
        Assert.Contains("X1/out_low", netlist.Nodes);
        Assert.Contains("X2/out_low", netlist.Nodes);

        foreach ((double input, double expected) in new[]
        {
            (0.0, 0.0),
            (2.5, 2.5),
            (5.0, 5.0),
        })
        {
            DcSolution solution = netlist.WithElementValue("VIN", input).Solve();
            Assert.Equal(expected, solution.NodeVoltages["out"], 2);
            Assert.Equal(0, solution.ElementPowers.Values.Sum(), 9);
            Assert.InRange(solution.RelativeResidual, 0, 1);
        }

        // At the middle level each stage's summing divider carries 25 uA, so the two-stage
        // chain draws about twice the single stage's middle-state current.
        DcSolution middle = netlist.WithElementValue("VIN", 2.5).Solve();
        double supply = -middle.ElementCurrents["VDD"];
        Assert.InRange(supply, 80e-6, 120e-6);
    }

    [Fact]
    public void SubcircuitPortVoltageAndCurrentAreExplainedByTheChildren()
    {
        ElectricalCircuit divider = CreateDividerChild();
        ElectricalCircuit parent = CreateParent(divider.Id, 10);
        DcNetlist netlist = parent.ToDcNetlist(Resolve(divider));
        DcSolution solution = netlist.Solve();

        double current = solution.ElementCurrents["X1/R1"];
        Assert.Equal(10.0 / 3_000.0, current, 12);
        Assert.Equal(current, solution.ElementCurrents["X1/R3"], 12);
        Assert.Equal(0, solution.ElementPowers.Values.Sum(), 10);
    }

    private static Func<string, ElectricalCircuit?> Resolve(params ElectricalCircuit[] circuits)
    {
        var byId = circuits.ToDictionary(circuit => circuit.Id, StringComparer.Ordinal);
        return id => byId.GetValueOrDefault(id);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root from the test output directory.");
    }

    private static string ExamplePath(string fileName) =>
        Path.Combine(RepositoryRoot(), "examples", fileName);

    private static CircuitProject CreateProject()
    {
        CircuitProject project = CircuitProject.CreateEmpty("Hierarchy");
        project.Definitions.Add(new CircuitDefinition { Id = "main", Name = "Main" });
        project.RootDefinitionId = "main";
        return project;
    }

    private static ElectricalCircuit CreateDividerChild()
    {
        var circuit = new ElectricalCircuit
        {
            Id = "divider",
            Name = "Loaded divider",
            GroundNodeId = "gnd",
        };
        circuit.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "mid", Name = "Middle" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Ports.Add(new ElectricalPort { Id = "in", Name = "Input", NodeId = "in" });
        circuit.Ports.Add(new ElectricalPort { Id = "out", Name = "Output", NodeId = "out" });
        circuit.Ports.Add(new ElectricalPort { Id = "gnd", Name = "Ground", NodeId = "gnd" });
        circuit.Components.Add(Resistor("R1", "in", "mid", 1_000));
        circuit.Components.Add(Resistor("R2", "mid", "out", 1_000));
        circuit.Components.Add(Resistor("R3", "out", "gnd", 1_000));
        return circuit;
    }

    private static ElectricalCircuit CreateParent(string childId, double volts)
    {
        var circuit = new ElectricalCircuit
        {
            Id = "parent",
            Name = "Parent",
            GroundNodeId = "gnd",
        };
        circuit.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Add(VoltageSource("V1", "in", "gnd", volts));
        circuit.Components.Add(Subcircuit("X1", childId, "in", "out", "gnd"));
        return circuit;
    }

    private static ElectricalCircuit CreateWrapper(string id, ElectricalCircuit child)
    {
        var circuit = new ElectricalCircuit
        {
            Id = id,
            Name = id,
            GroundNodeId = "gnd",
        };
        circuit.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Ports.Add(new ElectricalPort { Id = "in", Name = "Input", NodeId = "in" });
        circuit.Ports.Add(new ElectricalPort { Id = "out", Name = "Output", NodeId = "out" });
        circuit.Ports.Add(new ElectricalPort { Id = "gnd", Name = "Ground", NodeId = "gnd" });
        circuit.Components.Add(Subcircuit("X1", child, "in", "out", "gnd"));
        return circuit;
    }

    private static ElectricalCircuit CreateCycleCircuit(string id, string childId)
    {
        var circuit = new ElectricalCircuit
        {
            Id = id,
            Name = id,
            GroundNodeId = "gnd",
        };
        circuit.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Ports.Add(new ElectricalPort { Id = "in", Name = "Input", NodeId = "in" });
        circuit.Ports.Add(new ElectricalPort { Id = "out", Name = "Output", NodeId = "out" });
        circuit.Ports.Add(new ElectricalPort { Id = "gnd", Name = "Ground", NodeId = "gnd" });
        circuit.Components.Add(Resistor("R1", "out", "gnd", 1_000));
        circuit.Components.Add(Subcircuit("X1", childId, "in", "out", "gnd"));
        return circuit;
    }

    private static ElectricalComponent Resistor(string id, string nodeA, string nodeB, double ohms) =>
        Component(id, ElectricalComponentKind.Resistor, ohms, "a", nodeA, "b", nodeB);

    private static ElectricalComponent Capacitor(string id, string nodeA, string nodeB, double farads) =>
        Component(id, ElectricalComponentKind.Capacitor, farads, "a", nodeA, "b", nodeB);

    private static ElectricalComponent VoltageSource(
        string id,
        string positive,
        string negative,
        double volts) =>
        Component(
            id,
            ElectricalComponentKind.VoltageSource,
            volts,
            "a",
            positive,
            "b",
            negative);

    private static ElectricalComponent Subcircuit(
        string id,
        ElectricalCircuit child,
        string inNode = "in",
        string outNode = "out",
        string groundNode = "gnd")
    {
        // Bind exactly the ports the referenced circuit exposes, so a two-port child does not
        // acquire an invented input terminal in the test fixture.
        var component = new ElectricalComponent
        {
            Id = id,
            Name = id,
            Kind = ElectricalComponentKind.Subcircuit,
            CircuitId = child.Id,
        };
        foreach (ElectricalPort port in child.Ports)
        {
            string node = port.Id switch
            {
                "in" => inNode,
                "out" => outNode,
                _ => groundNode,
            };
            component.Terminals.Add(new ElectricalTerminal
            {
                Id = port.Id,
                Name = port.Name,
                Polarity = ElectricalTerminalPolarity.Passive,
                NodeId = node,
            });
        }

        return component;
    }

    private static ElectricalComponent Subcircuit(
        string id,
        string childId,
        string inNode,
        string outNode,
        string groundNode)
    {
        var component = new ElectricalComponent
        {
            Id = id,
            Name = id,
            Kind = ElectricalComponentKind.Subcircuit,
            CircuitId = childId,
        };
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "in",
            Name = "Input",
            Polarity = ElectricalTerminalPolarity.Passive,
            NodeId = inNode,
        });
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "out",
            Name = "Output",
            Polarity = ElectricalTerminalPolarity.Passive,
            NodeId = outNode,
        });
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "gnd",
            Name = "Ground",
            Polarity = ElectricalTerminalPolarity.Passive,
            NodeId = groundNode,
        });
        return component;
    }

    private static ElectricalComponent Component(
        string id,
        ElectricalComponentKind kind,
        double value,
        string firstTerminal,
        string firstNode,
        string secondTerminal,
        string secondNode)
    {
        var component = new ElectricalComponent
        {
            Id = id,
            Name = id,
            Kind = kind,
            Value = value,
        };
        ElectricalTerminalPolarity firstPolarity = kind == ElectricalComponentKind.VoltageSource
            ? ElectricalTerminalPolarity.Positive
            : ElectricalTerminalPolarity.Passive;
        ElectricalTerminalPolarity secondPolarity = kind == ElectricalComponentKind.VoltageSource
            ? ElectricalTerminalPolarity.Negative
            : ElectricalTerminalPolarity.Passive;
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = firstTerminal,
            Name = firstTerminal,
            Polarity = firstPolarity,
            NodeId = firstNode,
        });
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = secondTerminal,
            Name = secondTerminal,
            Polarity = secondPolarity,
            NodeId = secondNode,
        });
        return component;
    }
}
