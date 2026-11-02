using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class ElectricalHierarchyEditorTests
{
    [Fact]
    public void PortsAndASubcircuitInstanceCanBeAuthoredThroughThePanel()
    {
        EditorViewModel viewModel = CreateEditor();

        // Child circuit: n2 -- r1 -- n3 -- r2 -- ground, exposing n2, n3 and ground.
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalNode();
        viewModel.AddElectricalNode();
        viewModel.AddElectricalComponent();
        viewModel.SetElectricalTerminalNode("r1", "a", "n2");
        viewModel.SetElectricalTerminalNode("r1", "b", "n3");
        viewModel.AddElectricalComponent();
        viewModel.SetElectricalTerminalNode("r2", "a", "n3");
        viewModel.SetElectricalTerminalNode("r2", "b", "gnd");

        EditableField ports = viewModel.ElectricalCircuitFields.Single(field => field.Label == "Ports");
        ports.Value = "in:n2; out:n3; gnd:gnd";
        Assert.True(viewModel.ApplyField(ports));
        Assert.Equal(3, viewModel.SelectedElectricalCircuitModel!.Ports.Count);
        Assert.Contains(
            "3 ports",
            viewModel.ElectricalCircuits.Single(item => item.Id == "dc1").Summary,
            StringComparison.Ordinal);

        // Parent circuit: a 5 V source drives the child's input; the output reads the divider.
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalNode();
        viewModel.AddElectricalComponent();
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.VoltageSource);
        Assert.Equal(ElectricalComponentKind.VoltageSource, viewModel.SelectedElectricalCircuitModel!
            .FindComponent("r1")!.Kind);
        viewModel.SetElectricalTerminalNode("r1", "a", "n1");
        viewModel.SetElectricalTerminalNode("r1", "b", "gnd");
        EditableField voltage = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Value (V)");
        voltage.Value = "5";
        Assert.True(viewModel.ApplyField(voltage));

        viewModel.AddElectricalComponent();
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.Subcircuit);
        ElectricalComponent instance = viewModel.SelectedElectricalCircuitModel!.FindComponent("r2")!;
        Assert.Equal(ElectricalComponentKind.Subcircuit, instance.Kind);
        Assert.Empty(instance.Terminals);

        EditableField circuit = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Circuit");
        circuit.Value = "dc1";
        Assert.True(viewModel.ApplyField(circuit));
        Assert.Equal("dc1", instance.CircuitId);
        Assert.Equal(
            ["in", "out", "gnd"],
            instance.Terminals.Select(terminal => terminal.Id));

        viewModel.SetElectricalTerminalNode("r2", "in", "n1");
        viewModel.SetElectricalTerminalNode("r2", "out", "n2");
        viewModel.SetElectricalTerminalNode("r2", "gnd", "gnd");

        EditableField overrides = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Overrides");
        overrides.Value = "r2=3k";
        Assert.True(viewModel.ApplyField(overrides));
        Assert.Equal(3_000, Assert.Single(instance.Overrides).Value);
        Assert.Empty(viewModel.ElectricalDiagnostics);

        Assert.True(viewModel.SolveElectricalCircuit(), viewModel.ElectricalSolveSummary);
        ElectricalNodeResultItem output = viewModel.ElectricalNodeResults.Single(node => node.Id == "n2");
        Assert.Equal(3.75, output.Volts!.Value, 12);
        Assert.Contains("Solved 'dc2'", viewModel.ElectricalSolveSummary, StringComparison.Ordinal);

        EditableField refreshed = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Overrides");
        refreshed.Value = "missing=3k";
        Assert.False(viewModel.ApplyField(refreshed));
        Assert.Contains("no component", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3_000, Assert.Single(instance.Overrides).Value);
    }

    [Fact]
    public void SolvedHierarchyShowsPrivateChildNodesWithTheirInstanceScope()
    {
        EditorViewModel viewModel = new(
            ProjectSerializer.Load(
                ExamplePath("electrical-ternary-hierarchy.radix"), validate: false));
        viewModel.SelectedElectricalCircuit = viewModel.ElectricalCircuits
            .Single(item => item.Id == "chain");

        Assert.True(viewModel.SolveElectricalCircuit(), viewModel.ElectricalSolveSummary);

        ElectricalNodeResultItem childNode = viewModel.ElectricalNodeResults
            .Single(node => node.Id == "X1/out_low");
        Assert.Equal("X1", childNode.Scope);
        Assert.Equal("Low-threshold inverter output", childNode.Name);
        Assert.True(childNode.HasVoltage);

        // The ported output is the parent's 'mid' node, so it is one row, not a second X1/out row.
        Assert.DoesNotContain(
            viewModel.ElectricalNodeResults,
            node => node.Id == "X1/out");
        Assert.Equal(
            ["root", "X1", "X2"],
            viewModel.ElectricalNodeResults
                .Select(node => node.Scope)
                .Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void EditingAChildInterfaceKeepsParentTerminalsInStep()
    {
        EditorViewModel viewModel = CreateEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalNode();
        viewModel.AddElectricalComponent();
        viewModel.SetElectricalTerminalNode("r1", "a", "n2");
        viewModel.SetElectricalTerminalNode("r1", "b", "gnd");
        EditableField ports = viewModel.ElectricalCircuitFields.Single(field => field.Label == "Ports");
        ports.Value = "out:n2; gnd:gnd";
        Assert.True(viewModel.ApplyField(ports));

        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.Subcircuit);
        EditableField circuit = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Circuit");
        circuit.Value = "dc1";
        Assert.True(viewModel.ApplyField(circuit));
        Assert.Equal(2, viewModel.SelectedElectricalCircuitModel!.FindComponent("r1")!.Terminals.Count);

        viewModel.SelectedElectricalCircuit = viewModel.ElectricalCircuits
            .Single(item => item.Id == "dc1");
        viewModel.AddElectricalNode();
        ports = viewModel.ElectricalCircuitFields.Single(field => field.Label == "Ports");
        ports.Value = "out:n2; tap:n3; gnd:gnd";
        Assert.True(viewModel.ApplyField(ports));

        viewModel.SelectedElectricalCircuit = viewModel.ElectricalCircuits
            .Single(item => item.Id == "dc2");
        ElectricalComponent instance = viewModel.SelectedElectricalCircuitModel!.FindComponent("r1")!;
        Assert.Equal(
            ["out", "tap", "gnd"],
            instance.Terminals.Select(terminal => terminal.Id));
        Assert.Equal("gnd", instance.FindTerminal("tap")!.NodeId);
    }

    [Fact]
    public void InvalidPortTextIsRejectedWithoutChangingTheInterface()
    {
        EditorViewModel viewModel = CreateEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalNode();
        EditableField ports = viewModel.ElectricalCircuitFields.Single(field => field.Label == "Ports");
        ports.Value = "out:n2; gnd:gnd";
        Assert.True(viewModel.ApplyField(ports));

        ports.Value = "out:n2; out:gnd";
        Assert.False(viewModel.ApplyField(ports));
        Assert.Contains("repeated", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, viewModel.SelectedElectricalCircuitModel!.Ports.Count);

        ports.Value = "out:missing; gnd:gnd";
        Assert.False(viewModel.ApplyField(ports));
        Assert.Contains("unknown node", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, viewModel.SelectedElectricalCircuitModel.Ports.Count);
    }

    private static string ExamplePath(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "examples", fileName);
    }

    private static EditorViewModel CreateEditor()
    {
        var project = CircuitProject.CreateEmpty("Electrical hierarchy editor test");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);
        return new EditorViewModel(project);
    }
}
