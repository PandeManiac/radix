using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radix.Core.Analysis;

/// <summary>
/// Stable presentations of a <see cref="HierarchyGraph"/>: a bounded indented tree for
/// terminals, a JSON document for tools, and Graphviz DOT for diagrams. All formats are
/// pure projections of the graph and never read or mutate simulation state.
/// </summary>
public static class HierarchyReport
{
    public const string FormatName = "radix-hierarchy-report";
    public const int FormatVersion = 1;
    public const int DefaultDepth = 6;
    public const int MaxDepth = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Renders the hierarchy from its selected root. A definition's children are shown
    /// once; a later reference is marked <c>(shown above)</c> instead of repeating a
    /// shared subtree. The depth limit counts root as depth zero.
    /// </summary>
    public static string ToText(HierarchyGraph graph, int maxDepth = DefaultDepth)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        if (maxDepth > MaxDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), $"Depth must be at most {MaxDepth}.");
        }

        var text = new StringBuilder();
        text.Append("Project: ").AppendLine(graph.ProjectName);
        text.Append("Root:    ").AppendLine(graph.RootDefinitionId);
        text.Append("Graph:   ")
            .Append(graph.Nodes.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" definitions, ")
            .Append(graph.Edges.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" references");
        text.AppendLine();

        HierarchyNode? root = graph.Root;
        if (root is null)
        {
            text.Append("Root [").Append(graph.RootDefinitionId).AppendLine("] — missing definition");
            return text.ToString();
        }

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        WriteNode(root, depth: 0, null);
        return text.ToString();

        void WriteNode(HierarchyNode node, int depth, int? referenceCount)
        {
            bool repeated = depth > 0 && expanded.Contains(node.DefinitionId);
            string prefix = new(' ', depth * 2);
            text.Append(prefix)
                .Append(node.Name)
                .Append(" [").Append(node.DefinitionId).Append(']');
            if (referenceCount is int count)
            {
                text.Append(" ×").Append(count.ToString(CultureInfo.InvariantCulture));
            }

            text.Append(" — pins ")
                .Append(node.PinCount.ToString(CultureInfo.InvariantCulture)).Append(" · parts ")
                .Append(node.InstanceCount.ToString(CultureInfo.InvariantCulture)).Append(" · nets ")
                .Append(node.NetCount.ToString(CultureInfo.InvariantCulture));
            if (node.LaneCount > 0)
            {
                text.Append(" · lanes ").Append(node.LaneCount.ToString(CultureInfo.InvariantCulture));
            }

            if (repeated)
            {
                text.Append(" (shown above)");
            }
            else if (depth >= maxDepth && node.Children.Count > 0)
            {
                text.Append(" (depth limit)");
            }

            text.AppendLine();
            if (repeated || depth >= maxDepth)
            {
                return;
            }

            expanded.Add(node.DefinitionId);
            foreach (HierarchyEdge edge in node.Children)
            {
                if (graph.Find(edge.ChildDefinitionId) is not HierarchyNode child)
                {
                    text.Append(new string(' ', (depth + 1) * 2))
                        .Append(edge.ChildDefinitionId)
                        .Append(" ×").Append(edge.Count.ToString(CultureInfo.InvariantCulture))
                        .AppendLine(" — missing definition");
                    continue;
                }

                WriteNode(child, depth + 1, edge.Count);
            }
        }
    }

    public static string ToJson(HierarchyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var report = new GraphDto(
            FormatName,
            FormatVersion,
            graph.ProjectName,
            graph.RootDefinitionId,
            graph.Nodes.Count,
            graph.Edges.Count,
            graph.Nodes.Select(node => new NodeDto(
                node.DefinitionId,
                node.Name,
                node.IsRoot,
                node.PinCount,
                node.InstanceCount,
                node.NetCount,
                node.LaneCount,
                node.UsedBy,
                node.InstanceUses)).ToArray(),
            graph.Edges.Select(edge => new EdgeDto(
                edge.ParentDefinitionId,
                edge.ChildDefinitionId,
                edge.Count,
                edge.InstanceIds)).ToArray());
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string ToDot(HierarchyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var dot = new StringBuilder();
        dot.AppendLine("digraph radix {");
        dot.AppendLine("  rankdir=LR;");
        dot.AppendLine("  node [shape=box, fontname=\"monospace\"];");

        foreach (HierarchyNode node in graph.Nodes)
        {
            string label = string.Create(
                CultureInfo.InvariantCulture,
                $"{node.Name}\n{node.DefinitionId}\n{Count(node.PinCount, "pin")} · {Count(node.InstanceCount, "part")} · {Count(node.NetCount, "net")}");
            dot.Append("  \"").Append(Escape(node.DefinitionId)).Append("\" [label=\"")
                .Append(Escape(label)).Append('"');
            if (node.IsRoot)
            {
                dot.Append(", peripheries=2");
            }

            dot.AppendLine("];");
        }

        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (HierarchyEdge edge in graph.Edges)
        {
            if (graph.Find(edge.ChildDefinitionId) is null && missing.Add(edge.ChildDefinitionId))
            {
                dot.Append("  \"").Append(Escape(edge.ChildDefinitionId))
                    .Append("\" [label=\"").Append(Escape($"missing\n{edge.ChildDefinitionId}"))
                    .AppendLine("\", style=dashed];");
            }

            dot.Append("  \"").Append(Escape(edge.ParentDefinitionId))
                .Append("\" -> \"").Append(Escape(edge.ChildDefinitionId))
                .Append("\" [label=\"×").Append(edge.Count.ToString(CultureInfo.InvariantCulture))
                .AppendLine("\"];");
        }

        dot.AppendLine("}");
        return dot.ToString();
    }

    private static string Count(int value, string noun) =>
        value == 1
            ? string.Create(CultureInfo.InvariantCulture, $"1 {noun}")
            : string.Create(CultureInfo.InvariantCulture, $"{value} {noun}s");

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed record GraphDto(
        string Format,
        int Version,
        string Project,
        string RootDefinitionId,
        int Definitions,
        int References,
        IReadOnlyList<NodeDto> Nodes,
        IReadOnlyList<EdgeDto> Edges);

    private sealed record NodeDto(
        string Id,
        string Name,
        bool Root,
        int Pins,
        int Parts,
        int Nets,
        int Lanes,
        int UsedBy,
        int InstanceUses);

    private sealed record EdgeDto(
        string Parent,
        string Child,
        int Count,
        IReadOnlyList<string> Instances);
}
