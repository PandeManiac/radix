using Avalonia;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Editor.ViewModels;

namespace Radix.Editor.Rendering;

public static class PrimitiveDisplay
{
    public static string Name(PrimitiveSpec spec) => spec switch
    {
        ConstantSpec => "Constant",
        LookupSpec => "Lookup",
        DelaySpec => "Delay",
        WaveformSpec => "Waveform",
        ProbeSpec => "Probe",
        _ => spec.Kind,
    };
}

public sealed record PinVisual(
    string PinId,
    string Name,
    PinDirection Direction,
    Point Position,
    string? DeclaredDomain);

public sealed class ComponentBox
{
    public ComponentBox(
        ComponentInstance instance,
        string title,
        Rect bounds,
        IReadOnlyList<PinVisual> pins)
    {
        Instance = instance;
        Title = title;
        Bounds = bounds;
        Pins = pins;
        Inputs = pins.Where(p => p.Direction == PinDirection.Input).ToArray();
        Outputs = pins.Where(p => p.Direction == PinDirection.Output).ToArray();
    }

    public ComponentInstance Instance { get; }

    public string Title { get; }

    public Rect Bounds { get; }

    public IReadOnlyList<PinVisual> Pins { get; }

    public IReadOnlyList<PinVisual> Inputs { get; }

    public IReadOnlyList<PinVisual> Outputs { get; }

    public PinVisual? FindPin(string pinId) =>
        Pins.FirstOrDefault(p => string.Equals(p.PinId, pinId, StringComparison.Ordinal));
}

public sealed class PortBox
{
    public PortBox(PinDeclaration pin, Rect bounds, string netId, Point connector, int extendDirection)
    {
        Pin = pin;
        Bounds = bounds;
        NetId = netId;
        Connector = connector;
        ExtendDirection = extendDirection;
    }

    public PinDeclaration Pin { get; }

    public Rect Bounds { get; }

    public string NetId { get; }

    public Point Connector { get; }

    public int ExtendDirection { get; }
}

public sealed class NetWire
{
    public NetWire(
        string netId,
        string domainId,
        IReadOnlyList<Point> points,
        IReadOnlyList<(Point A, Point B)> segments,
        IReadOnlyList<Point> junctions,
        IReadOnlyList<Point> breaks,
        Point labelPoint,
        IReadOnlyList<EditorPin> endpoints,
        IReadOnlyList<NetWaypoint> routePoints)
    {
        NetId = netId;
        DomainId = domainId;
        Points = points;
        Segments = segments;
        Junctions = junctions;
        Breaks = breaks;
        LabelPoint = labelPoint;
        Endpoints = endpoints;
        RoutePoints = routePoints;
    }

    public string NetId { get; }

    public string DomainId { get; }

    public IReadOnlyList<Point> Points { get; }

    public IReadOnlyList<(Point A, Point B)> Segments { get; }

    /// <summary>Branch points only; a crossing is never a junction.</summary>
    public IReadOnlyList<Point> Junctions { get; }

    /// <summary>Points where a different net's wire visually bridges over this one.</summary>
    public IReadOnlyList<Point> Breaks { get; internal set; }

    public Point LabelPoint { get; }

    public IReadOnlyList<EditorPin> Endpoints { get; }

    /// <summary>Author bendpoints, drawn as draggable handles when the net is selected.</summary>
    public IReadOnlyList<NetWaypoint> RoutePoints { get; }
}

public readonly record struct PinHit(EditorPin Pin, Point Position, PinDirection Direction, string? DeclaredDomain);

public sealed class CircuitLayout
{
    public const double BoxWidth = 210;
    public const double HeaderHeight = 34;
    public const double PinSpacing = 22;
    public const double BoxBottomPadding = 12;

    private CircuitLayout(
        CircuitDefinition definition,
        IReadOnlyList<ComponentBox> components,
        IReadOnlyList<PortBox> ports,
        IReadOnlyList<NetWire> wires,
        Rect contentBounds)
    {
        Definition = definition;
        Components = components;
        Ports = ports;
        Wires = wires;
        ContentBounds = contentBounds;
    }

    public CircuitDefinition Definition { get; }

    public IReadOnlyList<ComponentBox> Components { get; }

    public IReadOnlyList<PortBox> Ports { get; }

    public IReadOnlyList<NetWire> Wires { get; }

    public Rect ContentBounds { get; }

