using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.Core.Compilation;

/// <summary>
/// Validates a project and flattens it into a <see cref="CompiledCircuit"/>. Hierarchy
/// is resolved with a union-find over authored nets and primitive pins, so the engine
/// never performs definition lookups.
/// </summary>
public sealed class CircuitCompiler
{
    public const int MaxHierarchyDepth = 64;

    /// <summary>Compiles a project or throws <see cref="CircuitCompileException"/>.</summary>
    public static CompiledCircuit Compile(CircuitProject project, string? rootDefinitionId = null)
    {
        var diagnostics = new DiagnosticBag();
        CompiledCircuit? circuit = CompileInternal(project, rootDefinitionId, diagnostics);
        return circuit ?? throw new CircuitCompileException(diagnostics);
    }

    /// <summary>
    /// Validates every definition as a simulation root, not only the project root or
    /// the parts reachable from it. This surfaces broken but currently unused library
    /// parts that would otherwise only fail when someone instantiates them. Global
    /// structure is validated once; per-definition diagnostics are deduplicated so a
    /// broken child reached through several roots is reported once.
    /// </summary>
    public static bool TryValidateAllDefinitions(CircuitProject project, out DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(project);
        var all = new DiagnosticBag();
        CircuitValidator.Validate(project, project.RootDefinitionId, all);
        if (all.HasErrors)
        {
            diagnostics = all;
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (CircuitDefinition definition in project.Definitions)
        {
            var definitionDiagnostics = new DiagnosticBag();
            var run = new FlatteningRun(project, definition.Id, definitionDiagnostics);
            run.Execute();
            foreach (Diagnostic diagnostic in definitionDiagnostics)
            {
                string key = string.Join('|',
                    diagnostic.Code,
                    diagnostic.Severity,
                    diagnostic.DefinitionId,
                    diagnostic.InstanceId,
                    diagnostic.NetId,
                    diagnostic.PinId,
                    diagnostic.Path,
                    diagnostic.Message);
                if (seen.Add(key))
                {
                    all.Add(diagnostic);
                }
            }
        }

        diagnostics = all;
        return !all.HasErrors;
    }

    /// <summary>Attempts compilation, returning diagnostics instead of throwing.</summary>
    public static bool TryCompile(
        CircuitProject project,
        string? rootDefinitionId,
        out CompiledCircuit? circuit,
        out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        circuit = CompileInternal(project, rootDefinitionId, diagnostics);
        return circuit is not null && !diagnostics.HasErrors;
    }

    private static CompiledCircuit? CompileInternal(
        CircuitProject project,
        string? rootDefinitionId,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string rootId = rootDefinitionId ?? project.RootDefinitionId;
        CircuitValidator.Validate(project, rootId, diagnostics);
        if (diagnostics.HasErrors)
        {
            return null;
        }

        var run = new FlatteningRun(project, rootId, diagnostics);
        CompiledCircuit? circuit = run.Execute();
        return diagnostics.HasErrors ? null : circuit;
    }

    private sealed class FlatteningRun
    {
        private readonly CircuitProject _project;
        private readonly string _rootId;
        private readonly DiagnosticBag _diagnostics;
        private readonly UnionFind _sets = new();
        private readonly List<ComponentBuilder> _components = [];
        private readonly List<PinElement> _pinElements = [];
        private readonly List<NetElement> _netElements = [];

        public FlatteningRun(CircuitProject project, string rootId, DiagnosticBag diagnostics)
        {
            _project = project;
            _rootId = rootId;
            _diagnostics = diagnostics;
        }

        public CompiledCircuit? Execute()
        {
            Dictionary<string, int> rootPorts = Instantiate(_rootId, string.Empty, 0, null);
            if (_diagnostics.HasErrors)
            {
                return null;
            }

            var netIndexByElementRoot = new Dictionary<int, int>();
            var orderedGroups = new List<NetGroupBuilder>();

            foreach (NetElement element in _netElements)
            {
                int root = _sets.Find(element.Element);
                if (!netIndexByElementRoot.TryGetValue(root, out _))
                {
                    var group = new NetGroupBuilder(
                        orderedGroups.Count,
                        element.DomainId,
                        element.InitialValue,
                        element.Label,
                        element.Path,
                        element.NetId,
                        element.DefinitionId);
                    netIndexByElementRoot[root] = group.Index;
                    orderedGroups.Add(group);
                }
                else
                {
                    NetGroupBuilder group = orderedGroups[netIndexByElementRoot[root]];
                    if (!string.Equals(group.DomainId, element.DomainId, StringComparison.Ordinal))
                    {
                        _diagnostics.Error(
                            "RAD117",
                            $"Nets merged through hierarchy use different domains: '{group.DomainId}' at '{group.Path}/{group.NetId}' and '{element.DomainId}' at '{element.Path}/{element.NetId}'.",
                            element.DefinitionId,
                            netId: element.NetId,
                            path: element.Path);
                    }

                    if (element.InitialValue is int initial)
                    {
                        if (group.InitialValue is null)
                        {
                            group.InitialValue = initial;
                        }
                        else if (group.InitialValue != initial)
                        {
                            _diagnostics.Error(
                                "RAD118",
                                $"Merged nets specify conflicting initial values {group.InitialValue} and {initial}.",
                                element.DefinitionId,
                                netId: element.NetId,
                                path: element.Path);
                        }
                    }

                    group.Label ??= element.Label;
                }
            }

            var pins = new List<CompiledPin>(_pinElements.Count);
            var pinLocation = new Dictionary<PinLocation, int>(_pinElements.Count);
            var drivers = new List<int>[orderedGroups.Count];
            var sinks = new List<int>[orderedGroups.Count];
            for (int i = 0; i < orderedGroups.Count; i++)
            {
                drivers[i] = [];
                sinks[i] = [];
            }

            foreach (PinElement pinElement in _pinElements)
            {
                ComponentBuilder component = _components[pinElement.Component];
                int root = _sets.Find(pinElement.Element);
                int net = netIndexByElementRoot.GetValueOrDefault(root, -1);
                var pin = new CompiledPin(pins.Count, pinElement.Component, pinElement.LocalPin, pinElement.PinId, pinElement.Direction, net);
                pins.Add(pin);
                pinLocation[new PinLocation(component.Path, component.InstanceId, pin.PinId)] = pin.Index;

                if (net >= 0)
                {
                    if (pin.Direction == PinDirection.Output)
                    {
                        drivers[net].Add(pin.Index);
                    }
                    else
                    {
                        sinks[net].Add(pin.Component);
                    }
                }
            }

            var nets = new CompiledNet[orderedGroups.Count];
            var netIndexByLocation = new Dictionary<NetLocation, int>(_netElements.Count);
            foreach (NetGroupBuilder group in orderedGroups)
            {
                if (_project.FindDomain(group.DomainId) is not SignalDomain domain)
                {
                    _diagnostics.Error(
                        "RAD123",
                        $"Net group uses unknown domain '{group.DomainId}'.",
                        group.DefinitionId,
                        netId: group.NetId,
                        path: group.Path);
                    continue;
                }

                int netIndex = group.Index;
                int[] sinkComponents = sinks[netIndex].Distinct().Order().ToArray();
                nets[netIndex] = new CompiledNet(
                    netIndex,
                    domain,
                    drivers[netIndex].ToArray(),
                    sinkComponents,
                    group.InitialValue ?? SignalValue.UndrivenIndex,
                    group.Label,
                    group.NetId,
                    group.Path,
                    group.DefinitionId);
            }

            foreach (NetElement element in _netElements)
            {
                int root = _sets.Find(element.Element);
                if (netIndexByElementRoot.TryGetValue(root, out int netIndex))
                {
                    netIndexByLocation[new NetLocation(element.Path, element.NetId)] = netIndex;
                }
            }

            if (_diagnostics.HasErrors)
            {
                return null;
            }

            var components = _components
                .Select(c => new CompiledComponent(c.Index, c.Path, c.DefinitionId, c.InstanceId, c.Spec, c.FirstPin, c.PinCount))
                .ToArray();

            ValidateNetsAndPrimitives(components, pins, nets);

            if (_diagnostics.HasErrors)
            {
                return null;
            }

            var ports = BuildPorts(rootPorts, netIndexByElementRoot, nets);
            ValidateRootPorts(ports, nets);

            var netIndexLookup = netIndexByLocation;
            return new CompiledCircuit(
                _rootId,
                components,
                pins,
                nets,
                ports,
                netIndexLookup,
                pinLocation,
                _diagnostics.ToArray());
        }

        private IReadOnlyList<CompiledPort> BuildPorts(
            Dictionary<string, int> rootPorts,
            Dictionary<int, int> netIndexByElementRoot,
            CompiledNet[] nets)
        {
            CircuitDefinition root = _project.RequireDefinition(_rootId);
            var ports = new List<CompiledPort>(root.Pins.Count);
            foreach (PinDeclaration pin in root.Pins)
            {
                if (!rootPorts.TryGetValue(pin.Id, out int element))
                {
                    continue;
                }

                if (!netIndexByElementRoot.TryGetValue(_sets.Find(element), out int netIndex))
                {
                    continue;
                }

                ports.Add(new CompiledPort(pin.Id, pin.Name, pin.Direction, nets[netIndex].Domain, netIndex));
            }

            return ports;
        }

        private void ValidateRootPorts(IReadOnlyList<CompiledPort> ports, CompiledNet[] nets)
        {
            foreach (CompiledPort port in ports)
            {
                CompiledNet net = nets[port.Net];
                if (port.Direction == PinDirection.Output && net.DriverPins.Length == 0)
                {
                    _diagnostics.Warning(
                        "RAD130",
                        $"Root output pin '{port.PinId}' is not driven by any component.",
                        _rootId,
                        netId: net.RepresentativeNetId,
                        pinId: port.PinId);
                }

                if (port.Direction == PinDirection.Input && net.SinkComponents.Length == 0)
                {
                    _diagnostics.Warning(
                        "RAD131",
                        $"Root input pin '{port.PinId}' does not reach any component.",
                        _rootId,
                        netId: net.RepresentativeNetId,
                        pinId: port.PinId);
                }
            }
        }

        private Dictionary<string, int> Instantiate(string definitionId, string path, int depth, string? parentInstanceId)
        {
            var interfaceNets = new Dictionary<string, int>(StringComparer.Ordinal);
            if (depth > MaxHierarchyDepth)
            {
                _diagnostics.Error(
                    "RAD120",
                    $"Definition hierarchy exceeds the maximum depth of {MaxHierarchyDepth}.",
                    definitionId,
                    parentInstanceId,
                    path: path);
                return interfaceNets;
            }

            CircuitDefinition? definition = _project.FindDefinition(definitionId);
            if (definition is null)
            {
                _diagnostics.Error("RAD124", $"Definition '{definitionId}' was not found during flattening.", definitionId, path: path);
                return interfaceNets;
            }

            var netElements = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Net net in definition.Nets)
            {
                int element = _sets.Add();
                netElements[net.Id] = element;
                _netElements.Add(new NetElement(
                    element,
                    path,
                    definition.Id,
                    net.Id,
                    net.DomainId,
                    net.InitialValue,
                    net.Label));
            }

            var primitivePins = new Dictionary<PinRef, int>();
            var childPorts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

            foreach (ComponentInstance instance in definition.Instances)
            {
                if (!instance.HasExactlyOneSource)
                {
                    continue;
                }

                if (instance.Primitive is PrimitiveSpec spec)
                {
                    IReadOnlyList<PinDescriptor> pinDescriptors = PrimitiveShapes.GetPins(spec);
                    int componentIndex = _components.Count;
                    _components.Add(new ComponentBuilder(
                        componentIndex,
                        path,
                        definition.Id,
                        instance.Id,
                        spec,
                        _pinElements.Count,
                        pinDescriptors.Count));

                    for (int local = 0; local < pinDescriptors.Count; local++)
                    {
                        PinDescriptor descriptor = pinDescriptors[local];
                        int element = _sets.Add();
                        _pinElements.Add(new PinElement(element, componentIndex, local, descriptor.Id, descriptor.Direction));
                        primitivePins[new PinRef(instance.Id, descriptor.Id)] = element;
                    }

                    continue;
                }

                string childPath = Join(path, instance.Id);
                childPorts[instance.Id] = Instantiate(instance.DefinitionId!, childPath, depth + 1, instance.Id);
            }

            foreach (Net net in definition.Nets)
            {
                int netElement = netElements[net.Id];
                foreach (PinRef pinRef in net.Pins)
                {
                    if (primitivePins.TryGetValue(pinRef, out int primitiveElement))
                    {
                        _sets.Union(netElement, primitiveElement);
                        continue;
                    }

                    if (childPorts.TryGetValue(pinRef.InstanceId, out Dictionary<string, int>? ports) &&
                        ports.TryGetValue(pinRef.PinId, out int childElement))
                    {
                        _sets.Union(netElement, childElement);
                        continue;
                    }

                    _diagnostics.Error(
                        "RAD126",
                        $"Could not resolve pin reference '{pinRef}' while flattening.",
                        definition.Id,
                        pinRef.InstanceId,
                        net.Id,
                        pinRef.PinId,
                        path);
                }
            }

            foreach (PortBinding port in definition.Ports)
            {
                if (netElements.TryGetValue(port.NetId, out int netElement))
                {
                    interfaceNets[port.PinId] = netElement;
                }
                else
                {
                    _diagnostics.Error(
                        "RAD127",
                        $"Port for pin '{port.PinId}' refers to missing net '{port.NetId}'.",
                        definition.Id,
                        pinId: port.PinId,
                        path: path);
                }
            }

            return interfaceNets;
        }

        private void ValidateNetsAndPrimitives(
            CompiledComponent[] components,
            List<CompiledPin> pins,
            CompiledNet[] nets)
        {
            foreach (CompiledNet net in nets)
            {
                if (net.DriverPins.Length > 1 && !net.Domain.IsMultipleDriverPolicy)
                {
                    _diagnostics.Warning(
                        "RAD119",
                        $"Net '{net.RepresentativeNetId}' has {net.DriverPins.Length} active drivers while domain '{net.Domain.Id}' uses a single-driver policy.",
                        net.RepresentativeDefinitionId,
                        netId: net.RepresentativeNetId,
                        path: net.RepresentativePath);
                }
            }

            foreach (CompiledComponent component in components)
            {
                var localPins = new CompiledPin[component.PinCount];
                for (int local = 0; local < component.PinCount; local++)
                {
                    localPins[local] = pins[component.FirstPin + local];
                }

                switch (component.Spec)
                {
                    case ConstantSpec constant:
                        ValidateConstant(component, localPins, constant, nets);
                        break;
                    case LookupSpec lookup:
                        ValidateLookup(component, localPins, lookup, nets);
                        break;
                    case DelaySpec delay:
                        ValidateDelay(component, localPins, delay, nets);
                        break;
                    case WaveformSpec waveform:
                        ValidateWaveform(component, localPins, waveform, nets);
                        break;
                    case ProbeSpec:
                        break;
                }
            }
        }

        private void ValidateConstant(
            CompiledComponent component,
            CompiledPin[] localPins,
            ConstantSpec constant,
            CompiledNet[] nets)
        {
            CompiledPin output = localPins[^1];
            if (output.Net < 0)
            {
                _diagnostics.Warning(
                    "RAD132",
                    "Constant output is not connected; the instance has no effect.",
                    component.DefinitionId,
                    component.InstanceId,
                    path: component.Path);
                return;
            }

            SignalDomain domain = nets[output.Net].Domain;
            if (!domain.IsValidValue(constant.Value))
            {
                _diagnostics.Error(
                    "RAD113",
                    $"Constant value {constant.Value} is outside domain '{domain.Id}' (0..{domain.ValueCount - 1}).",
                    component.DefinitionId,
                    component.InstanceId,
                    nets[output.Net].RepresentativeNetId,
                    output.PinId,
                    component.Path);
            }
        }

        private void ValidateLookup(
            CompiledComponent component,
            CompiledPin[] localPins,
            LookupSpec lookup,
            CompiledNet[] nets)
        {
            long tableEntries = 1;
            bool allInputsConnected = true;
            var sizes = new List<int>(lookup.InputCount);

            for (int i = 0; i < lookup.InputCount; i++)
            {
                CompiledPin input = localPins[i];
                if (input.Net < 0)
                {
                    allInputsConnected = false;
                    _diagnostics.Warning(
                        "RAD121",
                        $"Lookup input '{input.PinId}' is not connected.",
                        component.DefinitionId,
                        component.InstanceId,
                        pinId: input.PinId,
                        path: component.Path);
                    continue;
                }

                SignalDomain domain = nets[input.Net].Domain;
                sizes.Add(domain.ValueCount);
                tableEntries *= domain.ValueCount;
                if (tableEntries > 1_048_576)
                {
                    _diagnostics.Error(
                        "RAD133",
                        "Lookup input domains require more than 1,048,576 table entries.",
                        component.DefinitionId,
                        component.InstanceId,
                        path: component.Path);
                    return;
                }
            }

            if (allInputsConnected && tableEntries != lookup.Table.Count)
            {
                _diagnostics.Error(
                    "RAD111",
                    $"Lookup table has {lookup.Table.Count} entries but inputs with sizes [{string.Join(", ", sizes)}] require {tableEntries}.",
                    component.DefinitionId,
                    component.InstanceId,
                    path: component.Path);
            }

            CompiledPin output = localPins[^1];
            if (output.Net < 0)
            {
                _diagnostics.Warning(
                    "RAD134",
                    "Lookup output is not connected; the instance has no effect.",
                    component.DefinitionId,
                    component.InstanceId,
                    path: component.Path);
                return;
            }

            SignalDomain outputDomain = nets[output.Net].Domain;
            for (int entry = 0; entry < lookup.Table.Count; entry++)
            {
                int value = lookup.Table[entry];
                if (!outputDomain.IsValidValue(value))
                {
                    _diagnostics.Error(
                        "RAD112",
                        $"Lookup entry {entry} has value {value}, outside output domain '{outputDomain.Id}' (0..{outputDomain.ValueCount - 1}).",
                        component.DefinitionId,
                        component.InstanceId,
                        nets[output.Net].RepresentativeNetId,
                        output.PinId,
                        component.Path);
                    break;
                }
            }
        }

        private void ValidateDelay(
            CompiledComponent component,
            CompiledPin[] localPins,
            DelaySpec delay,
            CompiledNet[] nets)
        {
            CompiledPin input = localPins[0];
            CompiledPin output = localPins[1];

            if (input.Net < 0 || output.Net < 0)
            {
                _diagnostics.Warning(
                    "RAD135",
                    "Delay input or output is not connected.",
                    component.DefinitionId,
                    component.InstanceId,
                    path: component.Path);
                return;
            }

            CompiledNet inputNet = nets[input.Net];
            CompiledNet outputNet = nets[output.Net];
            if (!string.Equals(inputNet.Domain.Id, outputNet.Domain.Id, StringComparison.Ordinal))
            {
                _diagnostics.Error(
                    "RAD116",
                    $"Delay input domain '{inputNet.Domain.Id}' differs from output domain '{outputNet.Domain.Id}'. Use a lookup for an explicit domain bridge.",
                    component.DefinitionId,
                    component.InstanceId,
                    outputNet.RepresentativeNetId,
                    output.PinId,
                    component.Path);
                return;
            }

            if (!outputNet.Domain.IsValidValue(delay.InitialValue))
            {
                _diagnostics.Error(
                    "RAD114",
                    $"Delay initial value {delay.InitialValue} is outside domain '{outputNet.Domain.Id}'.",
                    component.DefinitionId,
                    component.InstanceId,
                    outputNet.RepresentativeNetId,
                    output.PinId,
                    component.Path);
            }
        }

        private void ValidateWaveform(
            CompiledComponent component,
            CompiledPin[] localPins,
            WaveformSpec waveform,
            CompiledNet[] nets)
        {
            CompiledPin output = localPins[0];
            if (output.Net < 0)
            {
                _diagnostics.Warning(
                    "RAD136",
                    "Waveform output is not connected; the instance has no effect.",
                    component.DefinitionId,
                    component.InstanceId,
                    path: component.Path);
                return;
            }

            SignalDomain domain = nets[output.Net].Domain;
            foreach (int value in waveform.Values)
            {
                if (!domain.IsValidValue(value))
                {
                    _diagnostics.Error(
                        "RAD115",
                        $"Waveform value {value} is outside domain '{domain.Id}'.",
                        component.DefinitionId,
                        component.InstanceId,
                        nets[output.Net].RepresentativeNetId,
                        output.PinId,
                        component.Path);
                    break;
                }
            }
        }

        private static string Join(string path, string instanceId) =>
            path.Length == 0 ? instanceId : $"{path}/{instanceId}";
    }

