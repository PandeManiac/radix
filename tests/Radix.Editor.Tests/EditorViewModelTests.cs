using Avalonia;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Editor.Services;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class EditorViewModelTests
{
    [Fact]
    public void CounterExampleSimulatesThroughEditorHost()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());

        viewModel.BeginSimulation();
        viewModel.AdvanceTicksText = "100";
        viewModel.AdvanceSimulation();

        Assert.False(viewModel.Simulator.IsHalted, viewModel.Simulator.HaltReason);
        Assert.Equal("1", PinValue(viewModel, "q0"));
        Assert.Equal("0", PinValue(viewModel, "q1"));
        Assert.Equal("1", PinValue(viewModel, "q2"));
        Assert.Equal("0", PinValue(viewModel, "q3"));
    }

    [Fact]
    public void PlacingAndDeletingPartIsUndoable()
    {
        var viewModel = CreateEmptyEditor();
        int before = viewModel.Definition.Instances.Count;

        viewModel.PlaceLibraryComponent(ComponentCatalog.Components.First(c => c.Name == "AND gate"));

        Assert.Equal(before + 1, viewModel.Definition.Instances.Count);
        Assert.True(viewModel.HasSelectedInstance);
        Assert.True(viewModel.IsDirty);

        viewModel.DeleteSelection();
        Assert.Equal(before, viewModel.Definition.Instances.Count);

        viewModel.Undo();
        Assert.Equal(before + 1, viewModel.Definition.Instances.Count);

        viewModel.Redo();
        Assert.Equal(before, viewModel.Definition.Instances.Count);
    }

    [Fact]
    public void DragMoveIsOneUndoStep()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.PlaceLibraryComponent(ComponentCatalog.Components.First(c => c.Name == "Delay line"));
        ComponentInstance instance = viewModel.Definition.Instances[0];
        double originalX = instance.X;

        viewModel.BeginDrag();
        viewModel.MoveInstance(instance.Id, originalX + 40, instance.Y + 20);
        viewModel.MoveInstance(instance.Id, originalX + 80, instance.Y + 40);
        viewModel.EndDrag();

        Assert.Equal(originalX + 80, instance.X, 1);

        viewModel.Undo();
        Assert.Equal(originalX, viewModel.Definition.Instances[0].X, 1);
    }

    [Fact]
    public void InterfacePinConnectsToPrimitivePin()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.NewPinName = "A";
        viewModel.NewPinIsInput = true;
        viewModel.NewPinDomain = viewModel.Domains.First(d => d.Id == StandardDomains.BinaryId);
        viewModel.AddPin();
        PinDeclaration pin = viewModel.Definition.Pins.Single();

        viewModel.PlaceLibraryComponent(ComponentCatalog.Components.First(c => c.Name == "AND gate"));
        ComponentInstance gate = viewModel.Definition.Instances.Single();
        viewModel.Connect(
            EditorPin.ForPort(pin.Id),
            EditorPin.ForInstance(gate.Id, "in0"));

        Net net = viewModel.Definition.FindPortNet(pin.Id)!;
        Assert.Contains(net.Pins, p => p.InstanceId == gate.Id && p.PinId == "in0");
        Assert.Equal(StandardDomains.BinaryId, net.DomainId);

        // Two interface pins with different declared domains are rejected rather
        // than silently coerced.
        viewModel.NewPinName = "T";
        viewModel.NewPinDomain = viewModel.Domains.First(d => d.Id == StandardDomains.TernaryId);
        viewModel.AddPin();
        PinDeclaration ternary = viewModel.Definition.Pins.Single(p => p.Name == "T");
        Net ternaryNet = viewModel.Definition.FindPortNet(ternary.Id)!;
        viewModel.Connect(EditorPin.ForPort(pin.Id), EditorPin.ForPort(ternary.Id));
        Assert.NotSame(ternaryNet, viewModel.Definition.FindPortNet(pin.Id));
        Assert.Empty(ternaryNet.Pins);
    }

    [Fact]
    public void SubcircuitIsSimulatedAsItsOwnRoot()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        viewModel.OpenDefinition(StandardLibrary.DFlipFlopId);
        viewModel.BeginSimulation();

        viewModel.SetPortValue("d", 1);
        viewModel.SetPortValue("clk", 0);
        viewModel.AdvanceTicksText = "20";
        viewModel.AdvanceSimulation();
        Assert.Equal("0", PinValue(viewModel, "q"));

        viewModel.SetPortValue("clk", 1);
        viewModel.AdvanceTicksText = "20";
        viewModel.AdvanceSimulation();
        Assert.Equal("1", PinValue(viewModel, "q"));
        Assert.Equal("0", PinValue(viewModel, "qn"));
    }

    [Fact]
    public void CustomDomainCanBeCreatedAndUsedForPins()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.NewDomainName = "Five state";
        viewModel.NewDomainValues = "lo mid hi X Z";
        viewModel.AddDomain();

        Core.Signals.SignalDomain? domain = viewModel.Project.FindDomain("five-state");
        Assert.NotNull(domain);
        Assert.Equal(5, domain!.ValueCount);

        DomainChoice choice = viewModel.Domains.Single(d => d.Id == domain.Id);
        viewModel.NewPinName = "BUS";
        viewModel.NewPinDomain = choice;
        viewModel.AddPin();

        PinDeclaration pin = viewModel.Definition.Pins.Single(p => p.Name == "BUS");
        Assert.Equal(domain.Id, pin.DomainId);
        Assert.Equal(domain.Id, viewModel.Definition.FindPortNet(pin.Id)!.DomainId);
    }

    [Fact]
    public void NewProjectContainsStandardDomainAndRoot()
    {
        var viewModel = CreateEmptyEditor();
        Assert.NotNull(viewModel.Project.FindDomain(StandardDomains.BinaryId));
        Assert.NotEmpty(viewModel.Project.Definitions);
        Assert.Equal(viewModel.Project.RootDefinitionId, viewModel.Definition.Id);
        Assert.DoesNotContain(viewModel.Definition.Pins, p => p.Direction == PinDirection.Input);
    }

    [Fact]
    public void LoadedExampleCanRoundTripSave()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateFullAdderDemo());
        string json = Core.Serialization.ProjectSerializer.SaveToString(viewModel.Project);
        CircuitProject reloaded = Core.Serialization.ProjectSerializer.LoadFromString(json);

        Assert.Equal(viewModel.Project.Name, reloaded.Name);
        Assert.Equal(viewModel.Project.Definitions.Count, reloaded.Definitions.Count);
        var editor = new EditorViewModel(reloaded);
        editor.BeginSimulation();
        Assert.False(editor.Simulator.IsHalted, editor.Simulator.HaltReason);
    }

    [Fact]
    public void CopyPasteCarriesInternalNetsAndUndoesAsOneStep()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.PlaceLibraryComponent(ComponentCatalog.Components.First(c => c.Name == "AND gate"));
        ComponentInstance gate = viewModel.Definition.Instances.Single();
        viewModel.PlaceLibraryComponent(ComponentCatalog.Components.First(c => c.Name == "OR gate"));
        ComponentInstance sink = viewModel.Definition.Instances.Single(i => i.Id != gate.Id);
        viewModel.Connect(
            EditorPin.ForInstance(gate.Id, PrimitiveShapes.OutputPinId),
            EditorPin.ForInstance(sink.Id, "in0"));

        int instancesBefore = viewModel.Definition.Instances.Count;
        int netsBefore = viewModel.Definition.Nets.Count;
        viewModel.SelectInstances([gate.Id, sink.Id]);
        Assert.True(viewModel.CopySelection());

        viewModel.Paste();

        Assert.Equal(instancesBefore + 2, viewModel.Definition.Instances.Count);
        Assert.Equal(netsBefore + 1, viewModel.Definition.Nets.Count);
        Assert.Equal(2, viewModel.SelectionCount);
        Net pastedNet = viewModel.Definition.Nets.Single(n =>
            n.Pins.Count == 2 &&
            n.Pins.All(p => viewModel.Definition.FindInstance(p.InstanceId)?.Primitive is not null) &&
            n.Pins.Any(p => p.InstanceId != gate.Id && p.InstanceId != sink.Id));
        Assert.DoesNotContain(pastedNet.Pins, p => p.InstanceId == gate.Id || p.InstanceId == sink.Id);

        viewModel.Undo();
        Assert.Equal(instancesBefore, viewModel.Definition.Instances.Count);
        Assert.Equal(netsBefore, viewModel.Definition.Nets.Count);
    }

    [Fact]
    public void LibrarySearchFiltersByNameCategoryAndDescription()
    {
        var viewModel = CreateEmptyEditor();

        viewModel.LibrarySearch = "ternary";
        Assert.Equal(["Ternary maximum"], viewModel.Palette.Select(p => p.Label));
        Assert.True(viewModel.HasPaletteResults);

        viewModel.LibrarySearch = "signal";
        Assert.All(viewModel.Palette, item => Assert.Equal("Signal", item.Category));

        viewModel.LibrarySearch = "zzzz";
        Assert.Empty(viewModel.Palette);
        Assert.False(viewModel.HasPaletteResults);
    }

    [Fact]
    public void LayoutIncludesWireJunctionsForMultiPinNets()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        Assert.NotNull(viewModel.Layout);
        Assert.NotEmpty(viewModel.Layout!.Components);
        Assert.NotEmpty(viewModel.Layout.Wires);
        Assert.Contains(viewModel.Layout.Wires, w => w.Endpoints.Count >= 2);
    }

    private static EditorViewModel CreateEmptyEditor()
    {
        var project = CircuitProject.CreateEmpty("Editor test");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);
        return new EditorViewModel(project);
    }

    private static string PinValue(EditorViewModel viewModel, string pinId) =>
        viewModel.IoPins.Single(p => p.PinId == pinId).ValueText;
}
