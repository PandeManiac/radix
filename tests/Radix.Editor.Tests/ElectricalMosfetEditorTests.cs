using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The Electrical panel is where a circuit is actually built. These tests drive the
/// MOSFET through the same view-model commands the buttons and fields call, so a panel
/// that cannot reach the device the solver supports shows up as a failure here.
/// </summary>
public sealed class ElectricalMosfetEditorTests
{
    private static EditorViewModel CreateEmptyEditor()
    {
        var project = CircuitProject.CreateEmpty("Electrical MOSFET test");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);
        return new EditorViewModel(project);
    }

    private static EditorViewModel WithMosfet()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.Mosfet);
        return viewModel;
    }

    [Fact]
    public void MosfetKindExposesThreeTerminalsAndItsOwnDefault()
    {
        EditorViewModel viewModel = WithMosfet();
        ElectricalComponent component = Assert.Single(
            viewModel.SelectedElectricalCircuitModel!.Components);

        Assert.Equal(ElectricalComponentKind.Mosfet, component.Kind);
        Assert.Equal(
            ["drain", "gate", "source"],
            component.Terminals.Select(terminal => terminal.Id).ToArray());
        Assert.Equal(3, viewModel.ElectricalTerminals.Count);
        Assert.Equal("MOSFET", component.Kind == ElectricalComponentKind.Mosfet
            ? viewModel.ElectricalComponentKinds.Single(choice => choice.Kind == component.Kind).Label
            : string.Empty);

        // The new device keeps the panel's node bindings but not the resistor's 1000 ohms:
        // a transconductance of 1000 A/V^2 would be a nonsensical device.
        Assert.Equal(1e-3, component.Value, 12);
        Assert.Equal("A/V^2", component.Unit);
        Assert.Equal("gnd", component.FindTerminal("gate")!.NodeId);
        Assert.Equal("gnd", component.FindTerminal("source")!.NodeId);
        Assert.Equal("n1", component.FindTerminal("drain")!.NodeId);
    }

    [Fact]
    public void ChannelAndThresholdAreEditableThroughTheInspector()
    {
        EditorViewModel viewModel = WithMosfet();
        ElectricalComponent component = Assert.Single(
            viewModel.SelectedElectricalCircuitModel!.Components);

        EditableField channel = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Channel (n/p)");
        Assert.Equal("n", channel.Value);
        channel.Value = "p";
        Assert.True(viewModel.ApplyField(channel));
        Assert.Equal(DcMosfetChannel.PChannel, component.Channel);

        // A bad channel is refused and leaves the device unchanged.
        EditableField rejected = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Channel (n/p)");
        rejected.Value = "q";
        Assert.False(viewModel.ApplyField(rejected));
        Assert.Equal(DcMosfetChannel.PChannel, component.Channel);

        EditableField threshold = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Threshold voltage (V)");
        threshold.Value = "1.5";
        Assert.True(viewModel.ApplyField(threshold));
        Assert.Equal(1.5, component.ThresholdVoltage);

        EditableField gain = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Value (A/V^2)");
        gain.Value = "2m";
        Assert.True(viewModel.ApplyField(gain));
        Assert.Equal(2e-3, component.Value, 12);

        EditableField lambda = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Channel-length modulation (1/V)");
        lambda.Value = "0.02";
        Assert.True(viewModel.ApplyField(lambda));
        Assert.Equal(0.02, component.ChannelLengthModulation, 12);

        // A negative lambda would model a device whose saturation current falls as its drain
        // voltage rises above the source, which no transistor does, so the field refuses it.
        EditableField rejectedLambda = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Channel-length modulation (1/V)");
        rejectedLambda.Value = "-0.5";
        Assert.False(viewModel.ApplyField(rejectedLambda));
        Assert.Equal(0.02, component.ChannelLengthModulation, 12);
    }

    [Fact]
    public void APanelBuiltMosfetCircuitValidatesAndSolves()
    {
        EditorViewModel viewModel = WithMosfet();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        ElectricalComponent mosfet = Assert.Single(circuit.Components);

        // Build a common-source stage around the new device: a 5 V rail on n1, a 1 kOhm
        // pull-up from the rail to the output node n2, and the channel switching n2.
        viewModel.AddElectricalNode();
        Assert.Contains(circuit.Nodes, node => node.Id == "n2");

        // The rail supply, set through the inspector the way a user would.
        viewModel.AddElectricalComponent();
        ElectricalComponent supply = circuit.Components.Single(component => component.Id != mosfet.Id);
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.VoltageSource);
        Assert.Equal(ElectricalComponentKind.VoltageSource, supply.Kind);
        EditableField supplyVolts = viewModel.ElectricalComponentFields
            .Single(field => field.Label.StartsWith("Value", StringComparison.Ordinal));
        supplyVolts.Value = "5";
        Assert.True(viewModel.ApplyField(supplyVolts));
        Assert.Equal(5, supply.Value, 12);

        viewModel.AddElectricalComponent();
        ElectricalComponent resistor = circuit.Components
            .Single(component => component.Id != mosfet.Id && component.Id != supply.Id);
        Assert.Equal(ElectricalComponentKind.Resistor, resistor.Kind);
        Assert.Equal(1000, resistor.Value, 12);

        viewModel.SetElectricalTerminalNode(supply.Id, "a", "n1");
        viewModel.SetElectricalTerminalNode(supply.Id, "b", "gnd");
        viewModel.SetElectricalTerminalNode(resistor.Id, "a", "n1");
        viewModel.SetElectricalTerminalNode(resistor.Id, "b", "n2");
        viewModel.SetElectricalTerminalNode(mosfet.Id, "drain", "n2");
        viewModel.SetElectricalTerminalNode(mosfet.Id, "gate", "n1");
        viewModel.SetElectricalTerminalNode(mosfet.Id, "source", "gnd");

        Assert.Equal("Valid authored circuit.", viewModel.ElectricalValidationSummary);
        Assert.True(viewModel.SolveElectricalCircuit());

        // Vgs = 5 V, Vt = 1 V, k = 1 mA/V^2, pull-up 1 kOhm. KCL at the output node,
        // (5 - V) / 1000 = k ((Vgs - Vt) Vds - Vds^2 / 2), reduces to V^2 - 10V + 10 = 0,
        // so the triode solution is V = 5 - sqrt(15) = 1.1270 V.
        ElectricalNodeResultItem output = viewModel.ElectricalNodeResults
            .Single(node => node.Id == "n2");
        double? volts = output.Volts;
        Assert.NotNull(volts);
        Assert.Equal(5 - Math.Sqrt(15), volts.Value, 3);
        Assert.Equal(5, viewModel.ElectricalNodeResults.Single(node => node.Id == "n1").Volts!.Value, 6);
        Assert.Contains(
            viewModel.ElectricalElementResults,
            element => element.KindId == "mosfet" && element.Id == mosfet.Id);
    }

    [Fact]
    public void SwitchingKindPreservesNodeBindingsBothWays()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        ElectricalComponent component = Assert.Single(circuit.Components);
        Assert.Equal("gnd", component.FindTerminal("a")!.NodeId);
        Assert.Equal("n1", component.FindTerminal("b")!.NodeId);

        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.Mosfet);
        Assert.Equal("n1", component.FindTerminal("drain")!.NodeId);
        Assert.Equal("gnd", component.FindTerminal("gate")!.NodeId);
        Assert.Equal("gnd", component.FindTerminal("source")!.NodeId);

        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.Resistor);
        Assert.Equal(2, component.Terminals.Count);
        Assert.Equal("n1", component.FindTerminal("a")!.NodeId);
        Assert.Equal("gnd", component.FindTerminal("b")!.NodeId);
    }

    [Fact]
    public void AnAuthoredMosfetSurvivesSaveAndReopen()
    {
        EditorViewModel viewModel = WithMosfet();
        ElectricalComponent component = Assert.Single(
            viewModel.SelectedElectricalCircuitModel!.Components);

        EditableField channel = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Channel (n/p)");
        channel.Value = "p";
        Assert.True(viewModel.ApplyField(channel));
        EditableField threshold = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Threshold voltage (V)");
        threshold.Value = "1.4";
        Assert.True(viewModel.ApplyField(threshold));
        EditableField lambda = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Channel-length modulation (1/V)");
        lambda.Value = "0.02";
        Assert.True(viewModel.ApplyField(lambda));

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            Assert.True(viewModel.SaveProject(path));
            CircuitProject reloaded = ProjectSerializer.Load(path);
            ElectricalComponent mosfet = Assert.Single(reloaded.ElectricalCircuits[0].Components);
            Assert.Equal(ElectricalComponentKind.Mosfet, mosfet.Kind);
            Assert.Equal(DcMosfetChannel.PChannel, mosfet.Channel);
            Assert.Equal(1.4, mosfet.ThresholdVoltage);
            Assert.Equal(0.02, mosfet.ChannelLengthModulation, 12);
            Assert.Equal(1e-3, mosfet.Value, 12);
            Assert.Equal(3, mosfet.Terminals.Count);
            Assert.False(ElectricalCircuitValidator.Validate(reloaded.ElectricalCircuits[0]).HasErrors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemovingTheMosfetKindChoiceWouldBreakThePanel()
    {
        // Guards the panel's kind list directly: the solver having a device is not enough
        // if the composer never offers it.
        EditorViewModel viewModel = CreateEmptyEditor();
        Assert.Contains(
            viewModel.ElectricalComponentKinds,
            choice => choice.Kind == ElectricalComponentKind.Mosfet);
        Assert.Equal(
            "A/V^2",
            viewModel.ElectricalComponentKinds
                .Single(choice => choice.Kind == ElectricalComponentKind.Mosfet)
                .Unit);
    }
}
