using Radix.Core.Signals;

namespace Radix.Core.Model;

/// <summary>Fluent construction of a new project.</summary>
public sealed class ProjectBuilder
{
    private readonly CircuitProject _project;

    public ProjectBuilder(string name)
    {
        _project = new CircuitProject { Name = name };
        foreach (SignalDomain domain in StandardDomains.All())
        {
            _project.Domains.Add(domain);
        }
    }

    public CircuitProject Project => _project;

    public DefinitionBuilder Define(string id, string name = "")
    {
        var builder = new DefinitionBuilder(_project, id, string.IsNullOrWhiteSpace(name) ? id : name);
        if (_project.RootDefinitionId.Length == 0)
        {
            _project.RootDefinitionId = id;
        }

        return builder;
    }

    public ProjectBuilder EnsureDomain(SignalDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        if (_project.FindDomain(domain.Id) is null)
        {
            _project.Domains.Add(domain);
        }

        return this;
    }

    public CircuitProject Build(string? rootDefinitionId = null)
    {
        if (rootDefinitionId is not null)
        {
            _project.RootDefinitionId = rootDefinitionId;
        }

        return _project;
    }
}

/// <summary>Builds one structural definition. All methods throw on obvious mistakes.</summary>
public sealed class DefinitionBuilder
{
    private readonly CircuitProject _project;
    private int _netCounter;

    public DefinitionBuilder(CircuitProject project, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        Definition = new CircuitDefinition { Id = id, Name = name };
        project.Definitions.Add(Definition);
        if (project.RootDefinitionId.Length == 0)
        {
            project.RootDefinitionId = id;
        }
    }

    public CircuitDefinition Definition { get; }

    /// <summary>Adds an interface pin and a dedicated net bound to it; returns that net.</summary>
    public Net AddPin(
        string id,
        string name,
        PinDirection direction,
        string domainId,
        string? netId = null)
    {
        if (_project.FindDomain(domainId) is null)
        {
            throw new InvalidOperationException($"Unknown domain '{domainId}'.");
        }

        if (Definition.FindPin(id) is not null)
        {
            throw new InvalidOperationException($"Definition '{Definition.Id}' already has pin '{id}'.");
        }

        Definition.Pins.Add(new PinDeclaration
        {
            Id = id,
            Name = name,
            Direction = direction,
            DomainId = domainId,
        });

        string resolvedNetId = UniqueNetId(netId ?? $"{id}_net");
        var net = new Net { Id = resolvedNetId, DomainId = domainId };
        Definition.Nets.Add(net);
        Definition.Ports.Add(new PortBinding(id, resolvedNetId));
        return net;
    }

    public ComponentInstance AddInstance(
        string id,
        string definitionId,
        double x = 0,
        double y = 0,
        string? label = null)
    {
        if (_project.FindDefinition(definitionId) is null)
        {
            throw new InvalidOperationException($"Unknown definition '{definitionId}'.");
        }

        var instance = new ComponentInstance
        {
            Id = id,
            DefinitionId = definitionId,
            X = x,
            Y = y,
            Label = label,
        };
        AddInstanceCore(instance);
        return instance;
    }

    public ComponentInstance AddPrimitive(
        string id,
        PrimitiveSpec spec,
        double x = 0,
        double y = 0,
        string? label = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var instance = new ComponentInstance
        {
            Id = id,
            Primitive = spec,
            X = x,
            Y = y,
            Label = label,
        };
        AddInstanceCore(instance);
        return instance;
    }

    private void AddInstanceCore(ComponentInstance instance)
    {
        if (Definition.FindInstance(instance.Id) is not null)
        {
            throw new InvalidOperationException($"Definition '{Definition.Id}' already has instance '{instance.Id}'.");
        }

        Definition.Instances.Add(instance);
    }

    public Net AddNet(string domainId, params PinRef[] pins) =>
        AddNet($"n{++_netCounter}", domainId, pins);

    public Net AddNet(string id, string domainId, params PinRef[] pins)
    {
        if (_project.FindDomain(domainId) is null)
        {
            throw new InvalidOperationException($"Unknown domain '{domainId}'.");
        }

        if (Definition.FindNet(id) is not null)
        {
            throw new InvalidOperationException($"Definition '{Definition.Id}' already has net '{id}'.");
        }

        var net = new Net { Id = id, DomainId = domainId };
        Definition.Nets.Add(net);
        Connect(net, pins);
        return net;
    }

    public Net Connect(Net net, params PinRef[] pins)
    {
        ArgumentNullException.ThrowIfNull(net);
        foreach (PinRef pin in pins)
        {
            if (!net.Pins.Contains(pin))
            {
                net.Pins.Add(pin);
            }
        }

        return net;
    }

    public Net Connect(string netId, params PinRef[] pins)
    {
        Net net = Definition.FindNet(netId)
            ?? throw new InvalidOperationException($"Definition '{Definition.Id}' has no net '{netId}'.");
        return Connect(net, pins);
    }

    public PinRef Pin(string instanceId, string pinId)
    {
        if (Definition.FindInstance(instanceId) is null)
        {
            throw new InvalidOperationException($"Definition '{Definition.Id}' has no instance '{instanceId}'.");
        }

        return new PinRef(instanceId, pinId);
    }

    public Net SetInitial(Net net, int? value)
    {
        ArgumentNullException.ThrowIfNull(net);
        net.InitialValue = value;
        return net;
    }

    public Net Rename(Net net, string label)
    {
        ArgumentNullException.ThrowIfNull(net);
        net.Label = label;
        return net;
    }

    private string UniqueNetId(string preferred)
    {
        if (Definition.FindNet(preferred) is null)
        {
            return preferred;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{preferred}_{i}";
            if (Definition.FindNet(candidate) is null)
            {
                return candidate;
            }
        }
    }
}
