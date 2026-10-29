using Radix.Core.Model;

namespace Radix.Core.Analysis;

/// <summary>
/// One parent-to-child definition relationship. Instances are grouped by the child
/// definition so the graph reports reference counts without losing authored order.
/// </summary>
public sealed record HierarchyEdge(
    string ParentDefinitionId,
    string ChildDefinitionId,
    IReadOnlyList<string> InstanceIds)
{
    public int Count => InstanceIds.Count;
}

/// <summary>
/// One definition in the project hierarchy, with structural counts and reverse
/// references. <see cref="Children"/> follows authored instance order and groups
/// repeated instantiations of the same definition.
/// </summary>
public sealed record HierarchyNode(
    string DefinitionId,
    string Name,
    bool IsRoot,
    int PinCount,
    int InstanceCount,
    int NetCount,
    int LaneCount,
    IReadOnlyList<HierarchyEdge> Children,
    int UsedBy,
    int InstanceUses);

/// <summary>
/// Read-only, deterministic definition graph for a project. Every definition is a
/// node, even when it is unreachable from the selected root; edges point from a parent
/// to each child definition it instantiates. Missing child definitions remain as
/// dangling edge targets so an invalid project can still be inspected, but they do not
/// become nodes. Definitions are ordered by authorship, and each parent's children are
/// ordered by the first authored instance of that child.
/// </summary>
public sealed class HierarchyGraph
{
    private HierarchyGraph(
        string projectName,
        string rootDefinitionId,
        IReadOnlyList<HierarchyNode> nodes,
        IReadOnlyList<HierarchyEdge> edges)
    {
        ProjectName = projectName;
        RootDefinitionId = rootDefinitionId;
        Nodes = nodes;
        Edges = edges;
    }

    public string ProjectName { get; }

    public string RootDefinitionId { get; }

    public IReadOnlyList<HierarchyNode> Nodes { get; }

    public IReadOnlyList<HierarchyEdge> Edges { get; }

    public HierarchyNode? Root => Find(RootDefinitionId);

    /// <summary>
    /// Builds the graph. The selected root defaults to the project root and is allowed
    /// to be missing, in which case the graph still lists every authored definition.
    /// </summary>
    public static HierarchyGraph Build(CircuitProject project, string? rootDefinitionId = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        string rootId = rootDefinitionId ?? project.RootDefinitionId;
        var edges = new List<HierarchyEdge>();
        var nodes = new List<HierarchyNode>(project.Definitions.Count);
        foreach (CircuitDefinition definition in project.Definitions)
        {
            var childOrder = new List<string>();
            var childInstances = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (ComponentInstance instance in definition.Instances)
            {
                if (instance.DefinitionId is not string childId)
                {
                    continue;
                }

                if (!childInstances.TryGetValue(childId, out List<string>? ids))
                {
                    ids = [];
                    childInstances[childId] = ids;
                    childOrder.Add(childId);
                }

                ids.Add(instance.Id);
            }

            var children = new List<HierarchyEdge>(childOrder.Count);
            foreach (string childId in childOrder)
            {
                var edge = new HierarchyEdge(definition.Id, childId, childInstances[childId].ToArray());
                children.Add(edge);
                edges.Add(edge);
            }

            nodes.Add(new HierarchyNode(
                definition.Id,
                definition.Name,
                string.Equals(definition.Id, rootId, StringComparison.Ordinal),
                definition.Pins.Count,
                definition.Instances.Count,
                definition.Nets.Count,
                definition.NumericLanes.Count,
                children,
                UsedBy: 0,
                InstanceUses: 0));
        }

        var usedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var instanceUses = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (HierarchyEdge edge in edges)
        {
            usedBy[edge.ChildDefinitionId] = usedBy.GetValueOrDefault(edge.ChildDefinitionId) + 1;
            instanceUses[edge.ChildDefinitionId] = instanceUses.GetValueOrDefault(edge.ChildDefinitionId) + edge.Count;
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            HierarchyNode node = nodes[i];
            nodes[i] = node with
            {
                UsedBy = usedBy.GetValueOrDefault(node.DefinitionId),
                InstanceUses = instanceUses.GetValueOrDefault(node.DefinitionId),
            };
        }

        return new HierarchyGraph(project.Name, rootId, nodes, edges);
    }

    public HierarchyNode? Find(string definitionId) =>
        Nodes.FirstOrDefault(node => string.Equals(node.DefinitionId, definitionId, StringComparison.Ordinal));

    public IEnumerable<HierarchyEdge> ChildrenOf(string definitionId) =>
        Edges.Where(edge => string.Equals(edge.ParentDefinitionId, definitionId, StringComparison.Ordinal));

    public IEnumerable<HierarchyEdge> ParentsOf(string definitionId) =>
        Edges.Where(edge => string.Equals(edge.ChildDefinitionId, definitionId, StringComparison.Ordinal));
}
