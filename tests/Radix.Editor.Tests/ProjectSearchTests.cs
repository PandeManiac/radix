using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Editor.Services;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class ProjectSearchTests
{
    [Fact]
    public void EmptyQueryListsEveryDefinitionWithoutInstancesOrNets()
    {
        var builder = new ProjectBuilder("search");
        builder.Define("alpha", "Alpha");
        builder.Define("beta", "Beta");
        CircuitProject project = builder.Build("alpha");

        ProjectSearchResults results = ProjectSearch.Search(project, "alpha", "  ");

        Assert.Equal(2, results.TotalMatches);
        Assert.Equal(["Alpha", "Beta"], results.Items.Select(item => item.Title));
        Assert.All(results.Items, item => Assert.Equal(ProjectSearchKind.Definition, item.Kind));
    }

    [Fact]
    public void ExactTitleOutranksPrefixAndSubstringMatches()
    {
        var builder = new ProjectBuilder("rank");
        builder.Define("divider", "Clock divider");
        builder.Define("clock", "Clock");
        builder.Define("timer", "System clock monitor");
        CircuitProject project = builder.Build("clock");

        ProjectSearchResults results = ProjectSearch.Search(project, "clock", "clock");

        Assert.Equal("Clock", results.Items[0].Title);
        Assert.Equal("Clock divider", results.Items[1].Title);
        Assert.Equal("System clock monitor", results.Items[2].Title);
    }

    [Fact]
    public void SearchFindsDefinitionsInstancesAndNetsAcrossTheProject()
    {
        var builder = new ProjectBuilder("cross-project");
        DefinitionBuilder child = builder.Define("full-adder", "Full adder");
        child.AddPrimitive("sum", new LookupSpec(2, [0, 1, 1, 0]), label: "Sum gate");

        DefinitionBuilder parent = builder.Define("ripple", "Ripple carry");
        parent.AddInstance("fa1", "full-adder", label: "first stage");
        Net carry = parent.AddNet("carry_net", StandardDomains.BinaryId);
        parent.Rename(carry, "Carry out");
        CircuitProject project = builder.Build("ripple");

        ProjectSearchResult definition = Assert.Single(
            ProjectSearch.Search(project, "ripple", "full-adder").Items,
            item => item.Kind == ProjectSearchKind.Definition);
        Assert.Equal(ProjectSearchKind.Definition, definition.Kind);
        Assert.Equal("full-adder", definition.DefinitionId);

        ProjectSearchResult stage = ProjectSearch.Search(project, "ripple", "first stage").Items[0];
        Assert.Equal(ProjectSearchKind.Instance, stage.Kind);
        Assert.Equal("ripple", stage.DefinitionId);
        Assert.Equal("fa1", stage.InstanceId);
        Assert.Equal("in Ripple carry", stage.Context);

        ProjectSearchResult net = ProjectSearch.Search(project, "ripple", "carry out").Items[0];
        Assert.Equal(ProjectSearchKind.Net, net.Kind);
        Assert.Equal("carry_net", net.NetId);
        Assert.Equal("in Ripple carry", net.Context);

        ProjectSearchResult primitive = ProjectSearch.Search(project, "full-adder", "sum gate").Items[0];
        Assert.Equal(ProjectSearchKind.Instance, primitive.Kind);
        Assert.Equal("sum", primitive.InstanceId);
        Assert.Equal("full-adder", primitive.DefinitionId);
    }

    [Fact]
    public void EveryTermMustMatchAndTermsCanSpanDifferentFields()
    {
        var builder = new ProjectBuilder("terms");
        DefinitionBuilder adder = builder.Define("full-adder", "Full adder");
        adder.Definition.Description = "computes sum and carry";
        builder.Define("unrelated", "Unrelated");
        CircuitProject project = builder.Build();

        ProjectSearchResults match = ProjectSearch.Search(project, null, "adder carry");
        Assert.Contains(match.Items, item => item.DefinitionId == "full-adder");

        ProjectSearchResults mismatch = ProjectSearch.Search(project, null, "adder multiply");
        Assert.DoesNotContain(mismatch.Items, item => item.DefinitionId == "full-adder");
    }

    [Fact]
    public void ActiveDefinitionWinsTiesWithoutHidingMatchesElsewhere()
    {
        var builder = new ProjectBuilder("context");
        DefinitionBuilder first = builder.Define("first", "First");
        first.Rename(first.AddNet("first_clk", StandardDomains.BinaryId), "Clock");
        DefinitionBuilder second = builder.Define("second", "Second");
        second.Rename(second.AddNet("second_clk", StandardDomains.BinaryId), "Clock");
        CircuitProject project = builder.Build("first");

        ProjectSearchResults results = ProjectSearch.Search(project, "second", "clock");

        Assert.Equal(2, results.Items.Count);
        Assert.Equal("second_clk", results.Items[0].NetId);
        Assert.Equal("first_clk", results.Items[1].NetId);
    }

    [Fact]
    public void EqualScoresPreferDefinitionsThenInstancesThenNets()
    {
        var builder = new ProjectBuilder("kinds");
        DefinitionBuilder definition = builder.Define("carry", "Carry");
        definition.AddPrimitive("part", new ConstantSpec(0), label: "Carry");
        definition.Rename(definition.AddNet("net", StandardDomains.BinaryId), "Carry");
        CircuitProject project = builder.Build("carry");

        ProjectSearchResults results = ProjectSearch.Search(project, "carry", "carry");

        Assert.Equal(
            [ProjectSearchKind.Definition, ProjectSearchKind.Instance, ProjectSearchKind.Net],
            results.Items.Select(item => item.Kind));
    }

    [Fact]
    public void LimitCapsRowsButReportsEveryMatch()
    {
        var builder = new ProjectBuilder("limit");
        for (int i = 0; i < 6; i++)
        {
            builder.Define($"part{i}", $"Part {i}");
        }

        CircuitProject project = builder.Build("part0");
        ProjectSearchResults results = ProjectSearch.Search(project, "part0", "part", limit: 2);

        Assert.Equal(2, results.Items.Count);
        Assert.Equal(6, results.TotalMatches);
        Assert.True(results.IsTruncated);
        Assert.Equal(ProjectSearchResults.Empty.Items, ProjectSearch.Search(project, "part0", "part", limit: 0).Items);
    }

    [Fact]
    public void SearchIsDeterministicAndReadOnly()
    {
        CircuitProject project = ExampleProjects.CreateCounterDemo();
        string before = ProjectSerializer.SaveToString(project, includeView: true);

        ProjectSearchResults first = ProjectSearch.Search(project, project.RootDefinitionId, "clock");
        ProjectSearchResults second = ProjectSearch.Search(project, project.RootDefinitionId, "clock");
        string after = ProjectSerializer.SaveToString(project, includeView: true);

        Assert.Equal(first.Items, second.Items);
        Assert.Equal(before, after);
    }

    [Fact]
    public void PaletteNavigatesToAnInstanceInOneStep()
    {
        EditorViewModel viewModel = CreatePaletteEditor();

        viewModel.OpenSearch();
        Assert.True(viewModel.IsSearchOpen);
        Assert.True(viewModel.HasSearchResults);
        Assert.Contains("definitions", viewModel.SearchSummary);

        viewModel.SearchQuery = "carry gate";
        Assert.Equal(ProjectSearchKind.Instance, Assert.IsType<ProjectSearchItem>(viewModel.SelectedSearchItem).Result.Kind);
        viewModel.ActivateSelectedSearch();

        Assert.False(viewModel.IsSearchOpen);
        Assert.False(viewModel.HasSelectedNet);
        Assert.Equal("gate", viewModel.SelectedInstanceId);
        Assert.Equal("main", viewModel.Definition.Id);
        Assert.Contains("Found instance 'gate'", viewModel.StatusText);
    }

    [Fact]
    public void PaletteNavigatesToNetsAndDefinitions()
    {
        EditorViewModel viewModel = CreatePaletteEditor();

        viewModel.OpenSearch();
        viewModel.SearchQuery = "Clock net";
        viewModel.ActivateSelectedSearch();
        Assert.True(viewModel.HasSelectedNet);
        Assert.Equal("clock_net", viewModel.SelectedNetId);

        viewModel.OpenSearch();
        viewModel.SearchQuery = "other";
        viewModel.ActivateSelectedSearch();
        Assert.Equal("other", viewModel.Definition.Id);
        Assert.False(viewModel.IsSearchOpen);
        Assert.Contains("Found definition 'Other'", viewModel.StatusText);
    }

    [Fact]
    public void PaletteSelectionMovesWithinResultsAndClearsOnClose()
    {
        EditorViewModel viewModel = CreatePaletteEditor();
        viewModel.OpenSearch();
        viewModel.SearchQuery = "a";

        Assert.NotNull(viewModel.SelectedSearchItem);
        int count = viewModel.SearchResults.Count;
        viewModel.MoveSearchSelection(100);
        Assert.Same(viewModel.SearchResults[^1], viewModel.SelectedSearchItem);
        viewModel.MoveSearchSelection(-100);
        Assert.Same(viewModel.SearchResults[0], viewModel.SelectedSearchItem);

        viewModel.CloseSearch();
        Assert.False(viewModel.IsSearchOpen);
        Assert.Null(viewModel.SelectedSearchItem);
        Assert.Equal(count, viewModel.SearchResults.Count);
    }

    [Fact]
    public void OpeningANewProjectClosesThePalette()
    {
        EditorViewModel viewModel = CreatePaletteEditor();
        viewModel.OpenSearch();
        Assert.True(viewModel.IsSearchOpen);

        viewModel.NewProject();

        Assert.False(viewModel.IsSearchOpen);
        Assert.Null(viewModel.SelectedSearchItem);
    }

    private static EditorViewModel CreatePaletteEditor()
    {
        var builder = new ProjectBuilder("palette");
        DefinitionBuilder main = builder.Define("main", "Main");
        main.AddPrimitive("gate", new LookupSpec(1, [0, 1]), label: "carry gate");
        main.Rename(main.AddNet("clock_net", StandardDomains.BinaryId), "Clock net");
        builder.Define("other", "Other");
        return new EditorViewModel(builder.Build("main"));
    }
}
