using Radix.Core.Interpretation;
using Radix.Core.Signals;

namespace Radix.Core.Model;

/// <summary>
/// Structural validation of a project. Validation checks everything that can be
/// decided without knowing the domains of every connected net; the compiler performs
/// the remaining value/domain/table checks while flattening.
/// </summary>
public static class CircuitValidator
{
    public static DiagnosticBag Validate(CircuitProject project, string? rootDefinitionId = null)
    {
        var bag = new DiagnosticBag();
        Validate(project, rootDefinitionId, bag);
        return bag;
    }

    public static void Validate(CircuitProject project, string? rootDefinitionId, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string rootId = rootDefinitionId ?? project.RootDefinitionId;

        ValidateFormat(project, diagnostics);
        ValidateDomains(project, diagnostics);
        ValidateCustomNumericFormats(project, diagnostics);
        ValidateDefinitionIdentities(project, diagnostics);
        ValidateRoot(project, rootId, diagnostics);
        ValidateHierarchy(project, diagnostics);

        foreach (CircuitDefinition definition in project.Definitions)
        {
            ValidateDefinition(project, definition, diagnostics);
        }
    }

    private static void ValidateFormat(CircuitProject project, DiagnosticBag diagnostics)
    {
        if (project.FormatVersion < 1)
        {
            diagnostics.Error("RAD001", $"Invalid project format version {project.FormatVersion}.");
        }
        else if (project.FormatVersion > CircuitProject.CurrentFormatVersion)
        {
            diagnostics.Error(
                "RAD001",
                $"Project format version {project.FormatVersion} is newer than this build supports ({CircuitProject.CurrentFormatVersion}).");
        }

        if (string.IsNullOrWhiteSpace(project.Name))
        {
            diagnostics.Warning("RAD002", "Project has no name.");
        }

        if (project.Settings.MaxEvaluationsPerComponentPerTimestamp < 1)
        {
            diagnostics.Warning("RAD003", "Simulation evaluation limit must be at least 1; it will be clamped.");
        }

        if (project.Settings.MaxRecordedNetChanges < 0)
        {
            diagnostics.Warning("RAD004", "Recorded-change limit must be non-negative; it will be clamped to zero.");
        }

        if (project.Settings.MaxPendingEvents < 0)
        {
            diagnostics.Warning("RAD005", "Pending-event limit must be non-negative; it will be clamped to zero (unlimited).");
        }
    }

    private static void ValidateDomains(CircuitProject project, DiagnosticBag diagnostics)
    {
        if (project.Domains.Count == 0)
        {
            diagnostics.Error("RAD010", "A project needs at least one signal domain.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SignalDomain domain in project.Domains)
        {
            if (!Identifiers.IsValid(domain.Id))
            {
                diagnostics.Error("RAD011", $"Signal domain has an invalid ID. {Identifiers.Rule}");
            }
            else if (!seen.Add(domain.Id))
            {
                diagnostics.Error("RAD012", $"Duplicate signal domain ID '{domain.Id}'.");
            }

            if (domain.ValueCount < 1)
            {
                diagnostics.Error("RAD013", $"Domain '{domain.Id}' has no values.", definitionId: null);
            }

            IReadOnlyList<string> names = domain.ValueNames;
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            {
                diagnostics.Warning("RAD014", $"Domain '{domain.Id}' has duplicate value labels.");
            }
        }
    }

    private static void ValidateCustomNumericFormats(CircuitProject project, DiagnosticBag diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (NumericFormatDefinition format in project.CustomNumericFormats)
        {
            if (!Identifiers.IsValid(format.Id))
            {
                diagnostics.Error(
                    "RAD102",
                    $"Custom numeric format has an invalid ID. {Identifiers.Rule}");
            }
            else if (NumericFormatCatalog.IsBuiltInId(format.Id))
            {
                diagnostics.Error(
                    "RAD103",
                    $"Custom numeric format ID '{format.Id}' is reserved by the built-in catalog.");
            }
            else if (!seen.Add(format.Id))
            {
                diagnostics.Error("RAD104", $"Duplicate custom numeric format ID '{format.Id}'.");
            }

            if (project.FindDomain(format.DomainId) is null)
            {
                diagnostics.Error(
                    "RAD105",
                    $"Custom numeric format '{format.Id}' refers to unknown domain '{format.DomainId}'.");
            }

            try
            {
                _ = format.ToFormat();
            }
            catch (ArgumentException exception)
            {
                diagnostics.Error(
                    "RAD106",
                    $"Custom numeric format '{format.Id}' is invalid: {exception.Message}");
            }
        }
    }

    private static void ValidateDefinitionIdentities(CircuitProject project, DiagnosticBag diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (CircuitDefinition definition in project.Definitions)
        {
            if (!Identifiers.IsValid(definition.Id))
            {
                diagnostics.Error("RAD020", $"Definition has an invalid ID. {Identifiers.Rule}", definition.Id);
            }
            else if (!seen.Add(definition.Id))
            {
                diagnostics.Error("RAD021", $"Duplicate definition ID '{definition.Id}'.", definition.Id);
            }

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                diagnostics.Warning("RAD022", "Definition has no display name.", definition.Id);
            }
        }
    }

    private static void ValidateRoot(CircuitProject project, string rootId, DiagnosticBag diagnostics)
    {
        if (string.IsNullOrWhiteSpace(rootId))
        {
            diagnostics.Error("RAD030", "Project has no root definition.");
            return;
        }

        if (project.FindDefinition(rootId) is null)
        {
            diagnostics.Error("RAD031", $"Root definition '{rootId}' does not exist.");
        }
    }

    private static void ValidateHierarchy(CircuitProject project, DiagnosticBag diagnostics)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (CircuitDefinition definition in project.Definitions)
        {
            if (!state.ContainsKey(definition.Id))
            {
                Visit(definition, project, state, stack, reported, diagnostics);
            }
        }
    }

