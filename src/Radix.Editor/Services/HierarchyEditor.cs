using System.Globalization;
using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.Editor.Services;

public sealed record HierarchyExtractResult(
    bool Success,
    string Message,
    CircuitDefinition? Definition = null,
    ComponentInstance? Instance = null)
{
    public static HierarchyExtractResult Fail(string message) => new(false, message);

    public static HierarchyExtractResult Ok(string message, CircuitDefinition definition, ComponentInstance instance) =>
        new(true, message, definition, instance);
}

/// <summary>
/// Structural hierarchy operations that keep parent connectivity intact. These
/// operate on model objects only and never touch rendering or simulation state.
/// </summary>
public static class HierarchyEditor
{
    /// <summary>
    /// Moves the selected instances into a new definition and replaces them with one
    /// instance of that definition. Every net crossing the selection boundary becomes
    /// an interface pin; nets entirely inside become internal nets of the new part.
    /// </summary>
    public static HierarchyExtractResult ExtractSelection(
        CircuitProject project,
        CircuitDefinition parent,
        IReadOnlyCollection<string> instanceIds,
        string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(instanceIds);

        var selectedIds = instanceIds.ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return HierarchyExtractResult.Fail("Select one or more parts to extract.");
        }

        var selected = parent.Instances.Where(i => selectedIds.Contains(i.Id)).ToList();
        if (selected.Count != selectedIds.Count)
        {
            return HierarchyExtractResult.Fail("The selection refers to parts that are no longer in this definition.");
        }

        var boundary = new List<(Net Net, PinDirection Direction)>();
        var internalNets = new List<Net>();
        foreach (Net net in parent.Nets)
        {
            if (!net.Pins.Any(pin => selectedIds.Contains(pin.InstanceId)))
            {
                continue;
            }

            bool hasOutsidePins = net.Pins.Any(pin => !selectedIds.Contains(pin.InstanceId));
            bool hasPort = parent.Ports.Any(port => string.Equals(port.NetId, net.Id, StringComparison.Ordinal));
            if (!hasOutsidePins && !hasPort)
            {
                internalNets.Add(net);
                continue;
            }

            PinDirection? direction = BoundaryDirection(project, parent, net, selectedIds);
            if (direction is null)
            {
                return HierarchyExtractResult.Fail(
                    $"Net '{net.Label ?? net.Id}' has drivers on both sides of the selection; " +
                    "split it or choose a smaller selection before extracting.");
            }

            boundary.Add((net, direction.Value));
        }

        string trimmedName = name.Trim();
        if (trimmedName.Length == 0)
        {
            trimmedName = "Component";
        }

        string definitionId = DefinitionEditor.UniqueId(
            "custom." + Slug(trimmedName),
            id => project.FindDefinition(id) is not null);
        var definition = new CircuitDefinition
        {
            Id = definitionId,
            Name = trimmedName,
            Description = $"Extracted from {parent.Name}.",
        };
        project.Definitions.Add(definition);

        foreach (ComponentInstance source in selected)
        {
            definition.Instances.Add(CloneInstance(source));
        }

        var netMap = new Dictionary<string, Net>(StringComparer.Ordinal);
        foreach (Net source in internalNets.Concat(boundary.Select(b => b.Net)))
        {
            Net net = CloneNet(source);
            net.Id = DefinitionEditor.UniqueNetId(definition, Slug(source.Id));
            net.Pins.Clear();
            foreach (PinRef pin in source.Pins.Where(pin => selectedIds.Contains(pin.InstanceId)))
            {
                net.Pins.Add(pin);
            }

            netMap[source.Id] = net;
            definition.Nets.Add(net);
        }

        // Inputs are listed before outputs, then by their vertical order in the old
        // layout, so the generated interface reads like the schematic.
        var orderedBoundary = boundary
            .OrderBy(b => b.Direction == PinDirection.Input ? 0 : 1)
            .ThenBy(b => RepresentativeY(parent, b.Net))
            .ThenBy(b => RepresentativeX(parent, b.Net))
            .ThenBy(b => b.Net.Id, StringComparer.Ordinal)
            .ToList();

