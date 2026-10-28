using Avalonia;
using Radix.Core;
using Radix.Core.Interpretation;
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
    public void PriorityDriverDomainIsValidatedAndAuthoredFromTheInspector()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.NewDomainName = "Wired";
        viewModel.NewDomainValues = "0 1 X Z";
        viewModel.SelectedDriverPolicy = viewModel.DriverPolicies.Single(policy => policy.Id == "priority");
        Assert.True(viewModel.IsPriorityDriverPolicy);

        viewModel.NewDomainPriorities = "1 1 3";
        viewModel.NewDomainConflictValue = "2";
        viewModel.AddDomain();
        Assert.Contains("exactly 4", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(viewModel.Project.FindDomain("wired"));

        viewModel.NewDomainPriorities = "1 1 3 nope";
        viewModel.AddDomain();
        Assert.Contains("non-negative whole numbers", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(viewModel.Project.FindDomain("wired"));

        viewModel.NewDomainPriorities = "1 1 3 0";
        viewModel.NewDomainConflictValue = "4";
        viewModel.AddDomain();
        Assert.Contains("between 0 and 3", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(viewModel.Project.FindDomain("wired"));

        viewModel.NewDomainConflictValue = "2";
        viewModel.AddDomain();
        SignalDomain? domain = viewModel.Project.FindDomain("wired");
        Assert.NotNull(domain);
        var policy = Assert.IsType<PriorityDriverResolution>(domain!.Drivers);
        Assert.Equal([1, 1, 3, 0], policy.Priorities);
        Assert.Equal(2, policy.ConflictValue);
        Assert.True(domain.IsMultipleDriverPolicy);

        DomainChoice choice = viewModel.Domains.Single(candidate => candidate.Id == domain.Id);
        Assert.Contains("0 1 X Z", choice.Summary, StringComparison.Ordinal);
        Assert.Contains("priority [0:1, 1:1, 2:3, 3:0]", choice.Summary, StringComparison.Ordinal);
        Assert.Contains("ties resolve to 2", choice.Summary, StringComparison.Ordinal);

        viewModel.NewDomainName = "Plain";
        viewModel.NewDomainValues = "low high";
        viewModel.SelectedDriverPolicy = viewModel.DriverPolicies.Single(candidate => candidate.Id == "single");
        viewModel.AddDomain();
        Assert.IsType<SingleDriverResolution>(viewModel.Project.FindDomain("plain")!.Drivers);

        viewModel.Undo();
        Assert.Null(viewModel.Project.FindDomain("plain"));
        viewModel.Undo();
        Assert.Null(viewModel.Project.FindDomain("wired"));
    }

    [Fact]
    public void DomainDriverPolicyCanBeEditedInPlace()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.NewDomainName = "Wired";
        viewModel.NewDomainValues = "0 1 X Z";
        viewModel.AddDomain();
        SignalDomain original = viewModel.Project.FindDomain("wired")!;
        Assert.IsType<SingleDriverResolution>(original.Drivers);

        viewModel.BeginEditDomainPolicy("wired");
        Assert.True(viewModel.IsEditingDomainPolicy);
        Assert.Equal("Update domain policy", viewModel.DomainSaveButtonText);
        Assert.False(viewModel.IsDomainValueFieldsEnabled);
        Assert.Equal("Wired", viewModel.NewDomainName);
        Assert.Equal("0 1 X Z", viewModel.NewDomainValues);
        Assert.Equal("1 1 1 1", viewModel.NewDomainPriorities);

        viewModel.SelectedDriverPolicy = viewModel.DriverPolicies.Single(policy => policy.Id == "priority");
        viewModel.NewDomainPriorities = "1 2 3 0";
        viewModel.NewDomainConflictValue = "2";
        viewModel.AddDomain();

        Assert.False(viewModel.IsEditingDomainPolicy);
        Assert.Equal("Add domain", viewModel.DomainSaveButtonText);
        Assert.True(viewModel.IsDomainValueFieldsEnabled);
        Assert.Empty(viewModel.NewDomainName);

        SignalDomain updated = viewModel.Project.FindDomain("wired")!;
        Assert.Equal(["0", "1", "X", "Z"], updated.ValueNames);
        var policy = Assert.IsType<PriorityDriverResolution>(updated.Drivers);
        Assert.Equal([1, 2, 3, 0], policy.Priorities);
        Assert.Equal(2, policy.ConflictValue);
        Assert.Contains("Updated driver policy", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        viewModel.Undo();
        Assert.IsType<SingleDriverResolution>(viewModel.Project.FindDomain("wired")!.Drivers);
        viewModel.Redo();
        Assert.IsType<PriorityDriverResolution>(viewModel.Project.FindDomain("wired")!.Drivers);
    }

    [Fact]
    public void DomainDriverPolicyEditRejectsInvalidInputAndCancels()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.NewDomainName = "Wired";
        viewModel.NewDomainValues = "0 1 X Z";
        viewModel.AddDomain();

        viewModel.BeginEditDomainPolicy("wired");
        viewModel.SelectedDriverPolicy = viewModel.DriverPolicies.Single(policy => policy.Id == "priority");
        viewModel.NewDomainPriorities = "1 1 3";
        viewModel.AddDomain();
        Assert.Contains("exactly 4", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.IsEditingDomainPolicy);
        Assert.IsType<SingleDriverResolution>(viewModel.Project.FindDomain("wired")!.Drivers);

        viewModel.NewDomainPriorities = "1 1 3 0";
        viewModel.NewDomainConflictValue = "9";
        viewModel.AddDomain();
        Assert.Contains("between 0 and 3", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.IsEditingDomainPolicy);

        viewModel.CancelDomainEdit();
        Assert.False(viewModel.IsEditingDomainPolicy);
        Assert.Equal("Domain policy edit cancelled", viewModel.StatusText);
        Assert.Empty(viewModel.NewDomainName);
        Assert.IsType<SingleDriverResolution>(viewModel.Project.FindDomain("wired")!.Drivers);

        viewModel.BeginEditDomainPolicy("missing");
        Assert.False(viewModel.IsEditingDomainPolicy);
        Assert.Contains("no longer exists", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DomainDriverPolicyEditSurvivesProjectReplacementAndUndoReset()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.NewDomainName = "Wired";
        viewModel.NewDomainValues = "0 1";
        viewModel.AddDomain();

        viewModel.BeginEditDomainPolicy("wired");
        viewModel.NewProject();
        Assert.False(viewModel.IsEditingDomainPolicy);
        Assert.True(viewModel.IsDomainValueFieldsEnabled);

        viewModel.NewDomainName = "Loose";
        viewModel.NewDomainValues = "0 1";
        viewModel.AddDomain();
        viewModel.BeginEditDomainPolicy("loose");
        Assert.True(viewModel.IsEditingDomainPolicy);
        viewModel.Undo();
        Assert.False(viewModel.IsEditingDomainPolicy);
        Assert.True(viewModel.IsDomainValueFieldsEnabled);
    }

    [Fact]
    public void DefinitionCanBeDuplicatedAsAnIndependentVariant()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.AddDefinition();
        string originalId = viewModel.Definition.Id;
        AddInputPin(viewModel, "A");
        viewModel.PlaceLibraryComponent(ComponentCatalog.Components.First(c => c.Name == "AND gate"));
        int instances = viewModel.Definition.Instances.Count;
        int nets = viewModel.Definition.Nets.Count;
        string netId = viewModel.Definition.Nets[0].Id;

        viewModel.DuplicateDefinition();

        CircuitDefinition copy = viewModel.Definition;
        Assert.NotEqual(originalId, copy.Id);
        Assert.Equal($"{originalId}-copy", copy.Id);
        Assert.Equal("New component copy", copy.Name);
        Assert.Equal(instances, copy.Instances.Count);
        Assert.Equal(nets, copy.Nets.Count);
        Assert.Single(copy.Pins);
        Assert.Equal(copy.Id, viewModel.Project.View.ActiveDefinitionId);
        Assert.False(viewModel.HasSelection);
        Assert.Contains(viewModel.Definitions, item => item.Id == copy.Id && item.Name == copy.Name);
        Assert.True(viewModel.IsDirty);

        viewModel.SetNetWireColor(netId, "#123456");
        CircuitDefinition original = viewModel.Project.FindDefinition(originalId)!;
        Assert.Null(original.FindNet(netId)!.WireColor);

        viewModel.DuplicateDefinition();
        Assert.Equal($"{originalId}-copy2", viewModel.Definition.Id);
        Assert.Equal("New component copy 2", viewModel.Definition.Name);

        viewModel.Undo();
        Assert.Equal($"{originalId}-copy", viewModel.Definition.Id);
        Assert.Null(viewModel.Project.FindDefinition($"{originalId}-copy2"));
        viewModel.Undo();
        Assert.Equal($"{originalId}-copy", viewModel.Definition.Id);
        Assert.Null(viewModel.Project.FindDefinition(copy.Id)!.FindNet(netId)!.WireColor);
        viewModel.Undo();
        Assert.Equal(originalId, viewModel.Definition.Id);
        Assert.Null(viewModel.Project.FindDefinition(copy.Id));
    }

    [Fact]
    public void DefinitionListItemCanDuplicateANonActiveDefinition()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.AddDefinition();
        string originalId = viewModel.Definition.Id;
        viewModel.OpenDefinition("main");

        DefinitionListItem item = viewModel.Definitions.Single(entry => entry.Id == originalId);
        item.DuplicateCommand.Execute(null);

        CircuitDefinition clone = viewModel.Definition;
        Assert.Equal($"{originalId}-copy", clone.Id);
        Assert.NotEqual(originalId, clone.Id);
        Assert.Equal(clone.Id, viewModel.Project.View.ActiveDefinitionId);
    }

    [Fact]
    public void DuplicatingAStandardPartProducesAValidProject()
    {
        var viewModel = CreateEmptyEditor();
        viewModel.OpenDefinition(StandardLibrary.DFlipFlopId);
        string sourceName = viewModel.Definition.Name;

        viewModel.DuplicateDefinition();

        CircuitDefinition copy = viewModel.Definition;
        Assert.StartsWith($"{StandardLibrary.DFlipFlopId}-copy", copy.Id, StringComparison.Ordinal);
        Assert.Equal($"{sourceName} copy", copy.Name);
        Assert.True(copy.Instances.Count > 0);
        DiagnosticBag report = CircuitValidator.Validate(viewModel.Project);
        Assert.False(report.HasErrors, string.Join('\n', report));
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
    public void InterfacePinsCanBeReorderedWithoutChangingBindingsOrLanes()
    {
        var viewModel = CreateEmptyEditor();
        string a = AddInputPin(viewModel, "A");
        string b = AddInputPin(viewModel, "B");
        string c = AddInputPin(viewModel, "C");
        var portNets = viewModel.Definition.Pins.ToDictionary(
            pin => pin.Id,
            pin => viewModel.Definition.FindPortNet(pin.Id)!.Id);

        foreach (LanePinChoice choice in viewModel.LanePinChoices)
        {
            choice.IsSelected = true;
        }

        viewModel.NewLaneName = "Inputs";
        viewModel.AddNumericLane();
        NumericLane lane = Assert.Single(viewModel.Definition.NumericLanes);
        Assert.Equal([a, b, c], lane.PinIds);

        Assert.False(viewModel.Pins[0].MoveUpCommand.CanExecute(null));
        Assert.False(viewModel.Pins[^1].MoveDownCommand.CanExecute(null));

        viewModel.Pins.Single(pin => pin.Id == c).MoveUpCommand.Execute(null);
        Assert.Equal([a, c, b], viewModel.Definition.Pins.Select(pin => pin.Id));
        viewModel.Pins.Single(pin => pin.Id == c).MoveUpCommand.Execute(null);
        Assert.Equal([c, a, b], viewModel.Definition.Pins.Select(pin => pin.Id));
        Assert.Equal([a, b, c], lane.PinIds);
        Assert.Equal(
            portNets,
            viewModel.Definition.Pins.ToDictionary(pin => pin.Id, pin => viewModel.Definition.FindPortNet(pin.Id)!.Id));

        foreach (LanePinChoice choice in viewModel.LanePinChoices)
        {
            choice.IsSelected = true;
        }

        viewModel.NewLaneName = "Reordered";
        viewModel.AddNumericLane();
        NumericLane reordered = viewModel.Definition.NumericLanes.Single(candidate => candidate.Name == "Reordered");
        Assert.Equal([c, a, b], reordered.PinIds);

        CircuitProject loaded = Core.Serialization.ProjectSerializer.LoadFromString(
            Core.Serialization.ProjectSerializer.SaveToString(viewModel.Project),
            validate: false);
        Assert.Equal(
            [c, a, b],
            loaded.RequireDefinition(viewModel.Definition.Id).Pins.Select(pin => pin.Id));

        viewModel.Undo();
        viewModel.Undo();
        viewModel.Undo();
        Assert.Equal([a, b, c], viewModel.Definition.Pins.Select(pin => pin.Id));
        Assert.Single(viewModel.Definition.NumericLanes);
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
            viewModel.SyncViewport(1.4, 20, -10);
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
    public void NumericLaneDecodesInterfacePinsAndFollowsSimulation()
    {
        var viewModel = CreateEmptyEditor();
        string b0 = AddInputPin(viewModel, "B0");
        string b1 = AddInputPin(viewModel, "B1");
        string b2 = AddInputPin(viewModel, "B2");

        foreach (LanePinChoice choice in viewModel.LanePinChoices)
        {
            choice.IsSelected = choice.PinId is var id && (id == b0 || id == b1 || id == b2);
        }

        viewModel.SelectedLaneFormat = viewModel.FormatChoices.Single(option =>
            option.Format.Id == NumericFormats.UnsignedBinary.Id);
        viewModel.NewLaneName = "Value";
        viewModel.AddNumericLane();

        NumericLane lane = Assert.Single(viewModel.Definition.NumericLanes);
        Assert.Equal([b0, b1, b2], lane.PinIds);
        Assert.Equal("Value", lane.Name);
        Assert.Equal("—", viewModel.NumericLanes.Single().ValueText);

        viewModel.BeginSimulation();
        NumericLaneListItem card = viewModel.NumericLanes.Single();
        Assert.True(card.CanDrive);

        card.DriveText = "5";
        viewModel.DriveNumericLane(lane.Id);
        Assert.Equal("5", card.ValueText);
        Assert.Equal("1", PinValue(viewModel, b0));
        Assert.Equal("0", PinValue(viewModel, b1));
        Assert.Equal("1", PinValue(viewModel, b2));

        card.DriveText = "7";
        viewModel.DriveNumericLane(lane.Id);
        Assert.Equal("7", card.ValueText);

        card.DriveText = "8";
        viewModel.DriveNumericLane(lane.Id);
        Assert.Equal("7", card.ValueText);
        Assert.Contains("outside", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        card.DriveText = "not a number";
        viewModel.DriveNumericLane(lane.Id);
        Assert.Contains("whole number", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        viewModel.ClearDrives();
        Assert.Equal("invalid", card.ValueText);
        Assert.Contains("undriven", card.ValueHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutputOnlyLaneIsReadOnly()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        NumericLaneListItem card = Assert.Single(viewModel.NumericLanes);
        Assert.False(card.CanDrive);

        card.DriveText = "5";
        viewModel.DriveNumericLane(card.Id);

        Assert.Contains("every pin is an input", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(viewModel.Simulator.Drives);
    }

    [Fact]
    public void CustomNumericFormatsFeedLaneChoicesAndRemainInUseUntilLanesAreGone()
    {
        var viewModel = CreateEmptyEditor();
        string t0 = AddInputPin(viewModel, "T0", StandardDomains.TernaryId);
        string t1 = AddInputPin(viewModel, "T1", StandardDomains.TernaryId);
        string t2 = AddInputPin(viewModel, "T2", StandardDomains.TernaryId);

        viewModel.NewFormatName = "Custom balanced";
        viewModel.NewFormatDomain = viewModel.Domains.Single(domain => domain.Id == StandardDomains.TernaryId);
        viewModel.NewFormatDigits = "-1 0 1";
        viewModel.NewFormatRadix = "3";
        viewModel.SelectedFormatMode = viewModel.FormatModes.Single(mode => mode.Id == "standard");
        viewModel.NewFormatLeastSignificantFirst = true;
        viewModel.AddCustomNumericFormat();

        NumericFormatDefinition format = Assert.Single(viewModel.Project.CustomNumericFormats);
        Assert.Contains(viewModel.FormatChoices, option => option.Format.Id == format.Id);

        foreach (LanePinChoice choice in viewModel.LanePinChoices)
        {
            choice.IsSelected = choice.PinId is var id && (id == t0 || id == t1 || id == t2);
        }

        viewModel.SelectedLaneFormat = viewModel.FormatChoices.Single(option => option.Format.Id == format.Id);
        viewModel.NewLaneName = "Balanced";
        viewModel.AddNumericLane();
        NumericLane lane = Assert.Single(viewModel.Definition.NumericLanes);
        Assert.Equal(format.Id, lane.FormatId);

        viewModel.RemoveCustomNumericFormat(format.Id);
        Assert.Contains("used by", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(viewModel.Project.CustomNumericFormats, candidate => candidate.Id == format.Id);

        viewModel.BeginSimulation();
        NumericLaneListItem card = Assert.Single(viewModel.NumericLanes);
        card.DriveText = "-4";
        viewModel.DriveNumericLane(lane.Id);
        Assert.Equal("-4", card.ValueText);

        viewModel.RemoveNumericLane(lane.Id);
        viewModel.RemoveCustomNumericFormat(format.Id);
        Assert.Empty(viewModel.Project.CustomNumericFormats);
        Assert.DoesNotContain(viewModel.FormatChoices, option => option.Format.Id == format.Id);

        viewModel.Undo();
        Assert.Single(viewModel.Project.CustomNumericFormats);
    }

    [Fact]
    public void EditingCustomNumericFormatKeepsItsIdAndLaneReferences()
    {
        var viewModel = CreateEmptyEditor();
        string t0 = AddInputPin(viewModel, "T0", StandardDomains.TernaryId);
        string t1 = AddInputPin(viewModel, "T1", StandardDomains.TernaryId);
        string t2 = AddInputPin(viewModel, "T2", StandardDomains.TernaryId);

        viewModel.NewFormatName = "Custom balanced";
        viewModel.NewFormatDomain = viewModel.Domains.Single(domain => domain.Id == StandardDomains.TernaryId);
        viewModel.NewFormatDigits = "-1 0 1";
        viewModel.NewFormatRadix = "3";
        viewModel.SelectedFormatMode = viewModel.FormatModes.Single(mode => mode.Id == "standard");
        viewModel.AddCustomNumericFormat();
        NumericFormatDefinition format = Assert.Single(viewModel.Project.CustomNumericFormats);

        foreach (LanePinChoice choice in viewModel.LanePinChoices)
        {
            choice.IsSelected = choice.PinId is var id && (id == t0 || id == t1 || id == t2);
        }

        viewModel.SelectedLaneFormat = viewModel.FormatChoices.Single(option => option.Format.Id == format.Id);
        viewModel.NewLaneName = "Balanced";
        viewModel.AddNumericLane();
        NumericLane lane = Assert.Single(viewModel.Definition.NumericLanes);

        viewModel.BeginSimulation();
        NumericLaneListItem card = Assert.Single(viewModel.NumericLanes);
        card.DriveText = "-4";
        viewModel.DriveNumericLane(lane.Id);
        Assert.Equal("-4", card.ValueText);

        viewModel.BeginEditCustomNumericFormat(format.Id);
        Assert.True(viewModel.IsEditingFormat);
        Assert.Equal("Update numeric format", viewModel.FormatSaveButtonText);
        Assert.Equal("Custom balanced", viewModel.NewFormatName);
        Assert.Equal("-1 0 1", viewModel.NewFormatDigits);
        Assert.Equal("3", viewModel.NewFormatRadix);
        Assert.Equal("standard", viewModel.SelectedFormatMode?.Id);
        Assert.Contains("Preview", viewModel.FormatPreviewText, StringComparison.Ordinal);
        Assert.Contains("-4", viewModel.FormatPreviewText, StringComparison.Ordinal);

        viewModel.NewFormatName = "Balanced renamed";
        viewModel.AddCustomNumericFormat();

        Assert.False(viewModel.IsEditingFormat);
        Assert.Equal("Add numeric format", viewModel.FormatSaveButtonText);
        NumericFormatDefinition updated = Assert.Single(viewModel.Project.CustomNumericFormats);
        Assert.Equal(format.Id, updated.Id);
        Assert.Equal("Balanced renamed", updated.Name);
        Assert.Equal(format.Id, Assert.Single(viewModel.Definition.NumericLanes).FormatId);
        Assert.Contains(viewModel.FormatChoices, option =>
            option.Format.Id == format.Id && option.Format.Name == "Balanced renamed");

        viewModel.Undo();
        NumericFormatDefinition restored = Assert.Single(viewModel.Project.CustomNumericFormats);
        Assert.Equal("Custom balanced", restored.Name);
        Assert.Equal(format.Id, Assert.Single(viewModel.Definition.NumericLanes).FormatId);
    }

    [Fact]
    public void CancellingFormatEditLeavesTheProjectUnchanged()
    {
        var viewModel = CreateEmptyEditor();
        AddInputPin(viewModel, "B0");
        viewModel.NewFormatName = "Keep";
        viewModel.NewFormatDomain = viewModel.Domains.Single(domain => domain.Id == StandardDomains.BinaryId);
        viewModel.NewFormatDigits = "0 1";
        viewModel.NewFormatRadix = "2";
        viewModel.SelectedFormatMode = viewModel.FormatModes.Single(mode => mode.Id == "standard");
        viewModel.AddCustomNumericFormat();
        NumericFormatDefinition format = Assert.Single(viewModel.Project.CustomNumericFormats);

        viewModel.BeginEditCustomNumericFormat(format.Id);
        viewModel.NewFormatName = "Discarded";
        viewModel.NewFormatDigits = "0 0 1";
        viewModel.NewFormatRadix = "3";
        viewModel.AddCustomNumericFormat();
        Assert.True(viewModel.IsEditingFormat);
        Assert.Contains("distinct", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Keep", Assert.Single(viewModel.Project.CustomNumericFormats).Name);

        viewModel.NewFormatDigits = "0 1 2";
        viewModel.CancelCustomNumericFormatEdit();

        Assert.False(viewModel.IsEditingFormat);
        Assert.Equal("Add numeric format", viewModel.FormatSaveButtonText);
        Assert.Equal(string.Empty, viewModel.NewFormatName);
        NumericFormatDefinition unchanged = Assert.Single(viewModel.Project.CustomNumericFormats);
        Assert.Equal("Keep", unchanged.Name);
        Assert.Equal([0L, 1L], unchanged.DigitValues);
        Assert.Equal(2, unchanged.Radix);
    }

    [Fact]
    public void NumericLaneRejectsMixedDomainsAndPrunesRemovedPins()
    {
        var viewModel = CreateEmptyEditor();
        string binary = AddInputPin(viewModel, "B0");
        viewModel.NewPinName = "T0";
        viewModel.NewPinIsInput = true;
        viewModel.NewPinDomain = viewModel.Domains.Single(domain => domain.Id == StandardDomains.TernaryId);
        viewModel.AddPin();
        string ternary = viewModel.Definition.Pins.Single(pin => pin.Name == "T0").Id;

        foreach (LanePinChoice choice in viewModel.LanePinChoices)
        {
            choice.IsSelected = choice.PinId == binary || choice.PinId == ternary;
        }

        viewModel.AddNumericLane();
        Assert.Empty(viewModel.Definition.NumericLanes);
        Assert.Contains("one signal domain", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        viewModel.LanePinChoices.Single(choice => choice.PinId == binary).IsSelected = true;
        viewModel.LanePinChoices.Single(choice => choice.PinId == ternary).IsSelected = false;
        viewModel.AddNumericLane();
        string laneId = Assert.Single(viewModel.Definition.NumericLanes).Id;

        viewModel.RemovePin(binary);
        Assert.Empty(viewModel.Definition.NumericLanes);

        viewModel.Undo();
        Assert.NotNull(viewModel.Definition.FindPin(binary));
        NumericLane restored = Assert.Single(viewModel.Definition.NumericLanes);
        Assert.Equal(laneId, restored.Id);
        Assert.Equal([binary], restored.PinIds);
    }

    [Fact]
    public void EditingNumericLaneKeepsItsIdAndCommitsOneUndoStep()
    {
        var viewModel = CreateEmptyEditor();
        string b0 = AddInputPin(viewModel, "B0");
        string b1 = AddInputPin(viewModel, "B1");
        string b2 = AddInputPin(viewModel, "B2");

        foreach (LanePinChoice choice in viewModel.LanePinChoices)
        {
            choice.IsSelected = choice.PinId is var id && (id == b0 || id == b1 || id == b2);
        }

        viewModel.SelectedLaneFormat = viewModel.FormatChoices.Single(option =>
            option.Format.Id == NumericFormats.UnsignedBinary.Id);
        viewModel.NewLaneName = "Value";
        viewModel.AddNumericLane();
        string laneId = Assert.Single(viewModel.Definition.NumericLanes).Id;

        viewModel.BeginEditNumericLane(laneId);
        Assert.True(viewModel.IsEditingLane);
        Assert.Equal("Update numeric lane", viewModel.LaneSaveButtonText);
        Assert.Equal("Value", viewModel.NewLaneName);
        Assert.Equal(
            [b0, b1, b2],
            viewModel.LanePinChoices.Where(choice => choice.IsSelected).Select(choice => choice.PinId));

        viewModel.NewLaneName = "Count";
        viewModel.SelectedLaneFormat = viewModel.FormatChoices.Single(option =>
            option.Format.Id == NumericFormats.TwosComplement.Id);
        viewModel.LanePinChoices.Single(choice => choice.PinId == b2).IsSelected = false;
        viewModel.AddNumericLane();

        NumericLane edited = Assert.Single(viewModel.Definition.NumericLanes);
        Assert.Equal(laneId, edited.Id);
        Assert.Equal("Count", edited.Name);
        Assert.Equal(NumericFormats.TwosComplement.Id, edited.FormatId);
        Assert.Equal([b0, b1], edited.PinIds);
        Assert.False(viewModel.IsEditingLane);
        Assert.Equal("Add numeric lane", viewModel.LaneSaveButtonText);
        Assert.DoesNotContain(viewModel.LanePinChoices, choice => choice.IsSelected);

        viewModel.Undo();
        NumericLane restored = Assert.Single(viewModel.Definition.NumericLanes);
        Assert.Equal(laneId, restored.Id);
        Assert.Equal("Value", restored.Name);
        Assert.Equal(NumericFormats.UnsignedBinary.Id, restored.FormatId);
        Assert.Equal([b0, b1, b2], restored.PinIds);
    }

    [Fact]
    public void CancellingOrRemovingAnEditedLaneLeavesNoPendingDraft()
    {
        var viewModel = CreateEmptyEditor();
        string b0 = AddInputPin(viewModel, "B0");
        string b1 = AddInputPin(viewModel, "B1");

        foreach (LanePinChoice choice in viewModel.LanePinChoices)
        {
            choice.IsSelected = choice.PinId is var id && (id == b0 || id == b1);
        }

        viewModel.NewLaneName = "Pair";
        viewModel.AddNumericLane();
        string laneId = Assert.Single(viewModel.Definition.NumericLanes).Id;

        viewModel.BeginEditNumericLane(laneId);
        viewModel.NewLaneName = "Changed";
        viewModel.LanePinChoices.Single(choice => choice.PinId == b1).IsSelected = false;
        viewModel.CancelNumericLaneEdit();

        Assert.False(viewModel.IsEditingLane);
        Assert.Equal(string.Empty, viewModel.NewLaneName);
        Assert.DoesNotContain(viewModel.LanePinChoices, choice => choice.IsSelected);
        NumericLane unchanged = Assert.Single(viewModel.Definition.NumericLanes);
        Assert.Equal("Pair", unchanged.Name);
        Assert.Equal([b0, b1], unchanged.PinIds);

        viewModel.BeginEditNumericLane(laneId);
        foreach (LanePinChoice choice in viewModel.LanePinChoices)
        {
            choice.IsSelected = false;
        }

        viewModel.AddNumericLane();
        Assert.True(viewModel.IsEditingLane);
        Assert.Contains("at least one", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([b0, b1], Assert.Single(viewModel.Definition.NumericLanes).PinIds);

        viewModel.LanePinChoices.Single(choice => choice.PinId == b0).IsSelected = true;
        viewModel.NewLaneName = "Solo";
        viewModel.AddNumericLane();
        NumericLane solo = Assert.Single(viewModel.Definition.NumericLanes);
        Assert.Equal(laneId, solo.Id);
        Assert.Equal("Solo", solo.Name);
        Assert.Equal([b0], solo.PinIds);
        Assert.False(viewModel.IsEditingLane);

        viewModel.BeginEditNumericLane(laneId);
        viewModel.RemoveNumericLane(laneId);
        Assert.False(viewModel.IsEditingLane);
        Assert.Empty(viewModel.Definition.NumericLanes);
        Assert.Equal("Add numeric lane", viewModel.LaneSaveButtonText);
    }

    [Fact]
    public void RemovingAnInterfacePinPrunesDanglingParentConnections()
    {
        var viewModel = CreateEmptyEditor();
        string rootId = viewModel.Definition.Id;
        viewModel.AddDefinition();
        string childId = viewModel.Definition.Id;
        viewModel.NewPinName = "Y";
        viewModel.NewPinIsInput = false;
        viewModel.NewPinDomain = viewModel.Domains.Single(domain => domain.Id == StandardDomains.BinaryId);
        viewModel.AddPin();
        string childPin = viewModel.Definition.Pins.Single().Id;

        viewModel.OpenDefinition(rootId);
        viewModel.NewPinName = "OUT";
        viewModel.NewPinIsInput = false;
        viewModel.AddPin();
        string rootPin = viewModel.Definition.Pins.Single().Id;
        viewModel.PlaceDefinition(childId);
        ComponentInstance child = Assert.Single(viewModel.Definition.Instances);
        viewModel.Connect(EditorPin.ForInstance(child.Id, childPin), EditorPin.ForPort(rootPin));
        Assert.Contains(viewModel.Definition.FindPortNet(rootPin)!.Pins, p => p.InstanceId == child.Id && p.PinId == childPin);

        viewModel.OpenDefinition(childId);
        viewModel.RemovePin(childPin);

        Assert.Contains("dangling", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(viewModel.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        viewModel.OpenDefinition(rootId);
        Net parentNet = viewModel.Definition.FindPortNet(rootPin)!;
        Assert.DoesNotContain(parentNet.Pins, p => p.InstanceId == child.Id);

        viewModel.Undo();
        Assert.NotNull(viewModel.Project.RequireDefinition(childId).FindPin(childPin));
        Net restoredNet = viewModel.Project.RequireDefinition(rootId).FindPortNet(rootPin)!;
        Assert.Contains(restoredNet.Pins, p => p.InstanceId == child.Id && p.PinId == childPin);
    }

    [Fact]
    public void NetNavigatorFiltersSelectsAndReadsNets()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        Assert.NotEmpty(viewModel.NetList);
        Assert.True(viewModel.HasNetList);

        Net q0 = viewModel.Definition.Nets.First(net => net.Id == "q0_net");
        NetListItem q0Item = viewModel.NetList.Single(item => item.Id == q0.Id);
        Assert.Equal("q0_net", q0Item.Name);
        Assert.Contains("Binary", q0Item.Summary, StringComparison.Ordinal);

        q0Item.SelectCommand.Execute(null);
        Assert.Equal(q0.Id, viewModel.SelectedNetId);

        viewModel.BeginSimulation();
        viewModel.AdvanceTicksText = "100";
        viewModel.AdvanceSimulation();
        Assert.Equal("1", viewModel.NetList.Single(item => item.Id == q0.Id).ValueText);

        Net q2 = viewModel.Definition.Nets.First(net => net.Id == "q2_net");
        viewModel.NetSearch = "Q2";
        Assert.NotEmpty(viewModel.NetList);
        Assert.All(viewModel.NetList, item => Assert.Contains("Q2", item.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.NetList, item => item.Id == q2.Id);
        Assert.Contains("matching", viewModel.NetListSummary, StringComparison.OrdinalIgnoreCase);

        viewModel.NetSearch = "zzzz";
        Assert.Empty(viewModel.NetList);
        Assert.False(viewModel.HasNetList);

        viewModel.NetSearch = string.Empty;
        Assert.NotEmpty(viewModel.NetList);
        Assert.Equal(viewModel.Definition.Nets.Count, viewModel.NetList.Count);
    }

    [Fact]
    public void DiagnosticsNavigateToDefinitionInstanceAndNet()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateAccumulatorCpuDemo());
        CircuitDefinition root = viewModel.Project.RequireDefinition("demo.accumulator");
        string instanceId = root.Instances[0].Id;
        string netId = root.Nets[0].Id;
        string otherDefinitionId = viewModel.Project.Definitions.First(definition => definition.Id != root.Id).Id;
        viewModel.OpenDefinition(otherDefinitionId);
        var focus = new List<Rect>();
        viewModel.FocusRequested += bounds => focus.Add(bounds);

        Assert.True(viewModel.NavigateToDiagnostic(
            new Diagnostic("RAD900", DiagnosticSeverity.Warning, "instance target", root.Id, instanceId)));
        Assert.Equal(root.Id, viewModel.Definition.Id);
        Assert.Equal(instanceId, viewModel.SelectedInstanceId);
        Assert.NotEmpty(focus);
        Assert.Contains("RAD900", viewModel.StatusText, StringComparison.Ordinal);

        Assert.True(viewModel.NavigateToDiagnostic(
            new Diagnostic("RAD901", DiagnosticSeverity.Warning, "net target", root.Id, NetId: netId)));
        Assert.Equal(netId, viewModel.SelectedNetId);
        Assert.Contains("Located net", viewModel.StatusText, StringComparison.Ordinal);

        Assert.False(viewModel.NavigateToDiagnostic(
            new Diagnostic("RAD902", DiagnosticSeverity.Info, "project-wide")));
        Assert.Contains("no circuit location", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        Assert.False(viewModel.NavigateToDiagnostic(
            new Diagnostic("RAD903", DiagnosticSeverity.Error, "missing definition", "no.such.definition")));
        Assert.Contains("no longer exists", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticLocateCommandSelectsTheReportedNet()
    {
        var project = CircuitProject.CreateEmpty("broken");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);
        var broken = new Net { Id = "broken_net", DomainId = StandardDomains.BinaryId };
        broken.Pins.Add(new PinRef("missing", "out"));
        root.Nets.Add(broken);

        var viewModel = new EditorViewModel(project);
        DiagnosticItem item = Assert.Single(viewModel.Diagnostics, candidate => candidate.Code == "RAD077");
        Assert.True(item.CanLocate);
        Assert.Equal("broken_net", item.Diagnostic.NetId);

        item.LocateCommand.Execute(null);

        Assert.Equal("broken_net", viewModel.SelectedNetId);
        Assert.Contains("Located net", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewMetadataRoundTripsWithoutDirtyingTheProject()
    {
        var viewModel = CreateEmptyEditor();
        string rootId = viewModel.Definition.Id;
        viewModel.AddDefinition();
        string childId = viewModel.Definition.Id;

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            Assert.True(viewModel.SaveProject(path));
            viewModel.SyncViewport(1.25, 90.5, -12);
            Assert.False(viewModel.IsDirty);
            Assert.True(viewModel.SaveProject(path));

            CircuitProject loaded = Core.Serialization.ProjectSerializer.Load(path);
            Assert.Equal(childId, loaded.View.ActiveDefinitionId);
            Assert.Equal(1.25, loaded.View.Scale);
            Assert.Equal(90.5, loaded.View.OffsetX);
            Assert.Equal(-12, loaded.View.OffsetY);

            bool restoreRequested = false;
            var opened = CreateEmptyEditor();
            opened.ViewRestoreRequested += () => restoreRequested = true;
            Assert.True(opened.LoadProject(path));
            Assert.True(restoreRequested);
            Assert.Equal(childId, opened.Definition.Id);
            Assert.Equal(rootId, opened.Project.RootDefinitionId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenDefinitionUpdatesSavedActiveDefinition()
    {
        var viewModel = CreateEmptyEditor();
        string rootId = viewModel.Definition.Id;
        viewModel.AddDefinition();
        string childId = viewModel.Definition.Id;

        viewModel.OpenDefinition(rootId);
        Assert.Equal(rootId, viewModel.Project.View.ActiveDefinitionId);
        viewModel.OpenDefinition(childId);
        Assert.Equal(childId, viewModel.Project.View.ActiveDefinitionId);
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

    private static string AddInputPin(
        EditorViewModel viewModel,
        string name,
        string domainId = StandardDomains.BinaryId)
    {
        viewModel.NewPinName = name;
        viewModel.NewPinIsInput = true;
        viewModel.NewPinDomain = viewModel.Domains.First(domain => domain.Id == domainId);
        viewModel.AddPin();
        return viewModel.Definition.Pins.Single(pin => pin.Name == name).Id;
    }
}
