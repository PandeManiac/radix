using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Editor.Services;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;
using ProjectBuilder = Radix.Core.Model.ProjectBuilder;

namespace Radix.Editor.Tests;

public sealed class ComponentLibraryTests
{
    [Fact]
    public void ExportImportsOnlyNewPartsAndReusesStandardDependencies()
    {
        var source = CreateEmptyEditor();
        string customId = ExtractTwoNotPart(source, "Gadget");
        string json = ComponentLibraryService.Export(source.Project, customId);

        var target = CreateEmptyEditor();
        int before = target.Project.Definitions.Count;
        LibraryImportResult first = ComponentLibraryService.Import(target.Project, json);

        Assert.True(first.Success, first.Message);
        Assert.Equal(customId, first.RootDefinitionId);
        Assert.Equal(before + 1, target.Project.Definitions.Count);
        CircuitDefinition imported = Assert.Single(target.Project.Definitions, d => d.Id == customId);
        Assert.Equal(2, imported.Instances.Count);

        LibraryImportResult second = ComponentLibraryService.Import(target.Project, json);
        Assert.True(second.Success, second.Message);
        Assert.Equal(before + 1, target.Project.Definitions.Count);
    }

    [Fact]
    public void ConflictingDefinitionIdIsRenamedInsteadOfOverwritten()
    {
        var target = CreateEmptyEditor();
        LibraryComponent not = ComponentCatalog.Components.First(c => c.Name == "NOT gate");
        target.PlaceLibraryComponent(not);
        target.SelectInstances([target.Definition.Instances.Single().Id]);
        target.NewComponentName = "Gadget";
        target.ExtractSelectionToComponent();
        string localId = target.Project.Definitions.Single(d => d.Name == "Gadget").Id;

        var source = CreateEmptyEditor();
        string importedId = ExtractTwoNotPart(source, "Gadget");
        Assert.Equal(localId, importedId);
        string json = ComponentLibraryService.Export(source.Project, importedId);

        int before = target.Project.Definitions.Count;
        LibraryImportResult result = ComponentLibraryService.Import(target.Project, json);

        Assert.True(result.Success, result.Message);
        Assert.Equal(before + 1, target.Project.Definitions.Count);
        Assert.NotNull(target.Project.FindDefinition(localId));
        Assert.Single(target.Project.FindDefinition(localId)!.Instances);
        Assert.NotNull(result.RootDefinitionId);
        Assert.NotEqual(localId, result.RootDefinitionId);
        Assert.Equal(2, target.Project.FindDefinition(result.RootDefinitionId!)!.Instances.Count);
    }

