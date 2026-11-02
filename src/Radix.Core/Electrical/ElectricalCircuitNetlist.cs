namespace Radix.Core.Electrical;

/// <summary>
/// Expands an authored circuit into the solver's flat netlist. A subcircuit is not a behavioural
/// stand-in: its referenced circuit's elements are added to the same MNA system, with every internal
/// node and element ID prefixed by the instance path. The prefix uses '/', which
/// <see cref="Radix.Core.Model.Identifiers"/> forbids inside an authored ID, so an expanded name
/// cannot collide with one the user wrote.
/// </summary>
internal static class ElectricalCircuitNetlist
{
    public static DcNetlist BuildDc(
        ElectricalCircuit root,
        Func<string, ElectricalCircuit?>? resolve)
    {
        var netlist = new DcNetlist(root.GroundNodeId);
        var rootNodes = root.Nodes.ToDictionary(
            node => node.Id,
            node => node.Id,
            StringComparer.Ordinal);
        AddDc(netlist, root, string.Empty, rootNodes, resolve);
        return netlist;
    }

    public static TransientNetlist BuildTransient(
        ElectricalCircuit root,
        Func<string, ElectricalCircuit?>? resolve)
    {
        var netlist = new TransientNetlist(root.GroundNodeId);
        var rootNodes = root.Nodes.ToDictionary(
            node => node.Id,
            node => node.Id,
            StringComparer.Ordinal);
        AddTransient(netlist, root, string.Empty, rootNodes, resolve);
        return netlist;
    }

    private static void AddDc(
        DcNetlist netlist,
        ElectricalCircuit circuit,
        string prefix,
        IReadOnlyDictionary<string, string> nodes,
        Func<string, ElectricalCircuit?>? resolve)
    {
        foreach (ElectricalComponent component in circuit.Components)
        {
            if (ElectricalComponentMetadata.IsSubcircuit(component.Kind))
            {
                ExpandDc(netlist, circuit, component, prefix, nodes, resolve);
                continue;
            }

            string id = prefix + component.Id;
            (string nodeA, string nodeB) = BranchNodes(component, nodes);
            switch (component.Kind)
            {
                case ElectricalComponentKind.Resistor:
                    netlist.AddResistor(id, nodeA, nodeB, component.Value);
                    break;
                case ElectricalComponentKind.VoltageSource:
                    netlist.AddVoltageSource(id, nodeA, nodeB, component.Value);
                    break;
                case ElectricalComponentKind.CurrentSource:
                    netlist.AddCurrentSource(id, nodeA, nodeB, component.Value);
                    break;
                case ElectricalComponentKind.Capacitor:
                    // A capacitor is an open circuit at DC, so it contributes no DC element.
                    break;
                case ElectricalComponentKind.Inductor:
                    // An ideal inductor is a zero-volt short at DC.
                    netlist.AddVoltageSource(id, nodeA, nodeB, 0);
                    break;
                case ElectricalComponentKind.Mosfet:
                    // A DC operating point carries no charge, so gate capacitance is omitted here
                    // rather than silently changed; BuildTransient stamps it.
                    netlist.AddMosfet(
                        id,
                        nodeA,
                        nodes[component.FindTerminal("gate")!.NodeId],
                        nodeB,
                        component.Channel,
                        component.ThresholdVoltage,
                        component.Value,
                        component.ChannelLengthModulation);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported electrical component kind '{component.Kind}'.");
            }
        }
    }

    private static void ExpandDc(
        DcNetlist netlist,
        ElectricalCircuit parent,
        ElectricalComponent instance,
        string prefix,
        IReadOnlyDictionary<string, string> parentNodes,
        Func<string, ElectricalCircuit?>? resolve)
    {
        ElectricalCircuit child = ReferencedCircuit(instance, resolve);
        string childPrefix = prefix + instance.Id + "/";
        Dictionary<string, string> childNodes = ChildNodeMap(
            child,
            instance,
            childPrefix,
            parentNodes);
        AddDc(netlist, child, childPrefix, childNodes, resolve);
    }