    public NetWire? FindWire(string netId) =>
        Wires.FirstOrDefault(w => string.Equals(w.NetId, netId, StringComparison.Ordinal));

    public IReadOnlyCollection<string> NetsForInstance(ComponentInstance instance)
    {
        var nets = new HashSet<string>(StringComparer.Ordinal);
        foreach (Net net in Definition.Nets)
        {
            if (net.Pins.Any(p => string.Equals(p.InstanceId, instance.Id, StringComparison.Ordinal)))
            {
                nets.Add(net.Id);
            }
        }

        return nets;
    }

    public IReadOnlyCollection<string> NetsForPin(EditorPin pin)
    {
        var nets = new HashSet<string>(StringComparer.Ordinal);
        foreach (Net net in Definition.Nets)
        {
            bool match = pin.Kind == EditorPinKind.Port
                ? Definition.FindPortNet(pin.PinId)?.Id == net.Id
                : net.Pins.Any(p =>
                    string.Equals(p.InstanceId, pin.OwnerId, StringComparison.Ordinal) &&
                    string.Equals(p.PinId, pin.PinId, StringComparison.Ordinal));
            if (match)
            {
                nets.Add(net.Id);
            }
        }

        return nets;
    }

    /// <summary>
    /// Builds layout geometry. Passing the previous layout for the same definition
    /// allows unchanged nets to reuse their routes, which keeps dragging responsive;
    /// callers that need a canonical route should rebuild without it.
    /// </summary>
    public static CircuitLayout Build(CircuitDefinition definition, CircuitProject project, CircuitLayout? previous = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(project);

        var components = new List<ComponentBox>(definition.Instances.Count);
        foreach (ComponentInstance instance in definition.Instances)
        {
            IReadOnlyList<PinDescriptor> descriptors = project.GetInstancePins(instance);
            string title = instance.Label ?? TitleFor(instance, project);
            int rows = Math.Max(1, Math.Max(
                descriptors.Count(p => p.Direction == PinDirection.Input),
                descriptors.Count(p => p.Direction == PinDirection.Output)));
            double height = HeaderHeight + (rows * PinSpacing) + BoxBottomPadding;
            var bounds = new Rect(instance.X, instance.Y, BoxWidth, height);

            var pins = new List<PinVisual>(descriptors.Count);
            int inputIndex = 0;
            int outputIndex = 0;
            foreach (PinDescriptor descriptor in descriptors)
            {
                Point position = descriptor.Direction == PinDirection.Input
                    ? new Point(bounds.Left, bounds.Top + HeaderHeight + 10 + (inputIndex++ * PinSpacing))
                    : new Point(bounds.Right, bounds.Top + HeaderHeight + 10 + (outputIndex++ * PinSpacing));
                pins.Add(new PinVisual(descriptor.Id, DisplayPinName(instance, descriptor), descriptor.Direction, position, descriptor.DomainId));
            }

            components.Add(new ComponentBox(instance, title, bounds, pins));
        }

        Rect componentBounds = components.Count == 0
            ? new Rect(380, 140, BoxWidth, 80)
            : components.Select(c => c.Bounds).Aggregate((a, b) => a.Union(b));

        var ports = new List<PortBox>();
        double inputX = components.Count == 0 ? 40 : componentBounds.Left - 280;
        double outputX = components.Count == 0 ? componentBounds.Right + 80 : componentBounds.Right + 80;
        double portTop = Math.Max(40, componentBounds.Top);
        int inputPortIndex = 0;
        int outputPortIndex = 0;
        foreach (PinDeclaration pin in definition.Pins)
        {
            Net? net = definition.FindPortNet(pin.Id);
            if (net is null)
            {
                continue;
            }

            double y = portTop + (pin.Direction == PinDirection.Input ? inputPortIndex++ : outputPortIndex++) * 46;
            var bounds = pin.Direction == PinDirection.Input
                ? new Rect(inputX, y, 150, 30)
                : new Rect(outputX, y, 150, 30);
            Point connector = pin.Direction == PinDirection.Input
                ? new Point(bounds.Right, bounds.Center.Y)
                : new Point(bounds.Left, bounds.Center.Y);
            int extend = pin.Direction == PinDirection.Input ? 1 : -1;
            ports.Add(new PortBox(pin, bounds, net.Id, connector, extend));
        }

        var wires = BuildWires(definition, components, ports, previous);

        Rect content = componentBounds;
        foreach (PortBox port in ports)
        {
            content = content.Union(port.Bounds);
        }

        if (content.Width < 1 || content.Height < 1)
        {
            content = new Rect(0, 0, 800, 500);
        }

        return new CircuitLayout(definition, components, ports, wires, content.Inflate(20));
    }

