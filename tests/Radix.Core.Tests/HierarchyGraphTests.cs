using System.Text.Json;
using Radix.Core.Analysis;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

public sealed class HierarchyGraphTests
{
    [Fact]
    public void BuildGroupsRepeatedInstancesAndCountsReverseReferences()
    {
        CircuitProject project = BuildExample(out _);
        HierarchyGraph graph = HierarchyGraph.Build(project);

        Assert.Equal("root", graph.RootDefinitionId);
        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(3, graph.Edges.Count);

        HierarchyNode leaf = Assert.IsType<HierarchyNode>(graph.Find("leaf"));
        Assert.False(leaf.IsRoot);
        Assert.Equal(1, leaf.InstanceCount);
        Assert.Equal(2, leaf.UsedBy);
        Assert.Equal(3, leaf.InstanceUses);

        HierarchyEdge midLeaf = Assert.Single(graph.ChildrenOf("mid"));
        Assert.Equal(("mid", "leaf"), (midLeaf.ParentDefinitionId, midLeaf.ChildDefinitionId));
        Assert.Equal(["l1", "l2"], midLeaf.InstanceIds);
        Assert.Equal(2, midLeaf.Count);

        HierarchyNode root = Assert.IsType<HierarchyNode>(graph.Root);
        Assert.True(root.IsRoot);
        Assert.Equal(["mid", "leaf"], root.Children.Select(edge => edge.ChildDefinitionId));
        Assert.Equal(0, root.UsedBy);
        Assert.Equal(["l1", "l2", "l0"], graph.Edges
            .Where(edge => edge.ChildDefinitionId == "leaf")
            .SelectMany(edge => edge.InstanceIds));
        Assert.Equal(2, graph.ParentsOf("leaf").Count());
    }

