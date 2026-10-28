using System.Globalization;
using Radix.Core.Signals;

namespace Radix.Core.Model;

/// <summary>A pin exposed by a definition or a primitive instance.</summary>
/// <param name="Id">Stable identifier within its owner.</param>
/// <param name="Name">Display name.</param>
/// <param name="Direction">Signal direction.</param>
/// <param name="DomainId">Declared domain for structural interface pins; null for primitive pins.</param>
public readonly record struct PinDescriptor(string Id, string Name, PinDirection Direction, string? DomainId);

/// <summary>Declares an interface pin of a structural definition.</summary>
public sealed class PinDeclaration
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public PinDirection Direction { get; set; }

    public string DomainId { get; set; } = string.Empty;

    public PinDescriptor ToDescriptor() => new(Id, Name, Direction, DomainId);
}

/// <summary>Reference to a pin of an instance at the same hierarchy level.</summary>
public readonly record struct PinRef(string InstanceId, string PinId)
{
    public override string ToString() => $"{InstanceId}.{PinId}";
}

/// <summary>Places a component instance on a definition's canvas.</summary>
public sealed class ComponentInstance
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Referenced structural definition, for hierarchical instances.</summary>
    public string? DefinitionId { get; set; }

    /// <summary>Inline primitive parameters, for leaf instances.</summary>
    public PrimitiveSpec? Primitive { get; set; }

    /// <summary>Optional display label overriding the definition name.</summary>
    public string? Label { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public bool IsPrimitive => Primitive is not null;

    /// <summary>Exactly one of <see cref="DefinitionId"/> or <see cref="Primitive"/> must be set.</summary>
    public bool HasExactlyOneSource => (DefinitionId is null) ^ (Primitive is null);

    public override string ToString() => Id;
}

/// <summary>
/// An electrically common node. All pins listed share one domain and one resolved
/// value. A net may have any number of drivers, subject to the domain policy.
/// </summary>
public sealed class Net
{
    public string Id { get; set; } = string.Empty;

    public string DomainId { get; set; } = string.Empty;

    public string? Label { get; set; }

    /// <summary>
    /// Optional value index used before the first driver commits (and while no driver
    /// is active). Null means the net begins undriven.
    /// </summary>
    public int? InitialValue { get; set; }

    public List<PinRef> Pins { get; } = [];

    public override string ToString() => Id;
}

/// <summary>Binds an interface pin of a definition to an internal net.</summary>
public sealed record PortBinding(string PinId, string NetId);

/// <summary>Simulation defaults stored with a project.</summary>
public sealed class ProjectSettings
{
    /// <summary>
    /// Safety valve for zero-delay feedback: maximum evaluations of one component at
    /// a single simulated timestamp before non-convergence is reported.
    /// </summary>
    public int MaxEvaluationsPerComponentPerTimestamp { get; set; } = 64;

    /// <summary>Maximum number of net changes retained for the editor's waveform view.</summary>
    public int MaxRecordedNetChanges { get; set; } = 500_000;

    /// <summary>Default number of ticks advanced by the editor's "step time" command.</summary>
    public long DefaultAdvanceTicks { get; set; } = 10;

    /// <summary>Whether the editor records every net transition for waveform inspection.</summary>
    public bool TraceAllNets { get; set; } = true;
}

/// <summary>
/// A reusable component definition: an interface plus a body of instances, nets, and
/// port bindings. There is no built-in primitive definition kind; primitives live
/// inline on instances.
/// </summary>
public sealed class CircuitDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public List<PinDeclaration> Pins { get; } = [];

    public List<ComponentInstance> Instances { get; } = [];

    public List<Net> Nets { get; } = [];

    public List<PortBinding> Ports { get; } = [];

    public IReadOnlyList<PinDescriptor> InterfacePins =>
        Pins.Select(p => p.ToDescriptor()).ToArray();

    public PinDeclaration? FindPin(string pinId) =>
        Pins.FirstOrDefault(p => string.Equals(p.Id, pinId, StringComparison.Ordinal));

    public ComponentInstance? FindInstance(string instanceId) =>
        Instances.FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.Ordinal));

    public Net? FindNet(string netId) =>
        Nets.FirstOrDefault(n => string.Equals(n.Id, netId, StringComparison.Ordinal));

    public PortBinding? FindPort(string pinId) =>
        Ports.FirstOrDefault(p => string.Equals(p.PinId, pinId, StringComparison.Ordinal));

    public Net? FindPortNet(string pinId)
    {
        PortBinding? port = FindPort(pinId);
        return port is null ? null : FindNet(port.NetId);
    }

    public override string ToString() => Id;
}

/// <summary>
/// A complete RADIX project: domain definitions and a library of component
/// definitions with one designated root. The model is intentionally mutable for
/// editor use; simulation compiles an immutable snapshot.
/// </summary>
public sealed class CircuitProject
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string Name { get; set; } = "Untitled";

    public string? Description { get; set; }

    /// <summary>Definition displayed when the project is opened.</summary>
    public string RootDefinitionId { get; set; } = string.Empty;

    public List<SignalDomain> Domains { get; } = [];

    public List<CircuitDefinition> Definitions { get; } = [];

    public ProjectSettings Settings { get; } = new();

    public SignalDomain? FindDomain(string? domainId) =>
        domainId is null ? null : Domains.FirstOrDefault(d => string.Equals(d.Id, domainId, StringComparison.Ordinal));

    public CircuitDefinition? FindDefinition(string? definitionId) =>
        definitionId is null ? null : Definitions.FirstOrDefault(d => string.Equals(d.Id, definitionId, StringComparison.Ordinal));

    public CircuitDefinition RequireDefinition(string definitionId) =>
        FindDefinition(definitionId) ?? throw new KeyNotFoundException($"Definition '{definitionId}' was not found.");

    /// <summary>Resolves a pin descriptor for an instance, or null when the pin does not exist.</summary>
    public PinDescriptor? FindInstancePin(ComponentInstance instance, string pinId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        foreach (PinDescriptor pin in GetInstancePins(instance))
        {
            if (string.Equals(pin.Id, pinId, StringComparison.Ordinal))
            {
                return pin;
            }
        }

        return null;
    }

    /// <summary>Pins of an instance, derived from its primitive or referenced definition.</summary>
    public IReadOnlyList<PinDescriptor> GetInstancePins(ComponentInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Primitive is not null)
        {
            return PrimitiveShapes.GetPins(instance.Primitive);
        }

        CircuitDefinition? definition = FindDefinition(instance.DefinitionId);
        return definition is null ? [] : definition.InterfacePins;
    }

    /// <summary>Creates an empty project with the standard binary domain.</summary>
    public static CircuitProject CreateEmpty(string name = "Untitled")
    {
        var project = new CircuitProject { Name = name };
        project.Domains.Add(StandardDomains.Binary());
        return project;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name} ({Definitions.Count} definitions)");
}
