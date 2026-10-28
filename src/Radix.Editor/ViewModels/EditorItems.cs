using System.Collections.ObjectModel;
using System.Windows.Input;
using Radix.Core.Interpretation;
using Radix.Core.Model;

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
        ICommand removeCommand)
    {
        Id = pin.Id;
        Name = pin.Name;
        DirectionText = pin.Direction == Core.Signals.PinDirection.Input ? "IN" : "OUT";
        DomainId = pin.DomainId;
        NetLabel = netLabel;
        Domains = domains;
        SelectedDomain = domains.FirstOrDefault(domain => domain.Id == pin.DomainId);
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

    public ICommand RemoveCommand { get; }
}

public sealed class DomainChoice
{
    public DomainChoice(string id, string name, bool isStandard)
    {
        Id = id;
        Name = name;
        IsStandard = isStandard;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsStandard { get; }

    public string Label => IsStandard ? $"{Name} ({Id})" : Id;

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
        ICommand removeCommand,
        ICommand driveCommand)
    {
        Id = lane.Id;
        Name = lane.Name;
        FormatId = lane.FormatId;
        FormatName = formatName;
        PinSummary = pinSummary;
        CanDrive = canDrive;
        RemoveCommand = removeCommand;
        DriveCommand = driveCommand;
    }

    public string Id { get; }

    public string Name { get; }

    public string FormatId { get; }

    public string FormatName { get; }

    public string PinSummary { get; }

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
