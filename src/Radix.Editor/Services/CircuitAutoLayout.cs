using Avalonia;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Editor.Rendering;

namespace Radix.Editor.Services;

/// <summary>
/// Deterministic presentation layout for a definition body. Instances are grouped
/// into strongly connected components, because sequential feedback is a cycle in the
/// wiring graph but not a layout loop. The condensation is layered left-to-right by
/// longest path, then each layer is ordered by barycenter sweeps to reduce crossings.
/// Authored position and ID break every tie, so repeated runs are stable. Nothing here
/// understands clocks, arithmetic, or any other circuit concept.
/// </summary>
public static class CircuitAutoLayout
{
    public const double StartX = 320;
    public const double StartY = 80;
    public const double ColumnGap = 120;
    public const double RowGap = 40;

    private const int CrossingReductionPasses = 4;

    public static AutoLayoutResult Arrange(CircuitProject project, CircuitDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(definition);

        int count = definition.Instances.Count;
        if (count == 0)
        {
            return new AutoLayoutResult(new Dictionary<string, Point>(StringComparer.Ordinal), 0);
        }

        var indexById = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            indexById[definition.Instances[i].Id] = i;
        }

        var successors = new HashSet<int>[count];
        for (int i = 0; i < count; i++)
        {
            successors[i] = [];
        }

        foreach (Net net in definition.Nets)
        {
            var drivers = new List<int>();
            var sinks = new List<int>();
            foreach (PinRef pinRef in net.Pins)
            {
                if (!indexById.TryGetValue(pinRef.InstanceId, out int index))
                {
                    continue;
                }

                ComponentInstance instance = definition.Instances[index];
                PinDescriptor? descriptor = project.FindInstancePin(instance, pinRef.PinId);
                if (descriptor is null)
                {
                    continue;
                }

                if (descriptor.Value.Direction == PinDirection.Output)
                {
                    drivers.Add(index);
                }
                else
                {
                    sinks.Add(index);
                }
            }

            foreach (int driver in drivers.Distinct())
            {
                foreach (int sink in sinks.Distinct())
                {
                    if (driver != sink)
                    {
                        successors[driver].Add(sink);
                    }
                }
            }
        }

        int[][] orderedSuccessors = successors.Select(edges => edges.Order().ToArray()).ToArray();
        List<List<int>> components = StronglyConnectedComponents(count, orderedSuccessors);
        int componentCount = components.Count;
        var componentOf = new int[count];
        for (int component = 0; component < componentCount; component++)
        {
            foreach (int node in components[component])
            {
                componentOf[node] = component;
            }
        }

        var componentSuccessors = new HashSet<int>[componentCount];
        var componentPredecessors = new HashSet<int>[componentCount];
        var indegree = new int[componentCount];
        for (int component = 0; component < componentCount; component++)
        {
            componentSuccessors[component] = [];
            componentPredecessors[component] = [];
        }

        for (int node = 0; node < count; node++)
        {
            int from = componentOf[node];
            foreach (int next in orderedSuccessors[node])
            {
                int to = componentOf[next];
                if (from == to)
                {
                    continue;
                }

                if (componentSuccessors[from].Add(to))
                {
                    indegree[to]++;
                }

                componentPredecessors[to].Add(from);
            }
        }

        int[][] orderedComponentSuccessors =
            componentSuccessors.Select(edges => edges.Order().ToArray()).ToArray();
        int[][] orderedComponentPredecessors =
            componentPredecessors.Select(edges => edges.Order().ToArray()).ToArray();

        var componentLayers = new int[componentCount];
        var ready = new PriorityQueue<int, int>();
        for (int component = 0; component < componentCount; component++)
        {
            if (indegree[component] == 0)
            {
                ready.Enqueue(component, component);
            }
        }

        while (ready.TryDequeue(out int component, out _))
        {
            foreach (int next in orderedComponentSuccessors[component])
            {
                componentLayers[next] = Math.Max(componentLayers[next], componentLayers[component] + 1);
                if (--indegree[next] == 0)
                {
                    ready.Enqueue(next, next);
                }
            }
        }

        int layerCount = componentLayers.Length == 0 ? 0 : componentLayers.Max() + 1;
        var layerComponents = new List<int>[layerCount];
        for (int layer = 0; layer < layerCount; layer++)
        {
            layerComponents[layer] = [];
        }

        for (int component = 0; component < componentCount; component++)
        {
            layerComponents[componentLayers[component]].Add(component);
        }