    private static void AddTransient(
        TransientNetlist netlist,
        ElectricalCircuit circuit,
        string prefix,
        IReadOnlyDictionary<string, string> nodes,
        Func<string, ElectricalCircuit?>? resolve)
    {
        foreach (ElectricalComponent component in circuit.Components)
        {
            if (ElectricalComponentMetadata.IsSubcircuit(component.Kind))
            {
                ExpandTransient(netlist, component, prefix, nodes, resolve);
                continue;
            }

            string id = prefix + component.Id;
            (string nodeA, string nodeB) = BranchNodes(component, nodes);
            switch (component.Kind)
            {
                case ElectricalComponentKind.Resistor:
                    netlist.AddResistor(id, nodeA, nodeB, component.Value);
                    break;
                case ElectricalComponentKind.VoltageSource:
                    netlist.AddVoltageSource(id, nodeA, nodeB, component.Value);
                    ApplyWaveform(netlist, component, id);
                    break;
                case ElectricalComponentKind.CurrentSource:
                    netlist.AddCurrentSource(id, nodeA, nodeB, component.Value);
                    ApplyWaveform(netlist, component, id);
                    break;
                case ElectricalComponentKind.Capacitor:
                    netlist.AddCapacitor(id, nodeA, nodeB, component.Value);
                    if (component.InitialValue is double volts)
                    {
                        netlist.SetCapacitorInitialVoltage(id, volts);
                    }

                    break;
                case ElectricalComponentKind.Inductor:
                    netlist.AddInductor(id, nodeA, nodeB, component.Value);
                    if (component.InitialValue is double amperes)
                    {
                        netlist.SetInductorInitialCurrent(id, amperes);
                    }

                    break;
                case ElectricalComponentKind.Mosfet:
                    netlist.AddMosfet(
                        id,
                        nodeA,
                        nodes[component.FindTerminal("gate")!.NodeId],
                        nodeB,
                        component.Channel,
                        component.ThresholdVoltage,
                        component.Value,
                        component.ChannelLengthModulation,
                        component.GateSourceCapacitance,
                        component.GateDrainCapacitance,
                        component.DrainSourceCapacitance);

                    // The solver re-evaluates device capacitances per netlist, so one device asking
                    // for the bias-dependent model makes the run use it. Asking on a device with no
                    // capacitance changes nothing, because there is no element to re-evaluate.
                    if (component.BiasDependentCapacitance)
                    {
                        netlist.UseBiasDependentCapacitance();
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported electrical component kind '{component.Kind}'.");
            }
        }
    }

    private static void ExpandTransient(
        TransientNetlist netlist,
        ElectricalComponent instance,
        string prefix,
        IReadOnlyDictionary<string, string> parentNodes,
        Func<string, ElectricalCircuit?>? resolve)
    {
        ElectricalCircuit child = ReferencedCircuit(instance, resolve);
        string childPrefix = prefix + instance.Id + "/";
        Dictionary<string, string> childNodes = ChildNodeMap(
            child,
            instance,
            childPrefix,
            parentNodes);
        AddTransient(netlist, child, childPrefix, childNodes, resolve);
    }

    /// <summary>
    /// Maps every node of a referenced circuit to a prefixed netlist node, then overrides the
    /// ported nodes with the parent node each instance terminal names. A port is therefore the same
    /// electrical node on both sides, while an unported internal node stays private to the instance.
    /// </summary>
    private static Dictionary<string, string> ChildNodeMap(
        ElectricalCircuit child,
        ElectricalComponent instance,
        string childPrefix,
        IReadOnlyDictionary<string, string> parentNodes)
    {
        var map = child.Nodes.ToDictionary(
            node => node.Id,
            node => childPrefix + node.Id,
            StringComparer.Ordinal);

        foreach (ElectricalPort port in child.Ports)
        {
            ElectricalTerminal terminal = instance.FindTerminal(port.Id)
                ?? throw new InvalidOperationException(
                    $"Subcircuit '{instance.Id}' is missing terminal '{port.Id}' after validation.");
            map[port.NodeId] = parentNodes[terminal.NodeId];
        }

        return map;
    }

    private static ElectricalCircuit ReferencedCircuit(
        ElectricalComponent instance,
        Func<string, ElectricalCircuit?>? resolve) =>
        resolve?.Invoke(instance.CircuitId)
        ?? throw new InvalidOperationException(
            $"Subcircuit '{instance.Id}' refers to circuit '{instance.CircuitId}', which is not in this project.");

    private static (string NodeA, string NodeB) BranchNodes(
        ElectricalComponent component,
        IReadOnlyDictionary<string, string> nodes) =>
        ElectricalComponentMetadata.IsMosfet(component.Kind)
            ? (nodes[component.FindTerminal("drain")!.NodeId], nodes[component.FindTerminal("source")!.NodeId])
            : (nodes[component.FindTerminal("a")!.NodeId], nodes[component.FindTerminal("b")!.NodeId]);

    private static void ApplyWaveform(
        TransientNetlist netlist,
        ElectricalComponent component,
        string elementId)
    {
        if (component.Waveform.Count == 0)
        {
            return;
        }

        netlist.SetWaveform(
            elementId,
            component.Waveform.Select(point => new TransientWaveformPoint(point.Time, point.Value)));
    }
}
