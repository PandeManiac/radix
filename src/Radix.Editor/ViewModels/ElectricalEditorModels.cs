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

/// <summary>One node's peak and settling summary, shown beside the transient points.</summary>
public sealed class ElectricalTransientMetricItem
{
    public ElectricalTransientMetricItem(TransientNodeMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        NodeId = metrics.NodeId;
        ExtremesText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"min {metrics.MinimumVolts:G4} · max {metrics.MaximumVolts:G4} · final {metrics.FinalVolts:G4}");
        PeakText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"peak @ {metrics.PeakTimeSeconds:G4} s");
        SettleText = metrics.SettlingTimeSeconds is double settling
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"settled at {settling:G4} s")
            : "not settled";
    }

    public string NodeId { get; }

    public string ExtremesText { get; }

    public string PeakText { get; }

    public string SettleText { get; }
}

/// <summary>
/// One node's presence on the transient plot. Hiding a trace keeps its palette slot, so the
/// remaining traces stay the same colour and a reader's memory of the picture survives.
/// </summary>
public sealed class ElectricalTransientSeriesItem : ObservableObject
{
    private readonly Action<ElectricalTransientSeriesItem> _changed;
    private bool _isVisible = true;

    public ElectricalTransientSeriesItem(
        string nodeId,
        int colorIndex,
        Action<ElectricalTransientSeriesItem> changed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(changed);
        NodeId = nodeId;
        ColorIndex = colorIndex;
        _changed = changed;
    }

    public string NodeId { get; }

    public int ColorIndex { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetField(ref _isVisible, value))
            {
                _changed(this);
            }
        }
    }

    public override string ToString() => NodeId;
}

/// <summary>
/// One level of a step-refinement study: its step count, its uniform step, the deviation from
/// the previous level, and the convergence order implied by two successive deviations. The
/// coarsest level is what the others are compared against, so it reports that instead of a zero
/// deviation, which would read as if it were exact.
/// </summary>
public sealed class ElectricalTransientRefinementItem
{
    public ElectricalTransientRefinementItem(TransientRefinementLevel level, bool isReference)
    {
        ArgumentNullException.ThrowIfNull(level);
        StepsText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{level.Steps} steps");
        StepText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{level.TimeStep:G4} s");
        DeviationText = isReference
            ? "reference"
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{level.MaximumDeviationVolts:G4} V");
        OrderText = level.ObservedOrder is double order
            ? order.ToString("G3", System.Globalization.CultureInfo.InvariantCulture)
            : "-";
    }

    public string StepsText { get; }

    public string StepText { get; }

    public string DeviationText { get; }

    public string OrderText { get; }

    public override string ToString() => $"{StepsText} · {DeviationText}";
}

/// <summary>One solved point of a transient run: its time and every node voltage.</summary>
public sealed class ElectricalTransientPointItem
{
    public ElectricalTransientPointItem(int index, TransientPoint point)
    {
        Index = index;
        Time = point.Time;
        TimeText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{point.Time:G6} s");
        VoltagesText = string.Join(
            "  ",
            point.NodeVoltages
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{entry.Key}={entry.Value:G6}")));
    }

    public int Index { get; }

    public double Time { get; }

    public string TimeText { get; }

    public string VoltagesText { get; }

    public override string ToString() => TimeText;
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
            ElectricalComponentKind.Capacitor => "Capacitor",
            ElectricalComponentKind.Inductor => "Inductor",
            ElectricalComponentKind.Mosfet => "MOSFET",
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
        Summary = ElectricalComponentMetadata.IsMosfet(component.Kind)
            ? $"{KindLabel} · {ElectricalComponentMetadata.ChannelName(component.Channel)}-channel · Vt {FormatValue(component.ThresholdVoltage)} V · {ValueText} {Unit}"
            : $"{KindLabel} · {ValueText} {Unit}";
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
