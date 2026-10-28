using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.Editor.Services;

/// <summary>Structural edits that keep nets, pins, and port bindings consistent.</summary>
public static class DefinitionEditor
{
    public static Net? NetForPin(CircuitDefinition definition, string pinId) =>
        definition.FindPortNet(pinId);

    public static string UniqueId(string stem, Func<string, bool> exists)
    {
        string candidate = stem;
        int suffix = 1;
        while (exists(candidate))
        {
            candidate = stem + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    public static string UniqueNetId(CircuitDefinition definition, string stem = "net") =>
        UniqueId(stem, id => definition.FindNet(id) is not null);

    public static string UniqueInstanceId(CircuitDefinition definition, string stem = "u") =>
        UniqueId(stem, id => definition.FindInstance(id) is not null);

    public static string UniquePinId(CircuitDefinition definition, string stem = "p") =>
        UniqueId(stem, id => definition.FindPin(id) is not null);

    public static string UniqueLaneId(CircuitDefinition definition, string stem = "lane") =>
        UniqueId(stem, id => definition.FindNumericLane(id) is not null);

    public static PinDeclaration? AddPin(
        CircuitProject project,
        CircuitDefinition definition,
        string baseName,
        PinDirection direction,
        string domainId)
    {
        if (project.FindDomain(domainId) is null)
        {
            return null;
        }

        string name = baseName.Trim();
        if (name.Length == 0)
        {
            name = direction == PinDirection.Input ? "in" : "out";
        }

        string id = UniquePinId(definition, direction == PinDirection.Input ? "in" : "out");
        var pin = new PinDeclaration
        {
            Id = id,
            Name = name,
            Direction = direction,
            DomainId = domainId,
        };
        definition.Pins.Add(pin);

        var net = new Net
        {
            Id = UniqueNetId(definition, id),
            DomainId = domainId,
            Label = name,
        };
        definition.Nets.Add(net);
        definition.Ports.Add(new PortBinding(id, net.Id));
        return pin;
    }

    public static bool RemovePin(CircuitDefinition definition, string pinId)
    {
        PinDeclaration? pin = definition.FindPin(pinId);
        if (pin is null)
        {
            return false;
        }

        Net? net = definition.FindPortNet(pinId);
        definition.Pins.Remove(pin);
        definition.Ports.RemoveAll(p => string.Equals(p.PinId, pinId, StringComparison.Ordinal));
        if (net is not null)
        {
            definition.Nets.Remove(net);
        }

        foreach (NumericLane lane in definition.NumericLanes)
        {
            lane.PinIds.RemoveAll(id => string.Equals(id, pinId, StringComparison.Ordinal));
        }

        definition.NumericLanes.RemoveAll(lane => lane.PinIds.Count == 0);
        return true;
    }

    /// <summary>
    /// Adds a numeric lane over existing interface pins, preserving the caller's
    /// order and dropping duplicates. Returns null when no known pin remains.
    /// </summary>
    public static NumericLane? AddNumericLane(
        CircuitDefinition definition,
        string name,
        string formatId,
        IEnumerable<string> pinIds)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string pinId in pinIds)
        {
            if (definition.FindPin(pinId) is not null && seen.Add(pinId))
            {
                ordered.Add(pinId);
            }
        }

        if (ordered.Count == 0)
        {
            return null;
        }

        string trimmed = name.Trim();
        var lane = new NumericLane
        {
            Id = UniqueLaneId(definition),
            Name = trimmed.Length == 0 ? $"Bus {ordered.Count}" : trimmed,
            FormatId = formatId,
        };
        lane.PinIds.AddRange(ordered);
        definition.NumericLanes.Add(lane);
        return lane;
    }

    public static bool RemoveNumericLane(CircuitDefinition definition, string laneId)
    {
        NumericLane? lane = definition.FindNumericLane(laneId);
        if (lane is null)
        {
            return false;
        }

        definition.NumericLanes.Remove(lane);
        return true;
    }

    public static bool SetPinDirection(CircuitDefinition definition, string pinId, PinDirection direction)
    {
        PinDeclaration? pin = definition.FindPin(pinId);
        if (pin is null)
        {
            return false;
        }

        pin.Direction = direction;
        return true;
    }

    public static bool SetPinDomain(
        CircuitProject project,
        CircuitDefinition definition,
        string pinId,
        string domainId)
    {
        if (project.FindDomain(domainId) is null)
        {
            return false;
        }

        PinDeclaration? pin = definition.FindPin(pinId);
        if (pin is null)
        {
            return false;
        }

        pin.DomainId = domainId;
        Net? net = definition.FindPortNet(pinId);
        if (net is not null)
        {
            net.DomainId = domainId;
        }

        return true;
    }

    public static bool SetPinName(CircuitDefinition definition, string pinId, string name)
    {
        PinDeclaration? pin = definition.FindPin(pinId);
        string trimmed = name.Trim();
        if (pin is null || trimmed.Length == 0)
        {
            return false;
        }

        string previous = pin.Name;
        pin.Name = trimmed;
        Net? net = definition.FindPortNet(pinId);
        if (net is not null &&
            (string.IsNullOrWhiteSpace(net.Label) || string.Equals(net.Label, previous, StringComparison.Ordinal)))
        {
            net.Label = trimmed;
        }

        return true;
    }

    public static Net EnsureNet(CircuitDefinition definition, string domainId, string? netId = null, string? label = null)
    {
        if (netId is not null && definition.FindNet(netId) is Net existing)
        {
            existing.DomainId = domainId;
            return existing;
        }

        var net = new Net
        {
            Id = UniqueNetId(definition, netId ?? "net"),
            DomainId = domainId,
            Label = label,
        };
        definition.Nets.Add(net);
        return net;
    }

    /// <summary>Removes a net and every pin reference that pointed at it.</summary>
    public static bool RemoveNet(CircuitDefinition definition, string netId)
    {
        Net? net = definition.FindNet(netId);
        if (net is null)
        {
            return false;
        }

        definition.Nets.Remove(net);
        definition.Ports.RemoveAll(p => string.Equals(p.NetId, netId, StringComparison.Ordinal));
        return true;
    }

    public static void RenameDefinition(CircuitDefinition definition, string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length > 0)
        {
            definition.Name = trimmed;
        }
    }

    public static void SetLabel(ComponentInstance instance, string? label)
    {
        string? trimmed = label?.Trim();
        instance.Label = string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Ensures a declared domain exists in the project, copying a standard domain if needed.</summary>
    public static SignalDomain EnsureDomain(CircuitProject project, string domainId)
    {
        SignalDomain? existing = project.FindDomain(domainId);
        if (existing is not null)
        {
            return existing;
        }

        SignalDomain? standard = StandardDomains.All().FirstOrDefault(d =>
            string.Equals(d.Id, domainId, StringComparison.Ordinal));
        SignalDomain created = standard ?? new SignalDomain(
            domainId,
            domainId,
            ["0", "1"],
            new SingleDriverResolution(),
            "User-defined domain created by the editor.");
        project.Domains.Add(created);
        return created;
    }
}