    private static string DisplayPinName(ComponentInstance instance, PinDescriptor descriptor)
    {
        if (instance.Primitive is LookupSpec lookup)
        {
            if (descriptor.Direction == PinDirection.Output)
            {
                return "Q";
            }

            int index = descriptor.Id.StartsWith("in", StringComparison.Ordinal) &&
                        int.TryParse(descriptor.Id.AsSpan(2), out int parsed) ? parsed : 0;
            return index < 26 ? ((char)('A' + index)).ToString() : descriptor.Name;
        }

        return instance.Primitive switch
        {
            DelaySpec when descriptor.Direction == PinDirection.Input => "D",
            DelaySpec => "Q",
            ProbeSpec => "IN",
            ConstantSpec => "Q",
            WaveformSpec => "Q",
            _ => descriptor.Name,
        };
    }

    private static string TitleFor(ComponentInstance instance, CircuitProject project)
    {
        if (instance.Primitive is PrimitiveSpec spec)
        {
            return PrimitiveDisplay.Name(spec);
        }

        return project.FindDefinition(instance.DefinitionId)?.Name ?? "Missing";
    }

    private static List<NetWire> BuildWires(
        CircuitDefinition definition,
        IReadOnlyList<ComponentBox> components,
        IReadOnlyList<PortBox> ports,
        CircuitLayout? previous)
    {
        var componentByInstance = components.ToDictionary(c => c.Instance.Id, StringComparer.Ordinal);
        var portsByNet = ports
            .GroupBy(p => p.NetId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var wires = new List<NetWire>(definition.Nets.Count);
        var routedSegments = new List<(Point A, Point B)>();
        int ordinal = 0;

        CircuitLayout? reuse = ReferenceEquals(previous?.Definition, definition) ? previous : null;
        Dictionary<string, NetWire> previousWires = reuse is null
            ? []
            : reuse.Wires.ToDictionary(w => w.NetId, StringComparer.Ordinal);
        Dictionary<string, ComponentBox> previousComponents = reuse is null
            ? []
            : reuse.Components.ToDictionary(c => c.Instance.Id, StringComparer.Ordinal);
        Dictionary<string, PortBox> previousPorts = reuse is null
            ? []
            : reuse.Ports.ToDictionary(p => p.Pin.Id, StringComparer.Ordinal);
        var previousPortsByNet = previousPorts.Values
            .GroupBy(p => p.NetId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        List<Rect> changedObstacles = reuse is null
            ? []
            : ChangedObstacles(components, ports, previousComponents, previousPorts);

        foreach (Net net in definition.Nets)
        {
            var terminals = new List<WireTerminal>();
            var editorPins = new List<EditorPin>();

            foreach (PinRef pinRef in net.Pins)
            {
                if (!componentByInstance.TryGetValue(pinRef.InstanceId, out ComponentBox? box))
                {
                    continue;
                }

                PinVisual? pin = box.FindPin(pinRef.PinId);
                if (pin is null)
                {
                    continue;
                }

                terminals.Add(new WireTerminal(
                    pin.Position,
                    pin.Direction == PinDirection.Input ? -1 : 1,
                    false,
                    pin.Direction));
                editorPins.Add(EditorPin.ForInstance(box.Instance.Id, pin.PinId));
            }

            if (portsByNet.TryGetValue(net.Id, out PortBox[]? netPorts))
            {
                foreach (PortBox port in netPorts)
                {
                    terminals.Add(new WireTerminal(port.Connector, port.ExtendDirection, true, port.Pin.Direction));
                    editorPins.Add(EditorPin.ForPort(port.Pin.Id));
                }
            }

            if (terminals.Count == 0)
            {
                continue;
            }

            if (previousWires.TryGetValue(net.Id, out NetWire? previousWire) &&
                CanReuseWire(net, previousWire, terminals, editorPins, previousComponents, previousPortsByNet, changedObstacles))
            {
                wires.Add(previousWire);
                routedSegments.AddRange(previousWire.Segments);
                ordinal++;
                continue;
            }

            WireRouter.RoutedWire routed = WireRouter.Route(
                net,
                ordinal++,
                terminals,
                components,
                ports,
                routedSegments);
            if (routed.Segments.Count == 0)
            {
                continue;
            }

            wires.Add(new NetWire(
                net.Id,
                net.DomainId,
                routed.Points,
                routed.Segments,
                routed.Junctions,
                [],
                routed.LabelPoint,
                editorPins,
                routed.AuthoredPoints));
            routedSegments.AddRange(routed.Segments);
        }

        AddCrossingBreaks(wires);
        return wires;
    }

    /// <summary>Bounds that changed since the previous layout, including removals.</summary>
    private static List<Rect> ChangedObstacles(
        IReadOnlyList<ComponentBox> components,
        IReadOnlyList<PortBox> ports,
        Dictionary<string, ComponentBox> previousComponents,
        Dictionary<string, PortBox> previousPorts)
    {
        var changed = new List<Rect>();
        var currentComponentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ComponentBox box in components)
        {
            currentComponentIds.Add(box.Instance.Id);
            if (!previousComponents.TryGetValue(box.Instance.Id, out ComponentBox? old))
            {
                changed.Add(box.Bounds);
            }
            else if (old.Bounds != box.Bounds)
            {
                changed.Add(old.Bounds);
                changed.Add(box.Bounds);
            }
        }

        foreach ((string instanceId, ComponentBox old) in previousComponents)
        {
            if (!currentComponentIds.Contains(instanceId))
            {
                changed.Add(old.Bounds);
            }
        }

        var currentPortIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PortBox port in ports)
        {
            currentPortIds.Add(port.Pin.Id);
            if (!previousPorts.TryGetValue(port.Pin.Id, out PortBox? old))
            {
                changed.Add(port.Bounds);
            }
            else if (old.Bounds != port.Bounds || old.Connector != port.Connector)
            {
                changed.Add(old.Bounds);
                changed.Add(port.Bounds);
            }
        }

        foreach ((string pinId, PortBox old) in previousPorts)
        {
            if (!currentPortIds.Contains(pinId))
            {
                changed.Add(old.Bounds);
            }
        }

        return changed;
    }

