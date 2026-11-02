using Radix.Core.Model;

namespace Radix.Core.Electrical;

/// <summary>DC electrical component kinds supported by the authored circuit model.</summary>
public enum ElectricalComponentKind
{
    Resistor,
    VoltageSource,
    CurrentSource,
    Capacitor,
    Inductor,

    /// <summary>A three-terminal voltage-controlled square-law MOSFET.</summary>
    Mosfet,
}

/// <summary>Polarity metadata for an authored electrical terminal.</summary>
public enum ElectricalTerminalPolarity
{
    Passive,
    Positive,
    Negative,
}

/// <summary>A terminal shape shared by every instance of an electrical component kind.</summary>
public readonly record struct ElectricalTerminalTemplate(
    string Id,
    string Name,
    ElectricalTerminalPolarity Polarity);

/// <summary>Human-facing metadata and required terminal shape for a component kind.</summary>
public static class ElectricalComponentMetadata
{
    private static readonly ElectricalTerminalTemplate[] ResistorTerminals =
    [
        new("a", "A", ElectricalTerminalPolarity.Passive),
        new("b", "B", ElectricalTerminalPolarity.Passive),
    ];

    private static readonly ElectricalTerminalTemplate[] SourceTerminals =
    [
        new("a", "Positive", ElectricalTerminalPolarity.Positive),
        new("b", "Negative", ElectricalTerminalPolarity.Negative),
    ];

    private static readonly ElectricalTerminalTemplate[] MosfetTerminals =
    [
        new("drain", "Drain", ElectricalTerminalPolarity.Passive),
        new("gate", "Gate", ElectricalTerminalPolarity.Passive),
        new("source", "Source", ElectricalTerminalPolarity.Passive),
    ];

    public static IReadOnlyList<ElectricalTerminalTemplate> Terminals(ElectricalComponentKind kind) =>
        kind switch
        {
            ElectricalComponentKind.Mosfet => MosfetTerminals,
            _ when HasPassiveTerminals(kind) => ResistorTerminals,
            _ => SourceTerminals,
        };

    /// <summary>Whether the kind's terminals are interchangeable passive terminals.</summary>
    public static bool HasPassiveTerminals(ElectricalComponentKind kind) =>
        kind is ElectricalComponentKind.Resistor
            or ElectricalComponentKind.Capacitor
            or ElectricalComponentKind.Inductor;

    /// <summary>Whether the kind is a three-terminal voltage-controlled MOSFET.</summary>
    public static bool IsMosfet(ElectricalComponentKind kind) => kind is ElectricalComponentKind.Mosfet;

    /// <summary>
    /// Whether the kind's primary parameter must be strictly positive. A MOSFET's primary
    /// parameter is its transconductance, which cannot be zero or negative.
    /// </summary>
    public static bool RequiresPositiveValue(ElectricalComponentKind kind) =>
        HasPassiveTerminals(kind) || IsMosfet(kind);

    /// <summary>The stable JSON name of a MOSFET channel.</summary>
    public static string ChannelName(DcMosfetChannel channel) =>
        channel switch
        {
            DcMosfetChannel.NChannel => "n",
            DcMosfetChannel.PChannel => "p",
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };

    public static DcMosfetChannel ParseChannel(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "n" or "n-channel" or "nchannel" => DcMosfetChannel.NChannel,
            "p" or "p-channel" or "pchannel" => DcMosfetChannel.PChannel,
            null or "" => throw new ArgumentException(
                "A MOSFET needs a channel: 'n' or 'p'.", nameof(value)),
            _ => throw new ArgumentException(
                $"Unknown MOSFET channel '{value}'; expected 'n' or 'p'.", nameof(value)),
        };

    /// <summary>Whether the kind stores state, so it can carry an initial value.</summary>
    public static bool HasState(ElectricalComponentKind kind) =>
        kind is ElectricalComponentKind.Capacitor or ElectricalComponentKind.Inductor;

