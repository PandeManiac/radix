using Radix.Core;
using Radix.Core.Electrical;

namespace Radix.Editor.ViewModels;

/// <summary>One authored electrical circuit in the project browser.</summary>
public sealed class ElectricalCircuitListItem
{
    public ElectricalCircuitListItem(ElectricalCircuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        Id = circuit.Id;
        Name = circuit.Name;
        Summary = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{circuit.Nodes.Count} nodes · {circuit.Components.Count} components");
    }

    public string Id { get; }

    public string Name { get; }

    public string Summary { get; }

    public override string ToString() => Name;
}

/// <summary>A selectable electrical node, used for ground and terminal bindings.</summary>
public sealed class ElectricalNodeChoice
{
    public ElectricalNodeChoice(ElectricalNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Id = node.Id;
        Name = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name;
    }

    public string Id { get; }

    public string Name { get; }

    public string Label => string.Equals(Id, Name, StringComparison.Ordinal) ? Id : $"{Name} ({Id})";

    public override string ToString() => Label;
}

/// <summary>One node row; the ground row is marked explicitly.</summary>
public sealed class ElectricalNodeListItem
{
    public ElectricalNodeListItem(ElectricalNode node, bool isGround)
    {
        ArgumentNullException.ThrowIfNull(node);
        Id = node.Id;
        Name = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name;
        IsGround = isGround;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsGround { get; }

    public string Summary => IsGround ? "ground · zero volts" : Id;

    public override string ToString() => Name;
}

public sealed class ElectricalComponentKindChoice
{
    public ElectricalComponentKindChoice(ElectricalComponentKind kind)
    {
        Kind = kind;
        Label = kind switch
        {
            ElectricalComponentKind.Resistor => "Resistor",
            ElectricalComponentKind.VoltageSource => "Voltage source",
            ElectricalComponentKind.CurrentSource => "Current source",
            _ => kind.ToString(),
        };
        Unit = ElectricalComponentMetadata.Unit(kind);
        ParameterName = ElectricalComponentMetadata.ParameterName(kind);
    }

    public ElectricalComponentKind Kind { get; }

    public string Label { get; }

    public string Unit { get; }

    public string ParameterName { get; }

    public override string ToString() => Label;
}

/// <summary>One component row with its authored value and kind metadata.</summary>
public sealed class ElectricalComponentListItem
{
    public ElectricalComponentListItem(ElectricalComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        Id = component.Id;
        Name = string.IsNullOrWhiteSpace(component.Name) ? component.Id : component.Name;
        Kind = component.Kind;
        KindLabel = new ElectricalComponentKindChoice(component.Kind).Label;
        ValueText = FormatValue(component.Value);
        Unit = component.Unit;
        Summary = $"{KindLabel} · {ValueText} {Unit}";
    }

    public string Id { get; }

    public string Name { get; }

    public ElectricalComponentKind Kind { get; }

    public string KindLabel { get; }

    public string ValueText { get; }

    public string Unit { get; }

    public string Summary { get; }

    public override string ToString() => Name;

