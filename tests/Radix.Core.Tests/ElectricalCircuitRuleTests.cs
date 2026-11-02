using Radix.Core.Analysis;
using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Electrical ERC over authored DC circuits. The rules report topology the DC
/// solver cannot solve; they are warnings so an electrical asset never blocks
/// digital compilation.
/// </summary>
public sealed class ElectricalCircuitRuleTests
{
    [Fact]
    public void ValidDividerHasNoElectricalRuleWarning()
    {
        CircuitProject project = ProjectWith(Divider());

        DiagnosticBag report = CircuitValidator.Validate(project);

        Assert.DoesNotContain(
            report,
            diagnostic => diagnostic.Code.StartsWith("RAD-ERC-1", StringComparison.Ordinal));
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void FloatingElectricalIslandIsReportedWithItsNodes()
    {
        ElectricalCircuit circuit = Divider();
        AddNode(circuit, "islandA");
        AddNode(circuit, "islandB");
        AddResistor(circuit, "R3", 1_000, "islandA", "islandB");
        AddResistor(circuit, "R4", 2_000, "islandA", "islandB");
        CircuitProject project = ProjectWith(circuit);

        DiagnosticBag report = CircuitValidator.Validate(project);

        Diagnostic diagnostic = Assert.Single(
            report,
            item => item.Code == ElectricalRuleChecker.ElectricalFloatingNodesCode);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(DiagnosticNature.Structural, diagnostic.Nature);
        Assert.Equal("electrical/divider", diagnostic.Path);
        Assert.Contains("'islandA'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("'islandB'", diagnostic.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Assumption));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Suggestion));
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void ReusedFloatingChildIsReportedOncePerOwningCircuit()
    {
        ElectricalCircuit child = Divider();
        child.Ports.Add(new ElectricalPort { Id = "in", Name = "Input", NodeId = "in" });
        child.Ports.Add(new ElectricalPort { Id = "out", Name = "Output", NodeId = "out" });
        child.Ports.Add(new ElectricalPort { Id = "gnd", Name = "Ground", NodeId = "gnd" });
        AddNode(child, "islandA");
        AddNode(child, "islandB");
        AddResistor(child, "R3", 1_000, "islandA", "islandB");
        AddResistor(child, "R4", 2_000, "islandA", "islandB");
        ElectricalCircuit parent = ParentWithTwoInstances(child);

        DiagnosticBag report = CircuitValidator.Validate(ProjectWith(child, parent));

        Diagnostic[] warnings = report
            .Where(diagnostic => diagnostic.Code == ElectricalRuleChecker.ElectricalFloatingNodesCode)
            .ToArray();
        Assert.True(
            warnings.Length == 2,
            string.Join(" || ", warnings.Select(warning => $"{warning.Path}: {warning.Message}")));

        Diagnostic childWarning = Assert.Single(
            warnings,
            diagnostic => diagnostic.Path == "electrical/divider");
        Assert.Contains("'islandA'", childWarning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'X1/islandA'", childWarning.Message, StringComparison.Ordinal);

        Diagnostic parentWarning = Assert.Single(
            warnings,
            diagnostic => diagnostic.Path == "electrical/parent");
        Assert.Contains("'X1/islandA'", parentWarning.Message, StringComparison.Ordinal);
        Assert.Contains("'X2/islandA'", parentWarning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'islandA'", parentWarning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContradictoryIdealVoltageSourcesAreReported()
    {
        ElectricalCircuit circuit = Divider();
        AddSource(circuit, "V2", ElectricalComponentKind.VoltageSource, 6, "in", "gnd");
        CircuitProject project = ProjectWith(circuit);

        DiagnosticBag report = CircuitValidator.Validate(project);

        Diagnostic diagnostic = Assert.Single(
            report,
            item => item.Code == ElectricalRuleChecker.ElectricalContradictorySourcesCode);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("'V2'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("no solution", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void ConsistentButRedundantSourceLoopIsReportedWithoutClaimingAContradiction()
    {
        var circuit = new ElectricalCircuit
        {
            Id = "redundant",
            Name = "Redundant sources",
            GroundNodeId = "gnd",
        };
        AddNode(circuit, "gnd");
        AddNode(circuit, "a");
        AddNode(circuit, "b");
        AddSource(circuit, "V1", ElectricalComponentKind.VoltageSource, 5, "a", "gnd");
        AddSource(circuit, "V2", ElectricalComponentKind.VoltageSource, 2, "a", "b");
        AddSource(circuit, "V3", ElectricalComponentKind.VoltageSource, 3, "b", "gnd");
        AddResistor(circuit, "R1", 1_000, "a", "gnd");

        DiagnosticBag report = CircuitValidator.Validate(ProjectWith(circuit));

        Assert.DoesNotContain(
            report,
            diagnostic => diagnostic.Code == ElectricalRuleChecker.ElectricalContradictorySourcesCode);
        Diagnostic diagnostic = Assert.Single(
            report,
            item => item.Code == ElectricalRuleChecker.ElectricalRedundantSourceLoopCode);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("not unique", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contradict", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void StructurallyInvalidCircuitIsSkippedWithoutThrowing()
    {
        ElectricalCircuit circuit = Divider();
        AddResistor(circuit, "R2", 1_000, "missing", "gnd");
        CircuitProject project = ProjectWith(circuit);

        DiagnosticBag report = CircuitValidator.Validate(project);

        Assert.DoesNotContain(
            report,
            diagnostic => diagnostic.Code.StartsWith("RAD-ERC-1", StringComparison.Ordinal));
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void ProjectElectricalValidationReportsACircuitStructuralError()
    {
        ElectricalCircuit circuit = Divider();
        circuit.FindComponent("R1")!.FindTerminal("b")!.NodeId = "missing";
        CircuitProject project = ProjectWith(circuit);

        DiagnosticBag report = ElectricalCircuitValidator.ValidateProject(project);

        Diagnostic diagnostic = Assert.Single(
            report,
            item => item.Code == "RAD-ELEC-011");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("electrical/divider/R1", diagnostic.Path);
        Assert.Contains("'missing'", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void SerializedProjectKeepsElectricalRuleWarnings()
    {
        ElectricalCircuit circuit = Divider();
        AddNode(circuit, "islandA");
        AddNode(circuit, "islandB");
        AddResistor(circuit, "R3", 1_000, "islandA", "islandB");
        CircuitProject project = ProjectWith(circuit);
        string json = ProjectSerializer.SaveToString(project);
        CircuitProject reloaded = ProjectSerializer.LoadFromString(json, validate: false);

        DiagnosticBag report = CircuitValidator.Validate(reloaded);

        Assert.Contains(
            report,
            diagnostic => diagnostic.Code == ElectricalRuleChecker.ElectricalFloatingNodesCode);
    }

    private static ElectricalCircuit Divider()
    {
        var circuit = new ElectricalCircuit
        {
            Id = "divider",
            Name = "Divider",
            GroundNodeId = "gnd",
        };
        AddNode(circuit, "gnd");
        AddNode(circuit, "in");
        AddNode(circuit, "out");
        AddSource(circuit, "V1", ElectricalComponentKind.VoltageSource, 10, "in", "gnd");
        AddResistor(circuit, "R1", 1_000, "in", "out");
        AddResistor(circuit, "R2", 1_000, "out", "gnd");
        return circuit;
    }

    private static void AddNode(ElectricalCircuit circuit, string id) =>
        circuit.Nodes.Add(new ElectricalNode { Id = id, Name = id });

    private static void AddResistor(
        ElectricalCircuit circuit,
        string id,
        double ohms,
        string nodeA,
        string nodeB) =>
        AddComponent(circuit, id, ElectricalComponentKind.Resistor, ohms, nodeA, nodeB);

    private static void AddSource(
        ElectricalCircuit circuit,
        string id,
        ElectricalComponentKind kind,
        double value,
        string nodeA,
        string nodeB) =>
        AddComponent(circuit, id, kind, value, nodeA, nodeB);

    private static void AddComponent(
        ElectricalCircuit circuit,
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
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "a",
            Name = "A",
            Polarity = ElectricalComponentMetadata.Terminals(kind)[0].Polarity,
            NodeId = nodeA,
        });
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "b",
            Name = "B",
            Polarity = ElectricalComponentMetadata.Terminals(kind)[1].Polarity,
            NodeId = nodeB,
        });
        circuit.Components.Add(component);
    }

    private static CircuitProject ProjectWith(params ElectricalCircuit[] circuits)
    {
        var builder = new ProjectBuilder("electrical erc");
        var root = builder.Define("root", "Root");
        Net output = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("src", new ConstantSpec(0));
        root.Connect(output, root.Pin("src", "out"));
        CircuitProject project = builder.Build("root");
        project.ElectricalCircuits.AddRange(circuits);
        return project;
    }

    private static ElectricalCircuit ParentWithTwoInstances(ElectricalCircuit child)
    {
        var parent = new ElectricalCircuit
        {
            Id = "parent",
            Name = "Two instances",
            GroundNodeId = "gnd",
        };
        AddNode(parent, "gnd");
        AddNode(parent, "in");
        AddNode(parent, "out1");
        AddNode(parent, "out2");
        AddSource(parent, "V1", ElectricalComponentKind.VoltageSource, 10, "in", "gnd");
        parent.Components.Add(Subcircuit("X1", child.Id, "in", "out1", "gnd"));
        parent.Components.Add(Subcircuit("X2", child.Id, "in", "out2", "gnd"));
        return parent;
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
}