        // Members of one SCC stay contiguous. Without this, two feedback parts could
        // be split by an unrelated component that happened to sit between their Y
        // coordinates, defeating both grouping and crossing reduction.
        var componentMembers = new int[componentCount][];
        var componentTopY = new double[componentCount];
        var componentTopId = new string[componentCount];
        for (int component = 0; component < componentCount; component++)
        {
            componentMembers[component] = components[component]
                .OrderBy(node => definition.Instances[node].Y)
                .ThenBy(node => definition.Instances[node].Id, StringComparer.Ordinal)
                .ToArray();
            ComponentInstance top = definition.Instances[componentMembers[component][0]];
            componentTopY[component] = top.Y;
            componentTopId[component] = top.Id;
        }

        foreach (List<int> layer in layerComponents)
        {
            layer.Sort((left, right) =>
            {
                int byY = componentTopY[left].CompareTo(componentTopY[right]);
                if (byY != 0)
                {
                    return byY;
                }

                int byId = string.CompareOrdinal(componentTopId[left], componentTopId[right]);
                return byId != 0 ? byId : left.CompareTo(right);
            });
        }

        List<List<int>> bestLayers = CloneLayers(layerComponents);
        int bestCrossings = CountCrossings(bestLayers, orderedComponentSuccessors);
        for (int pass = 0; pass < CrossingReductionPasses && layerCount > 1; pass++)
        {
            var orderIndex = BuildOrderIndex(layerComponents);
            for (int layer = 1; layer < layerCount; layer++)
            {
                OrderLayerByBarycenter(
                    layerComponents[layer],
                    orderedComponentPredecessors,
                    componentLayers,
                    layer - 1,
                    orderIndex);
            }

            ConsiderOrder(layerComponents, orderedComponentSuccessors, ref bestLayers, ref bestCrossings);

            orderIndex = BuildOrderIndex(layerComponents);
            for (int layer = layerCount - 2; layer >= 0; layer--)
            {
                OrderLayerByBarycenter(
                    layerComponents[layer],
                    orderedComponentSuccessors,
                    componentLayers,
                    layer + 1,
                    orderIndex);
            }

            ConsiderOrder(layerComponents, orderedComponentSuccessors, ref bestLayers, ref bestCrossings);
        }

        var positions = new Dictionary<string, Point>(count, StringComparer.Ordinal);
        for (int layer = 0; layer < layerCount; layer++)
        {
            double x = StartX + (layer * (CircuitLayout.BoxWidth + ColumnGap));
            double y = StartY;
            foreach (int component in bestLayers[layer])
            {
                foreach (int node in componentMembers[component])
                {
                    ComponentInstance instance = definition.Instances[node];
                    positions[instance.Id] = new Point(x, y);
                    double height = CircuitLayout.MeasureHeight(project.GetInstancePins(instance));
                    y += height + RowGap;
                }
            }
        }

