using Avalonia;
using Radix.Core;
using Radix.Core.Analysis;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Core.Testing;
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
    public void PresentationEditsPreserveCompiledSimulationState()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        viewModel.BeginSimulation();
        viewModel.AdvanceTicksText = "100";
        viewModel.AdvanceSimulation();
        long time = viewModel.Simulator.Now;
        Assert.True(viewModel.Simulator.IsCompiled);

        ComponentInstance instance = viewModel.Definition.Instances[0];
        viewModel.BeginDrag();
        viewModel.MoveInstance(instance.Id, instance.X + 12, instance.Y + 8);
        viewModel.EndDrag();
        Assert.True(viewModel.Simulator.IsCompiled);
        Assert.Equal(time, viewModel.Simulator.Now);

        viewModel.NudgeInstance(instance.Id, 5, 0);
        Assert.True(viewModel.Simulator.IsCompiled);
        Assert.Equal(time, viewModel.Simulator.Now);

        string netId = viewModel.Layout!.Wires.First().NetId;
        viewModel.SetNetWireColor(netId, "#123456");
        Assert.True(viewModel.Simulator.IsCompiled);
        Assert.Equal(time, viewModel.Simulator.Now);

        viewModel.SelectNet(netId);
        viewModel.AddNetWaypoint(netId, viewModel.Layout!.Wires.First(wire => wire.NetId == netId).LabelPoint);
        Assert.True(viewModel.Simulator.IsCompiled);
        Assert.Equal(time, viewModel.Simulator.Now);

        viewModel.SelectInstance(instance.Id);
        viewModel.DeleteSelection();
        Assert.False(viewModel.Simulator.IsCompiled);
        Assert.Equal(0, viewModel.Simulator.Now);
    }

    [Fact]
    public void WireColorPaletteAppliesAndClearsTheSelectedNetColor()
    {
        var viewModel = CreateLookupEditor();
        viewModel.SelectNet("a_net");

        WireColorChoice sky = viewModel.WireColorChoices.Single(choice => choice.Color == "#38BDF8");
        sky.ApplyCommand.Execute(null);

        Assert.Equal("#38BDF8", viewModel.Definition.FindNet("a_net")!.WireColor);
        Assert.True(viewModel.IsDirty);

        WireColorChoice automatic = viewModel.WireColorChoices.Single(choice => choice.Color is null);
        automatic.ApplyCommand.Execute(null);

        Assert.Null(viewModel.Definition.FindNet("a_net")!.WireColor);
        Assert.Equal("Wire color set to automatic", viewModel.StatusText);

        viewModel.Undo();
        Assert.Equal("#38BDF8", viewModel.Definition.FindNet("a_net")!.WireColor);
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
    public void WaveformRowValueAtHoldsTheLastChangeAtOrBeforeTheCursor()
    {
        var row = new WaveformRow(
            "n0",
            StandardDomains.Binary(),
            startValue: 1,
            changes:
            [
                new WaveValueChange(10, 0),
                new WaveValueChange(20, 1),
                new WaveValueChange(20, 0),
                new WaveValueChange(30, 1),
            ]);

        Assert.Equal(1, row.ValueAt(0));
        Assert.Equal(1, row.ValueAt(9));
        Assert.Equal(0, row.ValueAt(10));
        Assert.Equal(0, row.ValueAt(19));
        Assert.Equal(0, row.ValueAt(20));
        Assert.Equal(0, row.ValueAt(29));
        Assert.Equal(1, row.ValueAt(30));
        Assert.Equal(1, row.ValueAt(10_000));
    }

    [Fact]
    public void WaveformPinsShowANetBeforeTheDeclarationOrderedRows()
    {
        var viewModel = new EditorViewModel(WideNetProject(20));
        viewModel.BeginSimulation();

        Assert.Equal(EditorViewModel.MaxWaveformRows, viewModel.Waveform.Rows.Count);
        Assert.Equal("n0", viewModel.Waveform.Rows[0].Label);
        Assert.False(viewModel.HasWaveformPins);
        Assert.Contains("declaration order", viewModel.WaveformPinSummary, StringComparison.OrdinalIgnoreCase);

        viewModel.SelectNet("n19");
        Assert.False(viewModel.SelectedNetOnWaveform);
        Assert.Equal("Show in waveform", viewModel.WaveformPinButtonText);
        viewModel.ToggleSelectedNetWaveformPin();

        Assert.True(viewModel.SelectedNetOnWaveform);
        Assert.Equal("Hide from waveform", viewModel.WaveformPinButtonText);
        Assert.Equal(1, viewModel.WaveformPinCount);
        Assert.True(viewModel.HasWaveformPins);
        Assert.Contains("pinned", viewModel.WaveformPinSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("n19", viewModel.Waveform.Rows[0].Label);
        Assert.Equal(EditorViewModel.MaxWaveformRows, viewModel.Waveform.Rows.Count);
        Assert.Contains(viewModel.NetList, item =>
            item.Id == "n19" && item.Summary.Contains("waveform", StringComparison.Ordinal));

        viewModel.ToggleSelectedNetWaveformPin();
        Assert.False(viewModel.HasWaveformPins);
        Assert.Equal("n0", viewModel.Waveform.Rows[0].Label);
    }

    [Fact]
    public void WaveformPinsAreScopedPerDefinitionAndDroppedWithTheirNet()
    {
        var viewModel = new EditorViewModel(WideNetProject(18));
        viewModel.BeginSimulation();
        viewModel.SelectNet("n17");
        viewModel.ToggleSelectedNetWaveformPin();
        Assert.Equal(1, viewModel.WaveformPinCount);

        viewModel.AddDefinition();
        Assert.Equal(0, viewModel.WaveformPinCount);
        Assert.False(viewModel.HasWaveformPins);

        viewModel.OpenDefinition("main");
        Assert.Equal(1, viewModel.WaveformPinCount);

        viewModel.DuplicateDefinition();
        Assert.Equal(0, viewModel.WaveformPinCount);

        viewModel.OpenDefinition("main");
        viewModel.SelectNet("n17");
        viewModel.DeleteSelection();
        Assert.Equal(0, viewModel.WaveformPinCount);
        Assert.Null(viewModel.Project.FindDefinition("main")!.FindNet("n17"));
    }

    [Fact]
    public void ClearingWaveformPinsRestoresDeclarationOrder()
    {
        var viewModel = new EditorViewModel(WideNetProject(20));
        viewModel.BeginSimulation();
        viewModel.SelectNet("n19");
        viewModel.ToggleSelectedNetWaveformPin();
        Assert.Equal("n19", viewModel.Waveform.Rows[0].Label);

        viewModel.ClearWaveformPins();

        Assert.Equal(0, viewModel.WaveformPinCount);
        Assert.Equal("n0", viewModel.Waveform.Rows[0].Label);
        Assert.Contains("Cleared 1 waveform pin", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WaveformPinsAreSavedWithTheProjectViewButDoNotDirtyIt()
    {
        EditorViewModel viewModel = new(WideNetProject(18));
        viewModel.SelectNet("n17");
        viewModel.ToggleSelectedNetWaveformPin();
        Assert.False(viewModel.IsDirty);

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            Assert.True(viewModel.SaveProject(path));

            var opened = new EditorViewModel(WideNetProject(2));
            Assert.True(opened.LoadProject(path));
            opened.BeginSimulation();

            Assert.Equal(1, opened.WaveformPinCount);
            Assert.True(opened.HasWaveformPins);
            Assert.Equal("n17", opened.Waveform.Rows[0].Label);
            Assert.False(opened.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
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
    public void AutoArrangeIsAnUndoablePresentationEditThatKeepsTheSimulation()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        viewModel.BeginSimulation();
        viewModel.AdvanceTicksText = "100";
        viewModel.AdvanceSimulation();
        long time = viewModel.Simulator.Now;
        Dictionary<string, (double X, double Y)> before = viewModel.Definition.Instances
            .ToDictionary(instance => instance.Id, instance => (instance.X, instance.Y));

        viewModel.AutoArrange();

        Assert.True(viewModel.IsDirty);
        Assert.Equal(time, viewModel.Simulator.Now);
        Assert.Contains(viewModel.Definition.Instances, instance =>
            Math.Abs(instance.X - before[instance.Id].X) > 0.01 ||
            Math.Abs(instance.Y - before[instance.Id].Y) > 0.01);

        viewModel.Undo();
        Assert.All(viewModel.Definition.Instances, instance =>
        {
            Assert.Equal(before[instance.Id].X, instance.X);
            Assert.Equal(before[instance.Id].Y, instance.Y);
        });
    }

    [Fact]
    public void NetEndpointsListEveryInstancePinAndNavigateToThePart()
    {
        var builder = new ProjectBuilder("net endpoints");
        DefinitionBuilder root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("g1", new LookupSpec(1, [0, 1]));
        root.AddPrimitive("g2", new LookupSpec(1, [0, 1]));
        root.AddPrimitive("or", new LookupSpec(2, [0, 1, 1, 1]));
        Net first = root.AddNet("n1", StandardDomains.BinaryId);
        Net second = root.AddNet("n2", StandardDomains.BinaryId);
        root.Connect(a, root.Pin("g1", "in0"), root.Pin("g2", "in0"));
        root.Connect(first, root.Pin("g1", "out"), root.Pin("or", "in0"));
        root.Connect(second, root.Pin("g2", "out"), root.Pin("or", "in1"));
        root.Connect(y, root.Pin("or", "out"));

        var viewModel = new EditorViewModel(builder.Build("root"));
        viewModel.SelectNet("a_net");

        Assert.True(viewModel.HasNetEndpoints);
        Assert.Equal(["g1", "g2"], viewModel.NetEndpoints.Select(endpoint => endpoint.InstanceId));
        Assert.All(viewModel.NetEndpoints, endpoint =>
            Assert.Contains("in0 · in", endpoint.Detail, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.Fields, field => field.Label == "Ports" && field.Value.Contains("A (in)"));

        viewModel.NetEndpoints[0].SelectCommand.Execute(null);

        Assert.Equal("g1", viewModel.SelectedInstanceId);
        Assert.False(viewModel.HasNetEndpoints);
    }

    [Fact]
    public void DefinitionUsagesOpenTheParentAndSelectEveryInstance()
    {
        var builder = new ProjectBuilder("usages");
        DefinitionBuilder leaf = builder.Define("leaf", "Leaf");
        Net leafA = leaf.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net leafY = leaf.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        leaf.AddPrimitive("g", new LookupSpec(1, [0, 1]));
        leaf.Connect(leafA, leaf.Pin("g", "in0"));
        leaf.Connect(leafY, leaf.Pin("g", "out"));

        DefinitionBuilder parent = builder.Define("parent", "Parent");
        Net a = parent.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y = parent.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        parent.AddInstance("leaf1", "leaf", 100, 100);
        parent.AddInstance("leaf2", "leaf", 400, 100);
        parent.AddPrimitive("or", new LookupSpec(2, [0, 1, 1, 1]));
        Net first = parent.AddNet("n1", StandardDomains.BinaryId);
        Net second = parent.AddNet("n2", StandardDomains.BinaryId);
        parent.Connect(a, parent.Pin("leaf1", "a"), parent.Pin("leaf2", "a"));
        parent.Connect(first, parent.Pin("leaf1", "y"), parent.Pin("or", "in0"));
        parent.Connect(second, parent.Pin("leaf2", "y"), parent.Pin("or", "in1"));
        parent.Connect(y, parent.Pin("or", "out"));

        var viewModel = new EditorViewModel(builder.Build("parent"));
        viewModel.OpenDefinition("leaf");

        Assert.True(viewModel.HasDefinitionUsages);
        DefinitionUsageItem usage = Assert.Single(viewModel.DefinitionUsages);
        Assert.Equal("parent", usage.ParentId);
        Assert.Equal("Parent", usage.ParentName);
        Assert.Contains("leaf1", usage.Detail);
        Assert.Contains("leaf2", usage.Detail);
        Assert.Contains("used ×2", viewModel.Definitions.Single(definition => definition.Id == "leaf").Subtitle);

        usage.OpenCommand.Execute(null);

        Assert.Equal("parent", viewModel.Definition.Id);
        Assert.Equal(["leaf1", "leaf2"], viewModel.SelectedInstanceIds.Order().ToArray());
        Assert.False(viewModel.HasDefinitionUsages);
    }

    [Fact]
    public void ArrangeSelectionAlignsAndSpreadsPartsAsAPresentationEdit()
    {
        var viewModel = CreateEmptyEditor();
        LibraryComponent gate = ComponentCatalog.Components.First(c => c.Name == "AND gate");
        viewModel.PlaceLibraryComponent(gate);
        ComponentInstance first = viewModel.Definition.Instances.Single();
        viewModel.PlaceLibraryComponent(gate);
        ComponentInstance second = viewModel.Definition.Instances.Single(i => i.Id != first.Id);
        viewModel.PlaceLibraryComponent(gate);
        ComponentInstance third = viewModel.Definition.Instances.Single(i => i.Id != first.Id && i.Id != second.Id);

        viewModel.BeginSimulation();
        viewModel.AdvanceTicksText = "40";
        viewModel.AdvanceSimulation();
        long time = viewModel.Simulator.Now;

        viewModel.SelectInstances([first.Id, second.Id]);
        Assert.True(viewModel.AlignLeftCommand.CanExecute(null));
        Assert.True(viewModel.AlignTopCommand.CanExecute(null));
        Assert.False(viewModel.DistributeHorizontallyCommand.CanExecute(null));

        viewModel.SetInstanceState(first.Id, 40, 80);
        viewModel.SetInstanceState(second.Id, 120, 260);
        viewModel.SetInstanceState(third.Id, 300, 180);

        viewModel.AlignSelectionLeft();
        Assert.Equal(40, first.X);
        Assert.Equal(40, second.X);
        Assert.Equal(300, third.X);
        Assert.Equal(260, second.Y);
        Assert.True(viewModel.Simulator.IsCompiled);
        Assert.Equal(time, viewModel.Simulator.Now);

        viewModel.Undo();
        first = viewModel.Definition.FindInstance(first.Id)!;
        second = viewModel.Definition.FindInstance(second.Id)!;
        third = viewModel.Definition.FindInstance(third.Id)!;
        Assert.Equal(120, second.X);

        viewModel.AlignSelectionTop();
        Assert.Equal(80, second.Y);

        viewModel.SelectInstances([first.Id, second.Id, third.Id]);
        Assert.True(viewModel.DistributeHorizontallyCommand.CanExecute(null));
        viewModel.SetInstanceState(first.Id, 0, 100);
        viewModel.SetInstanceState(second.Id, 100, 300);
        viewModel.SetInstanceState(third.Id, 300, 200);

        viewModel.DistributeSelectionVertically();
        Assert.Equal([100.0, 200.0, 300.0], new[] { first.Y, second.Y, third.Y }.Order());
        Assert.Equal(100, first.Y);
        Assert.Equal(300, second.Y);
        Assert.Equal(0, first.X);
        Assert.Equal(300, third.X);

        viewModel.DistributeSelectionHorizontally();
        Assert.Equal(0, first.X);
        Assert.Equal(150, second.X);
        Assert.Equal(300, third.X);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void DefinitionNavigationBacksUpAndTruncatesTheForwardTrail()
    {
        var viewModel = CreateEmptyEditor();
        string root = viewModel.Definition.Id;
        Assert.False(viewModel.CanNavigateBack);
        Assert.False(viewModel.CanNavigateForward);

        viewModel.OpenDefinition(StandardLibrary.TernaryLatchId);
        viewModel.OpenDefinition(StandardLibrary.TernaryDFlipFlopId);
        Assert.True(viewModel.CanNavigateBack);

        viewModel.NavigateBack();
        Assert.Equal(StandardLibrary.TernaryLatchId, viewModel.Definition.Id);
        Assert.True(viewModel.CanNavigateForward);

        viewModel.NavigateBack();
        Assert.Equal(root, viewModel.Definition.Id);
        Assert.False(viewModel.CanNavigateBack);

        viewModel.NavigateForward();
        Assert.Equal(StandardLibrary.TernaryLatchId, viewModel.Definition.Id);

        // Revisit the latch without moving, then jump elsewhere: the forward trail is dropped.
        viewModel.OpenDefinition(StandardLibrary.TernaryLatchId);
        viewModel.OpenDefinition("std.t-min");
        Assert.False(viewModel.CanNavigateForward);
        viewModel.NavigateBack();
        Assert.Equal(StandardLibrary.TernaryLatchId, viewModel.Definition.Id);
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
    public void RecoveredProjectOpensDirtyWithItsOriginAndClearsAfterSave()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            var original = CreateEmptyEditor();
            original.PlaceLibraryComponent(ComponentCatalog.Components.First(c => c.Name == "NOT gate"));
            Assert.True(original.SaveProject(path));

            var recovered = CreateEmptyEditor();
            bool restoreRequested = false;
            recovered.ViewRestoreRequested += () => restoreRequested = true;

            bool loaded = recovered.LoadRecoveredProject(new RecoverySnapshot(
                path,
                DateTimeOffset.UtcNow,
                ProjectSerializer.Load(path)));

            Assert.True(loaded);
            Assert.True(restoreRequested);
            Assert.True(recovered.IsRecoveredProject);
            Assert.True(recovered.IsDirty);
            Assert.Equal(path, recovered.ProjectPath);
            Assert.Contains("Recovered", recovered.StatusText, StringComparison.Ordinal);
            Assert.Contains(recovered.Definition.Instances, instance => instance.Primitive is LookupSpec);
            Assert.Contains(
                recovered.RecentProjects,
                item => item.Path == Path.GetFullPath(path));

            Assert.True(recovered.SaveProject(path));
            Assert.False(recovered.IsRecoveredProject);
            Assert.False(recovered.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProjectSavedFiresOnlyForSuccessfulWrites()
    {
        var viewModel = CreateEmptyEditor();
        int saves = 0;
        viewModel.ProjectSaved += () => saves++;
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            Assert.True(viewModel.SaveProject(path));
            Assert.Equal(1, saves);

            // Saving onto an existing directory is not a writable project path.
            Assert.False(viewModel.SaveProject(Path.GetTempPath()));
            Assert.Equal(1, saves);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecentProjectsTrackLoadsAndSavesAndSkipMissingFiles()
    {
        var viewModel = CreateEmptyEditor();
        string first = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        string second = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            Assert.True(viewModel.SaveProject(first));
            Assert.Single(viewModel.RecentProjects);
            Assert.Equal(Path.GetFullPath(first), viewModel.RecentProjects[0].Path);
            Assert.Equal(Path.GetFileName(first), viewModel.RecentProjects[0].Header);

            Assert.True(viewModel.SaveProject(second));
            Assert.Equal(2, viewModel.RecentProjects.Count);
            Assert.Equal(Path.GetFullPath(second), viewModel.RecentProjects[0].Path);

            var opened = CreateEmptyEditor();
            string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
            opened.SetRecentProjects([second, missing, first]);
            Assert.Equal(2, opened.RecentProjects.Count);
            Assert.Equal(Path.GetFullPath(second), opened.RecentProjects[0].Path);

            Assert.True(opened.LoadProject(first));
            Assert.Equal(2, opened.RecentProjects.Count);
            Assert.Equal(Path.GetFullPath(first), opened.RecentProjects[0].Path);
            Assert.Equal(Path.GetFullPath(second), opened.RecentProjects[1].Path);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void RecentProjectsKeepNewestEntriesUpToTheLimit()
    {
        var viewModel = CreateEmptyEditor();
        var paths = Enumerable.Range(0, WorkspacePreferences.MaxRecentProjects + 3)
            .Select(_ => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix"))
            .ToList();
        try
        {
            foreach (string path in paths)
            {
                Assert.True(viewModel.SaveProject(path));
            }

            Assert.Equal(WorkspacePreferences.MaxRecentProjects, viewModel.RecentProjects.Count);
            Assert.Equal(Path.GetFullPath(paths[^1]), viewModel.RecentProjects[0].Path);
            Assert.DoesNotContain(viewModel.RecentProjects, item => item.Path == Path.GetFullPath(paths[0]));
        }
        finally
        {
            foreach (string path in paths)
            {
                File.Delete(path);
            }
        }
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

    [Fact]
    public void LookupTableEditorLabelsMixedRadixAddresses()
    {
        EditorViewModel viewModel = CreateLookupEditor();
        viewModel.SelectInstance("g");

        Assert.True(viewModel.HasLookupTable);
        Assert.Equal(4, viewModel.LookupCells.Count);
        Assert.Equal(["0 · 0,0", "1 · 0,1", "2 · 1,0", "3 · 1,1"],
            viewModel.LookupCells.Select(cell => cell.Address).ToArray());
        Assert.Equal([0, 1, 2, 3], viewModel.LookupCells.Select(cell => cell.Index).ToArray());
        Assert.Contains("output binary", viewModel.LookupTableSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void LookupTableCellCommitIsOneUndoStepAndRefreshesTheGrid()
    {
        EditorViewModel viewModel = CreateLookupEditor();
        viewModel.SelectInstance("g");

        LookupTableCell cell = viewModel.LookupCells[1];
        cell.Value = "1";
        Assert.True(viewModel.ApplyLookupCell(cell));

        LookupSpec updated = Assert.IsType<LookupSpec>(viewModel.Definition.FindInstance("g")!.Primitive);
        Assert.Equal([0, 1, 0, 1], updated.Table);
        Assert.Equal("1", viewModel.LookupCells[1].Value);
        Assert.True(viewModel.IsDirty);

        viewModel.Undo();
        LookupSpec restored = Assert.IsType<LookupSpec>(viewModel.Definition.FindInstance("g")!.Primitive);
        Assert.Equal([0, 0, 0, 1], restored.Table);
        Assert.Equal("0", viewModel.LookupCells[1].Value);
    }

    [Fact]
    public void LookupTableRejectsOutOfRangeAndNegativeEntriesWithoutMutating()
    {
        EditorViewModel viewModel = CreateLookupEditor();
        viewModel.SelectInstance("g");

        LookupTableCell cell = viewModel.LookupCells[0];
        cell.Value = "2";
        Assert.False(viewModel.ApplyLookupCell(cell));
        Assert.Contains("between 0 and 1", viewModel.StatusText, StringComparison.Ordinal);

        cell.Value = "-1";
        Assert.False(viewModel.ApplyLookupCell(cell));
        Assert.Contains("non-negative integer", viewModel.StatusText, StringComparison.Ordinal);

        LookupSpec unchanged = Assert.IsType<LookupSpec>(viewModel.Definition.FindInstance("g")!.Primitive);
        Assert.Equal([0, 0, 0, 1], unchanged.Table);
    }

    [Fact]
    public void RawTableFieldEditRefreshesTheLookupGridValues()
    {
        EditorViewModel viewModel = CreateLookupEditor();
        viewModel.SelectInstance("g");

        EditableField field = viewModel.Fields.Single(candidate => candidate.Label == "Table (raw)");
        field.Value = "1 1 0 0";
        viewModel.ApplyField(field);

        Assert.Equal(["1", "1", "0", "0"], viewModel.LookupCells.Select(cell => cell.Value).ToArray());
        LookupSpec updated = Assert.IsType<LookupSpec>(viewModel.Definition.FindInstance("g")!.Primitive);
        Assert.Equal([1, 1, 0, 0], updated.Table);
    }

    [Fact]
    public void SelectingANetHidesTheLookupTableEditor()
    {
        EditorViewModel viewModel = CreateLookupEditor();
        viewModel.SelectInstance("g");
        Assert.True(viewModel.HasLookupTable);

        viewModel.SelectNet("a");

        Assert.False(viewModel.HasLookupTable);
        Assert.Empty(viewModel.LookupCells);
    }

    [Fact]
    public void TestScriptRunsAgainstTheEditorProjectAndReportsPass()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radixtest");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "name": "editor pass",
                  "definition": "demo.dff",
                  "steps": [
                    { "at": 0, "set": { "clk": 0, "d": 1 } },
                    { "at": 20, "set": { "clk": 1 }, "within": { "ticks": 10, "expect": { "q": 1, "qn": 0 } } }
                  ]
                }
                """);

            Assert.True(viewModel.RunTestScript(path));

            Assert.True(viewModel.HasTestScriptResult);
            Assert.True(viewModel.TestScriptPassed);
            Assert.False(viewModel.HasTestScriptFailures);
            Assert.Empty(viewModel.TestScriptFailures);
            Assert.StartsWith("PASS", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Contains("editor pass", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Equal(path, viewModel.TestScriptPath);
            Assert.False(viewModel.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TestScriptAsyncRunsOnASnapshotAndReportsTheSameResult()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radixtest");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "name": "async pass",
                  "definition": "demo.dff",
                  "steps": [
                    { "at": 0, "set": { "clk": 0, "d": 1 } },
                    { "at": 20, "set": { "clk": 1 }, "within": { "ticks": 10, "expect": { "q": 1, "qn": 0 } } }
                  ]
                }
                """);

            Assert.True(await viewModel.RunTestScriptAsync(path));

            Assert.False(viewModel.IsTestScriptRunning);
            Assert.Equal(string.Empty, viewModel.TestScriptRunTitle);
            Assert.False(viewModel.TestScriptProgressIndeterminate);
            Assert.True(viewModel.HasTestScriptResult);
            Assert.True(viewModel.TestScriptPassed);
            Assert.False(viewModel.HasTestScriptFailures);
            Assert.StartsWith("PASS", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Contains("async pass", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.False(viewModel.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TestScriptAsyncCancelsInsideALongWindow()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radixtest");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "definition": "demo.dff",
                  "steps": [ { "at": 0, "stable": { "ticks": 20000000, "expect": { "q": 0 } } } ]
                }
                """);

            Task<bool> run = viewModel.RunTestScriptAsync(path);
            Assert.True(viewModel.IsTestScriptRunning);
            Assert.Equal("Running script…", viewModel.TestScriptRunTitle);
            Assert.True(viewModel.TestScriptProgressIndeterminate);

            viewModel.CancelTestScript();
            Assert.False(await run);

            Assert.False(viewModel.IsTestScriptRunning);
            Assert.True(viewModel.HasTestScriptResult);
            Assert.False(viewModel.TestScriptPassed);
            Assert.Contains("cancelled", viewModel.TestScriptSummary, StringComparison.OrdinalIgnoreCase);
            Assert.False(viewModel.TestScriptProgressIndeterminate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TestScriptResultIsMarkedStaleByCircuitEditsButNotByMovingParts()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radixtest");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "definition": "demo.dff",
                  "steps": [ { "at": 0, "set": { "clk": 0, "d": 1 }, "expect": { "q": 0 } } ]
                }
                """);

            Assert.True(viewModel.RunTestScript(path));
            Assert.False(viewModel.IsTestScriptResultStale);

            ComponentInstance instance = viewModel.Definition.FindInstance("ff")!;
            viewModel.SetInstanceState(instance.Id, instance.X + 20, instance.Y + 10);
            Assert.False(viewModel.IsTestScriptResultStale);

            viewModel.SelectInstance(instance.Id);
            viewModel.DeleteSelection();
            Assert.True(viewModel.HasTestScriptResult);
            Assert.True(viewModel.IsTestScriptResultStale);

            viewModel.ClearTestScriptResult();
            Assert.False(viewModel.IsTestScriptResultStale);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TestScriptFailureIsListedAndMarked()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radixtest");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "name": "editor fail",
                  "definition": "demo.dff",
                  "steps": [
                    { "at": 0, "set": { "clk": 0, "d": 0 }, "expect": { "q": 1 } }
                  ]
                }
                """);

            Assert.False(viewModel.RunTestScript(path));

            Assert.True(viewModel.HasTestScriptResult);
            Assert.False(viewModel.TestScriptPassed);
            Assert.True(viewModel.HasTestScriptFailures);
            TestScriptFailureItem failure = Assert.Single(viewModel.TestScriptFailures);
            Assert.Equal(1, failure.Step);
            Assert.Contains("pin 'q'", failure.Display, StringComparison.Ordinal);
            Assert.StartsWith("FAIL", viewModel.TestScriptSummary, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MalformedTestScriptIsReportedWithoutFailures()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radixtest");
        try
        {
            File.WriteAllText(path, "{ not json");

            Assert.False(viewModel.RunTestScript(path));

            Assert.True(viewModel.HasTestScriptResult);
            Assert.False(viewModel.TestScriptPassed);
            Assert.Empty(viewModel.TestScriptFailures);
            Assert.StartsWith("Test script error:", viewModel.TestScriptSummary, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TestScriptSuiteRunsAFolderAndListsFailuresWithTheirScript()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string directory = Path.Combine(Path.GetTempPath(), "radix-editor-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "a-pass.radixtest"),
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "name": "suite pass",
                  "steps": [ { "at": 0, "set": { "clk": 0, "d": 1 }, "expect": { "q": 0 } } ]
                }
                """);
            File.WriteAllText(
                Path.Combine(directory, "b-fail.radixtest"),
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "name": "suite fail",
                  "steps": [ { "at": 0, "set": { "clk": 0, "d": 1 }, "expect": { "q": 1 } } ]
                }
                """);

            Assert.False(viewModel.RunTestScriptSuite(directory));

            Assert.True(viewModel.HasTestScriptResult);
            Assert.False(viewModel.TestScriptPassed);
            Assert.True(viewModel.HasTestScriptFailures);
            TestScriptFailureItem failure = Assert.Single(viewModel.TestScriptFailures);
            Assert.Contains("b-fail.radixtest", failure.Display, StringComparison.Ordinal);
            Assert.Contains("pin 'q'", failure.Display, StringComparison.Ordinal);
            Assert.StartsWith("FAIL", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Contains("2 script(s)", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Contains("1 failed", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Equal(directory, viewModel.TestScriptPath);
            Assert.False(viewModel.IsDirty);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TestScriptSuitePassesForAFolderOfPassingScripts()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string directory = Path.Combine(Path.GetTempPath(), "radix-editor-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string passing =
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [ { "at": 0, "set": { "clk": 0, "d": 1 }, "expect": { "q": 0 } } ]
                }
                """;
            File.WriteAllText(Path.Combine(directory, "one.radixtest"), passing);
            File.WriteAllText(Path.Combine(directory, "two.radixtest"), passing);

            Assert.True(viewModel.RunTestScriptSuite(directory));

            Assert.True(viewModel.HasTestScriptResult);
            Assert.True(viewModel.TestScriptPassed);
            Assert.False(viewModel.HasTestScriptFailures);
            Assert.Empty(viewModel.TestScriptFailures);
            Assert.StartsWith("PASS", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Contains("2 script(s)", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Contains("2 step(s)", viewModel.TestScriptSummary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TestScriptSuiteAsyncRunsOnASnapshotAndReportsProgress()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string directory = Path.Combine(Path.GetTempPath(), "radix-editor-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string passing =
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [ { "at": 0, "set": { "clk": 0, "d": 1 }, "expect": { "q": 0 } } ]
                }
                """;
            File.WriteAllText(Path.Combine(directory, "one.radixtest"), passing);
            File.WriteAllText(Path.Combine(directory, "two.radixtest"), passing);
            var progress = new RecordingSuiteProgress();

            Assert.True(await viewModel.RunTestScriptSuiteAsync(directory, progress));

            Assert.True(viewModel.HasTestScriptResult);
            Assert.True(viewModel.TestScriptPassed);
            Assert.False(viewModel.HasTestScriptFailures);
            Assert.False(viewModel.IsTestScriptRunning);
            Assert.StartsWith("PASS", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Contains("2 script(s)", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Equal(2, progress.Reports.Count);
            Assert.Equal(1, progress.Reports[0].Completed);
            Assert.Equal(2, progress.Reports[0].Total);
            Assert.Equal(1d, progress.Reports[1].Fraction);
            Assert.False(viewModel.IsDirty);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TestScriptSuiteAgainstASnapshotIsStaleWhenTheProjectChangesDuringTheRun()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string directory = Path.Combine(Path.GetTempPath(), "radix-editor-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "slow.radixtest"),
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "definition": "demo.dff",
                  "steps": [ { "at": 0, "stable": { "ticks": 20000, "expect": { "q": 0 } } } ]
                }
                """);

            Task<bool> run = viewModel.RunTestScriptSuiteAsync(directory);
            viewModel.SelectInstance("ff");
            viewModel.DeleteSelection();

            Assert.True(await run);
            Assert.True(viewModel.HasTestScriptResult);
            Assert.True(viewModel.IsTestScriptResultStale);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TestScriptSuiteAsyncReportsCancellationBetweenScripts()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string directory = Path.Combine(Path.GetTempPath(), "radix-editor-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "one.radixtest"),
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [ { "at": 0, "set": { "clk": 0, "d": 1 } } ]
                }
                """);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.False(await viewModel.RunTestScriptSuiteAsync(
                directory,
                cancellationToken: cancellation.Token));

            Assert.True(viewModel.HasTestScriptResult);
            Assert.False(viewModel.TestScriptPassed);
            Assert.Contains("cancelled", viewModel.TestScriptSummary, StringComparison.OrdinalIgnoreCase);
            Assert.False(viewModel.IsTestScriptRunning);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TestScriptSuiteReportsAMalformedScriptAndRunsTheRest()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string directory = Path.Combine(Path.GetTempPath(), "radix-editor-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "good.radixtest"),
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [ { "at": 0, "set": { "clk": 0, "d": 1 }, "expect": { "q": 0 } } ]
                }
                """);
            File.WriteAllText(Path.Combine(directory, "bad.radixtest"), "{ not json");

            Assert.False(viewModel.RunTestScriptSuite(directory));

            Assert.True(viewModel.HasTestScriptResult);
            Assert.False(viewModel.TestScriptPassed);
            TestScriptFailureItem failure = Assert.Single(viewModel.TestScriptFailures);
            Assert.Contains("bad.radixtest", failure.Display, StringComparison.Ordinal);
            Assert.Contains("error:", failure.Display, StringComparison.Ordinal);
            Assert.StartsWith("SUITE ERROR", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Contains("1 error(s)", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Contains("1 passed", viewModel.TestScriptSummary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TestScriptSuiteWithNoScriptsIsReported()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string directory = Path.Combine(Path.GetTempPath(), "radix-editor-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(viewModel.RunTestScriptSuite(directory));

            Assert.True(viewModel.HasTestScriptResult);
            Assert.False(viewModel.TestScriptPassed);
            Assert.Empty(viewModel.TestScriptFailures);
            Assert.Contains("no .radixtest scripts found", viewModel.TestScriptSummary, StringComparison.Ordinal);
            Assert.Equal(directory, viewModel.TestScriptPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NewProjectClearsTheLastTestScriptResult()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radixtest");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "definition": "demo.dff",
                  "steps": [ { "at": 0, "set": { "clk": 0, "d": 0 } } ]
                }
                """);
            Assert.True(viewModel.RunTestScript(path));
            Assert.True(viewModel.HasTestScriptResult);

            viewModel.NewProject();

            Assert.False(viewModel.HasTestScriptResult);
            Assert.False(viewModel.TestScriptPassed);
            Assert.Empty(viewModel.TestScriptFailures);
            Assert.Equal(string.Empty, viewModel.TestScriptSummary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProjectSettingsLoadApplyUndoAndRejectInvalidValues()
    {
        var project = CircuitProject.CreateEmpty("Settings");
        project.Definitions.Add(new CircuitDefinition { Id = "main", Name = "Main" });
        project.RootDefinitionId = "main";
        StandardLibrary.Install(project);
        project.Settings.DefaultAdvanceTicks = 42;
        project.Settings.MaxEvaluationsPerComponentPerTimestamp = 96;
        project.Settings.MaxRecordedNetChanges = 1234;
        project.Settings.TraceAllNets = false;

        var viewModel = new EditorViewModel(project);
        Assert.Equal("42", viewModel.DefaultAdvanceTicksText);
        Assert.Equal("96", viewModel.EvaluationGuardText);
        Assert.Equal("1234", viewModel.TraceCapacityText);
        Assert.False(viewModel.RecordNetTransitions);

        viewModel.DefaultAdvanceTicksText = "25";
        viewModel.EvaluationGuardText = "128";
        viewModel.TraceCapacityText = "0";
        viewModel.RecordNetTransitions = true;
        Assert.True(viewModel.ApplyProjectSettings());
        Assert.True(viewModel.IsDirty);
        Assert.Equal(25, viewModel.Project.Settings.DefaultAdvanceTicks);
        Assert.Equal(128, viewModel.Project.Settings.MaxEvaluationsPerComponentPerTimestamp);
        Assert.Equal(0, viewModel.Project.Settings.MaxRecordedNetChanges);
        Assert.True(viewModel.Project.Settings.TraceAllNets);

        viewModel.Undo();
        Assert.Equal(42, viewModel.Project.Settings.DefaultAdvanceTicks);
        Assert.Equal("42", viewModel.DefaultAdvanceTicksText);

        viewModel.DefaultAdvanceTicksText = "0";
        Assert.False(viewModel.ApplyProjectSettings());
        Assert.Equal(42, viewModel.Project.Settings.DefaultAdvanceTicks);
        Assert.Contains("not applied", viewModel.StatusText, StringComparison.Ordinal);

        viewModel.ReloadProjectSettings();
        Assert.Equal("42", viewModel.DefaultAdvanceTicksText);
    }

    [Fact]
    public async Task SweepRendersEveryInputCombinationOfTheCurrentDefinition()
    {
        EditorViewModel viewModel = CreateLookupEditor();
        viewModel.SweepSettleTicksText = "2";

        Assert.True(await viewModel.RunSweepAsync());
        Assert.False(viewModel.IsSweepRunning);
        Assert.Equal(string.Empty, viewModel.SweepProgressText);

        Assert.True(viewModel.HasSweepResult);
        Assert.False(viewModel.HasSweepError);
        Assert.Contains("a | b | y", viewModel.SweepResultText, StringComparison.Ordinal);
        Assert.Contains("0 | 0 | 0", viewModel.SweepResultText, StringComparison.Ordinal);
        Assert.Contains("0 | 1 | 0", viewModel.SweepResultText, StringComparison.Ordinal);
        Assert.Contains("1 | 0 | 0", viewModel.SweepResultText, StringComparison.Ordinal);
        Assert.Contains("1 | 1 | 1", viewModel.SweepResultText, StringComparison.Ordinal);
        Assert.Contains("4 row(s)", viewModel.SweepSummary, StringComparison.Ordinal);
        Assert.Contains("settle 2 tick(s)", viewModel.SweepSummary, StringComparison.Ordinal);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task SweepRejectsBadLimitsAndOversizedInputSpaces()
    {
        EditorViewModel viewModel = CreateLookupEditor();

        viewModel.SweepSettleTicksText = "-1";
        Assert.False(await viewModel.RunSweepAsync());
        Assert.True(viewModel.HasSweepError);
        Assert.Contains("settle ticks", viewModel.SweepSummary, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.SweepResultText);

        viewModel.SweepSettleTicksText = "2";
        viewModel.SweepMaxRowsText = "2";
        Assert.False(await viewModel.RunSweepAsync());
        Assert.True(viewModel.HasSweepError);
        Assert.Contains("above the limit of 2", viewModel.SweepSummary, StringComparison.Ordinal);

        viewModel.SweepMaxRowsText = "4096";
        viewModel.ClearSweepResult();
        Assert.False(viewModel.HasSweepResult);
        Assert.Equal(string.Empty, viewModel.SweepSummary);
    }

    [Fact]
    public async Task SweepResultIsDroppedByStructuralEditsButSurvivesPresentationEdits()
    {
        EditorViewModel viewModel = CreateLookupEditor();
        Assert.True(await viewModel.RunSweepAsync());

        ComponentInstance gate = viewModel.Definition.FindInstance("g")!;
        viewModel.NudgeInstance(gate.Id, 8, 0);
        Assert.True(viewModel.HasSweepResult);

        viewModel.PlaceLibraryComponent(ComponentCatalog.Components.First(c => c.Name == "NOT gate"));
        Assert.False(viewModel.HasSweepResult);
        Assert.Equal(string.Empty, viewModel.SweepResultText);
    }

    [Fact]
    public async Task SweepCanBeCancelledAndRestoresIdleState()
    {
        EditorViewModel viewModel = CreateLookupEditor();
        var cancelling = new InlineProgress<SweepProgress>(report =>
        {
            if (report.CompletedRows >= 1)
            {
                viewModel.CancelSweep();
            }
        });

        bool completed = await viewModel.RunSweepAsync(cancelling);

        Assert.False(completed);
        Assert.False(viewModel.IsSweepRunning);
        Assert.False(viewModel.HasSweepResult);
        Assert.Equal(string.Empty, viewModel.SweepResultText);
        Assert.Equal("Sweep cancelled.", viewModel.SweepSummary);
        Assert.True(viewModel.RunSweepCommand.CanExecute(null));
        Assert.False(viewModel.CancelSweepCommand.CanExecute(null));
    }

    private static EditorViewModel CreateLookupEditor()
    {
        var builder = new ProjectBuilder("lookup editor");
        DefinitionBuilder root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net b = root.AddPin("b", "B", PinDirection.Input, StandardDomains.BinaryId);
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("g", new LookupSpec(2, [0, 0, 0, 1], 1));
        root.Connect(a, root.Pin("g", "in0"));
        root.Connect(b, root.Pin("g", "in1"));
        root.Connect(y, root.Pin("g", "out"));
        return new EditorViewModel(builder.Build("root"));
    }

    private static CircuitProject WideNetProject(int netCount)
    {
        var project = CircuitProject.CreateEmpty("Waveform test");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);
        for (int i = 0; i < netCount; i++)
        {
            var net = new Net { Id = $"n{i}", DomainId = StandardDomains.BinaryId };
            root.Nets.Add(net);
            string instanceId = $"c{i}";
            root.Instances.Add(new ComponentInstance
            {
                Id = instanceId,
                Primitive = new ConstantSpec(i % 2),
            });
            net.Pins.Add(new PinRef(instanceId, "out"));
        }

        return project;
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RecordingSuiteProgress : IProgress<SimulationScriptSuiteProgress>
    {
        public List<SimulationScriptSuiteProgress> Reports { get; } = [];

        public void Report(SimulationScriptSuiteProgress value) => Reports.Add(value);
    }

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