    /// <summary>Whether the kind is an independent source, so a transient run can drive it.</summary>
    public static bool SupportsWaveform(ElectricalComponentKind kind) =>
        kind is ElectricalComponentKind.VoltageSource or ElectricalComponentKind.CurrentSource;

    public static string ParameterName(ElectricalComponentKind kind) =>
        kind switch
        {
            ElectricalComponentKind.Resistor => "Resistance",
            ElectricalComponentKind.VoltageSource => "Voltage",
            ElectricalComponentKind.CurrentSource => "Current",
            ElectricalComponentKind.Capacitor => "Capacitance",
            ElectricalComponentKind.Inductor => "Inductance",
            ElectricalComponentKind.Mosfet => "Transconductance",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public static string Unit(ElectricalComponentKind kind) =>
        kind switch
        {
            ElectricalComponentKind.Resistor => "ohm",
            ElectricalComponentKind.VoltageSource => "V",
            ElectricalComponentKind.CurrentSource => "A",
            ElectricalComponentKind.Capacitor => "F",
            ElectricalComponentKind.Inductor => "H",
            ElectricalComponentKind.Mosfet => "A/V^2",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>The stable JSON name of a component kind.</summary>
    public static string KindName(ElectricalComponentKind kind) =>
        kind switch
        {
            ElectricalComponentKind.Resistor => "resistor",
            ElectricalComponentKind.VoltageSource => "voltage-source",
            ElectricalComponentKind.CurrentSource => "current-source",
            ElectricalComponentKind.Capacitor => "capacitor",
            ElectricalComponentKind.Inductor => "inductor",
            ElectricalComponentKind.Mosfet => "mosfet",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}

/// <summary>A named electrical node. Ground is identified separately by the circuit.</summary>
public sealed class ElectricalNode
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public override string ToString() => Id;
}

/// <summary>
/// An explicit electrical terminal binding. Its ID and polarity are validated against
/// the component kind's metadata, and its node ID is resolved in the owning circuit.
/// </summary>
public sealed class ElectricalTerminal
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ElectricalTerminalPolarity Polarity { get; set; }

    public string NodeId { get; set; } = string.Empty;

    public override string ToString() => $"{Id}->{NodeId}";
}

/// <summary>
/// One piecewise-linear breakpoint of an authored source drive: the source value at an
/// absolute time from the start of the run, in the source's unit.
/// </summary>
public sealed class ElectricalWaveformPoint
{
    public double Time { get; set; }

    public double Value { get; set; }

    public override string ToString() => $"{Time}:{Value}";
}

/// <summary>One authored electrical component with an explicit value and terminal bindings.</summary>
public sealed class ElectricalComponent
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ElectricalComponentKind Kind { get; set; }

    /// <summary>
    /// Resistance, voltage, current, capacitance, or inductance in the unit reported by
    /// <see cref="Unit"/>.
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// Optional initial state for a reactive component: the capacitor voltage in volts
    /// or the inductor current in amperes at the start of a transient run. Null uses the
    /// circuit's DC operating point. Components without state reject a value.
    /// </summary>
    public double? InitialValue { get; set; }

    /// <summary>
    /// MOSFET channel polarity. Meaningful only when <see cref="Kind"/> is
    /// <see cref="ElectricalComponentKind.Mosfet"/>; other kinds ignore it and the
    /// serializer does not store it for them.
    /// </summary>
    public DcMosfetChannel Channel { get; set; } = DcMosfetChannel.NChannel;

    /// <summary>
    /// MOSFET threshold voltage magnitude in volts (negative for a depletion device).
    /// Meaningful only for <see cref="ElectricalComponentKind.Mosfet"/>; other kinds
    /// ignore it and the serializer does not store it for them.
    /// </summary>
    public double ThresholdVoltage { get; set; } = 1;

    /// <summary>
    /// Optional piecewise-linear drive for an independent source, in the source's unit.
    /// Empty keeps <see cref="Value"/> constant, which is also what a DC solve uses.
    /// Breakpoints use absolute times, are evaluated by linear interpolation, and clamp
    /// outside their range. Capacitors, inductors, and resistors reject a waveform.
    /// </summary>
    public List<ElectricalWaveformPoint> Waveform { get; } = [];