    private sealed class ComponentBuilder
    {
        public ComponentBuilder(
            int index,
            string path,
            string definitionId,
            string instanceId,
            PrimitiveSpec spec,
            int firstPin,
            int pinCount)
        {
            Index = index;
            Path = path;
            DefinitionId = definitionId;
            InstanceId = instanceId;
            Spec = spec;
            FirstPin = firstPin;
            PinCount = pinCount;
        }

        public int Index { get; }

        public string Path { get; }

        public string DefinitionId { get; }

        public string InstanceId { get; }

        public PrimitiveSpec Spec { get; }

        public int FirstPin { get; }

        public int PinCount { get; }
    }

    private sealed record PinElement(int Element, int Component, int LocalPin, string PinId, PinDirection Direction);

    private sealed record NetElement(
        int Element,
        string Path,
        string DefinitionId,
        string NetId,
        string DomainId,
        int? InitialValue,
        string? Label);

    private sealed class NetGroupBuilder
    {
        public NetGroupBuilder(
            int index,
            string domainId,
            int? initialValue,
            string? label,
            string path,
            string netId,
            string definitionId)
        {
            Index = index;
            DomainId = domainId;
            InitialValue = initialValue;
            Label = label;
            Path = path;
            NetId = netId;
            DefinitionId = definitionId;
        }