        var pinByNet = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((Net source, PinDirection direction) in orderedBoundary)
        {
            string pinId = DefinitionEditor.UniquePinId(definition, direction == PinDirection.Input ? "in" : "out");
            string baseName = string.IsNullOrWhiteSpace(source.Label)
                ? direction == PinDirection.Input ? "In" : "Out"
                : source.Label;
            string pinName = UniquePinName(definition, baseName);
            definition.Pins.Add(new PinDeclaration
            {
                Id = pinId,
                Name = pinName,
                Direction = direction,
                DomainId = source.DomainId,
            });
            definition.Ports.Add(new PortBinding(pinId, netMap[source.Id].Id));
            pinByNet[source.Id] = pinId;
        }

        double instanceX = selected.Min(instance => instance.X);
        double instanceY = selected.Min(instance => instance.Y);
        parent.Instances.RemoveAll(instance => selectedIds.Contains(instance.Id));

        string instanceId = DefinitionEditor.UniqueInstanceId(parent, Slug(trimmedName));
        var replacement = new ComponentInstance
        {
            Id = instanceId,
            DefinitionId = definitionId,
            Label = trimmedName,
            X = instanceX,
            Y = instanceY,
        };
        parent.Instances.Add(replacement);

        foreach ((Net source, _) in boundary)
        {
            source.Pins.RemoveAll(pin => selectedIds.Contains(pin.InstanceId));
            source.Pins.Add(new PinRef(instanceId, pinByNet[source.Id]));
        }