        return new AutoLayoutResult(positions, layerCount);
    }

    private static List<List<int>> CloneLayers(IReadOnlyList<List<int>> layers) =>
        layers.Select(layer => layer.ToList()).ToList();

    private static int[] BuildOrderIndex(IReadOnlyList<List<int>> layers)
    {
        int count = layers.Sum(layer => layer.Count);
        var index = new int[count];
        foreach (List<int> layer in layers)
        {
            for (int position = 0; position < layer.Count; position++)
            {
                index[layer[position]] = position;
            }
        }

        return index;
    }

    /// <summary>
    /// Sorts one layer by the average position of its neighbors in the adjacent
    /// layer. A component with no neighbor in that layer keeps its current index,
    /// which prevents unrelated parts from drifting between passes.
    /// </summary>
    private static void OrderLayerByBarycenter(
        List<int> layer,
        IReadOnlyList<int[]> adjacency,
        IReadOnlyList<int> componentLayers,
        int neighborLayer,
        int[] orderIndex)
    {
        var keyed = new (int Component, int Current, double Score)[layer.Count];
        for (int current = 0; current < layer.Count; current++)
        {
            int component = layer[current];
            double sum = 0;
            int neighbors = 0;
            foreach (int next in adjacency[component])
            {
                if (componentLayers[next] == neighborLayer)
                {
                    sum += orderIndex[next];
                    neighbors++;
                }
            }

            keyed[current] = (component, current, neighbors == 0 ? current : sum / neighbors);
        }

        Array.Sort(keyed, (left, right) =>
        {
            int byScore = left.Score.CompareTo(right.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            int byCurrent = left.Current.CompareTo(right.Current);
            return byCurrent != 0 ? byCurrent : left.Component.CompareTo(right.Component);
        });

        for (int i = 0; i < keyed.Length; i++)
        {
            layer[i] = keyed[i].Component;
            orderIndex[layer[i]] = i;
        }
    }

    private static void ConsiderOrder(
        IReadOnlyList<List<int>> layers,
        IReadOnlyList<int[]> successors,
        ref List<List<int>> bestLayers,
        ref int bestCrossings)
    {
        int crossings = CountCrossings(layers, successors);
        if (crossings >= bestCrossings)
        {
            return;
        }

        bestCrossings = crossings;
        bestLayers = CloneLayers(layers);
    }

    /// <summary>
    /// Counts crossings between every adjacent pair of layers. Edges are grouped by
    /// their upper endpoint and a Fenwick tree counts already-seen lower positions,
    /// so the cost is O(E log V) rather than the naive O(E²).
    /// </summary>
    private static int CountCrossings(
        IReadOnlyList<List<int>> layers,
        IReadOnlyList<int[]> successors)
    {
        int total = 0;
        for (int layer = 0; layer + 1 < layers.Count; layer++)
        {
            List<int> upper = layers[layer];
            List<int> lower = layers[layer + 1];
            var lowerPosition = new Dictionary<int, int>(lower.Count);
            for (int position = 0; position < lower.Count; position++)
            {
                lowerPosition[lower[position]] = position;
            }

            var tree = new int[lower.Count + 1];
            int seen = 0;
            for (int upperPosition = 0; upperPosition < upper.Count; upperPosition++)
            {
                var targets = new List<int>();
                foreach (int next in successors[upper[upperPosition]])
                {
                    if (lowerPosition.TryGetValue(next, out int target))
                    {
                        targets.Add(target);
                    }
                }

                if (targets.Count == 0)
                {
                    continue;
                }

                targets.Sort();
                foreach (int target in targets)
                {
                    total += seen - PrefixSum(tree, target + 1);
                }

                foreach (int target in targets)
                {
                    AddToTree(tree, target + 1);
                    seen++;
                }
            }
        }

        return total;
    }

    private static int PrefixSum(int[] tree, int index)
    {
        int sum = 0;
        for (int i = index; i > 0; i -= i & -i)
        {
            sum += tree[i];
        }

        return sum;
    }

    private static void AddToTree(int[] tree, int index)
    {
        for (int i = index; i < tree.Length; i += i & -i)
        {
            tree[i]++;
        }
    }

    /// <summary>Iterative Tarjan SCC so large definitions cannot overflow the stack.</summary>
    private static List<List<int>> StronglyConnectedComponents(
        int count,
        IReadOnlyList<int[]> successors)
    {
        var index = new int[count];
        var low = new int[count];
        var onStack = new bool[count];
        Array.Fill(index, -1);
        var stack = new Stack<int>();
        var components = new List<List<int>>();
        int nextIndex = 0;

        for (int start = 0; start < count; start++)
        {
            if (index[start] >= 0)
            {
                continue;
            }

            var callStack = new Stack<(int Node, int EdgeIndex)>();
            index[start] = low[start] = nextIndex++;
            stack.Push(start);
            onStack[start] = true;
            callStack.Push((start, 0));

            while (callStack.Count > 0)
            {
                (int node, int edgeIndex) = callStack.Pop();
                if (edgeIndex < successors[node].Length)
                {
                    int next = successors[node][edgeIndex];
                    callStack.Push((node, edgeIndex + 1));
                    if (index[next] < 0)
                    {
                        index[next] = low[next] = nextIndex++;
                        stack.Push(next);
                        onStack[next] = true;
                        callStack.Push((next, 0));
                    }
                    else if (onStack[next])
                    {
                        low[node] = Math.Min(low[node], index[next]);
                    }

                    continue;
                }

                if (low[node] == index[node])
                {
                    var component = new List<int>();
                    int member;
                    do
                    {
                        member = stack.Pop();
                        onStack[member] = false;
                        component.Add(member);
                    }
                    while (member != node);

                    component.Sort();
                    components.Add(component);
                }

                if (callStack.Count > 0)
                {
                    int parent = callStack.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
            }
        }

        return components;
    }
}

/// <summary>New body positions and the number of occupied left-to-right layers.</summary>
public sealed record AutoLayoutResult(IReadOnlyDictionary<string, Point> Positions, int Layers);
