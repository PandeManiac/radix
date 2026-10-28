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
