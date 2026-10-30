using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

public sealed class ElectricalCircuitTests
{
    [Fact]
    public void AuthoredDividerBuildsAndSolvesWithExplicitTerminals()
    {
        ElectricalCircuit circuit = CreateDivider();

        DcNetlist netlist = circuit.ToDcNetlist();
        DcSolution solution = netlist.Solve();

        Assert.Equal(3, netlist.Elements.Count);
        Assert.Equal(10, solution.NodeVoltages["in"]);
        Assert.Equal(5, solution.NodeVoltages["out"]);
        Assert.Equal(5e-3, solution.ElementCurrents["R1"]);
        Assert.Equal(-5e-3, solution.ElementCurrents["V1"]);
        Assert.Equal(ElectricalComponentKind.Resistor, circuit.FindComponent("R1")!.Kind);
        Assert.Equal("ohm", circuit.FindComponent("R1")!.Unit);
        Assert.Equal("a", circuit.FindComponent("R1")!.Terminals[0].Id);
        Assert.Equal("in", circuit.FindComponent("R1")!.Terminals[0].NodeId);
    }

    [Fact]
    public void ComponentMetadataDefinesTerminalPolarityAndUnits()
    {
        Assert.Equal(
            ["a", "b"],
            ElectricalComponentMetadata.Terminals(ElectricalComponentKind.Resistor)
                .Select(terminal => terminal.Id));
        Assert.Equal(
            ElectricalTerminalPolarity.Positive,
            ElectricalComponentMetadata.Terminals(ElectricalComponentKind.VoltageSource)[0].Polarity);
        Assert.Equal("Resistance", ElectricalComponentMetadata.ParameterName(ElectricalComponentKind.Resistor));
        Assert.Equal("V", ElectricalComponentMetadata.Unit(ElectricalComponentKind.VoltageSource));
    }

    [Fact]
    public void ProjectRoundTripPreservesAuthoredElectricalCircuit()
    {
        CircuitProject project = CreateProject();
        project.ElectricalCircuits.Add(CreateDivider());

        string json = ProjectSerializer.SaveToString(project);
        Assert.Contains("\"electricalCircuits\"", json, StringComparison.Ordinal);

        CircuitProject loaded = ProjectSerializer.LoadFromString(json, validate: false);
        ElectricalCircuit circuit = Assert.Single(loaded.ElectricalCircuits);

        Assert.Equal(8, loaded.FormatVersion);
        Assert.Equal("divider", circuit.Id);
        Assert.Equal(5, circuit.ToDcNetlist().Solve().NodeVoltages["out"]);
        Assert.Equal(ElectricalComponentKind.VoltageSource, loaded.ElectricalCircuits[0].Components[0].Kind);
    }

    [Fact]
    public void MissingTerminalAndUnknownNodeAreValidationErrors()
    {
        var missing = new ElectricalCircuit
        {
            Id = "missing",
            Name = "Missing terminal",
            GroundNodeId = "gnd",
        };
        missing.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        var source = new ElectricalComponent
        {
            Id = "I1",
            Kind = ElectricalComponentKind.CurrentSource,
            Value = 1e-3,
        };
        source.Terminals.Add(new ElectricalTerminal
        {
            Id = "a",
            Name = "Positive",
            Polarity = ElectricalTerminalPolarity.Positive,
            NodeId = "gnd",
        });
        missing.Components.Add(source);

        DiagnosticBag diagnostics = ElectricalCircuitValidator.Validate(missing);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "RAD-ELEC-008");
        Assert.Throws<ElectricalCircuitValidationException>(() => missing.ToDcNetlist());

        ElectricalCircuit unknownNode = CreateDivider();
        unknownNode.FindComponent("R1")!.FindTerminal("b")!.NodeId = "missing";
        DiagnosticBag unknownDiagnostics = ElectricalCircuitValidator.Validate(unknownNode);
        Assert.Contains(unknownDiagnostics, diagnostic => diagnostic.Code == "RAD-ELEC-011");
    }

    [Fact]
    public void InvalidValueSelfLoopAndUngroundedCircuitAreRejected()
    {
        ElectricalCircuit invalidValue = CreateDivider();
        invalidValue.FindComponent("R1")!.Value = 0;
        Assert.Contains(
            ElectricalCircuitValidator.Validate(invalidValue),
            diagnostic => diagnostic.Code == "RAD-ELEC-007");

        ElectricalCircuit selfLoop = CreateDivider();
        selfLoop.FindComponent("R1")!.FindTerminal("b")!.NodeId = "in";
        Assert.Contains(
            ElectricalCircuitValidator.Validate(selfLoop),
            diagnostic => diagnostic.Code == "RAD-ELEC-013");

        var ungrounded = new ElectricalCircuit
        {
            Id = "ungrounded",
            Name = "Ungrounded",
            GroundNodeId = "gnd",
        };
        ungrounded.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        ungrounded.Nodes.Add(new ElectricalNode { Id = "a", Name = "A" });
        ungrounded.Nodes.Add(new ElectricalNode { Id = "b", Name = "B" });
        ungrounded.Components.Add(Component(
            "R1",
            ElectricalComponentKind.Resistor,
            1_000,
            "a",
            "b"));
        Assert.Contains(
            ElectricalCircuitValidator.Validate(ungrounded),
            diagnostic => diagnostic.Code == "RAD-ELEC-014");
    }

    [Fact]
    public void SerializedReportIncludesCircuitIdentity()
    {
        DcNetlist netlist = CreateDivider().ToDcNetlist();
        DcSolution solution = netlist.Solve();

        string json = DcNetlistJson.ToReportJson("examples/dc-divider.radix", netlist, solution, "divider");
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("divider", document.RootElement.GetProperty("circuit").GetString());
    }

    private static CircuitProject CreateProject()
    {
        CircuitProject project = CircuitProject.CreateEmpty("DC project");
        project.Definitions.Add(new CircuitDefinition { Id = "main", Name = "Main" });
        project.RootDefinitionId = "main";
        return project;
    }

    private static ElectricalCircuit CreateDivider()
    {
        var circuit = new ElectricalCircuit
        {
            Id = "divider",
            Name = "Voltage divider",
            GroundNodeId = "gnd",
        };
        circuit.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Add(Component(
            "V1",
            ElectricalComponentKind.VoltageSource,
            10,
            "in",
            "gnd"));
        circuit.Components.Add(Component(
            "R1",
            ElectricalComponentKind.Resistor,
            1_000,
            "in",
            "out"));
        circuit.Components.Add(Component(
            "R2",
            ElectricalComponentKind.Resistor,
            1_000,
            "out",
            "gnd"));
        return circuit;
    }

    private static ElectricalComponent Component(
        string id,
        ElectricalComponentKind kind,
        double value,
        string nodeA,
        string nodeB)
    {
        var component = new ElectricalComponent
        {
            Id = id,
            Name = id,
            Kind = kind,
            Value = value,
        };
        bool source = kind != ElectricalComponentKind.Resistor;
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "a",
            Name = source ? "Positive" : "A",
            Polarity = source ? ElectricalTerminalPolarity.Positive : ElectricalTerminalPolarity.Passive,
            NodeId = nodeA,
        });
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "b",
            Name = source ? "Negative" : "B",
            Polarity = source ? ElectricalTerminalPolarity.Negative : ElectricalTerminalPolarity.Passive,
            NodeId = nodeB,
        });
        return component;
    }
}