        parent.Nets.RemoveAll(net => internalNets.Contains(net));

        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"Extracted {selected.Count} parts into '{trimmedName}' with {definition.Pins.Count} pins");
        return HierarchyExtractResult.Ok(message, definition, replacement);
    }

    /// <summary>
    /// True when placing <paramref name="targetDefinitionId"/> inside
    /// <paramref name="hostDefinitionId"/> would make the hierarchy recursive. The
    /// validator catches this too, but the editor should not create the bad edge.
    /// </summary>
    public static bool WouldCreateCycle(
        CircuitProject project,
        string hostDefinitionId,
        string targetDefinitionId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.Equals(hostDefinitionId, targetDefinitionId, StringComparison.Ordinal))
        {
            return true;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(targetDefinitionId);
        while (pending.Count > 0)
        {
            string id = pending.Pop();
            if (!visited.Add(id) || project.FindDefinition(id) is not CircuitDefinition definition)
            {
                continue;
            }

            foreach (ComponentInstance instance in definition.Instances)
            {
                if (instance.DefinitionId is not string dependency)
                {
                    continue;
                }

                if (string.Equals(dependency, hostDefinitionId, StringComparison.Ordinal))
                {
                    return true;
                }

                pending.Push(dependency);
            }
        }

        return false;
    }

    /// <summary>
    /// Binds an existing net to a new interface pin. Direction is chosen by the
    /// caller because a net can legitimately be observed or driven at the boundary.
    /// </summary>
    public static PinDeclaration? PromoteNet(
        CircuitProject project,
        CircuitDefinition definition,
        Net net,
        PinDirection direction,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(net);

        if (definition.Ports.Any(port => string.Equals(port.NetId, net.Id, StringComparison.Ordinal)))
        {
            return null;
        }

        if (!definition.Nets.Contains(net))
        {
            return null;
        }

        string baseName = string.IsNullOrWhiteSpace(name) ? net.Label ?? net.Id : name.Trim();
        string pinId = DefinitionEditor.UniquePinId(definition, direction == PinDirection.Input ? "in" : "out");
        var pin = new PinDeclaration
        {
            Id = pinId,
            Name = UniquePinName(definition, baseName),
            Direction = direction,
            DomainId = net.DomainId,
        };
        definition.Pins.Add(pin);
        definition.Ports.Add(new PortBinding(pinId, net.Id));
        net.Label ??= pin.Name;
        return pin;
    }

    private static PinDirection? BoundaryDirection(
        CircuitProject project,
        CircuitDefinition parent,
        Net net,
        HashSet<string> selectedIds)
    {
        bool internalDriver = false;
        bool externalDriver = false;
        bool internalSink = false;
        bool externalSink = false;
        bool sawKnownPin = false;

        foreach (PinRef pin in net.Pins)
        {
            ComponentInstance? instance = parent.FindInstance(pin.InstanceId);
            if (instance is null)
            {
                continue;
            }

            PinDescriptor? descriptor = project.FindInstancePin(instance, pin.PinId);
            if (descriptor is null)
            {
                continue;
            }

            sawKnownPin = true;
            bool inside = selectedIds.Contains(pin.InstanceId);
            if (descriptor.Value.Direction == PinDirection.Output)
            {
                if (inside)
                {
                    internalDriver = true;
                }
                else
                {
                    externalDriver = true;
                }
            }
            else if (inside)
            {
                internalSink = true;
            }
            else
            {
                externalSink = true;
            }
        }

        foreach (PortBinding port in parent.Ports.Where(p => string.Equals(p.NetId, net.Id, StringComparison.Ordinal)))
        {
            PinDeclaration? pin = parent.FindPin(port.PinId);
            if (pin is null)
            {
                continue;
            }

            sawKnownPin = true;
            if (pin.Direction == PinDirection.Input)
            {
                externalDriver = true;
            }
            else
            {
                externalSink = true;
            }
        }

        if (!sawKnownPin)
        {
            return null;
        }

        if (internalDriver && externalDriver)
        {
            return null;
        }

        if (internalDriver)
        {
            return PinDirection.Output;
        }

        if (externalDriver)
        {
            return PinDirection.Input;
        }

        // Nothing drives the net: expose it as an input so an external source can
        // drive the internal sinks that remain inside the new component.
        return internalSink || externalSink ? PinDirection.Input : null;
    }

    private static double RepresentativeY(CircuitDefinition definition, Net net)
    {
        double[] values = net.Pins
            .Select(pin => definition.FindInstance(pin.InstanceId))
            .Where(instance => instance is not null)
            .Select(instance => instance!.Y)
            .ToArray();
        return values.Length == 0 ? 0 : values.Min();
    }

    private static double RepresentativeX(CircuitDefinition definition, Net net)
    {
        double[] values = net.Pins
            .Select(pin => definition.FindInstance(pin.InstanceId))
            .Where(instance => instance is not null)
            .Select(instance => instance!.X)
            .ToArray();
        return values.Length == 0 ? 0 : values.Min();
    }

    private static string UniquePinName(CircuitDefinition definition, string baseName)
    {
        string stem = baseName.Trim();
        if (stem.Length == 0)
        {
            stem = "Pin";
        }

        var used = definition.Pins.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string candidate = stem;
        int suffix = 1;
        while (used.Contains(candidate))
        {
            candidate = stem + (++suffix).ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    private static ComponentInstance CloneInstance(ComponentInstance source) => new()
    {
        Id = source.Id,
        DefinitionId = source.DefinitionId,
        Primitive = source.Primitive,
        Label = source.Label,
        X = source.X,
        Y = source.Y,
    };

    private static Net CloneNet(Net source)
    {
        var net = new Net
        {
            Id = source.Id,
            DomainId = source.DomainId,
            Label = source.Label,
            InitialValue = source.InitialValue,
            WireColor = source.WireColor,
        };
        net.RoutePoints.AddRange(source.RoutePoints);
        net.Pins.AddRange(source.Pins);
        return net;
    }

    private static string Slug(string label)
    {
        string stem = new string(label
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');
        return stem.Length == 0 ? "part" : stem;
    }
}