    private static void Visit(
        CircuitDefinition definition,
        CircuitProject project,
        Dictionary<string, int> state,
        List<string> stack,
        HashSet<string> reported,
        DiagnosticBag diagnostics)
    {
        state[definition.Id] = 1;
        stack.Add(definition.Id);

        foreach (ComponentInstance instance in definition.Instances)
        {
            if (instance.DefinitionId is null)
            {
                continue;
            }

            CircuitDefinition? child = project.FindDefinition(instance.DefinitionId);
            if (child is null)
            {
                // Missing definitions are reported per instance by ValidateDefinition.
                continue;
            }

            state.TryGetValue(child.Id, out int childState);
            if (childState == 0)
            {
                Visit(child, project, state, stack, reported, diagnostics);
            }
            else if (childState == 1)
            {
                int start = stack.IndexOf(child.Id);
                string cycle = string.Join(" -> ", stack.Skip(start).Append(child.Id));
                string key = string.Join("|", stack.Skip(start));
                if (reported.Add(key))
                {
                    diagnostics.Error("RAD032", $"Definitions form a recursive cycle: {cycle}.", definition.Id, instance.Id);
                }
            }
        }

        stack.RemoveAt(stack.Count - 1);
        state[definition.Id] = 2;
    }

    private static void ValidateDefinition(CircuitProject project, CircuitDefinition definition, DiagnosticBag diagnostics)
    {
        ValidateInterface(project, definition, diagnostics);
        ValidateInstances(project, definition, diagnostics);
        ValidateNets(project, definition, diagnostics);
        ValidatePorts(project, definition, diagnostics);
        ValidateNumericLanes(project, definition, diagnostics);
    }

    private static void ValidateNumericLanes(
        CircuitProject project,
        CircuitDefinition definition,
        DiagnosticBag diagnostics)
    {
        var laneIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (NumericLane lane in definition.NumericLanes)
        {
            if (!Identifiers.IsValid(lane.Id))
            {
                diagnostics.Error("RAD095", $"Numeric lane has an invalid ID. {Identifiers.Rule}", definition.Id);
            }
            else if (!laneIds.Add(lane.Id))
            {
                diagnostics.Error("RAD096", $"Duplicate numeric lane ID '{lane.Id}'.", definition.Id);
            }

            INumericFormat? format = NumericFormatCatalog.Find(project, lane.FormatId);
            if (format is null)
            {
                diagnostics.Error(
                    "RAD097",
                    $"Numeric lane '{lane.Id}' uses unknown format '{lane.FormatId}'.",
                    definition.Id);
            }

            if (lane.PinIds.Count == 0)
            {
                diagnostics.Warning("RAD098", $"Numeric lane '{lane.Id}' has no pins.", definition.Id);
                continue;
            }

            var seenPins = new HashSet<string>(StringComparer.Ordinal);
            foreach (string pinId in lane.PinIds)
            {
                if (!seenPins.Add(pinId))
                {
                    diagnostics.Error(
                        "RAD099",
                        $"Numeric lane '{lane.Id}' repeats pin '{pinId}'.",
                        definition.Id,
                        pinId: pinId);
                    continue;
                }

                PinDeclaration? pin = definition.FindPin(pinId);
                if (pin is null)
                {
                    diagnostics.Error(
                        "RAD100",
                        $"Numeric lane '{lane.Id}' refers to unknown interface pin '{pinId}'.",
                        definition.Id,
                        pinId: pinId);
                    continue;
                }

                if (format is not null &&
                    !string.Equals(pin.DomainId, format.DomainId, StringComparison.Ordinal))
                {
                    diagnostics.Warning(
                        "RAD101",
                        $"Numeric lane '{lane.Id}' pin '{pinId}' uses domain '{pin.DomainId}', but format '{format.Id}' is defined for '{format.DomainId}'.",
                        definition.Id,
                        pinId: pinId);
                }
            }
        }
    }