    private static string FormatValue(double value) =>
        value.ToString("0.###############", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One explicit terminal binding for the selected component.</summary>
public sealed class ElectricalTerminalListItem : ObservableObject
{
    private ElectricalNodeChoice? _selectedNode;

    public ElectricalTerminalListItem(
        ElectricalComponent component,
        ElectricalTerminal terminal,
        IReadOnlyList<ElectricalNodeChoice> nodes)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(nodes);
        ComponentId = component.Id;
        Id = terminal.Id;
        Name = string.IsNullOrWhiteSpace(terminal.Name) ? terminal.Id : terminal.Name;
        Polarity = terminal.Polarity;
        NodeId = terminal.NodeId;
        var choices = nodes.ToList();
        _selectedNode = choices.FirstOrDefault(node =>
            string.Equals(node.Id, terminal.NodeId, StringComparison.Ordinal));
        if (_selectedNode is null && terminal.NodeId.Length > 0)
        {
            _selectedNode = new ElectricalNodeChoice(new ElectricalNode
            {
                Id = terminal.NodeId,
                Name = $"{terminal.NodeId} (missing)",
            });
            choices.Add(_selectedNode);
        }

        Nodes = choices;
    }

    public string ComponentId { get; }

    public string Id { get; }

    public string Name { get; }

    public ElectricalTerminalPolarity Polarity { get; }

    public string NodeId { get; }

    public IReadOnlyList<ElectricalNodeChoice> Nodes { get; }

    public ElectricalNodeChoice? SelectedNode
    {
        get => _selectedNode;
        set => SetField(ref _selectedNode, value);
    }

    public string PolarityText => Polarity.ToString().ToLowerInvariant();

    public string Summary => $"{Name} · {PolarityText}";
}


/// <summary>One solved or unreferenced authored node in the Electrical solve preview.</summary>
public sealed class ElectricalNodeResultItem
{
    public ElectricalNodeResultItem(ElectricalNode node, double? volts)
    {
        ArgumentNullException.ThrowIfNull(node);
        Id = node.Id;
        Name = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name;
        Volts = volts;
        HasVoltage = volts is not null;
        VoltageText = volts is double value
            ? $"{value.ToString("G12", System.Globalization.CultureInfo.InvariantCulture)} V"
            : "unreferenced";
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>Solved node voltage, or null when the node is unreferenced.</summary>
    public double? Volts { get; }

    public bool HasVoltage { get; }

    public string VoltageText { get; }
}

/// <summary>One solved component with terminal voltage, current, and absorbed power.</summary>
public sealed class ElectricalElementResultItem
{
    public ElectricalElementResultItem(DcElement element, ElectricalComponent? component, DcSolution solution)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(solution);
        Id = element.Id;
        Name = component is null || string.IsNullOrWhiteSpace(component.Name) ? element.Id : component.Name;
        NodeA = element.NodeA;
        NodeB = element.NodeB;
        Value = element.Value;
        Unit = element.Unit;
        Volts = solution.ElementVoltages[element.Id];
        Amperes = solution.ElementCurrents[element.Id];
        AbsorbedPowerWatts = solution.ElementPowers[element.Id];
        KindId = element.Kind switch
        {
            DcElementKind.Resistor => "resistor",
            DcElementKind.VoltageSource => "voltage-source",
            DcElementKind.CurrentSource => "current-source",
            _ => element.Kind.ToString().ToLowerInvariant(),
        };
        KindLabel = element.Kind switch
        {
            DcElementKind.Resistor => "Resistor",
            DcElementKind.VoltageSource => "Voltage source",
            DcElementKind.CurrentSource => "Current source",
            _ => element.Kind.ToString(),
        };
        ValueText = $"{Format(element.Value)} {element.Unit}";
        VoltageText = $"{Format(solution.ElementVoltages[element.Id])} V";
        CurrentText = $"{Format(solution.ElementCurrents[element.Id])} A";
        PowerText = $"{Format(solution.ElementPowers[element.Id])} W";
    }

    public string Id { get; }

    public string Name { get; }

    public string KindId { get; }

    public string KindLabel { get; }

    public string NodeA { get; }

    public string NodeB { get; }

    public double Value { get; }

    public string Unit { get; }

    public double Volts { get; }

    public double Amperes { get; }

    public double AbsorbedPowerWatts { get; }

    public string Nodes => $"{NodeA} → {NodeB}";

    public string ValueText { get; }

    public string VoltageText { get; }

    public string CurrentText { get; }

    public string PowerText { get; }

    public string Summary => $"{KindLabel} · {Nodes} · {ValueText}";

    private static string Format(double value) =>
        value.ToString("G12", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A current validation message for the selected authored circuit.</summary>
public sealed class ElectricalDiagnosticItem
{
    public ElectricalDiagnosticItem(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        Code = diagnostic.Code;
        Message = diagnostic.Message;
        Severity = diagnostic.Severity;
        Location = diagnostic.Location;
        Display = diagnostic.ToString();
    }

    public string Code { get; }

    public string Message { get; }

    public DiagnosticSeverity Severity { get; }

    public string Location { get; }

    public string Display { get; }

    public bool IsError => Severity == DiagnosticSeverity.Error;

    public bool IsWarning => Severity == DiagnosticSeverity.Warning;
}
