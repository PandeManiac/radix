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
        ICommand placeCommand,
        ICommand duplicateCommand,
        int usageCount)
    {
        Id = definition.Id;
        Name = definition.Name;
        IsRoot = isRoot;
        IsCurrent = isCurrent;
        OpenCommand = openCommand;
        PlaceCommand = placeCommand;
        DuplicateCommand = duplicateCommand;
        Subtitle = $"{(isRoot ? "root · " : string.Empty)}{definition.Pins.Count} pins · {definition.Instances.Count} instances" +
            (usageCount > 0 ? $" · used ×{usageCount}" : string.Empty);
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

    public ICommand DuplicateCommand { get; }
}

/// <summary>
/// One parent definition that instantiates the definition being edited. Selecting
/// the row opens that parent and selects every matching instance there.
/// </summary>
public sealed class DefinitionUsageItem
{
    public DefinitionUsageItem(
        CircuitDefinition parent,
        IReadOnlyList<string> instanceIds,
        ICommand openCommand)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(instanceIds);
        ParentId = parent.Id;
        ParentName = parent.Name;
        Detail = instanceIds.Count == 1
            ? $"instance {instanceIds[0]}"
            : $"{instanceIds.Count} instances · {string.Join(", ", instanceIds.Take(8))}{(instanceIds.Count > 8 ? "…" : string.Empty)}";
        OpenCommand = openCommand;
    }

    public string ParentId { get; }

    public string ParentName { get; }

    public string Detail { get; }

    public ICommand OpenCommand { get; }
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
    public DiagnosticItem(Diagnostic diagnostic, ICommand locateCommand)
    {
        Diagnostic = diagnostic;
        Severity = diagnostic.Severity;
        Code = diagnostic.Code;
        Message = diagnostic.Message;
        Location = diagnostic.Location;
        Display = diagnostic.ToString();
        LocateCommand = locateCommand;
        CanLocate = diagnostic.DefinitionId is not null ||
            diagnostic.InstanceId is not null ||
            diagnostic.NetId is not null ||
            diagnostic.PinId is not null;
    }

    public Diagnostic Diagnostic { get; }

    public DiagnosticSeverity Severity { get; }

    public bool IsError => Severity == DiagnosticSeverity.Error;

    public bool IsWarning => Severity == DiagnosticSeverity.Warning;

    public string Code { get; }

    public string Message { get; }

    public string Location { get; }

    public string Display { get; }

    public ICommand LocateCommand { get; }

    /// <summary>True when the diagnostic names something in the circuit.</summary>
    public bool CanLocate { get; }
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

    /// <summary>
    /// Value held at an absolute tick. Changes are ordered, so this binary-searches
    /// instead of replaying the row on every cursor render.
    /// </summary>
    public int ValueAt(long time)
    {
        int low = 0;
        int high = Changes.Count - 1;
        int value = StartValue;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (Changes[middle].Time <= time)
            {
                value = Changes[middle].Value;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return value;
    }
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

/// <summary>One entry in the Recent projects flyout.</summary>
public sealed class RecentProjectItem
{
    public RecentProjectItem(string path, Action<string> open)
    {
        Path = path;
        Header = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        Directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        OpenCommand = new RelayCommand(() => open(path));
    }

    /// <summary>Full path; also the tooltip so long names stay discoverable.</summary>
    public string Path { get; }

    /// <summary>File name shown in the menu.</summary>
    public string Header { get; }

    /// <summary>Containing directory, shown as a second line.</summary>
    public string Directory { get; }

    public ICommand OpenCommand { get; }
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