    private static void ValidateInterface(CircuitProject project, CircuitDefinition definition, DiagnosticBag diagnostics)
    {
        var pinIds = new HashSet<string>(StringComparer.Ordinal);
        var pinNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (PinDeclaration pin in definition.Pins)
        {
            if (!Identifiers.IsValid(pin.Id))
            {
                diagnostics.Error("RAD040", $"Interface pin has an invalid ID. {Identifiers.Rule}", definition.Id);
            }
            else if (!pinIds.Add(pin.Id))
            {
                diagnostics.Error("RAD041", $"Duplicate interface pin ID '{pin.Id}'.", definition.Id, pinId: pin.Id);
            }

            if (string.IsNullOrWhiteSpace(pin.Name))
            {
                diagnostics.Warning("RAD042", "Interface pin has no display name.", definition.Id, pinId: pin.Id);
            }
            else if (!pinNames.Add(pin.Name))
            {
                diagnostics.Warning("RAD043", $"Duplicate interface pin name '{pin.Name}'.", definition.Id, pinId: pin.Id);
            }

            if (project.FindDomain(pin.DomainId) is null)
            {
                diagnostics.Error("RAD044", $"Interface pin refers to unknown domain '{pin.DomainId}'.", definition.Id, pinId: pin.Id);
            }
        }
    }

    private static void ValidateInstances(CircuitProject project, CircuitDefinition definition, DiagnosticBag diagnostics)
    {
        var instanceIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (ComponentInstance instance in definition.Instances)
        {
            if (!Identifiers.IsValid(instance.Id))
            {
                diagnostics.Error("RAD050", $"Instance has an invalid ID. {Identifiers.Rule}", definition.Id);
            }
            else if (!instanceIds.Add(instance.Id))
            {
                diagnostics.Error("RAD051", $"Duplicate instance ID '{instance.Id}'.", definition.Id, instance.Id);
            }

            if (!instance.HasExactlyOneSource)
            {
                diagnostics.Error(
                    "RAD052",
                    "Instance must have exactly one of a referenced definition or primitive parameters.",
                    definition.Id,
                    instance.Id);
                continue;
            }

            if (instance.DefinitionId is not null)
            {
                if (project.FindDefinition(instance.DefinitionId) is null)
                {
                    diagnostics.Error(
                        "RAD053",
                        $"Instance refers to unknown definition '{instance.DefinitionId}'.",
                        definition.Id,
                        instance.Id);
                }

                continue;
            }

            if (instance.Primitive is null)
            {
                continue;
            }

            ValidatePrimitiveParameters(definition, instance, diagnostics);

            IReadOnlyList<PinDescriptor> pins = project.GetInstancePins(instance);
            if (pins.Count == 0)
            {
                diagnostics.Warning("RAD054", "Primitive instance has no pins.", definition.Id, instance.Id);
            }
        }
    }

    private static void ValidatePrimitiveParameters(
        CircuitDefinition definition,
        ComponentInstance instance,
        DiagnosticBag diagnostics)
    {
        PrimitiveSpec spec = instance.Primitive!;
        switch (spec)
        {
            case LookupSpec lookup:
                if (lookup.InputCount > PrimitiveShapes.MaxInputs)
                {
                    diagnostics.Error(
                        "RAD055",
                        $"Lookup input count {lookup.InputCount} exceeds the supported maximum {PrimitiveShapes.MaxInputs}.",
                        definition.Id,
                        instance.Id);
                }

                if (lookup.Table.Count == 0)
                {
                    diagnostics.Error("RAD056", "Lookup table must contain at least one entry.", definition.Id, instance.Id);
                }

                if (lookup.Table.Any(v => v < 0))
                {
                    diagnostics.Error("RAD057", "Lookup table contains a negative value index.", definition.Id, instance.Id);
                }

                if (lookup.Table.Count > 1_048_576)
                {
                    diagnostics.Error("RAD058", "Lookup table exceeds the 1,048,576-entry limit.", definition.Id, instance.Id);
                }

                break;
            case WaveformSpec waveform:
                if (waveform.Values.Count == 0)
                {
                    diagnostics.Error("RAD059", "Waveform needs at least one phase.", definition.Id, instance.Id);
                }

                break;
            case ConstantSpec constant when constant.Value < 0:
                diagnostics.Error("RAD060", "Constant value must be non-negative.", definition.Id, instance.Id);
                break;
            case DelaySpec delay when delay.DelayTicks < 1:
                diagnostics.Error("RAD061", "Delay must be at least one tick.", definition.Id, instance.Id);
                break;
        }
    }

