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
    public void PinInterfaceCanBeRenamedRetargetedAndFlipped()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.NewPinName = "A";
        viewModel.NewPinIsInput = true;
        viewModel.NewPinDomain = viewModel.Domains.First(d => d.Id == StandardDomains.BinaryId);
        viewModel.AddPin();
        PinDeclaration pin = viewModel.Definition.Pins.Single();

        viewModel.RenamePin(pin.Id, "DATA");
        Assert.Equal("DATA", pin.Name);
        Assert.Equal("DATA", viewModel.Definition.FindPortNet(pin.Id)!.Label);

        DomainChoice ternary = viewModel.Domains.First(d => d.Id == StandardDomains.TernaryId);
        viewModel.ChangePinDomain(pin.Id, ternary.Id);
        Assert.Equal(ternary.Id, pin.DomainId);
        Assert.Equal(ternary.Id, viewModel.Definition.FindPortNet(pin.Id)!.DomainId);

        viewModel.TogglePinDirection(pin.Id);
        Assert.Equal(PinDirection.Output, pin.Direction);

        viewModel.Undo();
        Assert.Equal(PinDirection.Input, viewModel.Definition.FindPin(pin.Id)!.Direction);
        viewModel.Undo();
        Assert.Equal(StandardDomains.BinaryId, viewModel.Definition.FindPin(pin.Id)!.DomainId);
        viewModel.Undo();
        Assert.Equal("A", viewModel.Definition.FindPin(pin.Id)!.Name);
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
    public void ExtractSelectionCreatesPinnedReusablePartAndPreservesSimulation()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.NewPinName = "A";
        viewModel.NewPinIsInput = true;
        viewModel.NewPinDomain = viewModel.Domains.First(d => d.Id == StandardDomains.BinaryId);
        viewModel.AddPin();
        PinDeclaration input = viewModel.Definition.Pins.Single();
        viewModel.NewPinName = "Y";
        viewModel.NewPinIsInput = false;
        viewModel.AddPin();
        PinDeclaration output = viewModel.Definition.Pins.Single(p => p.Name == "Y");

        LibraryComponent not = ComponentCatalog.Components.First(c => c.Name == "NOT gate");
        viewModel.PlaceLibraryComponent(not);
        ComponentInstance first = viewModel.Definition.Instances.Single();
        viewModel.PlaceLibraryComponent(not);
        ComponentInstance second = viewModel.Definition.Instances.Single(i => i.Id != first.Id);
        viewModel.Connect(EditorPin.ForPort(input.Id), EditorPin.ForInstance(first.Id, "in0"));
        viewModel.Connect(
            EditorPin.ForInstance(first.Id, PrimitiveShapes.OutputPinId),
            EditorPin.ForInstance(second.Id, "in0"));
        viewModel.Connect(
            EditorPin.ForInstance(second.Id, PrimitiveShapes.OutputPinId),
            EditorPin.ForPort(output.Id));

        int definitionsBefore = viewModel.Project.Definitions.Count;
        viewModel.SelectInstances([first.Id, second.Id]);
        viewModel.NewComponentName = "Double inverter";
        viewModel.ExtractSelectionToComponent();

        Assert.Equal(definitionsBefore + 1, viewModel.Project.Definitions.Count);
        ComponentInstance replacement = Assert.Single(viewModel.Definition.Instances);
        Assert.Equal("Double inverter", replacement.Label);
        CircuitDefinition part = viewModel.Project.FindDefinition(replacement.DefinitionId!)!;
        Assert.Equal(2, part.Instances.Count);
        Assert.Equal(2, part.Pins.Count);
        Assert.Equal(PinDirection.Input, part.Pins.Single(p => p.Name == "A").Direction);
        Assert.Equal(PinDirection.Output, part.Pins.Single(p => p.Name == "Y").Direction);
        Assert.Single(part.Nets, n => !part.Ports.Any(p => p.NetId == n.Id));

        // Parent connectivity survived: the port nets now terminate on the new part.
        Net parentInput = viewModel.Definition.FindPortNet(input.Id)!;
        Assert.Contains(parentInput.Pins, p => p.InstanceId == replacement.Id);
        Net parentOutput = viewModel.Definition.FindPortNet(output.Id)!;
        Assert.Contains(parentOutput.Pins, p => p.InstanceId == replacement.Id);

        viewModel.BeginSimulation();
        viewModel.SetPortValue(input.Id, 1);
        viewModel.AdvanceTicksText = "20";
        viewModel.AdvanceSimulation();
        Assert.Equal("1", PinValue(viewModel, output.Id));

        viewModel.Undo();
        Assert.Equal(2, viewModel.Definition.Instances.Count);
        Assert.Equal(definitionsBefore, viewModel.Project.Definitions.Count);
    }

    [Fact]
    public void DirtyStateTracksTheLastSavedSnapshotAcrossUndoAndRedo()
    {
        var viewModel = CreateEmptyEditor();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            Assert.True(viewModel.SaveProject(path));
            Assert.False(viewModel.IsDirty);

            viewModel.PlaceLibraryComponent(ComponentCatalog.Components.First(c => c.Name == "NOT gate"));
            Assert.True(viewModel.IsDirty);

            // Undoing back to the saved content clears the marker, and redoing the
            // edit marks the project dirty again.
            viewModel.Undo();
            Assert.False(viewModel.IsDirty);
            viewModel.Redo();
            Assert.True(viewModel.IsDirty);

            // After saving the edited state, undo is dirty and redo is clean.
            Assert.True(viewModel.SaveProject(path));
            Assert.False(viewModel.IsDirty);
            viewModel.Undo();
            Assert.True(viewModel.IsDirty);
            viewModel.Redo();
            Assert.False(viewModel.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PlacingAnAncestorIntoADescendantIsRejected()
    {
        var viewModel = CreateEmptyEditor();
        string rootId = viewModel.Definition.Id;
        viewModel.AddDefinition();
        string partId = viewModel.Definition.Id;

        viewModel.OpenDefinition(rootId);
        viewModel.PlaceDefinition(partId);
        Assert.Single(viewModel.Definition.Instances);

        viewModel.OpenDefinition(partId);
        int before = viewModel.Definition.Instances.Count;
        viewModel.PlaceDefinition(rootId);

        Assert.Equal(before, viewModel.Definition.Instances.Count);
        Assert.Contains("recursive", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractSelectionRejectsNetsDrivenFromBothSides()
    {
        var viewModel = CreateEmptyEditor();
        LibraryComponent not = ComponentCatalog.Components.First(c => c.Name == "NOT gate");
        viewModel.PlaceLibraryComponent(not);
        ComponentInstance first = viewModel.Definition.Instances.Single();
        viewModel.PlaceLibraryComponent(not);
        ComponentInstance second = viewModel.Definition.Instances.Single(i => i.Id != first.Id);
        viewModel.Connect(
            EditorPin.ForInstance(first.Id, PrimitiveShapes.OutputPinId),
            EditorPin.ForInstance(second.Id, PrimitiveShapes.OutputPinId));

        int definitionsBefore = viewModel.Project.Definitions.Count;
        viewModel.SelectInstances([first.Id]);
        viewModel.ExtractSelectionToComponent();

        Assert.Equal(definitionsBefore, viewModel.Project.Definitions.Count);
        Assert.Equal(2, viewModel.Definition.Instances.Count);
        Assert.Contains("drivers on both sides", viewModel.StatusText);
    }

    [Fact]
    public void PromoteNetAddsInterfacePinAndBinding()
    {
        var viewModel = CreateEmptyEditor();
        LibraryComponent not = ComponentCatalog.Components.First(c => c.Name == "NOT gate");
        viewModel.PlaceLibraryComponent(not);
        ComponentInstance first = viewModel.Definition.Instances.Single();
        viewModel.PlaceLibraryComponent(not);
        ComponentInstance second = viewModel.Definition.Instances.Single(i => i.Id != first.Id);
        viewModel.Connect(
            EditorPin.ForInstance(first.Id, PrimitiveShapes.OutputPinId),
            EditorPin.ForInstance(second.Id, "in0"));

        Net net = viewModel.Definition.Nets.Single(n => n.Pins.Any(p => p.InstanceId == first.Id));
        viewModel.SelectNet(net.Id);
        viewModel.PromoteNetName = "MID";
        viewModel.PromoteSelectedNet(PinDirection.Output);

        PinDeclaration pin = viewModel.Definition.Pins.Single(p => p.Name == "MID");
        Assert.Equal(PinDirection.Output, pin.Direction);
        Assert.Same(net, viewModel.Definition.FindPortNet(pin.Id));
        Assert.Equal("MID", net.Label);
        Assert.False(viewModel.CanPromoteSelectedNet);

        viewModel.Undo();
        Assert.Empty(viewModel.Definition.Pins);
        Assert.Null(viewModel.Definition.FindNet(net.Id)!.Label);
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