    [Fact]
    public void EditorImportOpensImportedPartAndUndoesInOneStep()
    {
        var source = CreateEmptyEditor();
        string customId = ExtractTwoNotPart(source, "Gadget");
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ComponentLibraryService.Extension);
        try
        {
            Assert.True(source.ExportDefinition(path, customId));
            var target = CreateEmptyEditor();
            int before = target.Project.Definitions.Count;

            Assert.True(target.ImportLibrary(path));
            Assert.Equal(customId, target.Definition.Id);
            Assert.True(target.IsDirty);
            Assert.Equal(before + 1, target.Project.Definitions.Count);

            target.Undo();
            Assert.Equal(before, target.Project.Definitions.Count);
            Assert.Equal(target.Project.RootDefinitionId, target.Definition.Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExportOmitsUnrelatedDomainsThatWouldConflict()
    {
        var source = CreateEmptyEditor();
        string customId = ExtractTwoNotPart(source, "Gadget");
        source.Project.Domains.Add(new SignalDomain(
            "unused",
            "Unused",
            ["x"],
            new SingleDriverResolution()));
        string json = ComponentLibraryService.Export(source.Project, customId);

        var target = CreateEmptyEditor();
        target.Project.Domains.Add(new SignalDomain(
            "unused",
            "Unused locally",
            ["y"],
            new SingleDriverResolution()));
        int before = target.Project.Definitions.Count;

        LibraryImportResult result = ComponentLibraryService.Import(target.Project, json);

        Assert.True(result.Success, result.Message);
        Assert.Equal(before + 1, target.Project.Definitions.Count);
        Assert.DoesNotContain(target.Project.Domains, d => d.Id == "unused" && d.ValueNames[0] == "x");
    }

    [Fact]
    public void ExportedPartKeepsNumericLanePresentation()
    {
        var source = CreateEmptyEditor();
        source.NewPinName = "A";
        source.NewPinIsInput = true;
        source.NewPinDomain = source.Domains.Single(domain => domain.Id == StandardDomains.BinaryId);
        source.AddPin();
        string customId = source.Definition.Id;
        PinDeclaration pin = Assert.Single(source.Definition.Pins);
        var lane = new NumericLane { Id = "input", Name = "Input", FormatId = NumericFormats.UnsignedBinary.Id };
        lane.PinIds.Add(pin.Id);
        source.Definition.NumericLanes.Add(lane);

        string json = ComponentLibraryService.Export(source.Project, customId);
        var target = CreateEmptyEditor();
        LibraryImportResult result = ComponentLibraryService.Import(target.Project, json);

        Assert.True(result.Success, result.Message);
        CircuitDefinition imported = target.Project.RequireDefinition(result.RootDefinitionId!);
        NumericLane importedLane = Assert.Single(imported.NumericLanes);
        Assert.Equal("Input", importedLane.Name);
        Assert.Equal(NumericFormats.UnsignedBinary.Id, importedLane.FormatId);
        Assert.Equal([lane.PinIds[0]], importedLane.PinIds);
    }

    [Fact]
    public void ExportOmitsCustomFormatsThatNoLaneUses()
    {
        var source = CreateEmptyEditor();
        source.Project.CustomNumericFormats.Add(new NumericFormatDefinition(
            "unused-format",
            "Unused",
            StandardDomains.BinaryId,
            [0, 1],
            2));

        string json = ComponentLibraryService.Export(source.Project, source.Definition.Id);
        CircuitProject library = Radix.Core.Serialization.ProjectSerializer.LoadFromString(json);

        Assert.Empty(library.CustomNumericFormats);
    }

    [Fact]
    public void CustomFormatConflictRenamesFormatAndRewritesLaneReferences()
    {
        var source = CreateEmptyEditor();
        source.NewPinName = "A";
        source.NewPinIsInput = true;
        source.NewPinDomain = source.Domains.Single(domain => domain.Id == StandardDomains.BinaryId);
        source.AddPin();
        PinDeclaration pin = Assert.Single(source.Definition.Pins);
        source.Project.CustomNumericFormats.Add(new NumericFormatDefinition(
            "custom-twos",
            "Source complement",
            StandardDomains.BinaryId,
            [0, 1],
            2,
            WeightingMode.RadixComplement));
        var lane = new NumericLane { Id = "value", Name = "Value", FormatId = "custom-twos" };
        lane.PinIds.Add(pin.Id);
        source.Definition.NumericLanes.Add(lane);
        string json = ComponentLibraryService.Export(source.Project, source.Definition.Id);

        var target = CreateEmptyEditor();
        target.Project.CustomNumericFormats.Add(new NumericFormatDefinition(
            "custom-twos",
            "Local unsigned",
            StandardDomains.BinaryId,
            [0, 1],
            2));
        LibraryImportResult result = ComponentLibraryService.Import(target.Project, json);

        Assert.True(result.Success, result.Message);
        HashSet<string> formatIds = target.Project.CustomNumericFormats
            .Select(format => format.Id)
            .ToHashSet(StringComparer.Ordinal);
        NumericLane importedLane = target.Project.RequireDefinition(result.RootDefinitionId!).NumericLanes.Single();
        Assert.Contains(importedLane.FormatId, formatIds);
        Assert.NotEqual("custom-twos", importedLane.FormatId);
        NumericFormatDefinition importedFormat = target.Project.FindCustomNumericFormat(importedLane.FormatId)!;
        Assert.Equal("Source complement", importedFormat.Name);
        Assert.Equal(WeightingMode.RadixComplement, importedFormat.Weighting);
    }

    [Fact]
    public void GeneratedPartLibraryImportsReusesStdPartsAndSimulates()
    {
        GeneratedPart part = PartGenerator.CreateRam(8, 4);
        string json = Radix.Core.Serialization.ProjectLibrary.Export(part.Project, part.RootDefinitionId);
        var target = CreateEmptyEditor();
        int before = target.Project.Definitions.Count;
        int dffsBefore = target.Project.Definitions.Count(d => d.Id == StandardLibrary.DFlipFlopId);

        LibraryImportResult result = ComponentLibraryService.Import(target.Project, json);

        Assert.True(result.Success, result.Message);
        Assert.Equal(part.RootDefinitionId, result.RootDefinitionId);
        Assert.Equal(before + 2, target.Project.Definitions.Count);
        Assert.Equal(dffsBefore, target.Project.Definitions.Count(d => d.Id == StandardLibrary.DFlipFlopId));
        Assert.NotNull(target.Project.FindDefinition("gen.word-reg-4"));

        var engine = new Radix.Core.Simulation.SimulationEngine(
            Radix.Core.Compilation.CircuitCompiler.Compile(target.Project, result.RootDefinitionId));
        void Drive(string pinId, int value) => engine.SetPortValue(engine.FindPortIndex(pinId), value);

        Drive("addr0", 1);
        Drive("addr1", 1);
        Drive("addr2", 0);
        Drive("d0", 1);
        Drive("d1", 0);
        Drive("d2", 0);
        Drive("d3", 1);
        Drive("we", 1);
        Drive("clk", 0);
        engine.AdvanceTo(20);
        Drive("clk", 1);
        engine.AdvanceTo(50);
        Drive("clk", 0);
        Drive("we", 0);
        engine.AdvanceTo(90);

        Assert.Equal(1, engine.GetPortValue(engine.FindPortIndex("q0")));
        Assert.Equal(0, engine.GetPortValue(engine.FindPortIndex("q1")));
        Assert.Equal(0, engine.GetPortValue(engine.FindPortIndex("q2")));
        Assert.Equal(1, engine.GetPortValue(engine.FindPortIndex("q3")));
        Assert.False(engine.IsHalted, engine.HaltReason);
        Assert.Empty(engine.RuntimeDiagnostics);
    }

    [Fact]
    public void DomainConflictAbortsWithoutPartialMerge()
    {
        var builder = new ProjectBuilder("Conflict library");
        builder.Define("custom.only");
        CircuitProject libraryProject = builder.Build("custom.only");
        libraryProject.Domains.RemoveAll(d => d.Id == StandardDomains.BinaryId);
        libraryProject.Domains.Add(new SignalDomain(
            StandardDomains.BinaryId,
            "Binary",
            ["0", "1", "2"],
            new SingleDriverResolution()));
        string json = Radix.Core.Serialization.ProjectSerializer.SaveToString(libraryProject);

        var target = CreateEmptyEditor();
        int definitionsBefore = target.Project.Definitions.Count;
        int domainsBefore = target.Project.Domains.Count;

        LibraryImportResult result = ComponentLibraryService.Import(target.Project, json);

        Assert.False(result.Success);
        Assert.Contains("Domain", result.Message);
        Assert.Equal(definitionsBefore, target.Project.Definitions.Count);
        Assert.Equal(domainsBefore, target.Project.Domains.Count);
    }

    private static string ExtractTwoNotPart(EditorViewModel viewModel, string name)
    {
        LibraryComponent not = ComponentCatalog.Components.First(c => c.Name == "NOT gate");
        viewModel.PlaceLibraryComponent(not);
        ComponentInstance first = viewModel.Definition.Instances.Single();
        viewModel.PlaceLibraryComponent(not);
        ComponentInstance second = viewModel.Definition.Instances.Single(i => i.Id != first.Id);
        viewModel.Connect(
            EditorPin.ForInstance(first.Id, PrimitiveShapes.OutputPinId),
            EditorPin.ForInstance(second.Id, "in0"));
        viewModel.SelectInstances([first.Id, second.Id]);
        viewModel.NewComponentName = name;
        viewModel.ExtractSelectionToComponent();
        return viewModel.Project.Definitions.Single(definition => definition.Name == name).Id;
    }

    private static EditorViewModel CreateEmptyEditor()
    {
        var project = CircuitProject.CreateEmpty("Library test");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);
        return new EditorViewModel(project);
    }
}
