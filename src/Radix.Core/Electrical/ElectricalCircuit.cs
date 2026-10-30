using Radix.Core.Model;

namespace Radix.Core.Electrical;

/// <summary>DC electrical component kinds supported by the authored circuit model.</summary>
public enum ElectricalComponentKind
{
    Resistor,
    VoltageSource,
    CurrentSource,
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

    public static IReadOnlyList<ElectricalTerminalTemplate> Terminals(ElectricalComponentKind kind) =>
        kind == ElectricalComponentKind.Resistor ? ResistorTerminals : SourceTerminals;

    public static string ParameterName(ElectricalComponentKind kind) =>
        kind switch
        {
            ElectricalComponentKind.Resistor => "Resistance",
            ElectricalComponentKind.VoltageSource => "Voltage",
            ElectricalComponentKind.CurrentSource => "Current",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public static string Unit(ElectricalComponentKind kind) =>
        kind switch
        {
            ElectricalComponentKind.Resistor => "ohm",
            ElectricalComponentKind.VoltageSource => "V",
            ElectricalComponentKind.CurrentSource => "A",
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

/// <summary>One authored electrical component with an explicit value and terminal bindings.</summary>
public sealed class ElectricalComponent
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ElectricalComponentKind Kind { get; set; }

    /// <summary>Resistance, voltage, or current in the unit reported by <see cref="Unit"/>.</summary>
    public double Value { get; set; }

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
            string nodeA = component.FindTerminal("a")!.NodeId;
            string nodeB = component.FindTerminal("b")!.NodeId;
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
                default:
                    throw new InvalidOperationException($"Unsupported electrical component kind '{component.Kind}'.");
            }
        }

        return netlist;
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

            if (!double.IsFinite(component.Value) ||
                (component.Kind == ElectricalComponentKind.Resistor && component.Value <= 0))
            {
                diagnostics.Error(
                    "RAD-ELEC-007",
                    $"Component '{component.Id}' has an invalid {component.ParameterName.ToLowerInvariant()} value.",
                    path: path);
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

            ElectricalTerminal? terminalA = component.FindTerminal("a");
            ElectricalTerminal? terminalB = component.FindTerminal("b");
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