    private static void ValidateNets(CircuitProject project, CircuitDefinition definition, DiagnosticBag diagnostics)
    {
        var netIds = new HashSet<string>(StringComparer.Ordinal);
        var pinOwner = new Dictionary<PinRef, string>();
        var connectedPins = new HashSet<PinRef>();

        // Index instances once. Resolving each pin reference with a linear scan would
        // make validation quadratic on designs with many gates and nets.
        var instancesById = new Dictionary<string, ComponentInstance>(definition.Instances.Count, StringComparer.Ordinal);
        foreach (ComponentInstance instance in definition.Instances)
        {
            instancesById.TryAdd(instance.Id, instance);
        }

        foreach (Net net in definition.Nets)
        {
            if (!Identifiers.IsValid(net.Id))
            {
                diagnostics.Error("RAD070", $"Net has an invalid ID. {Identifiers.Rule}", definition.Id);
            }
            else if (!netIds.Add(net.Id))
            {
                diagnostics.Error("RAD071", $"Duplicate net ID '{net.Id}'.", definition.Id, netId: net.Id);
            }

            SignalDomain? domain = project.FindDomain(net.DomainId);
            if (domain is null)
            {
                diagnostics.Error("RAD072", $"Net refers to unknown domain '{net.DomainId}'.", definition.Id, netId: net.Id);
            }
            else if (net.InitialValue is int initial && !domain.IsValidValue(initial))
            {
                diagnostics.Error(
                    "RAD073",
                    $"Net initial value {initial} is outside domain '{domain.Id}' (0..{domain.ValueCount - 1}).",
                    definition.Id,
                    netId: net.Id);
            }

            if (net.Pins.Count == 0)
            {
                diagnostics.Warning("RAD074", "Net has no connected pins.", definition.Id, netId: net.Id);
            }

            if (!WireAnnotation.TryNormalizeColor(net.WireColor, out _))
            {
                diagnostics.Error(
                    "RAD078",
                    $"Net wire color '{net.WireColor}' is invalid; use #RRGGBB or 'auto'.",
                    definition.Id,
                    netId: net.Id);
            }

            if (net.RoutePoints.Count > WireAnnotation.MaxRoutePoints)
            {
                diagnostics.Error(
                    "RAD079",
                    $"Net has {net.RoutePoints.Count} wire bendpoints; the maximum is {WireAnnotation.MaxRoutePoints}.",
                    definition.Id,
                    netId: net.Id);
            }
            else if (net.RoutePoints.Any(point => !point.IsFinite))
            {
                diagnostics.Error(
                    "RAD080",
                    "Net has a non-finite or out-of-range wire bendpoint.",
                    definition.Id,
                    netId: net.Id);
            }

            var pinsInNet = new HashSet<PinRef>();
            foreach (PinRef pinRef in net.Pins)
            {
                if (!pinsInNet.Add(pinRef))
                {
                    diagnostics.Error(
                        "RAD075",
                        $"Pin '{pinRef}' is listed more than once on the same net.",
                        definition.Id,
                        pinRef.InstanceId,
                        net.Id,
                        pinRef.PinId);
                    continue;
                }

                if (pinOwner.TryGetValue(pinRef, out string? existingNet))
                {
                    diagnostics.Error(
                        "RAD076",
                        $"Pin '{pinRef}' is connected to both net '{existingNet}' and net '{net.Id}'.",
                        definition.Id,
                        pinRef.InstanceId,
                        net.Id,
                        pinRef.PinId);
                    continue;
                }

                pinOwner[pinRef] = net.Id;
                connectedPins.Add(pinRef);

                instancesById.TryGetValue(pinRef.InstanceId, out ComponentInstance? instance);
                if (instance is null)
                {
                    diagnostics.Error(
                        "RAD077",
                        $"Net refers to unknown instance '{pinRef.InstanceId}'.",
                        definition.Id,
                        pinRef.InstanceId,
                        net.Id,
                        pinRef.PinId);
                    continue;
                }

                PinDescriptor? pin = project.FindInstancePin(instance, pinRef.PinId);
                if (pin is null)
                {
                    diagnostics.Error(
                        "RAD078",
                        $"Instance '{instance.Id}' has no pin '{pinRef.PinId}'.",
                        definition.Id,
                        instance.Id,
                        net.Id,
                        pinRef.PinId);
                    continue;
                }

                if (pin.Value.DomainId is string pinDomain && domain is not null &&
                    !string.Equals(pinDomain, net.DomainId, StringComparison.Ordinal))
                {
                    diagnostics.Error(
                        "RAD079",
                        $"Pin '{pinRef}' declares domain '{pinDomain}' but net '{net.Id}' is '{net.DomainId}'.",
                        definition.Id,
                        instance.Id,
                        net.Id,
                        pinRef.PinId);
                }

                if (pin.Value.Direction == PinDirection.Output)
                {
                    // Driver count is checked once the net scan is complete.
                }
            }

            if (domain is not null)
            {
                WarnOnMultipleDrivers(project, definition, net, domain, instancesById, diagnostics);
            }
        }

        // Unconnected primitive input pins are almost always wiring mistakes.
        foreach (ComponentInstance instance in definition.Instances)
        {
            foreach (PinDescriptor pin in project.GetInstancePins(instance))
            {
                if (pin.Direction == PinDirection.Input && !connectedPins.Contains(new PinRef(instance.Id, pin.Id)))
                {
                    diagnostics.Warning(
                        "RAD080",
                        $"Input pin '{pin.Id}' of instance '{instance.Id}' is not connected.",
                        definition.Id,
                        instance.Id,
                        pinId: pin.Id);
                }
            }
        }
    }