    /// <summary>
    /// True when a cached wire still connects the same endpoints through the same
    /// authored bendpoints and no moved component body now blocks it.
    /// </summary>
    private static bool CanReuseWire(
        Net net,
        NetWire wire,
        List<WireTerminal> terminals,
        List<EditorPin> editorPins,
        Dictionary<string, ComponentBox> previousComponents,
        Dictionary<string, PortBox[]> previousPortsByNet,
        List<Rect> changedObstacles)
    {
        if (!wire.Endpoints.SequenceEqual(editorPins) ||
            wire.RoutePoints.Count != net.RoutePoints.Count)
        {
            return false;
        }

        for (int i = 0; i < net.RoutePoints.Count; i++)
        {
            if (wire.RoutePoints[i] != net.RoutePoints[i])
            {
                return false;
            }
        }

        var previousTerminals = new List<WireTerminal>(terminals.Count);
        foreach (PinRef pinRef in net.Pins)
        {
            if (!previousComponents.TryGetValue(pinRef.InstanceId, out ComponentBox? box) ||
                box.FindPin(pinRef.PinId) is not PinVisual pin)
            {
                return false;
            }

            previousTerminals.Add(new WireTerminal(
                pin.Position,
                pin.Direction == PinDirection.Input ? -1 : 1,
                false,
                pin.Direction));
        }

        if (previousPortsByNet.TryGetValue(net.Id, out PortBox[]? previousPorts))
        {
            foreach (PortBox port in previousPorts)
            {
                previousTerminals.Add(new WireTerminal(port.Connector, port.ExtendDirection, true, port.Pin.Direction));
            }
        }

        if (previousTerminals.Count != terminals.Count)
        {
            return false;
        }

        for (int i = 0; i < terminals.Count; i++)
        {
            if (previousTerminals[i] != terminals[i])
            {
                return false;
            }
        }

        foreach ((Point a, Point b) in wire.Segments)
        {
            foreach (Rect obstacle in changedObstacles)
            {
                if (SegmentHits(a, b, obstacle.Inflate(3)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SegmentHits(Point a, Point b, Rect rect)
    {
        double minX = Math.Min(a.X, b.X);
        double maxX = Math.Max(a.X, b.X);
        double minY = Math.Min(a.Y, b.Y);
        double maxY = Math.Max(a.Y, b.Y);
        return maxX >= rect.Left &&
               minX <= rect.Right &&
               maxY >= rect.Top &&
               minY <= rect.Bottom;
    }

    /// <summary>
    /// Marks a visual gap in the lower-priority wire wherever two distinct nets cross.
    /// Junctions are deliberately excluded: a dot always means an electrical connection.
    /// </summary>
    private static void AddCrossingBreaks(List<NetWire> wires)
    {
        if (wires.Count < 2 || wires.Count > 900)
        {
            return;
        }

        foreach (NetWire wire in wires)
        {
            wire.Breaks = [];
        }

        var order = wires.OrderBy(w => w.NetId, StringComparer.Ordinal).ToArray();
        var breakPoints = new Dictionary<NetWire, HashSet<Point>>();
        for (int i = 0; i < order.Length; i++)
        {
            NetWire under = order[i];
            Rect underBounds = WireBounds(under);
            for (int j = i + 1; j < order.Length; j++)
            {
                NetWire over = order[j];
                if (!underBounds.Intersects(WireBounds(over)))
                {
                    continue;
                }

                foreach ((Point a1, Point a2) in under.Segments)
                {
                    foreach ((Point b1, Point b2) in over.Segments)
                    {
                        if (!WireRouter.Intersects(a1, a2, b1, b2, out Point crossing))
                        {
                            continue;
                        }

                        if (NearAnyVertex(crossing, under) || NearAnyVertex(crossing, over))
                        {
                            continue;
                        }

                        if (!breakPoints.TryGetValue(under, out HashSet<Point>? points))
                        {
                            points = [];
                            breakPoints[under] = points;
                        }

                        points.Add(crossing);
                    }
                }
            }
        }

        foreach ((NetWire wire, HashSet<Point> points) in breakPoints)
        {
            wire.Breaks = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        }
    }

    private static bool NearAnyVertex(Point point, NetWire wire)
    {
        foreach (Point vertex in wire.Points)
        {
            if (Math.Abs(vertex.X - point.X) < 2 && Math.Abs(vertex.Y - point.Y) < 2)
            {
                return true;
            }
        }

        foreach (Point junction in wire.Junctions)
        {
            if (Math.Abs(junction.X - point.X) < 2 && Math.Abs(junction.Y - point.Y) < 2)
            {
                return true;
            }
        }

        return false;
    }

    private static Rect WireBounds(NetWire wire)
    {
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        foreach ((Point a, Point b) in wire.Segments)
        {
            minX = Math.Min(minX, Math.Min(a.X, b.X));
            minY = Math.Min(minY, Math.Min(a.Y, b.Y));
            maxX = Math.Max(maxX, Math.Max(a.X, b.X));
            maxY = Math.Max(maxY, Math.Max(a.Y, b.Y));
        }

        return new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    public ComponentBox? HitComponent(Point point)
    {
        for (int i = Components.Count - 1; i >= 0; i--)
        {
            if (Components[i].Bounds.Contains(point))
            {
                return Components[i];
            }
        }

        return null;
    }

    public PortBox? HitPortBody(Point point)
    {
        for (int i = Ports.Count - 1; i >= 0; i--)
        {
            if (Ports[i].Bounds.Contains(point))
            {
                return Ports[i];
            }
        }

        return null;
    }

    public PinHit? HitPin(Point point, double tolerance)
    {
        PinHit? best = null;
        double bestDistance = tolerance;

        foreach (ComponentBox component in Components)
        {
            foreach (PinVisual pin in component.Pins)
            {
                double distance = Distance(point, pin.Position);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = new PinHit(EditorPin.ForInstance(component.Instance.Id, pin.PinId), pin.Position, pin.Direction, pin.DeclaredDomain);
                }
            }
        }

        foreach (PortBox port in Ports)
        {
            double distance = Distance(point, port.Connector);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = new PinHit(EditorPin.ForPort(port.Pin.Id), port.Connector, port.Pin.Direction, port.Pin.DomainId);
            }
        }

        return best;
    }

    public NetWire? HitWire(Point point, double tolerance)
    {
        NetWire? best = null;
        double bestDistance = tolerance;
        foreach (NetWire wire in Wires)
        {
            foreach ((Point a, Point b) in wire.Segments)
            {
                double distance = DistanceToSegment(point, a, b);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = wire;
                }
            }
        }

        return best;
    }

    public static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double DistanceToSegment(Point point, Point a, Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared < 1e-9)
        {
            return Distance(point, a);
        }

        double t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        return Distance(point, new Point(a.X + (t * dx), a.Y + (t * dy)));
    }


}
