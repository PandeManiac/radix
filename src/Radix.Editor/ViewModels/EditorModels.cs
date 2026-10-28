using System.Collections.ObjectModel;
using System.Windows.Input;
using Radix.Core;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.Editor.ViewModels;

public enum EditorPinKind
{
    Instance,
    Port,
}

/// <summary>A wire endpoint: an instance pin or a definition interface port.</summary>
public readonly record struct EditorPin(EditorPinKind Kind, string OwnerId, string PinId)
{
    public static EditorPin ForInstance(string instanceId, string pinId) => new(EditorPinKind.Instance, instanceId, pinId);

    public static EditorPin ForPort(string pinId) => new(EditorPinKind.Port, string.Empty, pinId);
}

public sealed class DefinitionListItem
{
    public DefinitionListItem(
        CircuitDefinition definition,
        bool isRoot,
        bool isCurrent,
        ICommand openCommand,
        ICommand placeCommand)
    {
        Id = definition.Id;
        Name = definition.Name;
        IsRoot = isRoot;
        IsCurrent = isCurrent;
        OpenCommand = openCommand;
        PlaceCommand = placeCommand;
        Subtitle = $"{(isRoot ? "root · " : string.Empty)}{definition.Pins.Count} pins · {definition.Instances.Count} instances";
        Prefix = isCurrent ? "▸ " : "  ";
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsRoot { get; }

    public bool IsCurrent { get; }

    public string Prefix { get; }

    public string Subtitle { get; }

    public ICommand OpenCommand { get; }

    public ICommand PlaceCommand { get; }
}

public sealed class PaletteItem
{
    public PaletteItem(string label, string category, string description, Action place)
    {
        Label = label;
        Category = category;
        Description = description;
        PlaceCommand = new RelayCommand(place);
    }

    public string Label { get; }

    public string Category { get; }

    public string Description { get; }

    public ICommand PlaceCommand { get; }
}

public sealed class IoValueOption
{
    public IoValueOption(string label, int value, Action<int> set)
    {
        Label = label;
        Value = value;
        SetCommand = new RelayCommand(() => set(value));
    }

    public string Label { get; }

    public int Value { get; }

    public ICommand SetCommand { get; }
}

public sealed class IoPinViewModel : ObservableObject
{
    private string _valueText = "–";

    public IoPinViewModel(string pinId, string name, PinDirection direction, SignalDomain domain, bool isInput)
    {
        PinId = pinId;
        Name = name;
        Direction = direction;
        Domain = domain;
        IsInput = isInput;
        Values = new ObservableCollection<IoValueOption>();
    }

    public string PinId { get; }

    public string Name { get; }

    public PinDirection Direction { get; }

    public SignalDomain Domain { get; }

    public bool IsInput { get; }

    public string DirectionText => Direction == PinDirection.Input ? "IN" : "OUT";

    public string DomainText => Domain.Id;

    public string ValueText
    {
        get => _valueText;
        set => SetField(ref _valueText, value);
    }

    public ObservableCollection<IoValueOption> Values { get; }
}

public sealed class WaveformNetViewModel
{
    public WaveformNetViewModel(string netId, string label, string domainName, ICommand removeCommand)
    {
        NetId = netId;
        Label = label;
        DomainName = domainName;
        RemoveCommand = removeCommand;
    }

    public string NetId { get; }

    public string Label { get; }

    public string DomainName { get; }

    public ICommand RemoveCommand { get; }
}

public sealed class SpeedOption
{
    public SpeedOption(string label, int ticksPerFrame)
    {
        Label = label;
        TicksPerFrame = ticksPerFrame;
    }

    public string Label { get; }

    public int TicksPerFrame { get; }
}

public sealed class DiagnosticItem
{
    public DiagnosticItem(Diagnostic diagnostic)
    {
        Severity = diagnostic.Severity;
        Code = diagnostic.Code;
        Message = diagnostic.Message;
        Location = diagnostic.Location;
        Display = diagnostic.ToString();
        Color = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => "#B3261E",
            DiagnosticSeverity.Warning => "#8A5A00",
            _ => "#44576A",
        };
    }

    public DiagnosticSeverity Severity { get; }

    public string Color { get; }

    public string Code { get; }

    public string Message { get; }

    public string Location { get; }

    public string Display { get; }
}

public readonly record struct WaveValueChange(long Time, int Value);

public sealed class WaveformRow
{
    public WaveformRow(string label, SignalDomain domain, int startValue, IReadOnlyList<WaveValueChange> changes)
    {
        Label = label;
        Domain = domain;
        StartValue = startValue;
        Changes = changes;
    }

    public string Label { get; }

    public SignalDomain Domain { get; }

    public int StartValue { get; }

    public IReadOnlyList<WaveValueChange> Changes { get; }
}

public sealed class WaveformSnapshot
{
    public static WaveformSnapshot Empty { get; } = new(0, 1, []);

    public WaveformSnapshot(long start, long end, IReadOnlyList<WaveformRow> rows)
    {
        Start = start;
        End = end;
        Rows = rows;
    }

    public long Start { get; }

    public long End { get; }

    public IReadOnlyList<WaveformRow> Rows { get; }
}

public sealed class InspectorField
{
    public InspectorField(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public string Value { get; }
}

public sealed class NumericDisplayOption
{
    public NumericDisplayOption(INumericFormat format)
    {
        Format = format;
        Label = format.Name;
    }

    public INumericFormat Format { get; }

    public string Label { get; }

    public override string ToString() => Label;
}
