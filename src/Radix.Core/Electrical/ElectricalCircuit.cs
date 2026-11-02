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

    /// <summary>
    /// An instance of another authored circuit, expanded into this circuit's netlist. Its terminals
    /// are the referenced circuit's ports, so the internal schematic is reused rather than replaced
    /// by a behavioural model.
    /// </summary>
    Subcircuit,
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

    /// <summary>
    /// The static terminal shape of a built-in kind. A subcircuit has no fixed shape: its terminals
    /// are the referenced circuit's ports, so this returns an empty list and the hierarchy validator
    /// resolves them against the project.
    /// </summary>
    public static IReadOnlyList<ElectricalTerminalTemplate> Terminals(ElectricalComponentKind kind) =>
        kind switch
        {
            ElectricalComponentKind.Mosfet => MosfetTerminals,
            ElectricalComponentKind.Subcircuit => [],
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

    /// <summary>Whether the kind is an instance of another authored circuit.</summary>
    public static bool IsSubcircuit(ElectricalComponentKind kind) => kind is ElectricalComponentKind.Subcircuit;

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
            ElectricalComponentKind.Subcircuit => "Circuit",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>
    /// The terminal shape a subcircuit instance must have, resolved from the referenced circuit's
    /// ports. A missing reference or a port with an invalid ID yields no template, which validation
    /// reports separately instead of inventing a terminal.
    /// </summary>
    public static IReadOnlyList<ElectricalTerminalTemplate> SubcircuitTerminals(
        ElectricalCircuit? referencedCircuit)
    {
        if (referencedCircuit is null)
        {
            return [];
        }

        return referencedCircuit.Ports
            .Where(port => Identifiers.IsValid(port.Id))
            .Select(port => new ElectricalTerminalTemplate(
                port.Id,
                string.IsNullOrWhiteSpace(port.Name) ? port.Id : port.Name,
                ElectricalTerminalPolarity.Passive))
            .ToArray();
    }

    public static string Unit(ElectricalComponentKind kind) =>
        kind switch
        {
            ElectricalComponentKind.Resistor => "ohm",
            ElectricalComponentKind.VoltageSource => "V",
            ElectricalComponentKind.CurrentSource => "A",
            ElectricalComponentKind.Capacitor => "F",
            ElectricalComponentKind.Inductor => "H",
            ElectricalComponentKind.Mosfet => "A/V^2",
            ElectricalComponentKind.Subcircuit => string.Empty,
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
            ElectricalComponentKind.Subcircuit => "subcircuit",
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
/// A named interface node of a reusable authored circuit. A port is not a component and carries no
/// current of its own: a subcircuit instance binds one terminal per port to a node in its parent, and
/// the referenced circuit's internal node is then the same electrical node as that parent node.
/// </summary>
public sealed class ElectricalPort
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The node inside the owning circuit that this port exposes.</summary>
    public string NodeId { get; set; } = string.Empty;

    public override string ToString() => $"{Id}->{NodeId}";
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
    /// MOSFET channel-length modulation after SPICE's <c>lambda</c>, in 1/V. Zero, the default,
    /// means the saturation current does not depend on the drain voltage. Meaningful only for
    /// <see cref="ElectricalComponentKind.Mosfet"/>; other kinds ignore it and the serializer
    /// does not store it for them.
    /// </summary>
    public double ChannelLengthModulation { get; set; }

    /// <summary>
    /// MOSFET gate-source capacitance in farads. Zero, the default, means the gate holds no charge
    /// and the device adds no dynamics. A DC solve ignores it either way, because charge only
    /// shapes how fast a node moves. Meaningful only for <see cref="ElectricalComponentKind.Mosfet"/>;
    /// other kinds ignore it and the serializer does not store it for them.
    /// </summary>
    public double GateSourceCapacitance { get; set; }

    /// <summary>
    /// MOSFET gate-drain capacitance in farads, the feedback path that couples a moving drain back
    /// into its own gate. Zero by default; ignored by a DC solve.
    /// </summary>
    public double GateDrainCapacitance { get; set; }

    /// <summary>
    /// MOSFET drain-source capacitance in farads. This is the device's own load on its output: with
    /// it, a drain node has a speed of its own instead of needing an explicit capacitor beside it.
    /// Zero by default; ignored by a DC solve.
    /// </summary>
    public double DrainSourceCapacitance { get; set; }

    /// <summary>
    /// Whether this device's gate capacitances follow the bias it is solved at, rather than staying
    /// at their authored values for the whole run. The solver applies the setting to the whole
    /// netlist, so enabling it on any device of a circuit makes that circuit's run bias-dependent;
    /// keeping it per device records which device asked for it rather than pretending the model is
    /// mixed within one solve.
    /// </summary>
    public bool BiasDependentCapacitance { get; set; }

    /// <summary>
    /// The referenced authored circuit when <see cref="Kind"/> is
    /// <see cref="ElectricalComponentKind.Subcircuit"/>; empty for every built-in kind. The
    /// component's terminals are the referenced circuit's ports, and each instance is expanded into
    /// the parent netlist under a prefix derived from this component's ID.
    /// </summary>
    public string CircuitId { get; set; } = string.Empty;

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

    /// <summary>
    /// Ascending voltage boundaries for reading this circuit's nodes as levels, empty when the
    /// circuit authors none and a reading should fall back to equal thirds of its rail. Stored on
    /// the circuit rather than per node because a circuit has one supply and one signalling scheme:
    /// two nodes reading against different boundaries would not describe a single logic family.
    /// </summary>
    public List<double> LevelThresholds { get; } = [];

    /// <summary>
    /// How many ticks a measured propagation delay may differ from the declared one before a run
    /// fails. Zero demands the declared number exactly. The tolerance is authored because a static
    /// transport delay cannot describe an asymmetric network - the sweep rises in 23 ticks and falls
    /// in 44 - and pretending otherwise would make every such circuit uncheckable.
    /// </summary>
    public int DelayToleranceTicks { get; set; }

    /// <summary>
    /// Tolerances that apply to a single transition, keyed by the output level it leaves and the one
    /// it reaches, so a file can demand exactness where it is confident and allow for a slow
    /// direction where it is not. An entry replaces the circuit-wide tolerance for that transition
    /// only; everything else falls back to it.
    /// </summary>
    public List<ElectricalDelayTolerance> DelayTolerances { get; } = [];

    /// <summary>
    /// Digital pins this circuit's nodes drive, with the rate that converts solved seconds into
    /// simulator ticks. Stored on the circuit because a binding is a claim about what this network
    /// means to the rest of the project, and one node may feed several pins.
    /// </summary>
    public List<ElectricalDriveBinding> DriveBindings { get; } = [];

    /// <summary>
    /// Named interface nodes exposed to a parent circuit that instantiates this one. A circuit may
    /// have no ports when it is only solved directly; a circuit used as a subcircuit must expose at
    /// least one, and must expose its ground node.
    /// </summary>
    public List<ElectricalPort> Ports { get; } = [];

    public ElectricalNode? FindNode(string nodeId) =>
        Nodes.FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal));

    public ElectricalComponent? FindComponent(string componentId) =>
        Components.FirstOrDefault(component =>
            string.Equals(component.Id, componentId, StringComparison.Ordinal));

    /// <summary>
    /// Validates and bridges this authored circuit to the independent solver. A circuit containing
    /// subcircuits needs <paramref name="resolve"/> so the referenced circuits can be validated and
    /// expanded; a flat circuit needs no project context and may omit it.
    /// </summary>
    public DcNetlist ToDcNetlist(Func<string, ElectricalCircuit?>? resolve = null)
    {
        DiagnosticBag diagnostics = ElectricalCircuitValidator.Validate(this, resolve);
        if (diagnostics.HasErrors)
        {
            throw new ElectricalCircuitValidationException(diagnostics);
        }

        return ElectricalCircuitNetlist.BuildDc(this, resolve);
    }

    /// <summary>
    /// Whether any component stores energy, so a transient run can do more than a DC solve. A
    /// subcircuit is counted conservatively: its internals are not followed without a project
    /// resolver, and offering a transient run that turns out to be purely resistive is harmless.
    /// </summary>
    public bool HasReactiveComponents =>
        Components.Any(component =>
            ElectricalComponentMetadata.HasState(component.Kind) ||
            ElectricalComponentMetadata.IsSubcircuit(component.Kind));

    /// <summary>
    /// Validates and bridges this authored circuit to the transient solver. Capacitors
    /// and inductors become the solver's reactive elements, their optional initial
    /// values become the solver's initial state, all other kinds map one to one, and a
    /// source's optional piecewise-linear waveform drives it in time. Subcircuits are
    /// expanded recursively into the same netlist, with their internal node and element
    /// IDs prefixed by the instance path.
    /// </summary>
    public TransientNetlist ToTransientNetlist(Func<string, ElectricalCircuit?>? resolve = null)
    {
        DiagnosticBag diagnostics = ElectricalCircuitValidator.Validate(this, resolve);
        if (diagnostics.HasErrors)
        {
            throw new ElectricalCircuitValidationException(diagnostics);
        }

        return ElectricalCircuitNetlist.BuildTransient(this, resolve);
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
    public static DiagnosticBag Validate(ElectricalCircuit circuit) => Validate(circuit, null);

    /// <summary>
    /// Validates one circuit and, when it contains subcircuits, the referenced hierarchy. A flat
    /// circuit needs no resolver, so existing callers remain valid; a subcircuit without one is an
    /// error rather than being silently treated as an empty component.
    /// </summary>
    public static DiagnosticBag Validate(
        ElectricalCircuit circuit,
        Func<string, ElectricalCircuit?>? resolve)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        DiagnosticBag diagnostics = ValidateOwn(circuit);
        if (!circuit.Components.Any(component =>
            ElectricalComponentMetadata.IsSubcircuit(component.Kind)))
        {
            return diagnostics;
        }

        if (resolve is null)
        {
            diagnostics.Error(
                "RAD-ELEC-029",
                $"Electrical circuit '{circuit.Id}' contains a subcircuit, which can only be resolved with the project's circuits.",
                path: ElectricalPath(circuit, null));
            return diagnostics;
        }

        var validated = new HashSet<string>(StringComparer.Ordinal) { circuit.Id };
        var stack = new HashSet<string>(StringComparer.Ordinal) { circuit.Id };
        ValidateHierarchy(circuit, resolve, diagnostics, validated, stack);
        return diagnostics;
    }

    private static DiagnosticBag ValidateOwn(ElectricalCircuit circuit)
    {
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

        var portIds = new HashSet<string>(StringComparer.Ordinal);
        var portNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ElectricalPort port in circuit.Ports)
        {
            if (!Identifiers.IsValid(port.Id))
            {
                diagnostics.Error(
                    "RAD-ELEC-025",
                    $"Electrical port has an invalid ID. {Identifiers.Rule}",
                    path: ElectricalPath(circuit, null));
            }
            else if (!portIds.Add(port.Id))
            {
                diagnostics.Error(
                    "RAD-ELEC-026",
                    $"Electrical circuit '{circuit.Id}' repeats port ID '{port.Id}'.",
                    path: ElectricalPath(circuit, null));
            }

            if (!nodeIds.Contains(port.NodeId))
            {
                diagnostics.Error(
                    "RAD-ELEC-027",
                    $"Electrical port '{port.Id}' on circuit '{circuit.Id}' refers to unknown node '{port.NodeId}'.",
                    path: ElectricalPath(circuit, null));
            }
            else if (!portNodes.Add(port.NodeId))
            {
                diagnostics.Error(
                    "RAD-ELEC-028",
                    $"Electrical circuit '{circuit.Id}' exposes node '{port.NodeId}' through more than one port; each port must name a distinct node.",
                    path: ElectricalPath(circuit, null));
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

            if (ElectricalComponentMetadata.IsSubcircuit(component.Kind))
            {
                if (!Identifiers.IsValid(component.CircuitId))
                {
                    diagnostics.Error(
                        "RAD-ELEC-029",
                        $"Component '{component.Id}' is a subcircuit and needs a valid referenced circuit ID.",
                        path: path);
                }
            }
            else if (component.CircuitId.Length > 0)
            {
                diagnostics.Error(
                    "RAD-ELEC-035",
                    $"Component '{component.Id}' is a {component.Kind} and must not name a referenced circuit.",
                    path: path);
            }
            else
            {
                bool needsPositiveValue = ElectricalComponentMetadata.RequiresPositiveValue(component.Kind);
                if (!double.IsFinite(component.Value) || (needsPositiveValue && component.Value <= 0))
                {
                    diagnostics.Error(
                        "RAD-ELEC-007",
                        $"Component '{component.Id}' has an invalid {component.ParameterName.ToLowerInvariant()} value.",
                        path: path);
                }
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

                if (!double.IsFinite(component.ChannelLengthModulation) ||
                    component.ChannelLengthModulation < 0)
                {
                    diagnostics.Error(
                        "RAD-ELEC-023",
                        $"Component '{component.Id}' has an invalid channel-length modulation; lambda must be a finite, non-negative number of 1/V.",
                        path: path);
                }

                // A negative capacitance would deliver energy instead of storing it, and a
                // non-finite one has no companion model, so both are refused rather than solved.
                if (!double.IsFinite(component.GateSourceCapacitance) ||
                    component.GateSourceCapacitance < 0 ||
                    !double.IsFinite(component.GateDrainCapacitance) ||
                    component.GateDrainCapacitance < 0 ||
                    !double.IsFinite(component.DrainSourceCapacitance) ||
                    component.DrainSourceCapacitance < 0)
                {
                    diagnostics.Error(
                        "RAD-ELEC-024",
                        $"Component '{component.Id}' has an invalid device capacitance; cgs, cgd and cds must be finite, non-negative numbers of farads.",
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

            if (ElectricalComponentMetadata.IsSubcircuit(component.Kind))
            {
                // A subcircuit's terminal shape is the referenced circuit's ports, which needs the
                // project. Validate the bindings that can be checked locally; hierarchy validation
                // checks the exact port coverage, and a missing target is reported there too.
                var terminalIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (ElectricalTerminal terminal in component.Terminals)
                {
                    if (!terminalIds.Add(terminal.Id))
                    {
                        diagnostics.Error(
                            "RAD-ELEC-036",
                            $"Component '{component.Id}' repeats terminal '{terminal.Id}'.",
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

                    if (terminal.Polarity != ElectricalTerminalPolarity.Passive)
                    {
                        diagnostics.Error(
                            "RAD-ELEC-034",
                            $"Terminal '{component.Id}.{terminal.Id}' is a subcircuit port and must be passive.",
                            path: path);
                    }
                }
            }
            else
            {
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

    /// <summary>
    /// Walks the references below an already validated circuit. Each reachable circuit is validated
    /// once, each reference is checked against the referenced ports, and a reference that closes a
    /// cycle is reported instead of recursing forever.
    /// </summary>
    private static void ValidateHierarchy(
        ElectricalCircuit circuit,
        Func<string, ElectricalCircuit?> resolve,
        DiagnosticBag diagnostics,
        HashSet<string> validated,
        HashSet<string> stack)
    {
        foreach (ElectricalComponent component in circuit.Components)
        {
            if (!ElectricalComponentMetadata.IsSubcircuit(component.Kind))
            {
                continue;
            }

            string path = ElectricalPath(circuit, component.Id);
            if (!Identifiers.IsValid(component.CircuitId))
            {
                diagnostics.Error(
                    "RAD-ELEC-029",
                    $"Subcircuit '{component.Id}' needs a referenced circuit ID.",
                    path: path);
                continue;
            }

            ElectricalCircuit? child = resolve(component.CircuitId);
            if (child is null)
            {
                diagnostics.Error(
                    "RAD-ELEC-029",
                    $"Subcircuit '{component.Id}' refers to circuit '{component.CircuitId}', which is not in this project.",
                    path: path);
                continue;
            }

            if (stack.Contains(child.Id))
            {
                diagnostics.Error(
                    "RAD-ELEC-033",
                    $"Subcircuit '{component.Id}' in '{circuit.Id}' closes a reference cycle through '{child.Id}'.",
                    path: path);
                continue;
            }

            ValidateSubcircuitTerminals(circuit, component, child, diagnostics, path);

            if (!validated.Add(child.Id))
            {
                continue;
            }

            diagnostics.AddRange(ValidateOwn(child));
            stack.Add(child.Id);
            ValidateHierarchy(child, resolve, diagnostics, validated, stack);
            stack.Remove(child.Id);
        }
    }

    private static void ValidateSubcircuitTerminals(
        ElectricalCircuit parent,
        ElectricalComponent component,
        ElectricalCircuit child,
        DiagnosticBag diagnostics,
        string path)
    {
        if (child.Ports.Count == 0)
        {
            diagnostics.Error(
                "RAD-ELEC-030",
                $"Subcircuit '{component.Id}' refers to circuit '{child.Id}', which exposes no ports. Add a port for every node the parent must connect.",
                path: path);
            return;
        }

        if (!child.Ports.Any(port =>
            string.Equals(port.NodeId, child.GroundNodeId, StringComparison.Ordinal)))
        {
            diagnostics.Error(
                "RAD-ELEC-031",
                $"Subcircuit '{component.Id}' refers to circuit '{child.Id}', whose ground '{child.GroundNodeId}' is not exposed as a port. A subcircuit has no implicit ground connection.",
                path: path);
        }

        var portIds = child.Ports
            .Where(port => Identifiers.IsValid(port.Id))
            .Select(port => port.Id)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ElectricalTerminal terminal in component.Terminals)
        {
            if (!seen.Add(terminal.Id))
            {
                diagnostics.Error(
                    "RAD-ELEC-036",
                    $"Component '{component.Id}' repeats terminal '{terminal.Id}'.",
                    path: path);
                continue;
            }

            if (!portIds.Contains(terminal.Id))
            {
                diagnostics.Error(
                    "RAD-ELEC-032",
                    $"Subcircuit '{component.Id}' has unexpected terminal '{terminal.Id}'; circuit '{child.Id}' exposes {string.Join(", ", portIds)}.",
                    path: path);
                continue;
            }

            if (parent.FindNode(terminal.NodeId) is null)
            {
                diagnostics.Error(
                    "RAD-ELEC-034",
                    $"Subcircuit terminal '{component.Id}.{terminal.Id}' refers to unknown parent node '{terminal.NodeId}'.",
                    path: path);
            }
        }

        foreach (string portId in portIds)
        {
            if (!seen.Contains(portId))
            {
                diagnostics.Error(
                    "RAD-ELEC-032",
                    $"Subcircuit '{component.Id}' is missing terminal '{portId}', one of circuit '{child.Id}'\'s ports.",
                    path: path);
            }
        }
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