    public List<ElectricalTerminal> Terminals { get; } = [];

    public string ParameterName => ElectricalComponentMetadata.ParameterName(Kind);

    public string Unit => ElectricalComponentMetadata.Unit(Kind);

    public ElectricalTerminal? FindTerminal(string terminalId) =>
        Terminals.FirstOrDefault(terminal =>
            string.Equals(terminal.Id, terminalId, StringComparison.Ordinal));

    public override string ToString() => Id;
}

/// <summary>
/// An authored DC circuit in a RADIX project. It owns its own nodes and component
/// terminals; it never reuses digital nets or interprets signal-domain indices as
/// electrical quantities.
/// </summary>
public sealed class ElectricalCircuit
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The node used as zero volts.</summary>
    public string GroundNodeId { get; set; } = string.Empty;

    public List<ElectricalNode> Nodes { get; } = [];

    public List<ElectricalComponent> Components { get; } = [];

    public ElectricalNode? FindNode(string nodeId) =>
        Nodes.FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal));

    public ElectricalComponent? FindComponent(string componentId) =>
        Components.FirstOrDefault(component =>
            string.Equals(component.Id, componentId, StringComparison.Ordinal));

    /// <summary>Validates and bridges this authored circuit to the independent solver.</summary>
    public DcNetlist ToDcNetlist()
    {
        DiagnosticBag diagnostics = ElectricalCircuitValidator.Validate(this);
        if (diagnostics.HasErrors)
        {
            throw new ElectricalCircuitValidationException(diagnostics);
        }

        var netlist = new DcNetlist(GroundNodeId);
        foreach (ElectricalComponent component in Components)
        {
            (string nodeA, string nodeB) = ChannelNodes(component);
            switch (component.Kind)
            {
                case ElectricalComponentKind.Resistor:
                    netlist.AddResistor(component.Id, nodeA, nodeB, component.Value);
                    break;
                case ElectricalComponentKind.VoltageSource:
                    netlist.AddVoltageSource(component.Id, nodeA, nodeB, component.Value);
                    break;
                case ElectricalComponentKind.CurrentSource:
                    netlist.AddCurrentSource(component.Id, nodeA, nodeB, component.Value);
                    break;
                case ElectricalComponentKind.Capacitor:
                    // A capacitor is an open circuit at DC, so it contributes no DC element.
                    break;
                case ElectricalComponentKind.Inductor:
                    // An ideal inductor is a zero-volt short at DC.
                    netlist.AddVoltageSource(component.Id, nodeA, nodeB, 0);
                    break;
                case ElectricalComponentKind.Mosfet:
                    netlist.AddMosfet(
                        component.Id,
                        nodeA,
                        component.FindTerminal("gate")!.NodeId,
                        nodeB,
                        component.Channel,
                        component.ThresholdVoltage,
                        component.Value);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported electrical component kind '{component.Kind}'.");
            }
        }

        return netlist;
    }

    /// <summary>Whether any component stores energy, so a transient run can do more than a DC solve.</summary>
    public bool HasReactiveComponents =>
        Components.Any(component => ElectricalComponentMetadata.HasState(component.Kind));

    /// <summary>
    /// Validates and bridges this authored circuit to the transient solver. Capacitors
    /// and inductors become the solver's reactive elements, their optional initial
    /// values become the solver's initial state, all other kinds map one to one, and a
    /// source's optional piecewise-linear waveform drives it in time.
    /// </summary>
    public TransientNetlist ToTransientNetlist()
    {
        DiagnosticBag diagnostics = ElectricalCircuitValidator.Validate(this);
        if (diagnostics.HasErrors)
        {
            throw new ElectricalCircuitValidationException(diagnostics);
        }

        var netlist = new TransientNetlist(GroundNodeId);
        foreach (ElectricalComponent component in Components)
        {
            (string nodeA, string nodeB) = ChannelNodes(component);
            switch (component.Kind)
            {
                case ElectricalComponentKind.Resistor:
                    netlist.AddResistor(component.Id, nodeA, nodeB, component.Value);
                    break;
                case ElectricalComponentKind.VoltageSource:
                    netlist.AddVoltageSource(component.Id, nodeA, nodeB, component.Value);
                    ApplyWaveform(netlist, component);
                    break;
                case ElectricalComponentKind.CurrentSource:
                    netlist.AddCurrentSource(component.Id, nodeA, nodeB, component.Value);
                    ApplyWaveform(netlist, component);
                    break;
                case ElectricalComponentKind.Capacitor:
                    netlist.AddCapacitor(component.Id, nodeA, nodeB, component.Value);
                    if (component.InitialValue is double volts)
                    {
                        netlist.SetCapacitorInitialVoltage(component.Id, volts);
                    }

                    break;
                case ElectricalComponentKind.Inductor:
                    netlist.AddInductor(component.Id, nodeA, nodeB, component.Value);
                    if (component.InitialValue is double amperes)
                    {
                        netlist.SetInductorInitialCurrent(component.Id, amperes);
                    }

                    break;
                case ElectricalComponentKind.Mosfet:
                    netlist.AddMosfet(
                        component.Id,
                        nodeA,
                        component.FindTerminal("gate")!.NodeId,
                        nodeB,
                        component.Channel,
                        component.ThresholdVoltage,
                        component.Value);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported electrical component kind '{component.Kind}'.");
            }
        }

        return netlist;
    }

    /// <summary>
    /// The two nodes that carry the branch current, reported as node A and node B. A
    /// two-terminal part uses its <c>a</c>/<c>b</c> terminals; a MOSFET uses its drain and
    /// source, because its gate is an insulated control terminal rather than a branch end.
    /// Validation has already proved those terminals exist, so a null here would be a
    /// validator gap rather than a user error.
    /// </summary>
    private static (string NodeA, string NodeB) ChannelNodes(ElectricalComponent component) =>
        ElectricalComponentMetadata.IsMosfet(component.Kind)
            ? (component.FindTerminal("drain")!.NodeId, component.FindTerminal("source")!.NodeId)
            : (component.FindTerminal("a")!.NodeId, component.FindTerminal("b")!.NodeId);

    private static void ApplyWaveform(TransientNetlist netlist, ElectricalComponent component)
    {
        if (component.Waveform.Count == 0)
        {
            return;
        }

        netlist.SetWaveform(
            component.Id,
            component.Waveform.Select(point => new TransientWaveformPoint(point.Time, point.Value)));
    }

    public override string ToString() => Id;
}

