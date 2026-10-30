using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class ElectricalEditorTests
{
    [Fact]
    public void AuthoredCircuitCanBeCreatedAndEditedThroughThePanel()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        Assert.False(viewModel.HasElectricalCircuits);
        Assert.False(viewModel.HasSelectedElectricalCircuit);

        viewModel.AddElectricalCircuit();

        Assert.True(viewModel.HasElectricalCircuits);
        Assert.True(viewModel.HasSelectedElectricalCircuit);
        Assert.Equal("dc1", viewModel.SelectedElectricalCircuitId);
        Assert.Equal(2, viewModel.ElectricalNodes.Count);
        Assert.Equal("gnd", viewModel.SelectedElectricalCircuitModel!.GroundNodeId);
        Assert.Equal("Valid authored circuit.", viewModel.ElectricalValidationSummary);
        Assert.True(viewModel.IsDirty);

        EditableField name = viewModel.ElectricalCircuitFields.Single(field => field.Label == "Name");
        name.Value = "Bench supply";
        Assert.True(viewModel.ApplyField(name));
        Assert.Equal("Bench supply", viewModel.SelectedElectricalCircuitModel.Name);

        EditableField description = viewModel.ElectricalCircuitFields.Single(field => field.Label == "Description");
        description.Value = "A small divider";
        Assert.True(viewModel.ApplyField(description));
        Assert.Equal("A small divider", viewModel.SelectedElectricalCircuitModel.Description);

        viewModel.SelectedElectricalNode = viewModel.ElectricalNodes.Single(node => node.Id == "n1");
        EditableField nodeName = viewModel.ElectricalNodeFields.Single(field => field.Label == "Node name");
        nodeName.Value = "Output";
        Assert.True(viewModel.ApplyField(nodeName));
        Assert.Equal("Output", viewModel.SelectedElectricalCircuitModel.FindNode("n1")!.Name);
    }

    [Fact]
    public void ComponentMetadataAndExplicitTerminalsAreEditable()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();

        ElectricalComponent component = Assert.Single(viewModel.SelectedElectricalCircuitModel!.Components);
        Assert.Equal("r1", component.Id);
        Assert.Equal(ElectricalComponentKind.Resistor, component.Kind);
        Assert.Equal(1_000, component.Value);
        Assert.Equal(2, viewModel.ElectricalTerminals.Count);
        Assert.Equal("gnd", component.FindTerminal("a")!.NodeId);
        Assert.Equal("n1", component.FindTerminal("b")!.NodeId);
        Assert.Equal(ElectricalTerminalPolarity.Passive, component.FindTerminal("a")!.Polarity);
        Assert.Equal(ElectricalTerminalPolarity.Passive, component.FindTerminal("b")!.Polarity);
        Assert.Equal("ohm", viewModel.SelectedElectricalComponentKind!.Unit);

        EditableField value = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));
        value.Value = "2200";
        Assert.True(viewModel.ApplyField(value));
        Assert.Equal(2_200, component.Value);

        EditableField componentName = viewModel.ElectricalComponentFields.Single(field => field.Label == "Name");
        componentName.Value = "R_LOAD";
        Assert.True(viewModel.ApplyField(componentName));
        Assert.Equal("R_LOAD", component.Name);

        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds.Single(choice =>
            choice.Kind == ElectricalComponentKind.VoltageSource);

        Assert.Equal(ElectricalComponentKind.VoltageSource, component.Kind);
        Assert.Equal(2_200, component.Value);
        Assert.Equal("V", viewModel.SelectedElectricalComponentKind!.Unit);
        Assert.Equal(ElectricalTerminalPolarity.Positive, component.FindTerminal("a")!.Polarity);
        Assert.Equal(ElectricalTerminalPolarity.Negative, component.FindTerminal("b")!.Polarity);
        Assert.Equal("gnd", component.FindTerminal("a")!.NodeId);
        Assert.Equal("n1", component.FindTerminal("b")!.NodeId);
        Assert.Equal(2, viewModel.ElectricalTerminals.Count);
    }

    [Fact]
    public void TerminalBindingsDriveValidationAndUndoRedo()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.AddElectricalNode();
        Assert.Equal("n2", viewModel.SelectedElectricalNodeId);

        viewModel.SetElectricalTerminalNode("r1", "b", "n2");
        ElectricalComponent component = viewModel.SelectedElectricalCircuitModel!.FindComponent("r1")!;
        Assert.Equal("n2", component.FindTerminal("b")!.NodeId);
        Assert.Empty(viewModel.ElectricalDiagnostics);

        viewModel.SetElectricalTerminalNode("r1", "a", "n2");
        Assert.Contains(
            viewModel.ElectricalDiagnostics,
            diagnostic => diagnostic.Code == "RAD-ELEC-013");

        viewModel.Undo();
        Assert.Equal("gnd", viewModel.SelectedElectricalCircuitModel!.FindComponent("r1")!
            .FindTerminal("a")!.NodeId);
        Assert.Empty(viewModel.ElectricalDiagnostics);

        viewModel.Redo();
        Assert.Equal("n2", viewModel.SelectedElectricalCircuitModel!.FindComponent("r1")!
            .FindTerminal("a")!.NodeId);
        Assert.Contains(
            viewModel.ElectricalDiagnostics,
            diagnostic => diagnostic.Code == "RAD-ELEC-013");
    }

    [Fact]
    public void ReferencedNodesAndGroundCannotBeRemoved()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();

        viewModel.SelectedElectricalNode = viewModel.ElectricalNodes.Single(node => node.Id == "gnd");
        viewModel.RemoveElectricalNode();
        Assert.Contains("ground", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, viewModel.SelectedElectricalCircuitModel!.Nodes.Count);

        viewModel.SelectedElectricalNode = viewModel.ElectricalNodes.Single(node => node.Id == "n1");
        viewModel.RemoveElectricalNode();
        Assert.Contains("bound", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, viewModel.SelectedElectricalCircuitModel!.Nodes.Count);

        viewModel.AddElectricalNode();
        string freeNode = viewModel.SelectedElectricalNodeId!;
        viewModel.RemoveElectricalNode();
        Assert.DoesNotContain(viewModel.SelectedElectricalCircuitModel!.Nodes, node => node.Id == freeNode);
    }

    [Fact]
    public void AuthoredCircuitsRoundTripWithTheirTerminals()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.AddElectricalNode();
        viewModel.SetElectricalTerminalNode("r1", "b", "n2");

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            Assert.True(viewModel.SaveProject(path));
            Assert.False(viewModel.IsDirty);

            EditorViewModel opened = CreateEmptyEditor();
            Assert.True(opened.LoadProject(path));

            ElectricalCircuit circuit = Assert.Single(opened.Project.ElectricalCircuits);
            Assert.Equal("dc1", circuit.Id);
            ElectricalComponent component = Assert.Single(circuit.Components);
            Assert.Equal("r1", component.Id);
            Assert.Equal("n2", component.FindTerminal("b")!.NodeId);
            Assert.NotNull(opened.SelectedElectricalCircuit);
            Assert.Empty(opened.ElectricalDiagnostics);
            Assert.False(opened.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SolveShowsNodeVoltagesCurrentsAndPower()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SetElectricalTerminalNode("r1", "a", "n1");
        viewModel.SetElectricalTerminalNode("r1", "b", "gnd");
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds.Single(choice =>
            choice.Kind == ElectricalComponentKind.VoltageSource);
        EditableField voltage = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));
        voltage.Value = "5";
        Assert.True(viewModel.ApplyField(voltage));

        viewModel.AddElectricalComponent();
        Assert.Equal("r2", viewModel.SelectedElectricalComponentId);
        Assert.True(viewModel.SolveElectricalCircuit());

        Assert.True(viewModel.HasElectricalSolveResult);
        Assert.False(viewModel.HasElectricalSolveError);
        Assert.Equal("5 V", viewModel.ElectricalNodeResults.Single(node => node.Id == "n1").VoltageText);
        Assert.Equal("0 V", viewModel.ElectricalNodeResults.Single(node => node.Id == "gnd").VoltageText);

        ElectricalElementResultItem source = viewModel.ElectricalElementResults.Single(element => element.Id == "r1");
        Assert.Equal("5 V", source.VoltageText);
        Assert.Equal("-0.005 A", source.CurrentText);
        Assert.Equal("-0.025 W", source.PowerText);

        ElectricalElementResultItem load = viewModel.ElectricalElementResults.Single(element => element.Id == "r2");
        Assert.Equal("-0.005 A", load.CurrentText);
        Assert.Equal("0.025 W", load.PowerText);
        Assert.Contains("Solved 'dc1'", viewModel.ElectricalSolveSummary);
    }

    [Fact]
    public void SolveExportsDisplayedValuesAsJsonAndCsv()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        Assert.False(viewModel.TryBuildElectricalSolveJson(out _));
        Assert.False(viewModel.TryBuildElectricalSolveCsv(out _));

        viewModel.SetElectricalTerminalNode("r1", "a", "n1");
        viewModel.SetElectricalTerminalNode("r1", "b", "gnd");
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds.Single(choice =>
            choice.Kind == ElectricalComponentKind.VoltageSource);
        EditableField voltage = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));
        voltage.Value = "5";
        Assert.True(viewModel.ApplyField(voltage));
        viewModel.AddElectricalComponent();
        Assert.Equal("r2", viewModel.SelectedElectricalComponentId);
        Assert.True(viewModel.SolveElectricalCircuit());

        Assert.True(viewModel.TryBuildElectricalSolveJson(out string json));
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            JsonElement root = document.RootElement;
            Assert.Equal("radix-dc-solve", root.GetProperty("format").GetString());
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal("dc1", root.GetProperty("circuit").GetString());
            Assert.Equal("gnd", root.GetProperty("ground").GetString());

            JsonElement n1 = root.GetProperty("nodes").EnumerateArray()
                .Single(node => node.GetProperty("id").GetString() == "n1");
            Assert.True(n1.GetProperty("solved").GetBoolean());
            Assert.Equal(5, n1.GetProperty("volts").GetDouble(), 12);

            JsonElement source = root.GetProperty("elements").EnumerateArray()
                .Single(element => element.GetProperty("id").GetString() == "r1");
            Assert.Equal("voltage-source", source.GetProperty("kind").GetString());
            Assert.Equal("n1", source.GetProperty("nodeA").GetString());
            Assert.Equal("gnd", source.GetProperty("nodeB").GetString());
            Assert.Equal(5, source.GetProperty("value").GetDouble(), 12);
            Assert.Equal("V", source.GetProperty("unit").GetString());
            Assert.Equal(5, source.GetProperty("volts").GetDouble(), 12);
            Assert.Equal(-0.005, source.GetProperty("amperes").GetDouble(), 12);
            Assert.Equal(-0.025, source.GetProperty("absorbedPowerWatts").GetDouble(), 12);
        }

        Assert.True(viewModel.TryBuildElectricalSolveCsv(out string csv));
        Assert.DoesNotContain('\r', csv);
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Equal(
            "circuit,type,id,name,kind,node_a,node_b,value,unit,volts,amperes,absorbed_power_watts,solved",
            lines[0]);
        Assert.Contains(lines, line => line.Contains("\"dc1\",node,\"n1\",\"Node 1\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.EndsWith(",5,\"\",\"\",true", StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains(
                ",element,\"r1\",\"R1\",\"voltage-source\",\"n1\",\"gnd\",5,\"V\",5,-0.005,-0.025,true",
                StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains(
                ",element,\"r2\",\"R2\",\"resistor\",\"gnd\",\"n1\",1000,\"ohm\",-5,-0.005,0.025,true",
                StringComparison.Ordinal));

        voltage = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));
        voltage.Value = "2000";
        Assert.True(viewModel.ApplyField(voltage));
        Assert.False(viewModel.HasElectricalSolveResult);
        Assert.False(viewModel.TryBuildElectricalSolveJson(out _));
        Assert.False(viewModel.TryBuildElectricalSolveCsv(out _));
    }

    [Fact]
    public void SolveReportsStructuralAndSingularFailuresAndClearsOnEdit()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SetElectricalTerminalNode("r1", "b", "gnd");

        Assert.False(viewModel.SolveElectricalCircuit());
        Assert.True(viewModel.HasElectricalSolveError);
        Assert.False(viewModel.HasElectricalSolveResult);
        Assert.Contains("structural", viewModel.ElectricalSolveSummary, StringComparison.OrdinalIgnoreCase);

        viewModel.SetElectricalTerminalNode("r1", "b", "n1");
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds.Single(choice =>
            choice.Kind == ElectricalComponentKind.CurrentSource);
        Assert.False(viewModel.SolveElectricalCircuit());
        Assert.True(viewModel.HasElectricalSolveError);
        Assert.Contains("singular", viewModel.ElectricalSolveSummary, StringComparison.OrdinalIgnoreCase);

        viewModel.AddElectricalComponent();
        Assert.True(viewModel.SolveElectricalCircuit());
        Assert.True(viewModel.HasElectricalSolveResult);
        EditableField value = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));
        value.Value = "2000";
        Assert.True(viewModel.ApplyField(value));
        Assert.False(viewModel.HasElectricalSolveResult);
        Assert.Empty(viewModel.ElectricalNodeResults);
        Assert.Empty(viewModel.ElectricalElementResults);
    }

    [Fact]
    public void MultipleCircuitsKeepIndependentSelectionsAndUndoRemoval()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.AddElectricalCircuit();

        Assert.Equal("dc2", viewModel.SelectedElectricalCircuitId);
        Assert.Null(viewModel.SelectedElectricalComponent);
        Assert.Equal(2, viewModel.ElectricalCircuits.Count);

        viewModel.SelectedElectricalCircuit = viewModel.ElectricalCircuits.Single(item => item.Id == "dc1");
        Assert.Equal("dc1", viewModel.SelectedElectricalCircuitId);
        Assert.Single(viewModel.ElectricalComponents);
        viewModel.SelectedElectricalComponent = viewModel.ElectricalComponents.Single();
        Assert.Equal(2, viewModel.ElectricalTerminals.Count);

        viewModel.RemoveElectricalCircuit();
        Assert.Single(viewModel.ElectricalCircuits);
        Assert.Equal("dc2", viewModel.SelectedElectricalCircuitId);

        viewModel.Undo();
        Assert.Equal(2, viewModel.ElectricalCircuits.Count);
        Assert.NotNull(viewModel.Project.FindElectricalCircuit("dc1"));
    }

    private static EditorViewModel CreateEmptyEditor()
    {
        var project = CircuitProject.CreateEmpty("Electrical editor test");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);
        return new EditorViewModel(project);
    }
}