    private static void WarnOnMultipleDrivers(
        CircuitProject project,
        CircuitDefinition definition,
        Net net,
        SignalDomain domain,
        IReadOnlyDictionary<string, ComponentInstance> instancesById,
        DiagnosticBag diagnostics)
    {
        if (net.Pins.Count < 2)
        {
            return;
        }

        int drivers = 0;
        foreach (PinRef pinRef in net.Pins)
        {
            if (!instancesById.TryGetValue(pinRef.InstanceId, out ComponentInstance? instance))
            {
                continue;
            }

            PinDescriptor? pin = project.FindInstancePin(instance, pinRef.PinId);
            if (pin?.Direction == PinDirection.Output)
            {
                drivers++;
            }
        }

        if (drivers > 1 && domain.Drivers is SingleDriverResolution)
        {
            diagnostics.Warning(
                "RAD081",
                $"Net '{net.Id}' has {drivers} output pins but domain '{domain.Id}' uses a single-driver policy; this will be resolved as a conflict.",
                definition.Id,
                netId: net.Id);
        }
    }

    private static void ValidatePorts(CircuitProject project, CircuitDefinition definition, DiagnosticBag diagnostics)
    {
        var portsByPin = new HashSet<string>(StringComparer.Ordinal);

        foreach (PortBinding port in definition.Ports)
        {
            PinDeclaration? pin = definition.FindPin(port.PinId);
            if (pin is null)
            {
                diagnostics.Error(
                    "RAD090",
                    $"Port binding refers to unknown interface pin '{port.PinId}'.",
                    definition.Id,
                    pinId: port.PinId);
                continue;
            }

            if (!portsByPin.Add(port.PinId))
            {
                diagnostics.Error(
                    "RAD091",
                    $"Interface pin '{port.PinId}' has more than one port binding.",
                    definition.Id,
                    pinId: port.PinId);
            }

            Net? net = definition.FindNet(port.NetId);
            if (net is null)
            {
                diagnostics.Error(
                    "RAD092",
                    $"Port binding for pin '{port.PinId}' refers to unknown net '{port.NetId}'.",
                    definition.Id,
                    netId: port.NetId,
                    pinId: port.PinId);
                continue;
            }

            if (project.FindDomain(pin.DomainId) is SignalDomain pinDomain)
            {
                if (!string.Equals(pin.DomainId, net.DomainId, StringComparison.Ordinal))
                {
                    diagnostics.Error(
                        "RAD093",
                        $"Interface pin '{pin.Id}' declares domain '{pin.DomainId}' but is bound to net '{net.Id}' of domain '{net.DomainId}'.",
                        definition.Id,
                        netId: net.Id,
                        pinId: pin.Id);
                }

                _ = pinDomain;
            }
        }

        foreach (PinDeclaration pin in definition.Pins)
        {
            if (!portsByPin.Contains(pin.Id))
            {
                diagnostics.Error(
                    "RAD094",
                    $"Interface pin '{pin.Id}' is not bound to a net.",
                    definition.Id,
                    pinId: pin.Id);
            }
        }
    }
}
