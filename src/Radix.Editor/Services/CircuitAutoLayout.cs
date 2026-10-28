using Avalonia;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Editor.Rendering;

namespace Radix.Editor.Services;

/// <summary>
/// Deterministic presentation layout for a definition body. Instances are grouped
/// into strongly connected components, because sequential feedback is a cycle in the
/// wiring graph but not a layout loop. The condensation is layered left-to-right by
/// longest path, then each layer is stacked in authored vertical order. Nothing here
/// understands clocks, arithmetic, or any other circuit concept.
/// </summary>
public static class CircuitAutoLayout
{
    public const double StartX = 320;
    public const double StartY = 80;
    public const double ColumnGap = 120;
    public const double RowGap = 40;

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
        var indegree = new int[componentCount];
        for (int component = 0; component < componentCount; component++)
        {
            componentSuccessors[component] = [];
        }

        for (int node = 0; node < count; node++)
        {
            int from = componentOf[node];
            foreach (int next in orderedSuccessors[node])
            {
                int to = componentOf[next];
                if (from != to && componentSuccessors[from].Add(to))
                {
                    indegree[to]++;
                }
            }
        }

        var layers = new int[componentCount];
        var ready = new PriorityQueue<int, int>();
        for (int component = 0; component < componentCount; component++)
        {
            if (indegree[component] == 0)
            {
                ready.Enqueue(component, component);
            }
        }

        int processed = 0;
        while (ready.TryDequeue(out int component, out _))
        {
            processed++;
            foreach (int next in componentSuccessors[component])
            {
                layers[next] = Math.Max(layers[next], layers[component] + 1);
                if (--indegree[next] == 0)
                {
                    ready.Enqueue(next, next);
                }
            }
        }

        if (processed != componentCount)
        {
            // A correctly condensed graph is acyclic. Keep this defensive fallback so
            // a bug cannot hang auto layout or leave unplaced instances.
            for (int component = 0; component < componentCount; component++)
            {
                layers[component] = Math.Max(layers[component], 0);
            }
        }

        var byLayer = new SortedDictionary<int, List<int>>();
        for (int node = 0; node < count; node++)
        {
            int layer = layers[componentOf[node]];
            if (!byLayer.TryGetValue(layer, out List<int>? nodes))
            {
                nodes = [];
                byLayer[layer] = nodes;
            }

            nodes.Add(node);
        }

        var positions = new Dictionary<string, Point>(count, StringComparer.Ordinal);
        foreach ((int layer, List<int> nodes) in byLayer)
        {
            nodes.Sort((left, right) =>
            {
                int byY = definition.Instances[left].Y.CompareTo(definition.Instances[right].Y);
                return byY != 0
                    ? byY
                    : string.CompareOrdinal(definition.Instances[left].Id, definition.Instances[right].Id);
            });

            double x = StartX + (layer * (CircuitLayout.BoxWidth + ColumnGap));
            double y = StartY;
            foreach (int node in nodes)
            {
                ComponentInstance instance = definition.Instances[node];
                positions[instance.Id] = new Point(x, y);
                double height = CircuitLayout.MeasureHeight(project.GetInstancePins(instance));
                y += height + RowGap;
            }
        }

        int depth = byLayer.Count == 0 ? 0 : byLayer.Keys.Max() + 1;
        return new AutoLayoutResult(positions, depth);
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