        public int Index { get; }

        public string DomainId { get; }

        public int? InitialValue { get; set; }

        public string? Label { get; set; }

        public string Path { get; }

        public string NetId { get; }

        public string DefinitionId { get; }
    }

    private sealed class UnionFind
    {
        private int[] _parent = new int[64];
        private byte[] _rank = new byte[64];
        private int _count;

        public int Add()
        {
            if (_count == _parent.Length)
            {
                Array.Resize(ref _parent, _parent.Length * 2);
                Array.Resize(ref _rank, _rank.Length * 2);
            }

            int index = _count++;
            _parent[index] = index;
            _rank[index] = 0;
            return index;
        }

        public int Find(int value)
        {
            int root = value;
            while (_parent[root] != root)
            {
                root = _parent[root];
            }

            while (_parent[value] != value)
            {
                int next = _parent[value];
                _parent[value] = root;
                value = next;
            }

            return root;
        }

        public void Union(int left, int right)
        {
            int leftRoot = Find(left);
            int rightRoot = Find(right);
            if (leftRoot == rightRoot)
            {
                return;
            }

            if (_rank[leftRoot] < _rank[rightRoot])
            {
                _parent[leftRoot] = rightRoot;
            }
            else if (_rank[leftRoot] > _rank[rightRoot])
            {
                _parent[rightRoot] = leftRoot;
            }
            else
            {
                _parent[rightRoot] = leftRoot;
                _rank[leftRoot]++;
            }
        }
    }
}