/// <summary>Thrown when an authored electrical circuit cannot be mapped to a netlist.</summary>
public sealed class ElectricalCircuitValidationException : Exception
{
    public ElectricalCircuitValidationException(DiagnosticBag diagnostics)
        : base("Electrical circuit validation failed.")
    {
        Diagnostics = diagnostics;
    }

    public DiagnosticBag Diagnostics { get; }
}

/// <summary>
/// Structural validation for authored electrical circuits. This is separate from
/// digital circuit validation so an electrical asset never changes digital compile
/// semantics. The solver bridge refuses to run when any error is present.
/// </summary>
public static class ElectricalCircuitValidator
{
    public static DiagnosticBag Validate(ElectricalCircuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        var diagnostics = new DiagnosticBag();

        if (!Identifiers.IsValid(circuit.Id))
        {
            diagnostics.Error(
                "RAD-ELEC-001",
                $"Electrical circuit has an invalid ID. {Identifiers.Rule}",
                path: ElectricalPath(circuit, null));
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ElectricalNode node in circuit.Nodes)
        {
            if (!Identifiers.IsValid(node.Id))
            {
                diagnostics.Error(
                    "RAD-ELEC-002",
                    $"Electrical node has an invalid ID. {Identifiers.Rule}",
                    path: ElectricalPath(circuit, null, node.Id));
            }
            else if (!nodeIds.Add(node.Id))
            {
                diagnostics.Error(
                    "RAD-ELEC-003",
                    $"Duplicate electrical node ID '{node.Id}'.",
                    path: ElectricalPath(circuit, null, node.Id));
            }
        }

        if (string.IsNullOrWhiteSpace(circuit.GroundNodeId) || !nodeIds.Contains(circuit.GroundNodeId))
        {
            diagnostics.Error(
                "RAD-ELEC-004",
                $"Electrical circuit ground node '{circuit.GroundNodeId}' does not exist.",
                path: ElectricalPath(circuit, null));
        }

        var componentIds = new HashSet<string>(StringComparer.Ordinal);
        int groundedTerminals = 0;
        foreach (ElectricalComponent component in circuit.Components)
        {
            string path = ElectricalPath(circuit, component.Id, null);
            if (!Identifiers.IsValid(component.Id))
            {
                diagnostics.Error(
                    "RAD-ELEC-005",
                    $"Electrical component has an invalid ID. {Identifiers.Rule}",
                    path: path);
            }
            else if (!componentIds.Add(component.Id))
            {
                diagnostics.Error(
                    "RAD-ELEC-006",
                    $"Duplicate electrical component ID '{component.Id}'.",
                    path: path);
            }

            bool needsPositiveValue = ElectricalComponentMetadata.RequiresPositiveValue(component.Kind);
            if (!double.IsFinite(component.Value) || (needsPositiveValue && component.Value <= 0))
            {
                diagnostics.Error(
                    "RAD-ELEC-007",
                    $"Component '{component.Id}' has an invalid {component.ParameterName.ToLowerInvariant()} value.",
                    path: path);
            }

            if (ElectricalComponentMetadata.IsMosfet(component.Kind))
            {
                if (!double.IsFinite(component.ThresholdVoltage))
                {
                    diagnostics.Error(
                        "RAD-ELEC-021",
                        $"Component '{component.Id}' has a non-finite threshold voltage; a MOSFET threshold must be a finite number of volts.",
                        path: path);
                }

                if (!Enum.IsDefined(component.Channel))
                {
                    diagnostics.Error(
                        "RAD-ELEC-022",
                        $"Component '{component.Id}' has an invalid MOSFET channel '{component.Channel}'.",
                        path: path);
                }
            }

            if (component.InitialValue is double initialValue)
            {
                if (!ElectricalComponentMetadata.HasState(component.Kind))
                {
                    diagnostics.Error(
                        "RAD-ELEC-015",
                        $"Component '{component.Id}' is a {component.Kind} and cannot have an initial value; only capacitors and inductors store state.",
                        path: path);
                }
                else if (!double.IsFinite(initialValue))
                {
                    diagnostics.Error(
                        "RAD-ELEC-016",
                        $"Component '{component.Id}' has a non-finite initial value.",
                        path: path);
                }
            }

            if (component.Waveform.Count > 0)
            {
                if (!ElectricalComponentMetadata.SupportsWaveform(component.Kind))
                {
                    diagnostics.Error(
                        "RAD-ELEC-017",
                        $"Component '{component.Id}' is a {component.Kind} and cannot have a waveform; only independent sources are driven in time.",
                        path: path);
                }
                else if (component.Waveform.Count < 2)
                {
                    diagnostics.Error(
                        "RAD-ELEC-018",
                        $"Component '{component.Id}' has a waveform with {component.Waveform.Count} breakpoint(s); a piecewise-linear drive needs at least two.",
                        path: path);
                }
                else
                {
                    double previousTime = double.NegativeInfinity;
                    foreach (ElectricalWaveformPoint point in component.Waveform)
                    {
                        if (!double.IsFinite(point.Time) || !double.IsFinite(point.Value))
                        {
                            diagnostics.Error(
                                "RAD-ELEC-019",
                                $"Component '{component.Id}' has a non-finite waveform breakpoint.",
                                path: path);
                            break;
                        }

                        if (point.Time <= previousTime)
                        {
                            diagnostics.Error(
                                "RAD-ELEC-020",
                                $"Component '{component.Id}' waveform breakpoints must have strictly increasing times; {point.Time} follows {previousTime}.",
                                path: path);
                            break;
                        }

                        previousTime = point.Time;
                    }
                }
            }

            IReadOnlyList<ElectricalTerminalTemplate> templates =
                ElectricalComponentMetadata.Terminals(component.Kind);
            foreach (ElectricalTerminalTemplate template in templates)
            {
                ElectricalTerminal[] matches = component.Terminals
                    .Where(terminal => string.Equals(terminal.Id, template.Id, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length == 0)
                {
                    diagnostics.Error(
                        "RAD-ELEC-008",
                        $"Component '{component.Id}' is missing terminal '{template.Id}' ({template.Name}).",
                        path: path);
                    continue;
                }

                if (matches.Length > 1)
                {
                    diagnostics.Error(
                        "RAD-ELEC-009",
                        $"Component '{component.Id}' repeats terminal '{template.Id}'.",
                        path: path);
                    continue;
                }

                ElectricalTerminal terminal = matches[0];
                if (terminal.Polarity != template.Polarity)
                {
                    diagnostics.Error(
                        "RAD-ELEC-010",
                        $"Terminal '{component.Id}.{terminal.Id}' has polarity '{terminal.Polarity}', expected '{template.Polarity}'.",
                        path: path);
                }

                if (!nodeIds.Contains(terminal.NodeId))
                {
                    diagnostics.Error(
                        "RAD-ELEC-011",
                        $"Terminal '{component.Id}.{terminal.Id}' refers to unknown node '{terminal.NodeId}'.",
                        path: path);
                }
                else if (string.Equals(terminal.NodeId, circuit.GroundNodeId, StringComparison.Ordinal))
                {
                    groundedTerminals++;
                }
            }

            foreach (ElectricalTerminal terminal in component.Terminals)
            {
                if (templates.All(template => !string.Equals(template.Id, terminal.Id, StringComparison.Ordinal)))
                {
                    diagnostics.Error(
                        "RAD-ELEC-012",
                        $"Component '{component.Id}' has unexpected terminal '{terminal.Id}'.",
                        path: path);
                }
            }

            // Only the channel pair must be distinct. A MOSFET may tie its gate to its
            // drain (diode-connected) or to its source (Vgs = 0), which are both normal
            // circuits, so the gate is deliberately not part of this check.
            (string first, string second) = ElectricalComponentMetadata.IsMosfet(component.Kind)
                ? ("drain", "source")
                : ("a", "b");
            ElectricalTerminal? terminalA = component.FindTerminal(first);
            ElectricalTerminal? terminalB = component.FindTerminal(second);
            if (terminalA is not null &&
                terminalB is not null &&
                !string.IsNullOrWhiteSpace(terminalA.NodeId) &&
                string.Equals(terminalA.NodeId, terminalB.NodeId, StringComparison.Ordinal))
            {
                diagnostics.Error(
                    "RAD-ELEC-013",
                    $"Component '{component.Id}' connects both terminals to node '{terminalA.NodeId}'.",
                    path: path);
            }
        }

        if (circuit.Components.Count > 0 && groundedTerminals == 0)
        {
            diagnostics.Error(
                "RAD-ELEC-014",
                $"Electrical circuit '{circuit.Id}' has no component terminal connected to ground '{circuit.GroundNodeId}'.",
                path: ElectricalPath(circuit, null));
        }

        return diagnostics;
    }

    private static string ElectricalPath(
        ElectricalCircuit circuit,
        string? componentId,
        string? nodeId = null) =>
        nodeId is null && componentId is null
            ? $"electrical/{circuit.Id}"
            : componentId is not null
                ? $"electrical/{circuit.Id}/{componentId}"
                : $"electrical/{circuit.Id}/node/{nodeId}";
}
