using System.Collections.ObjectModel;
using System.Windows.Input;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Testing;

namespace Radix.Editor.ViewModels;

/// <summary>A label/value row whose value can be edited and committed by the view.</summary>
public sealed class EditableField : ObservableObject
{
    private string _value;

    public EditableField(
        string label,
        string value,
        bool isReadOnly = false,
        string? hint = null,
        Func<string, bool>? apply = null)
    {
        Label = label;
        _value = value;
        IsReadOnly = isReadOnly;
        Hint = hint;
        Apply = apply;
        LastCommitted = value;
    }

    public string Label { get; }

    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    public bool IsReadOnly { get; }

    public string? Hint { get; }

    public Func<string, bool>? Apply { get; }

    public string LastCommitted { get; set; }

    public bool CanEdit => !IsReadOnly && Apply is not null;
}

public sealed class PinListItem
{
    public PinListItem(
        PinDeclaration pin,
        string netLabel,
        IReadOnlyList<DomainChoice> domains,
        ICommand moveUpCommand,
        ICommand moveDownCommand,
        ICommand removeCommand)
    {
        Id = pin.Id;
        Name = pin.Name;
        DirectionText = pin.Direction == Core.Signals.PinDirection.Input ? "IN" : "OUT";
        DomainId = pin.DomainId;
        NetLabel = netLabel;
        Domains = domains;
        SelectedDomain = domains.FirstOrDefault(domain => domain.Id == pin.DomainId);
        MoveUpCommand = moveUpCommand;
        MoveDownCommand = moveDownCommand;
        RemoveCommand = removeCommand;
    }

    public string Id { get; }

    public string Name { get; set; }

    public string DirectionText { get; }

    public string DomainId { get; }

    public string NetLabel { get; }

    public IReadOnlyList<DomainChoice> Domains { get; }

    public DomainChoice? SelectedDomain { get; set; }

    public string Summary => $"{DirectionText} · {DomainId} · {NetLabel}";

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand RemoveCommand { get; }
}

public sealed class DomainChoice
{
    public DomainChoice(string id, string name, bool isStandard, string summary, ICommand editPolicyCommand)
    {
        Id = id;
        Name = name;
        IsStandard = isStandard;
        Summary = summary;
        EditPolicyCommand = editPolicyCommand;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsStandard { get; }

    /// <summary>Value symbols and driver policy, shown in the project inspector.</summary>
    public string Summary { get; }

    public ICommand EditPolicyCommand { get; }

    public string Label => IsStandard ? $"{Name} ({Id})" : Id;

    public override string ToString() => Label;
}

/// <summary>Selectable multi-driver policy for a user-authored domain.</summary>
public sealed class DriverPolicyChoice
{
    public DriverPolicyChoice(string id, string label, string description)
    {
        Id = id;
        Label = label;
        Description = description;
    }

    public string Id { get; }

    public string Label { get; }

    public string Description { get; }

    public override string ToString() => Label;
}

/// <summary>One interface pin offered when composing a numeric lane.</summary>
public sealed class LanePinChoice
{
    public LanePinChoice(PinDeclaration pin)
    {
        PinId = pin.Id;
        Label = $"{pin.Name} · {pin.Id} · {pin.Direction.ToString().ToLowerInvariant()}";
        DomainId = pin.DomainId;
    }

    public string PinId { get; }

    public string Label { get; }

    public string DomainId { get; }

    public bool IsSelected { get; set; }
}

/// <summary>Live presentation of one saved numeric lane.</summary>
public sealed class NumericLaneListItem : ObservableObject
{
    private string _valueText = "—";
    private string _valueHint = "Start or advance the simulation to decode this lane.";
    private string _driveText = string.Empty;

    public NumericLaneListItem(
        NumericLane lane,
        string formatName,
        string pinSummary,
        bool canDrive,
        ICommand editCommand,
        ICommand removeCommand,
        ICommand driveCommand)
    {
        Id = lane.Id;
        Name = lane.Name;
        FormatId = lane.FormatId;
        FormatName = formatName;
        PinSummary = pinSummary;
        CanDrive = canDrive;
        EditCommand = editCommand;
        RemoveCommand = removeCommand;
        DriveCommand = driveCommand;
    }