    [Fact]
    public void TextReportUsesDeterministicLineFeedEndings()
    {
        string text = HierarchyReport.ToText(HierarchyGraph.Build(BuildExample(out _)));

        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TreeMarksSharedSubtreeOnceAndHonorsDepthLimit()
    {
        CircuitProject project = BuildExample(out _);
        HierarchyGraph graph = HierarchyGraph.Build(project);

        string text = HierarchyReport.ToText(graph);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Project: graph", lines[0]);
        Assert.Contains("Root [root] — pins 0 · parts 2 · nets 0", lines);
        Assert.Contains("  Middle [mid] ×1 — pins 0 · parts 2 · nets 0", lines);
        Assert.Contains("    Leaf [leaf] ×2 — pins 0 · parts 1 · nets 0", lines);
        Assert.Contains("  Leaf [leaf] ×1 — pins 0 · parts 1 · nets 0 (shown above)", lines);

        string shallow = HierarchyReport.ToText(graph, maxDepth: 1);
        Assert.Contains("  Middle [mid] ×1 — pins 0 · parts 2 · nets 0 (depth limit)", shallow.Split('\n'));
        Assert.DoesNotContain("    Leaf [leaf] ×2", shallow);
    }

    [Fact]
    public void JsonReportIsStableAndCarriesNodesEdgesAndReverseCounts()
    {
        CircuitProject project = BuildExample(out _);
        HierarchyGraph graph = HierarchyGraph.Build(project);

        string first = HierarchyReport.ToJson(graph);
        string second = HierarchyReport.ToJson(HierarchyGraph.Build(project));
        Assert.Equal(first, second);

        using JsonDocument document = JsonDocument.Parse(first);
        JsonElement root = document.RootElement;
        Assert.Equal(HierarchyReport.FormatName, root.GetProperty("format").GetString());
        Assert.Equal(HierarchyReport.FormatVersion, root.GetProperty("version").GetInt32());
        Assert.Equal("graph", root.GetProperty("project").GetString());
        Assert.Equal("root", root.GetProperty("rootDefinitionId").GetString());
        Assert.Equal(4, root.GetProperty("definitions").GetInt32());
        Assert.Equal(3, root.GetProperty("references").GetInt32());

        JsonElement leaf = Assert.Single(
            root.GetProperty("nodes").EnumerateArray(),
            node => node.GetProperty("id").GetString() == "leaf");
        Assert.False(leaf.GetProperty("root").GetBoolean());
        Assert.Equal(2, leaf.GetProperty("usedBy").GetInt32());
        Assert.Equal(3, leaf.GetProperty("instanceUses").GetInt32());

        JsonElement edge = Assert.Single(
            root.GetProperty("edges").EnumerateArray(),
            item => item.GetProperty("parent").GetString() == "mid");
        Assert.Equal("leaf", edge.GetProperty("child").GetString());
        Assert.Equal(2, edge.GetProperty("count").GetInt32());
        Assert.Equal(["l1", "l2"], edge.GetProperty("instances").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void DotReportEscapesLabelsAndDeclaresMissingDefinitions()
    {
        CircuitProject project = BuildExample(out DefinitionBuilder rootBuilder);
        rootBuilder.Definition.Name = "Root \"quoted\"";
        rootBuilder.Definition.Instances.Add(new ComponentInstance
        {
            Id = "ghost_use",
            DefinitionId = "ghost",
        });
        HierarchyGraph graph = HierarchyGraph.Build(project);

        string dot = HierarchyReport.ToDot(graph);

        Assert.StartsWith("digraph radix {", dot, StringComparison.Ordinal);
        Assert.Contains("label=\"Root \\\"quoted\\\"\\nroot\\n0 pins · 3 parts · 0 nets\", peripheries=2", dot);
        Assert.Contains("\"ghost\" [label=\"missing\\nghost\", style=dashed];", dot);
        Assert.Contains("\"root\" -> \"ghost\" [label=\"×1\"];", dot);
    }

    [Fact]
    public void MissingRootAndMissingChildRemainVisible()
    {
        CircuitProject project = BuildExample(out DefinitionBuilder rootBuilder);
        rootBuilder.Definition.Instances.Add(new ComponentInstance
        {
            Id = "ghost_use",
            DefinitionId = "ghost",
        });
        HierarchyGraph graph = HierarchyGraph.Build(project);
        Assert.Contains("  ghost ×1 — missing definition", HierarchyReport.ToText(graph));

        HierarchyGraph missingRoot = HierarchyGraph.Build(project, "ghost");
        Assert.Null(missingRoot.Root);
        Assert.Contains("Root [ghost] — missing definition", HierarchyReport.ToText(missingRoot));

        string json = HierarchyReport.ToJson(missingRoot);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("ghost", document.RootElement.GetProperty("rootDefinitionId").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("definitions").GetInt32());
        Assert.Contains(
            document.RootElement.GetProperty("edges").EnumerateArray(),
            edge => edge.GetProperty("child").GetString() == "ghost");
    }

    [Fact]
    public void GraphIsReadOnlyAndDeterministic()
    {
        CircuitProject project = BuildExample(out _);
        string before = ProjectSerializer.SaveToString(project, includeView: true);

        HierarchyGraph first = HierarchyGraph.Build(project);
        HierarchyGraph second = HierarchyGraph.Build(project);
        string after = ProjectSerializer.SaveToString(project, includeView: true);

        Assert.Equal(before, after);
        Assert.Equal(HierarchyReport.ToJson(first), HierarchyReport.ToJson(second));
        Assert.Equal(HierarchyReport.ToText(first), HierarchyReport.ToText(second));
        Assert.Equal(HierarchyReport.ToDot(first), HierarchyReport.ToDot(second));
    }

    private static CircuitProject BuildExample(out DefinitionBuilder rootBuilder)
    {
        var builder = new ProjectBuilder("graph");
        DefinitionBuilder leaf = builder.Define("leaf", "Leaf");
        leaf.AddPrimitive("constant", new ConstantSpec(0));

        DefinitionBuilder mid = builder.Define("mid", "Middle");
        mid.AddInstance("l1", "leaf");
        mid.AddInstance("l2", "leaf");

        rootBuilder = builder.Define("root", "Root");
        rootBuilder.AddInstance("m1", "mid");
        rootBuilder.AddInstance("l0", "leaf");

        builder.Define("unused", "Unused");
        return builder.Build("root");
    }
}
