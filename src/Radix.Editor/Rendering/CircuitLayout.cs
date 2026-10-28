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
        Point labelPoint,
        IReadOnlyList<EditorPin> endpoints)
    {
        NetId = netId;
        DomainId = domainId;
        Points = points;
        Segments = segments;
        Junctions = junctions;
        LabelPoint = labelPoint;
        Endpoints = endpoints;
    }

    public string NetId { get; }

    public string DomainId { get; }

    public IReadOnlyList<Point> Points { get; }

    public IReadOnlyList<(Point A, Point B)> Segments { get; }

    public IReadOnlyList<Point> Junctions { get; }

    public Point LabelPoint { get; }

    public IReadOnlyList<EditorPin> Endpoints { get; }
}

public readonly record struct PinHit(EditorPin Pin, Point Position, PinDirection Direction, string? DeclaredDomain);

public sealed class CircuitLayout
{
    public const double BoxWidth = 210;
    public const double HeaderHeight = 34;
    public const double PinSpacing = 22;
    public const double BoxBottomPadding = 12;

    private CircuitLayout(
        IReadOnlyList<ComponentBox> components,
        IReadOnlyList<PortBox> ports,
        IReadOnlyList<NetWire> wires,
        Rect contentBounds)
    {
        Components = components;
        Ports = ports;
        Wires = wires;
        ContentBounds = contentBounds;
    }

    public IReadOnlyList<ComponentBox> Components { get; }

    public IReadOnlyList<PortBox> Ports { get; }

    public IReadOnlyList<NetWire> Wires { get; }

    public Rect ContentBounds { get; }

    public static CircuitLayout Build(CircuitDefinition definition, CircuitProject project)
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

        var wires = BuildWires(definition, components, ports);

        Rect content = componentBounds;
        foreach (PortBox port in ports)
        {
            content = content.Union(port.Bounds);
        }

        if (content.Width < 1 || content.Height < 1)
        {
            content = new Rect(0, 0, 800, 500);
        }

        return new CircuitLayout(components, ports, wires, content.Inflate(20));
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
        IReadOnlyList<PortBox> ports)
    {
        var componentByInstance = components.ToDictionary(c => c.Instance.Id, StringComparer.Ordinal);
        var portsByNet = ports
            .GroupBy(p => p.NetId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var wires = new List<NetWire>();

        foreach (Net net in definition.Nets)
        {
            var endpoints = new List<Endpoint>();
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

                endpoints.Add(new Endpoint(pin.Position, pin.Direction == PinDirection.Input ? -1 : 1, pin.Direction));
                editorPins.Add(EditorPin.ForInstance(box.Instance.Id, pin.PinId));
            }

            if (portsByNet.TryGetValue(net.Id, out PortBox[]? netPorts))
            {
                foreach (PortBox port in netPorts)
                {
                    endpoints.Add(new Endpoint(port.Connector, port.ExtendDirection, port.Pin.Direction));
                    editorPins.Add(EditorPin.ForPort(port.Pin.Id));
                }
            }

            if (endpoints.Count == 0)
            {
                continue;
            }

            var points = new List<Point>();
            var segments = new List<(Point A, Point B)>();
            var junctions = new List<Point>();
            Point labelPoint;

            if (endpoints.Count == 1)
            {
                Endpoint only = endpoints[0];
                Point end = only.Position + new Vector(only.ExtendDirection * 26, 0);
                AddSegment(points, segments, only.Position, end);
                labelPoint = new Point((only.Position.X + end.X) / 2, only.Position.Y);
            }
            else if (endpoints.Count == 2)
            {
                Endpoint a = endpoints[0];
                Endpoint b = endpoints[1];
                Point ea = a.Position + new Vector(a.ExtendDirection * 20, 0);
                Point eb = b.Position + new Vector(b.ExtendDirection * 20, 0);
                double midX = (ea.X + eb.X) / 2;
                AddSegment(points, segments, a.Position, ea);
                AddSegment(points, segments, ea, new Point(midX, ea.Y));
                AddSegment(points, segments, new Point(midX, ea.Y), new Point(midX, eb.Y));
                AddSegment(points, segments, new Point(midX, eb.Y), eb);
                AddSegment(points, segments, eb, b.Position);
                labelPoint = new Point(midX, (ea.Y + eb.Y) / 2);
            }
            else
            {
                var extensions = new List<Point>(endpoints.Count);
                foreach (Endpoint endpoint in endpoints)
                {
                    Point ext = endpoint.Position + new Vector(endpoint.ExtendDirection * 20, 0);
                    extensions.Add(ext);
                    AddSegment(points, segments, endpoint.Position, ext);
                }

                double trunkX = extensions.Average(p => p.X);
                double minY = extensions.Min(p => p.Y);
                double maxY = extensions.Max(p => p.Y);

                foreach (Point ext in extensions)
                {
                    AddSegment(points, segments, ext, new Point(trunkX, ext.Y));
                    junctions.Add(new Point(trunkX, ext.Y));
                }

                if (Math.Abs(maxY - minY) > 0.5)
                {
                    AddSegment(points, segments, new Point(trunkX, minY), new Point(trunkX, maxY));
                }

                labelPoint = new Point(trunkX, (minY + maxY) / 2);
            }

            if (segments.Count > 0)
            {
                wires.Add(new NetWire(net.Id, net.DomainId, points, segments, junctions, labelPoint, editorPins));
            }
        }

        return wires;
    }

    private static void AddSegment(
        List<Point> points,
        List<(Point A, Point B)> segments,
        Point from,
        Point to)
    {
        if (Math.Abs(from.X - to.X) < 0.01 && Math.Abs(from.Y - to.Y) < 0.01)
        {
            return;
        }

        if (points.Count == 0 || !NearlyEqual(points[^1], from))
        {
            points.Add(from);
        }

        points.Add(to);
        segments.Add((from, to));
    }

    private static bool NearlyEqual(Point a, Point b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;

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

    private readonly record struct Endpoint(Point Position, int ExtendDirection, PinDirection Direction);
}