    public string Id { get; }

    public string Name { get; }

    public string FormatId { get; }

    public string FormatName { get; }

    public string PinSummary { get; }

    public ICommand EditCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand DriveCommand { get; }

    /// <summary>True when every pin in the lane is an input and can be driven.</summary>
    public bool CanDrive { get; }

    public string DriveText
    {
        get => _driveText;
        set => SetField(ref _driveText, value);
    }

    public string ValueText
    {
        get => _valueText;
        set => SetField(ref _valueText, value);
    }

    public string ValueHint
    {
        get => _valueHint;
        set => SetField(ref _valueHint, value);
    }
}

/// <summary>One row in the inspector's net navigator.</summary>
public sealed class NetListItem : ObservableObject
{
    private string _valueText = "—";
    private string _valueHint = "Start or advance the simulation to read this net.";

    public NetListItem(Net net, string name, string summary, ICommand selectCommand)
    {
        Id = net.Id;
        Name = name;
        Summary = summary;
        SelectCommand = selectCommand;
    }

    public string Id { get; }

    public string Name { get; }

    public string Summary { get; }

    public ICommand SelectCommand { get; }

    public string ValueText
    {
        get => _valueText;
        set => SetField(ref _valueText, value);
    }

    public string ValueHint
    {
        get => _valueHint;
        set => SetField(ref _valueHint, value);
    }
}

public sealed class NumericFormatModeChoice
{
    public NumericFormatModeChoice(string id, string label)
    {
        Id = id;
        Label = label;
    }

    public string Id { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

public sealed class CustomNumericFormatListItem
{
    public CustomNumericFormatListItem(
        NumericFormatDefinition definition,
        string summary,
        ICommand editCommand,
        ICommand removeCommand)
    {
        Id = definition.Id;
        Name = definition.Name;
        Summary = summary;
        EditCommand = editCommand;
        RemoveCommand = removeCommand;
    }

    public string Id { get; }

    public string Name { get; }

    public string Summary { get; }

    public ICommand EditCommand { get; }

    public ICommand RemoveCommand { get; }
}

/// <summary>One editable entry in a lookup table, labeled with its input values.</summary>
public sealed class LookupTableCell : ObservableObject
{
    private string _address;
    private string _value;

    public LookupTableCell(string instanceId, int index, string address, string value)
    {
        InstanceId = instanceId;
        Index = index;
        _address = address;
        _value = value;
    }

    public string InstanceId { get; }

    public int Index { get; }

    /// <summary>Entry index plus the mixed-radix input values, e.g. "3 · 0,1".</summary>
    public string Address
    {
        get => _address;
        set => SetField(ref _address, value);
    }

    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }
}

/// <summary>One failed assertion from an in-editor test-script run.</summary>
public sealed class TestScriptFailureItem
{
    public TestScriptFailureItem(ScriptFailure failure, string? source = null)
    {
        Step = failure.Step;
        At = failure.At;
        Message = failure.Message;
        string prefix = string.IsNullOrEmpty(source) ? string.Empty : $"[{source}] ";
        Display = $"{prefix}step {failure.Step} (t={failure.At}): {failure.Message}";
    }

    /// <summary>A suite entry that could not be loaded or compiled.</summary>
    public TestScriptFailureItem(string source, string error)
    {
        Step = 0;
        At = 0;
        Message = error;
        Display = $"[{System.IO.Path.GetFileName(source)}] error: {error}";
    }

    public int Step { get; }

    public long At { get; }

    public string Message { get; }

    public string Display { get; }
}

public sealed class IoState
{
    public IoState(string pinId, string name, string direction, string domainId)
    {
        PinId = pinId;
        Name = name;
        Direction = direction;
        DomainId = domainId;
    }

    public string PinId { get; }

    public string Name { get; }

    public string Direction { get; }

    public string DomainId { get; }

    public ObservableCollection<string>? Values { get; set; }
}
