using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Windows.Input;
using Avalonia;
using Radix.Core;
using Radix.Core.Compilation;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Radix.Editor.Rendering;
using Radix.Editor.Services;
using Radix.StdLib;

namespace Radix.Editor.ViewModels;

/// <summary>
/// Application-level editor state. Owns the project, current definition, selection,
/// undo history, and the headless simulation host. Rendering lives in the canvas.
/// </summary>
public sealed class EditorViewModel : ObservableObject
{
    private readonly Stack<EditorSnapshot> _undo = new();
    private readonly Stack<EditorSnapshot> _redo = new();
    private readonly RelayCommand _undoCommand;
    private readonly RelayCommand _redoCommand;
    private readonly RelayCommand _deleteCommand;
    private readonly RelayCommand _copyCommand;
    private readonly RelayCommand _pasteCommand;
    private readonly RelayCommand _duplicateCommand;
    private readonly RelayCommand _selectAllCommand;
    private readonly RelayCommand _extractCommand;
    private readonly RelayCommand _alignLeftCommand;
    private readonly RelayCommand _alignTopCommand;
    private readonly RelayCommand _distributeHorizontallyCommand;
    private readonly RelayCommand _distributeVerticallyCommand;
    private readonly RelayCommand _promoteInputCommand;
    private readonly RelayCommand _promoteOutputCommand;
    private readonly HashSet<string> _selectedInstanceIds = new(StringComparer.Ordinal);
    private CircuitProject _project;
    private string _definitionId = string.Empty;
    private ComponentInstance? _selectedInstance;
    private Net? _selectedNet;
    private string? _projectPath;
    private bool _dirty;
    private bool _pendingDragUndo;
    private bool _dragActive;
    private bool _dragReusedRoutes;
    private bool _refreshingDomainSelection;
    private Point _placementPoint = new(260, 160);
    private double _placementCascade;
    private CircuitLayout? _layout;
    private WaveformSnapshot _waveform = WaveformSnapshot.Empty;
    private DomainChoice? _selectedNetDomain;
    private DomainChoice? _newPinDomain;
    private string _statusText = "Ready";
    private string _newPinName = string.Empty;
    private bool _newPinIsInput = true;
    private string _advanceTicksText = "10";
    private string _newDomainName = string.Empty;
    private string _newDomainValues = "0 1 2";
    private string _newDomainPriorities = "1 1 1";
    private string _newDomainConflictValue = "0";
    private DriverPolicyChoice? _selectedDriverPolicy;
    private string? _editingDomainId;

    /// <summary>Net IDs pinned for the waveform, keyed by definition ID.</summary>
    private readonly Dictionary<string, List<string>> _waveformPins = new(StringComparer.Ordinal);
    private readonly List<string> _recentProjectPaths = [];
    private string _librarySearch = string.Empty;
    private string _netSearch = string.Empty;
    private string _netListSummary = string.Empty;
    private ClipboardPayload? _clipboard;
    private int _pasteOffset;
    private string? _savedProjectJson;
    private string _newComponentName = "Component";
    private string _promoteNetName = string.Empty;
    private string _newLaneName = string.Empty;
    private NumericDisplayOption? _selectedLaneFormat;
    private string _newFormatName = string.Empty;
    private string _newFormatDigits = "0 1 2";
    private string _newFormatRadix = "3";
    private DomainChoice? _newFormatDomain;
    private NumericFormatModeChoice? _selectedFormatMode;
    private bool _newFormatLeastSignificantFirst = true;
    private string _newFormatNegativeSign = "1";
    private string? _editingFormatId;
    private string? _editingLaneId;
    private string _formatPreviewText = string.Empty;
    private bool _refreshingFormatForm;


    public EditorViewModel(CircuitProject project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        EnsureProjectShape();
        _savedProjectJson = ProjectSerializer.SaveToString(_project, includeView: false);
        _definitionId = _project.FindDefinition(_project.View.ActiveDefinitionId)?.Id ?? _project.RootDefinitionId;
        Simulator = new SimulatorHost(_project, Definition);
        _advanceTicksText = _project.Settings.DefaultAdvanceTicks.ToString(CultureInfo.InvariantCulture);
        RefreshFormatChoices();
        _selectedFormatMode = FormatModes[0];
        _selectedLaneFormat = FormatChoices.FirstOrDefault(option => option.Format.Id == NumericFormats.UnsignedBinary.Id) ??
            FormatChoices.FirstOrDefault();
        _selectedDriverPolicy = DriverPolicies[0];

        _undoCommand = new RelayCommand(Undo, () => _undo.Count > 0);
        _redoCommand = new RelayCommand(Redo, () => _redo.Count > 0);

        UndoCommand = _undoCommand;
        RedoCommand = _redoCommand;
        NewProjectCommand = new RelayCommand(NewProject);
        AddDefinitionCommand = new RelayCommand(AddDefinition);
        RemoveDefinitionCommand = new RelayCommand(RemoveDefinition);
        AddPinCommand = new RelayCommand(AddPin);
        AddDomainCommand = new RelayCommand(AddDomain);
        CancelDomainEditCommand = new RelayCommand(CancelDomainEdit);
        ToggleWaveformPinCommand = new RelayCommand(ToggleSelectedNetWaveformPin);
        ClearWaveformPinsCommand = new RelayCommand(ClearWaveformPins);
        _deleteCommand = new RelayCommand(DeleteSelection, () => _selectedInstanceIds.Count > 0 || _selectedNet is not null);
        DeleteSelectionCommand = _deleteCommand;
        _copyCommand = new RelayCommand(() => CopySelection(), () => _selectedInstanceIds.Count > 0);
        CopyCommand = _copyCommand;
        _pasteCommand = new RelayCommand(Paste, () => _clipboard is not null);
        PasteCommand = _pasteCommand;
        _duplicateCommand = new RelayCommand(DuplicateSelection, () => _selectedInstanceIds.Count > 0);
        DuplicateCommand = _duplicateCommand;
        _selectAllCommand = new RelayCommand(SelectAllInstances, () => Definition.Instances.Count > 0);
        SelectAllCommand = _selectAllCommand;
        _extractCommand = new RelayCommand(ExtractSelectionToComponent, () => _selectedInstanceIds.Count > 0);
        ExtractSelectionCommand = _extractCommand;
        _alignLeftCommand = new RelayCommand(AlignSelectionLeft, () => _selectedInstanceIds.Count > 1);
        AlignLeftCommand = _alignLeftCommand;
        _alignTopCommand = new RelayCommand(AlignSelectionTop, () => _selectedInstanceIds.Count > 1);
        AlignTopCommand = _alignTopCommand;
        _distributeHorizontallyCommand = new RelayCommand(
            DistributeSelectionHorizontally,
            () => _selectedInstanceIds.Count > 2);
        DistributeHorizontallyCommand = _distributeHorizontallyCommand;
        _distributeVerticallyCommand = new RelayCommand(
            DistributeSelectionVertically,
            () => _selectedInstanceIds.Count > 2);
        DistributeVerticallyCommand = _distributeVerticallyCommand;
        _promoteInputCommand = new RelayCommand(
            () => PromoteSelectedNet(PinDirection.Input),
            () => CanPromoteSelectedNet);
        PromoteInputCommand = _promoteInputCommand;
        _promoteOutputCommand = new RelayCommand(
            () => PromoteSelectedNet(PinDirection.Output),
            () => CanPromoteSelectedNet);
        PromoteOutputCommand = _promoteOutputCommand;
        ClearNetRouteCommand = new RelayCommand(ClearSelectedNetRoute, () => _selectedNet is not null);
        BeginCommand = new RelayCommand(BeginSimulation);
        AdvanceCommand = new RelayCommand(AdvanceSimulation);
        StepEventCommand = new RelayCommand(StepEvent);
        StepClockCommand = new RelayCommand(StepClock);
        SettleCommand = new RelayCommand(Settle);
        ClearDrivesCommand = new RelayCommand(ClearDrives);
        FitCommand = new RelayCommand(() => FitRequested?.Invoke());
        CloseDefinitionCommand = new RelayCommand(() => OpenDefinition(_project.RootDefinitionId));
        AddNumericLaneCommand = new RelayCommand(AddNumericLane);
        CancelLaneEditCommand = new RelayCommand(CancelNumericLaneEdit);
        AddNumericFormatCommand = new RelayCommand(AddCustomNumericFormat);
        CancelFormatEditCommand = new RelayCommand(CancelCustomNumericFormatEdit);

        RefreshAll(fit: false);
    }

    public event Action? VisualInvalidated;

    public event Action? FitRequested;

    /// <summary>Raised after loading a project that has a saved viewport.</summary>
    public event Action? ViewRestoreRequested;

    /// <summary>Raised with world bounds that should be centered after navigation.</summary>
    public event Action<Rect>? FocusRequested;

    /// <summary>
    /// Raised when a recent-project entry is chosen. The window handles it so the
    /// same unsaved-changes prompt as the Open button is used.
    /// </summary>
    public event Action<string>? RecentProjectOpenRequested;

    /// <summary>Raised after a project is written successfully, so recovery can be cleared.</summary>
    public event Action? ProjectSaved;

    public CircuitProject Project => _project;

    /// <summary>
    /// Stores the canvas viewport with the project without marking the project dirty.
    /// Presentation state is saved on the next explicit save, not on every pan.
    /// </summary>
    public void SyncViewport(double scale, double offsetX, double offsetY)
    {
        if (!double.IsFinite(scale) || scale <= 0 ||
            !double.IsFinite(offsetX) || !double.IsFinite(offsetY))
        {
            return;
        }

        _project.View.Scale = scale;
        _project.View.OffsetX = offsetX;
        _project.View.OffsetY = offsetY;
    }

    public CircuitDefinition Definition =>
        _project.FindDefinition(_definitionId) ?? _project.Definitions[0];

    public SimulatorHost Simulator { get; private set; }

    public CircuitLayout? Layout
    {
        get => _layout;
        private set
        {
            _layout = value;
            RaisePropertyChanged();
        }
    }

    public ObservableCollection<DefinitionListItem> Definitions { get; } = [];

    public ObservableCollection<PaletteItem> Palette { get; } = [];

    public ObservableCollection<PinListItem> Pins { get; } = [];

    public ObservableCollection<IoPinViewModel> IoPins { get; } = [];

    public ObservableCollection<NetListItem> NetList { get; } = [];

    public ObservableCollection<NumericLaneListItem> NumericLanes { get; } = [];

    public ObservableCollection<LanePinChoice> LanePinChoices { get; } = [];

    public ObservableCollection<NumericDisplayOption> FormatChoices { get; } = [];

    public ObservableCollection<CustomNumericFormatListItem> CustomNumericFormats { get; } = [];

    public ObservableCollection<NumericFormatModeChoice> FormatModes { get; } =
    [
        new("standard", "Standard positional"),
        new("radix-complement", "Radix complement (top digit negative)"),
        new("diminished-radix-complement", "Diminished complement"),
        new("sign-magnitude", "Sign-magnitude"),
    ];

    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = [];

    public ObservableCollection<EditableField> Fields { get; } = [];

    public ObservableCollection<string> RecentTransitions { get; } = [];

    /// <summary>Recently opened or saved projects, newest first; missing files are dropped on load.</summary>
    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = [];

    public bool HasRecentProjects => RecentProjects.Count > 0;

    /// <summary>Paths captured into workspace settings when the window closes.</summary>
    public IReadOnlyList<string> RecentProjectPaths => _recentProjectPaths;

    public ObservableCollection<DomainChoice> Domains { get; } = [];

    public ObservableCollection<DriverPolicyChoice> DriverPolicies { get; } =
    [
        new("single", "Single driver", "Multiple active drivers are a conflict; the first compiled driver wins."),
        new("priority", "Priority table", "The highest-priority active driver wins; ties resolve to a conflict value."),
    ];

    public ObservableCollection<string> Events { get; } = [];

    public ICommand UndoCommand { get; }

    public ICommand RedoCommand { get; }

    public ICommand NewProjectCommand { get; }

    public ICommand AddDefinitionCommand { get; }

    public ICommand RemoveDefinitionCommand { get; }

    public ICommand AddPinCommand { get; }

    public ICommand AddDomainCommand { get; }

    public ICommand CancelDomainEditCommand { get; }

    public bool IsEditingDomainPolicy => _editingDomainId is not null;

    public string DomainSaveButtonText => IsEditingDomainPolicy ? "Update domain policy" : "Add domain";

    /// <summary>Value labels and count stay fixed during a policy edit.</summary>
    public bool IsDomainValueFieldsEnabled => !IsEditingDomainPolicy;

    /// <summary>Maximum waveform rows; pinned nets are shown first.</summary>
    public const int MaxWaveformRows = 16;

    public ICommand ToggleWaveformPinCommand { get; }

    public ICommand ClearWaveformPinsCommand { get; }

    public bool SelectedNetOnWaveform =>
        _selectedNet is not null && IsWaveformPinned(_selectedNet.Id);

    public string WaveformPinButtonText => SelectedNetOnWaveform
        ? "Hide from waveform"
        : "Show in waveform";

    public int WaveformPinCount => PinnedWaveformNetIds().Count;

    public bool HasWaveformPins => WaveformPinCount > 0;

    public string WaveformPinSummary
    {
        get
        {
            int pinned = WaveformPinCount;
            return pinned == 0
                ? $"Up to {MaxWaveformRows} nets, in declaration order. Pin a net from its inspector to show it first."
                : $"{pinned} pinned net(s) shown first, {MaxWaveformRows} rows max.";
        }
    }

    public ICommand DeleteSelectionCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand PasteCommand { get; }

    public ICommand DuplicateCommand { get; }

    public ICommand SelectAllCommand { get; }

    public ICommand ExtractSelectionCommand { get; }

    public ICommand AlignLeftCommand { get; }

    public ICommand AlignTopCommand { get; }

    public ICommand DistributeHorizontallyCommand { get; }

    public ICommand DistributeVerticallyCommand { get; }

    public ICommand PromoteInputCommand { get; }

    public ICommand PromoteOutputCommand { get; }

    public ICommand ClearNetRouteCommand { get; }

    public ICommand BeginCommand { get; }

    public ICommand AdvanceCommand { get; }

    public ICommand StepEventCommand { get; }

    public ICommand StepClockCommand { get; }

    public ICommand SettleCommand { get; }

    public ICommand ClearDrivesCommand { get; }

    public ICommand FitCommand { get; }

    public ICommand CloseDefinitionCommand { get; }

    public string ProjectTitle =>
        $"{_project.Name}{(_dirty ? " *" : string.Empty)}";

    public string DefinitionTitle => $"{(Definition.Id == _project.RootDefinitionId ? "root" : "part")} · {Definition.Name}";

    public string ProjectSubtitle =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{_project.Definitions.Count} definitions · {Definition.Instances.Count} instances · {Definition.Nets.Count} nets");

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string? ProjectPath
    {
        get => _projectPath;
        private set => SetField(ref _projectPath, value);
    }

    /// <summary>True when this session was restored from a crash snapshot and has not been saved.</summary>
    public bool IsRecoveredProject { get; private set; }

    public bool IsDirty
    {
        get => _dirty;
        private set
        {
            if (SetField(ref _dirty, value))
            {
                RaisePropertyChanged(nameof(ProjectTitle));
            }
        }
    }

    public WaveformSnapshot Waveform
    {
        get => _waveform;
        private set => SetField(ref _waveform, value);
    }

    public string SimTimeText => string.Create(CultureInfo.InvariantCulture, $"t = {Simulator.Now}");

    public string SimStatusText
    {
        get
        {
            if (!Simulator.IsCompiled)
            {
                return "not compiled";
            }

            if (Simulator.IsHalted)
            {
                return "halted";
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Simulator.EventsProcessed} events · {Simulator.ComponentEvaluations} evaluations");
        }
    }

    public string DiagnosticSummary
    {
        get
        {
            int errors = Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            int warnings = Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            return string.Create(CultureInfo.InvariantCulture, $"{errors} errors · {warnings} warnings");
        }
    }

    public string ZoomText => "100%";

    public bool HasSelection => _selectedInstanceIds.Count > 0 || _selectedNet is not null;

    public bool HasSelectedInstance => _selectedInstanceIds.Count > 0;

    public bool HasSelectedNet => _selectedNet is not null;

    public bool HasMultipleSelection => _selectedInstanceIds.Count > 1;

    public int SelectionCount => _selectedInstanceIds.Count + (_selectedNet is null ? 0 : 1);

    public IReadOnlyCollection<string> SelectedInstanceIds => _selectedInstanceIds;

    public string SelectedInstanceId => _selectedInstance?.Id ?? string.Empty;

    public string SelectedNetId => _selectedNet?.Id ?? string.Empty;

    public bool IsRootDefinition => Definition.Id == _project.RootDefinitionId;

    public bool SelectedNetHasRoute => _selectedNet?.RoutePoints.Count > 0;

    public string SelectionTitle
    {
        get
        {
            if (_selectedInstanceIds.Count > 1)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{_selectedInstanceIds.Count} parts");
            }

            if (_selectedInstance is not null)
            {
                return _selectedInstance.Label ?? TypeName(_selectedInstance);
            }

            if (_selectedNet is not null)
            {
                return _selectedNet.Label ?? _selectedNet.Id;
            }

            return Definition.Name;
        }
    }

    public string SelectionSubtitle
    {
        get
        {
            if (_selectedInstanceIds.Count > 1)
            {
                return "multi-selection · drag any selected part to move the group";
            }

            if (_selectedInstance is not null)
            {
                return $"instance · {_selectedInstance.Id}";
            }

            if (_selectedNet is not null)
            {
                return $"net · {_selectedNet.Id} · {_selectedNet.DomainId}";
            }

            return "definition · " + Definition.Id;
        }
    }

    public string LibrarySearch
    {
        get => _librarySearch;
        set
        {
            if (SetField(ref _librarySearch, value))
            {
                RefreshPalette();
                RefreshDefinitions();
            }
        }
    }

    public bool HasPaletteResults => Palette.Count > 0;

    public string NetSearch
    {
        get => _netSearch;
        set
        {
            if (SetField(ref _netSearch, value))
            {
                RefreshNetList();
            }
        }
    }

    public string NetListSummary => _netListSummary;

    public bool HasNetList => NetList.Count > 0;

    public bool HasNumericLanes => NumericLanes.Count > 0;

    public string NewLaneName
    {
        get => _newLaneName;
        set => SetField(ref _newLaneName, value);
    }

    public NumericDisplayOption? SelectedLaneFormat
    {
        get => _selectedLaneFormat;
        set => SetField(ref _selectedLaneFormat, value);
    }

    public ICommand AddNumericLaneCommand { get; }

    public ICommand CancelLaneEditCommand { get; }

    public bool IsEditingLane => _editingLaneId is not null;

    public string LaneSaveButtonText => IsEditingLane ? "Update numeric lane" : "Add numeric lane";

    public ICommand AddNumericFormatCommand { get; }

    public ICommand CancelFormatEditCommand { get; }

    public bool IsEditingFormat => _editingFormatId is not null;

    public string FormatSaveButtonText => IsEditingFormat ? "Update numeric format" : "Add numeric format";

    public string FormatPreviewText
    {
        get => _formatPreviewText;
        private set => SetField(ref _formatPreviewText, value);
    }

    public bool HasCustomNumericFormats => CustomNumericFormats.Count > 0;

    public string NewFormatName
    {
        get => _newFormatName;
        set
        {
            if (SetField(ref _newFormatName, value))
            {
                RefreshFormatPreview();
            }
        }
    }

    public string NewFormatDigits
    {
        get => _newFormatDigits;
        set
        {
            if (SetField(ref _newFormatDigits, value))
            {
                RefreshFormatPreview();
            }
        }
    }

    public string NewFormatRadix
    {
        get => _newFormatRadix;
        set
        {
            if (SetField(ref _newFormatRadix, value))
            {
                RefreshFormatPreview();
            }
        }
    }

    public DomainChoice? NewFormatDomain
    {
        get => _newFormatDomain;
        set
        {
            if (SetField(ref _newFormatDomain, value))
            {
                RefreshFormatPreview();
            }
        }
    }

    public NumericFormatModeChoice? SelectedFormatMode
    {
        get => _selectedFormatMode;
        set
        {
            if (SetField(ref _selectedFormatMode, value))
            {
                RefreshFormatPreview();
            }
        }
    }

    public bool NewFormatLeastSignificantFirst
    {
        get => _newFormatLeastSignificantFirst;
        set
        {
            if (SetField(ref _newFormatLeastSignificantFirst, value))
            {
                RefreshFormatPreview();
            }
        }
    }

    public string NewFormatNegativeSign
    {
        get => _newFormatNegativeSign;
        set
        {
            if (SetField(ref _newFormatNegativeSign, value))
            {
                RefreshFormatPreview();
            }
        }
    }

    public string NewComponentName
    {
        get => _newComponentName;
        set => SetField(ref _newComponentName, value);
    }

    public string PromoteNetName
    {
        get => _promoteNetName;
        set => SetField(ref _promoteNetName, value);
    }

    public bool CanPromoteSelectedNet =>
        _selectedNet is not null &&
        _selectedNet.Pins.Count > 0 &&
        !Definition.Ports.Any(port => string.Equals(port.NetId, _selectedNet.Id, StringComparison.Ordinal));

    public string NewPinName
    {
        get => _newPinName;
        set => SetField(ref _newPinName, value);
    }

    public bool NewPinIsInput
    {
        get => _newPinIsInput;
        set => SetField(ref _newPinIsInput, value);
    }

    public DomainChoice? NewPinDomain
    {
        get => _newPinDomain;
        set => SetField(ref _newPinDomain, value);
    }

    public DomainChoice? SelectedNetDomain
    {
        get => _selectedNetDomain;
        set
        {
            if (SetField(ref _selectedNetDomain, value) && !_refreshingDomainSelection && value is not null)
            {
                ChangeNetDomain(value.Id);
            }
        }
    }

    public string AdvanceTicksText
    {
        get => _advanceTicksText;
        set => SetField(ref _advanceTicksText, value);
    }

    public string NewDomainName
    {
        get => _newDomainName;
        set => SetField(ref _newDomainName, value);
    }

    public string NewDomainValues
    {
        get => _newDomainValues;
        set => SetField(ref _newDomainValues, value);
    }

    public string NewDomainPriorities
    {
        get => _newDomainPriorities;
        set => SetField(ref _newDomainPriorities, value);
    }

    public string NewDomainConflictValue
    {
        get => _newDomainConflictValue;
        set => SetField(ref _newDomainConflictValue, value);
    }

    public DriverPolicyChoice? SelectedDriverPolicy
    {
        get => _selectedDriverPolicy;
        set
        {
            if (SetField(ref _selectedDriverPolicy, value))
            {
                RaisePropertyChanged(nameof(IsPriorityDriverPolicy));
                RaisePropertyChanged(nameof(DriverPolicyHint));
            }
        }
    }

    public bool IsPriorityDriverPolicy => _selectedDriverPolicy?.Id == "priority";

    public string DriverPolicyHint => _selectedDriverPolicy?.Description ?? string.Empty;

    public void SetPlacementPoint(Point world)
    {
        _placementPoint = world;
    }

    public void SelectInstance(string? instanceId, bool additive = false)
    {
        _selectedNet = null;
        if (instanceId is null)
        {
            _selectedInstanceIds.Clear();
            _selectedInstance = null;
        }
        else if (additive)
        {
            if (!_selectedInstanceIds.Add(instanceId))
            {
                _selectedInstanceIds.Remove(instanceId);
            }

            _selectedInstance = _selectedInstanceIds.Count == 0
                ? null
                : Definition.FindInstance(instanceId) ?? FindSelectedInstance();
        }
        else
        {
            _selectedInstanceIds.Clear();
            _selectedInstanceIds.Add(instanceId);
            _selectedInstance = Definition.FindInstance(instanceId);
            if (_selectedInstance is null)
            {
                _selectedInstanceIds.Clear();
            }
        }

        RaiseSelectionChanged();
        RefreshInspector();
        VisualInvalidated?.Invoke();
    }

    public void SelectInstances(IEnumerable<string> instanceIds, bool additive = false)
    {
        _selectedNet = null;
        if (!additive)
        {
            _selectedInstanceIds.Clear();
        }

        foreach (string id in instanceIds)
        {
            if (Definition.FindInstance(id) is not null)
            {
                _selectedInstanceIds.Add(id);
            }
        }

        _selectedInstance = FindSelectedInstance();
        RaiseSelectionChanged();
        RefreshInspector();
        VisualInvalidated?.Invoke();
    }

    public void SelectAllInstances()
    {
        SelectInstances(Definition.Instances.Select(i => i.Id));
        StatusText = string.Create(CultureInfo.InvariantCulture, $"Selected {_selectedInstanceIds.Count} parts");
    }

    public void SelectNet(string? netId)
    {
        _selectedNet = netId is null ? null : Definition.FindNet(netId);
        _selectedInstance = null;
        _selectedInstanceIds.Clear();
        RaiseSelectionChanged();
        RefreshInspector();
        VisualInvalidated?.Invoke();
    }

    public void ClearSelection()
    {
        _selectedInstance = null;
        _selectedInstanceIds.Clear();
        _selectedNet = null;
        RaiseSelectionChanged();
        RefreshInspector();
        VisualInvalidated?.Invoke();
    }

    /// <summary>
    /// Opens the diagnostic's definition and selects the instance or net it names.
    /// Presentation focus only: no project structure or simulation state is changed.
    /// </summary>
    public bool NavigateToDiagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (diagnostic.DefinitionId is string definitionId)
        {
            if (_project.FindDefinition(definitionId) is null)
            {
                StatusText = $"Definition '{definitionId}' from {diagnostic.Code} no longer exists.";
                return false;
            }

            OpenDefinition(definitionId);
        }

        if (diagnostic.InstanceId is string instanceId &&
            Definition.FindInstance(instanceId) is ComponentInstance instance)
        {
            Rect? bounds = Layout?.Components
                .FirstOrDefault(box => string.Equals(box.Instance.Id, instance.Id, StringComparison.Ordinal))
                ?.Bounds;
            SelectInstance(instance.Id);
            FocusBounds(bounds);
            StatusText = $"Located instance '{instance.Id}' ({diagnostic.Code})";
            return true;
        }

        if (diagnostic.NetId is string netId && Definition.FindNet(netId) is Net net)
        {
            SelectNet(net.Id);
            FocusBounds(BoundsOfNet(net.Id));
            StatusText = $"Located net '{net.Id}' ({diagnostic.Code})";
            return true;
        }

        if (diagnostic.PinId is string pinId)
        {
            if (Definition.FindPortNet(pinId) is Net portNet)
            {
                SelectNet(portNet.Id);
                FocusBounds(BoundsOfNet(portNet.Id));
                StatusText = $"Located interface pin '{pinId}' ({diagnostic.Code})";
                return true;
            }

            if (Definition.FindPin(pinId) is PinDeclaration pin)
            {
                StatusText = $"Interface pin '{pin.Name}' has no net to select.";
                return false;
            }
        }

        if (diagnostic.DefinitionId is not null)
        {
            StatusText = $"Opened definition '{Definition.Name}' for {diagnostic.Code}; its target is gone.";
            return true;
        }

        StatusText = $"{diagnostic.Code} has no circuit location.";
        return false;
    }

    private void FocusBounds(Rect? bounds)
    {
        if (bounds is Rect rect)
        {
            FocusRequested?.Invoke(rect);
        }
    }

    private Rect? BoundsOfNet(string netId)
    {
        NetWire? wire = Layout?.FindWire(netId);
        if (wire is null || wire.Points.Count == 0)
        {
            return null;
        }

        double minX = wire.Points.Min(point => point.X);
        double minY = wire.Points.Min(point => point.Y);
        double maxX = wire.Points.Max(point => point.X);
        double maxY = wire.Points.Max(point => point.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private ComponentInstance? FindSelectedInstance()
    {
        for (int i = Definition.Instances.Count - 1; i >= 0; i--)
        {
            if (_selectedInstanceIds.Contains(Definition.Instances[i].Id))
            {
                return Definition.Instances[i];
            }
        }

        return null;
    }

    public void OpenDefinition(string definitionId)
    {
        if (_project.FindDefinition(definitionId) is null || string.Equals(_definitionId, definitionId, StringComparison.Ordinal))
        {
            return;
        }

        _definitionId = definitionId;
        _selectedInstanceIds.Clear();
        _selectedInstance = null;
        _selectedNet = null;
        Simulator.Retarget(_project, Definition);
        RefreshAll(fit: true);
        StatusText = $"Editing {Definition.Name}";
    }

    public void RenameProject(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        PushUndo();
        _project.Name = trimmed;
        AfterMutation(structural: false, invalidateSimulation: false);
    }

    public void NewProject()
    {
        CancelDomainEdit();
        _waveformPins.Clear();
        var project = CircuitProject.CreateEmpty("Untitled");
        var root = new CircuitDefinition { Id = "main", Name = "Main", Description = "Root circuit" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);

        _project = project;
        _definitionId = root.Id;
        _projectPath = null;
        _savedProjectJson = null;
        IsRecoveredProject = false;
        _selectedInstanceIds.Clear();
        _selectedInstance = null;
        _selectedNet = null;
        _clipboard = null;
        _pasteOffset = 0;
        _undo.Clear();
        _redo.Clear();
        Simulator = new SimulatorHost(_project, Definition);
        RefreshAll(fit: true);
        AdvanceTicksText = project.Settings.DefaultAdvanceTicks.ToString(CultureInfo.InvariantCulture);
        IsDirty = true;
        StatusText = "New project";
    }

    public bool LoadProject(string path)
    {
        try
        {
            CircuitProject loaded = ProjectSerializer.Load(path);
            CancelDomainEdit();
            _waveformPins.Clear();
            _project = loaded;
            EnsureProjectShape();
            _definitionId = loaded.FindDefinition(loaded.View.ActiveDefinitionId)?.Id ?? loaded.RootDefinitionId;
            _projectPath = path;
            _savedProjectJson = ProjectSerializer.SaveToString(_project, includeView: false);
            IsRecoveredProject = false;
            _selectedInstanceIds.Clear();
            _selectedInstance = null;
            _selectedNet = null;
            _clipboard = null;
            _pasteOffset = 0;
            _undo.Clear();
            _redo.Clear();
            Simulator = new SimulatorHost(_project, Definition);
            RefreshAll(fit: true);
            ViewRestoreRequested?.Invoke();
            AdvanceTicksText = _project.Settings.DefaultAdvanceTicks.ToString(CultureInfo.InvariantCulture);
            IsDirty = false;
            AddRecentProject(path);
            StatusText = $"Opened {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception exception) when (exception is ProjectFormatException or CircuitValidationException or IOException or UnauthorizedAccessException)
        {
            StatusText = "Open failed: " + exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Seeds the recent list from workspace settings. Entries whose files no longer
    /// exist are skipped so the flyout never offers a guaranteed failure.
    /// </summary>
    public void SetRecentProjects(IEnumerable<string>? paths)
    {
        _recentProjectPaths.Clear();
        if (paths is not null)
        {
            foreach (string path in paths)
            {
                if (_recentProjectPaths.Count == WorkspacePreferences.MaxRecentProjects)
                {
                    break;
                }

                string? normalized = TryNormalizeProjectPath(path);
                if (normalized is null || !File.Exists(normalized) ||
                    _recentProjectPaths.Contains(normalized, StringComparer.Ordinal))
                {
                    continue;
                }

                _recentProjectPaths.Add(normalized);
            }
        }

        RefreshRecentProjects();
    }

    public void OpenRecentProject(string path) => RecentProjectOpenRequested?.Invoke(path);

    private void AddRecentProject(string path)
    {
        string? normalized = TryNormalizeProjectPath(path);
        if (normalized is null)
        {
            return;
        }

        _recentProjectPaths.RemoveAll(candidate =>
            string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
        _recentProjectPaths.Insert(0, normalized);
        if (_recentProjectPaths.Count > WorkspacePreferences.MaxRecentProjects)
        {
            _recentProjectPaths.RemoveRange(
                WorkspacePreferences.MaxRecentProjects,
                _recentProjectPaths.Count - WorkspacePreferences.MaxRecentProjects);
        }

        RefreshRecentProjects();
    }

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        foreach (string path in _recentProjectPaths)
        {
            RecentProjects.Add(new RecentProjectItem(path, OpenRecentProject));
        }

        RaisePropertyChanged(nameof(HasRecentProjects));
    }

    private static string? TryNormalizeProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Restores a crash-recovery snapshot as an unsaved project. The snapshot already
    /// passed through the serializer when it was written, so a project left in an
    /// intermediate state still opens and can be repaired with diagnostics.
    /// </summary>
    public bool LoadRecoveredProject(RecoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            CancelDomainEdit();
            _waveformPins.Clear();
            _project = snapshot.Project;
            EnsureProjectShape();
            _definitionId = _project.FindDefinition(_project.View.ActiveDefinitionId)?.Id ?? _project.RootDefinitionId;
            _projectPath = snapshot.ProjectPath;
            _savedProjectJson = null;
            _selectedInstanceIds.Clear();
            _selectedInstance = null;
            _selectedNet = null;
            _clipboard = null;
            _pasteOffset = 0;
            _undo.Clear();
            _redo.Clear();
            Simulator = new SimulatorHost(_project, Definition);
            RefreshAll(fit: true);
            ViewRestoreRequested?.Invoke();
            AdvanceTicksText = _project.Settings.DefaultAdvanceTicks.ToString(CultureInfo.InvariantCulture);
            if (snapshot.ProjectPath is { Length: > 0 } recoveredPath && File.Exists(recoveredPath))
            {
                AddRecentProject(recoveredPath);
            }

            IsRecoveredProject = true;
            IsDirty = true;
            StatusText = snapshot.ProjectPath is { Length: > 0 } path
                ? $"Recovered unsaved changes from {Path.GetFileName(path)}"
                : "Recovered an unsaved project";
            return true;
        }
        catch (Exception exception) when (exception is ProjectFormatException or CircuitValidationException)
        {
            StatusText = "Recovery failed: " + exception.Message;
            return false;
        }
    }

    /// <summary>Exports one definition and its dependency closure as a portable library.</summary>
    public bool ExportDefinition(string path, string definitionId)
    {
        try
        {
            string json = ComponentLibraryService.Export(_project, definitionId);
            File.WriteAllText(path, json);
            string exportedName = _project.FindDefinition(definitionId)?.Name ?? definitionId;
            StatusText = $"Exported '{exportedName}' to {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProjectFormatException or InvalidOperationException)
        {
            StatusText = "Export failed: " + exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Merges a library into the current project. Domain conflicts, dependency gaps,
    /// and new validation errors abort without leaving partial definitions behind.
    /// </summary>
    public bool ImportLibrary(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            EditorSnapshot before = Capture();
            LibraryImportResult result = ComponentLibraryService.Import(_project, json);
            if (!result.Success || result.RootDefinitionId is not string rootId)
            {
                StatusText = result.Message;
                return false;
            }

            PushUndo(before);
            _definitionId = rootId;
            _selectedInstanceIds.Clear();
            _selectedInstance = null;
            _selectedNet = null;
            Simulator.Retarget(_project, Definition);
            RefreshAll(fit: true);
            IsDirty = true;
            StatusText = result.Message;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProjectFormatException or CircuitValidationException)
        {
            StatusText = "Import failed: " + exception.Message;
            return false;
        }
    }

    public bool SaveProject(string path)
    {
        try
        {
            ProjectSerializer.Save(path, _project);
            _projectPath = path;
            _savedProjectJson = ProjectSerializer.SaveToString(_project, includeView: false);
            IsDirty = false;
            IsRecoveredProject = false;
            AddRecentProject(path);
            StatusText = $"Saved {Path.GetFileName(path)}";
            ProjectSaved?.Invoke();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProjectFormatException)
        {
            StatusText = "Save failed: " + exception.Message;
            return false;
        }
    }

    public void BeginDrag()
    {
        _pendingDragUndo = true;
        _dragActive = true;
    }

    public void EndDrag()
    {
        bool needsCanonicalLayout = _dragReusedRoutes;
        _dragActive = false;
        _dragReusedRoutes = false;
        if (!_pendingDragUndo)
        {
            RefreshInspector();
        }

        _pendingDragUndo = false;
        if (needsCanonicalLayout)
        {
            // Drag frames reuse unchanged routes for responsiveness; the final frame
            // rebuilds canonically so the committed layout is never approximate.
            RebuildLayout();
        }
    }

    public void MoveInstance(string instanceId, double x, double y)
    {
        ComponentInstance? instance = Definition.FindInstance(instanceId);
        if (instance is null)
        {
            return;
        }

        if (_pendingDragUndo)
        {
            PushUndo();
            _pendingDragUndo = false;
        }

        instance.X = Math.Round(x, 1);
        instance.Y = Math.Round(y, 1);
        IsDirty = true;
        RebuildLayout(reuseRoutes: _dragActive);
        _dragReusedRoutes |= _dragActive;
        RefreshSimStatus();
        RefreshInspectorPosition(instance);
    }

    public void NudgeInstance(string instanceId, double dx, double dy)
    {
        if (_selectedInstanceIds.Count > 1)
        {
            NudgeSelection(dx, dy);
            return;
        }

        ComponentInstance? instance = Definition.FindInstance(instanceId);
        if (instance is null)
        {
            return;
        }

        PushUndo();
        instance.X += dx;
        instance.Y += dy;
        AfterMutation(structural: false, invalidateSimulation: false);
    }

    public void NudgeSelection(double dx, double dy)
    {
        if (_selectedInstanceIds.Count == 0)
        {
            return;
        }

        PushUndo();
        foreach (string id in _selectedInstanceIds)
        {
            ComponentInstance? instance = Definition.FindInstance(id);
            if (instance is not null)
            {
                instance.X += dx;
                instance.Y += dy;
            }
        }

        AfterMutation(structural: false, invalidateSimulation: false);
    }

    /// <summary>
    /// Arranges the selected parts without touching connectivity or the running
    /// simulation: presentation-only edits keep the compiled engine and its time.
    /// </summary>
    public void AlignSelectionLeft()
    {
        List<ComponentInstance> instances = SelectedInstances();
        if (instances.Count < 2)
        {
            return;
        }

        PushUndo();
        double left = instances.Min(instance => instance.X);
        foreach (ComponentInstance instance in instances)
        {
            instance.X = Math.Round(left, 1);
        }

        AfterMutation(structural: false, invalidateSimulation: false);
        StatusText = $"Aligned {instances.Count} parts to the left";
    }

    public void AlignSelectionTop()
    {
        List<ComponentInstance> instances = SelectedInstances();
        if (instances.Count < 2)
        {
            return;
        }

        PushUndo();
        double top = instances.Min(instance => instance.Y);
        foreach (ComponentInstance instance in instances)
        {
            instance.Y = Math.Round(top, 1);
        }

        AfterMutation(structural: false, invalidateSimulation: false);
        StatusText = $"Aligned {instances.Count} parts to the top";
    }

    /// <summary>
    /// Spaces the selected parts evenly between the outermost left edges. The two
    /// extremes stay where they are, so repeated distribution is stable.
    /// </summary>
    public void DistributeSelectionHorizontally()
    {
        List<ComponentInstance> instances = SelectedInstances()
            .OrderBy(instance => instance.X)
            .ThenBy(instance => instance.Y)
            .ThenBy(instance => instance.Id, StringComparer.Ordinal)
            .ToList();
        if (instances.Count < 3)
        {
            return;
        }

        PushUndo();
        double first = instances[0].X;
        double step = (instances[^1].X - first) / (instances.Count - 1);
        for (int i = 1; i < instances.Count - 1; i++)
        {
            instances[i].X = Math.Round(first + (step * i), 1);
        }

        AfterMutation(structural: false, invalidateSimulation: false);
        StatusText = $"Spread {instances.Count} parts horizontally";
    }

    public void DistributeSelectionVertically()
    {
        List<ComponentInstance> instances = SelectedInstances()
            .OrderBy(instance => instance.Y)
            .ThenBy(instance => instance.X)
            .ThenBy(instance => instance.Id, StringComparer.Ordinal)
            .ToList();
        if (instances.Count < 3)
        {
            return;
        }

        PushUndo();
        double first = instances[0].Y;
        double step = (instances[^1].Y - first) / (instances.Count - 1);
        for (int i = 1; i < instances.Count - 1; i++)
        {
            instances[i].Y = Math.Round(first + (step * i), 1);
        }

        AfterMutation(structural: false, invalidateSimulation: false);
        StatusText = $"Spread {instances.Count} parts vertically";
    }

    private List<ComponentInstance> SelectedInstances()
    {
        var instances = new List<ComponentInstance>(_selectedInstanceIds.Count);
        foreach (string id in _selectedInstanceIds)
        {
            if (Definition.FindInstance(id) is ComponentInstance instance)
            {
                instances.Add(instance);
            }
        }

        return instances;
    }

    public void SetInstanceState(string instanceId, double x, double y)
    {
        ComponentInstance? instance = Definition.FindInstance(instanceId);
        if (instance is null)
        {
            return;
        }

        if (_pendingDragUndo)
        {
            PushUndo();
            _pendingDragUndo = false;
        }

        instance.X = Math.Round(x, 1);
        instance.Y = Math.Round(y, 1);
        IsDirty = true;
        RebuildLayout();
        RefreshSimStatus();
    }

    public void DeleteSelection()
    {
        if (_selectedInstanceIds.Count > 0)
        {
            var ids = _selectedInstanceIds.ToArray();
            PushUndo();
            foreach (string id in ids)
            {
                ComponentInstance? instance = Definition.FindInstance(id);
                if (instance is null)
                {
                    continue;
                }

                Definition.Instances.Remove(instance);
                foreach (Net net in Definition.Nets)
                {
                    net.Pins.RemoveAll(p => string.Equals(p.InstanceId, id, StringComparison.Ordinal));
                }
            }

            CleanEmptyNets();
            _selectedInstanceIds.Clear();
            _selectedInstance = null;
            AfterMutation(structural: true);
            StatusText = ids.Length == 1
                ? $"Deleted instance {ids[0]}"
                : string.Create(CultureInfo.InvariantCulture, $"Deleted {ids.Length} instances");
            return;
        }

        if (_selectedNet is not null)
        {
            if (Definition.Ports.Any(p => string.Equals(p.NetId, _selectedNet.Id, StringComparison.Ordinal)))
            {
                StatusText = "Remove the interface pin before deleting its net.";
                return;
            }

            string id = _selectedNet.Id;
            PushUndo();
            DefinitionEditor.RemoveNet(Definition, id);
            _selectedNet = null;
            AfterMutation(structural: true);
            StatusText = $"Deleted net {id}";
        }
    }

    public void AddDefinition()
    {
        PushUndo();
        string id = DefinitionEditor.UniqueId(
            "part",
            candidate => _project.FindDefinition(candidate) is not null);
        var definition = new CircuitDefinition
        {
            Id = id,
            Name = "New component",
            Description = "Describe the interface and build it from parts.",
        };
        _project.Definitions.Add(definition);
        _definitionId = id;
        _selectedInstanceIds.Clear();
        _selectedInstance = null;
        _selectedNet = null;
        Simulator.Retarget(_project, Definition);
        RefreshAll(fit: true);
        IsDirty = true;
        StatusText = $"Created component {id}";
    }

    /// <summary>
    /// Copies the active definition into a new variant. The copy keeps definition-local
    /// IDs, so it is an independent body that still reuses the same child parts.
    /// </summary>
    public void DuplicateDefinition() => DuplicateDefinitionOf(Definition.Id);

    private void DuplicateDefinitionOf(string definitionId)
    {
        if (_project.FindDefinition(definitionId) is not CircuitDefinition source)
        {
            StatusText = $"Definition '{definitionId}' no longer exists.";
            return;
        }

        string id = NextDefinitionCopyId(source);
        string name = UniqueDefinitionName(source.Name + " copy");

        PushUndo();
        CircuitDefinition copy = source.Clone(id, name);
        int index = _project.Definitions.IndexOf(source);
        _project.Definitions.Insert(index < 0 ? _project.Definitions.Count : index + 1, copy);
        _definitionId = id;
        _selectedInstanceIds.Clear();
        _selectedInstance = null;
        _selectedNet = null;
        IsDirty = true;
        RefreshAll(fit: true);
        StatusText = $"Duplicated {source.Name} as {copy.Name} ({id})";
    }

    /// <summary>
    /// Builds a readable copy ID such as <c>part-copy</c>, reusing the base stem when a
    /// copy is duplicated again so repeated copies count up instead of nesting suffixes.
    /// </summary>
    private string NextDefinitionCopyId(CircuitDefinition source)
    {
        int copyIndex = source.Id.IndexOf("-copy", StringComparison.Ordinal);
        string stem = copyIndex > 0 ? source.Id[..copyIndex] : source.Id;
        if (stem.Length == 0)
        {
            stem = "part";
        }

        return DefinitionEditor.UniqueId(
            stem + "-copy",
            candidate => _project.FindDefinition(candidate) is not null);
    }

    private string UniqueDefinitionName(string name)
    {
        // Reuse the base name when copying a copy, so names count up ("Part copy 2")
        // instead of nesting ("Part copy copy").
        const string suffix = " copy";
        string stem = name;
        int copyIndex = name.IndexOf(suffix, StringComparison.Ordinal);
        if (copyIndex > 0)
        {
            stem = name[..copyIndex];
        }

        string candidate = stem + suffix;
        int index = 1;
        while (_project.Definitions.Any(definition =>
            string.Equals(definition.Name, candidate, StringComparison.Ordinal)))
        {
            candidate = stem + suffix + " " + (++index).ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    public void RemoveDefinition()
    {
        if (Definition.Id == _project.RootDefinitionId)
        {
            StatusText = "The root definition cannot be removed.";
            return;
        }

        if (_project.Definitions.Count <= 1)
        {
            return;
        }

        bool used = _project.Definitions.Any(d => d.Instances.Any(i =>
            string.Equals(i.DefinitionId, Definition.Id, StringComparison.Ordinal)));
        if (used)
        {
            StatusText = "This component is instantiated elsewhere; remove those instances first.";
            return;
        }

        PushUndo();
        string removed = Definition.Id;
        _project.Definitions.Remove(Definition);
        _definitionId = _project.RootDefinitionId;
        _selectedInstanceIds.Clear();
        _selectedInstance = null;
        _selectedNet = null;
        Simulator.Retarget(_project, Definition);
        RefreshAll(fit: true);
        IsDirty = true;
        StatusText = $"Removed {removed}";
    }

    public void AddDomain()
    {
        if (_editingDomainId is string editingId)
        {
            UpdateDomainPolicy(editingId);
            return;
        }

        string name = NewDomainName.Trim();
        if (name.Length == 0)
        {
            StatusText = "Give the domain a name.";
            return;
        }

        string[] values = NewDomainValues.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (values.Length == 0 || values.Length > 64)
        {
            StatusText = "A domain needs between 1 and 64 value labels.";
            return;
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            StatusText = "Domain value labels must be unique.";
            return;
        }

        if (!TryBuildDriverPolicy(values.Length, out IDriverResolution drivers, out string policyError))
        {
            StatusText = policyError;
            return;
        }

        string stem = Slug(name);
        string id = DefinitionEditor.UniqueId(stem, candidate => _project.FindDomain(candidate) is not null);
        SignalDomain domain;
        try
        {
            domain = new SignalDomain(
                id,
                name,
                values,
                drivers,
                "User-defined discrete domain. Values are symbols, not arithmetic digits.");
        }
        catch (ArgumentException exception)
        {
            StatusText = "Invalid domain: " + exception.Message;
            return;
        }

        PushUndo();
        _project.Domains.Add(domain);
        NewDomainName = string.Empty;
        AfterMutation(structural: true);
        NewPinDomain = Domains.FirstOrDefault(d => d.Id == id) ?? NewPinDomain;
        StatusText = domain.IsMultipleDriverPolicy
            ? $"Added domain {domain.Name} ({domain.ValueCount} values · priority resolution)"
            : $"Added domain {domain.Name} ({domain.ValueCount} values)";
    }

    /// <summary>
    /// Loads a domain's driver policy into the form. Value labels and count stay fixed
    /// so existing signals and pin domains remain valid.
    /// </summary>
    public void BeginEditDomainPolicy(string domainId)
    {
        if (_project.FindDomain(domainId) is not SignalDomain domain)
        {
            StatusText = $"Domain '{domainId}' no longer exists.";
            return;
        }

        _editingDomainId = domain.Id;
        NewDomainName = domain.Name;
        NewDomainValues = string.Join(" ", domain.ValueNames);
        if (domain.Drivers is PriorityDriverResolution priority)
        {
            SelectedDriverPolicy = DriverPolicies.Single(choice => choice.Id == "priority");
            NewDomainPriorities = string.Join(" ", priority.Priorities);
            NewDomainConflictValue = priority.ConflictValue.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            SelectedDriverPolicy = DriverPolicies.Single(choice => choice.Id == "single");
            NewDomainPriorities = string.Join(" ", Enumerable.Repeat(1, domain.ValueCount));
            NewDomainConflictValue = "0";
        }

        RaiseDomainEditChanged();
        StatusText = $"Editing driver policy for domain '{domain.Name}'";
    }

    public void CancelDomainEdit()
    {
        if (_editingDomainId is null)
        {
            return;
        }

        _editingDomainId = null;
        ResetDomainForm();
        RaiseDomainEditChanged();
        StatusText = "Domain policy edit cancelled";
    }

    private void UpdateDomainPolicy(string domainId)
    {
        if (_project.FindDomain(domainId) is not SignalDomain existing)
        {
            CancelDomainEdit();
            StatusText = $"Domain '{domainId}' no longer exists.";
            return;
        }

        if (!TryBuildDriverPolicy(existing.ValueCount, out IDriverResolution drivers, out string error))
        {
            StatusText = error;
            return;
        }

        SignalDomain updated;
        try
        {
            updated = new SignalDomain(
                existing.Id,
                existing.Name,
                existing.ValueNames,
                drivers,
                existing.Description);
        }
        catch (ArgumentException exception)
        {
            StatusText = "Invalid driver policy: " + exception.Message;
            return;
        }

        int index = _project.Domains.FindIndex(candidate =>
            string.Equals(candidate.Id, domainId, StringComparison.Ordinal));
        if (index < 0)
        {
            CancelDomainEdit();
            return;
        }

        PushUndo();
        _project.Domains[index] = updated;
        _editingDomainId = null;
        ResetDomainForm();
        RaiseDomainEditChanged();
        AfterMutation(structural: true);
        StatusText = $"Updated driver policy for domain {updated.Name}: {updated.Drivers.Describe()}";
    }

    private void ResetDomainForm()
    {
        NewDomainName = string.Empty;
        NewDomainValues = "0 1 2";
        SelectedDriverPolicy = DriverPolicies[0];
        NewDomainPriorities = "1 1 1";
        NewDomainConflictValue = "0";
    }

    private void RaiseDomainEditChanged()
    {
        RaisePropertyChanged(nameof(IsEditingDomainPolicy));
        RaisePropertyChanged(nameof(DomainSaveButtonText));
        RaisePropertyChanged(nameof(IsDomainValueFieldsEnabled));
    }

    private bool TryBuildDriverPolicy(int valueCount, out IDriverResolution drivers, out string error)
    {
        drivers = new SingleDriverResolution();
        error = string.Empty;
        if (!IsPriorityDriverPolicy)
        {
            return true;
        }

        string[] priorityTokens = NewDomainPriorities.Split(
            [' ', ',', ';', '\t'],
            StringSplitOptions.RemoveEmptyEntries);
        if (priorityTokens.Length != valueCount)
        {
            error = $"Enter exactly {valueCount} priority value(s), one per domain value.";
            return false;
        }

        var priorities = new int[priorityTokens.Length];
        for (int i = 0; i < priorityTokens.Length; i++)
        {
            if (!int.TryParse(priorityTokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out priorities[i]) ||
                priorities[i] < 0)
            {
                error = "Priorities must be non-negative whole numbers.";
                return false;
            }
        }

        if (!int.TryParse(NewDomainConflictValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int conflictValue) ||
            conflictValue < 0 || conflictValue >= valueCount)
        {
            error = $"Conflict value must be a value index between 0 and {valueCount - 1}.";
            return false;
        }

        try
        {
            drivers = new PriorityDriverResolution(priorities, conflictValue);
            return true;
        }
        catch (ArgumentException exception)
        {
            error = "Invalid priority policy: " + exception.Message;
            return false;
        }
    }

    public void AddPin()
    {
        DomainChoice? domain = NewPinDomain ?? Domains.FirstOrDefault();
        if (domain is null)
        {
            StatusText = "Add a signal domain first.";
            return;
        }

        PushUndo();
        PinDeclaration? pin = DefinitionEditor.AddPin(
            _project,
            Definition,
            NewPinName,
            NewPinIsInput ? PinDirection.Input : PinDirection.Output,
            domain.Id);
        NewPinName = string.Empty;
        AfterMutation(structural: true);
        StatusText = pin is null ? "Could not add pin." : $"Added pin {pin.Name}";
    }

    public void RenamePin(string pinId, string name)
    {
        PinDeclaration? pin = Definition.FindPin(pinId);
        string trimmed = name.Trim();
        if (pin is null || trimmed.Length == 0 || string.Equals(pin.Name, trimmed, StringComparison.Ordinal))
        {
            return;
        }

        PushUndo();
        if (DefinitionEditor.SetPinName(Definition, pinId, trimmed))
        {
            AfterMutation(structural: true);
            StatusText = $"Renamed pin to '{trimmed}'";
        }
    }

    public void ChangePinDomain(string pinId, string domainId)
    {
        PinDeclaration? pin = Definition.FindPin(pinId);
        if (pin is null ||
            string.Equals(pin.DomainId, domainId, StringComparison.Ordinal) ||
            _project.FindDomain(domainId) is null)
        {
            return;
        }

        PushUndo();
        if (DefinitionEditor.SetPinDomain(_project, Definition, pinId, domainId))
        {
            AfterMutation(structural: true);
            StatusText = $"Pin '{pin.Name}' now uses {domainId}";
        }
    }

    public void TogglePinDirection(string pinId)
    {
        PinDeclaration? pin = Definition.FindPin(pinId);
        if (pin is null)
        {
            return;
        }

        PinDirection direction = pin.Direction == PinDirection.Input ? PinDirection.Output : PinDirection.Input;
        PushUndo();
        DefinitionEditor.SetPinDirection(Definition, pinId, direction);
        AfterMutation(structural: true);
        StatusText = $"Pin '{pin.Name}' is now {(direction == PinDirection.Input ? "an input" : "an output")}";
    }

    public void AddCustomNumericFormat()
    {
        string name = NewFormatName.Trim();
        if (name.Length == 0)
        {
            StatusText = "Give the numeric format a name.";
            return;
        }

        string id;
        bool editing = _editingFormatId is not null;
        if (_editingFormatId is string editingId)
        {
            if (_project.FindCustomNumericFormat(editingId) is null)
            {
                CancelCustomNumericFormatEdit();
                StatusText = $"Numeric format '{editingId}' is no longer available.";
                return;
            }

            id = editingId;
        }
        else
        {
            id = DefinitionEditor.UniqueId(Slug(name), candidate =>
                NumericFormatCatalog.IsBuiltInId(candidate) || _project.FindCustomNumericFormat(candidate) is not null);
        }

        if (!TryBuildFormatDraft(id, out NumericFormatDefinition? format, out string error) || format is null)
        {
            StatusText = error;
            return;
        }

        PushUndo();
        if (editing)
        {
            int index = _project.CustomNumericFormats.FindIndex(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            _project.CustomNumericFormats[index] = format;
            _editingFormatId = null;
            ResetFormatForm();
            RaisePropertyChanged(nameof(IsEditingFormat));
            RaisePropertyChanged(nameof(FormatSaveButtonText));
        }
        else
        {
            _project.CustomNumericFormats.Add(format);
            ResetFormatForm();
        }

        AfterMutation(structural: true, invalidateSimulation: false);
        StatusText = editing
            ? $"Updated numeric format '{format.Name}' ({format.Id})"
            : $"Added numeric format '{format.Name}' ({format.Id})";
    }

    public void BeginEditCustomNumericFormat(string formatId)
    {
        if (_project.FindCustomNumericFormat(formatId) is not NumericFormatDefinition format)
        {
            StatusText = $"Numeric format '{formatId}' no longer exists.";
            return;
        }

        _editingFormatId = format.Id;
        _refreshingFormatForm = true;
        try
        {
            NewFormatName = format.Name;
            NewFormatDomain = Domains.FirstOrDefault(domain =>
                string.Equals(domain.Id, format.DomainId, StringComparison.Ordinal)) ?? Domains.FirstOrDefault();
            NewFormatDigits = string.Join(" ", format.DigitValues);
            NewFormatRadix = format.Radix.ToString(CultureInfo.InvariantCulture);
            SelectedFormatMode = FormatModes.FirstOrDefault(mode =>
                string.Equals(mode.Id, FormatModeId(format), StringComparison.Ordinal)) ?? FormatModes[0];
            NewFormatLeastSignificantFirst = format.LeastSignificantFirst;
            NewFormatNegativeSign = (format.NegativeSignValue ?? 1).ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _refreshingFormatForm = false;
        }

        RaisePropertyChanged(nameof(IsEditingFormat));
        RaisePropertyChanged(nameof(FormatSaveButtonText));
        RefreshFormatPreview();
        StatusText = $"Editing numeric format '{format.Name}'";
    }

    public void CancelCustomNumericFormatEdit()
    {
        if (_editingFormatId is null)
        {
            return;
        }

        _editingFormatId = null;
        ResetFormatForm();
        RaisePropertyChanged(nameof(IsEditingFormat));
        RaisePropertyChanged(nameof(FormatSaveButtonText));
        StatusText = "Format edit cancelled";
    }

    private void ResetFormatForm()
    {
        _refreshingFormatForm = true;
        try
        {
            NewFormatName = string.Empty;
            NewFormatDigits = "0 1 2";
            NewFormatRadix = "3";
            NewFormatDomain = Domains.FirstOrDefault();
            SelectedFormatMode = FormatModes[0];
            NewFormatLeastSignificantFirst = true;
            NewFormatNegativeSign = "1";
        }
        finally
        {
            _refreshingFormatForm = false;
        }

        RefreshFormatPreview();
    }

    private bool TryBuildFormatDraft(
        string id,
        out NumericFormatDefinition? format,
        out string error)
    {
        format = null;
        if (NewFormatDomain is null)
        {
            error = "Choose a signal domain for the numeric format.";
            return false;
        }

        string name = NewFormatName.Trim();
        if (name.Length == 0)
        {
            error = "Give the numeric format a name.";
            return false;
        }

        string[] digitTexts = NewFormatDigits.Split(
            [' ', ',', ';', '\t'],
            StringSplitOptions.RemoveEmptyEntries);
        var digits = new List<long>();
        foreach (string digitText in digitTexts)
        {
            if (!long.TryParse(digitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long digit))
            {
                error = $"Digit value '{digitText}' is not a whole number.";
                return false;
            }

            digits.Add(digit);
        }

        if (digits.Count == 0)
        {
            error = "A numeric format needs at least one digit value.";
            return false;
        }

        if (digits.Distinct().Count() != digits.Count)
        {
            error = "Numeric format digit values must be distinct.";
            return false;
        }

        if (!long.TryParse(NewFormatRadix.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long radix) ||
            radix < 2)
        {
            error = "Radix must be a whole number of at least 2.";
            return false;
        }

        string mode = SelectedFormatMode?.Id ?? "standard";
        WeightingMode weighting = mode switch
        {
            "radix-complement" => WeightingMode.RadixComplement,
            "diminished-radix-complement" => WeightingMode.DiminishedRadixComplement,
            _ => WeightingMode.Standard,
        };
        int? negativeSign = null;
        if (mode == "sign-magnitude")
        {
            if (!int.TryParse(NewFormatNegativeSign.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sign) ||
                sign < 0 ||
                sign >= digits.Count)
            {
                error = "Sign-magnitude needs a negative sign index inside the digit list.";
                return false;
            }

            negativeSign = sign;
        }

        try
        {
            format = new NumericFormatDefinition(
                id,
                name,
                NewFormatDomain.Id,
                digits,
                radix,
                weighting,
                NewFormatLeastSignificantFirst,
                negativeSign);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = "Invalid numeric format: " + exception.Message;
            return false;
        }
    }

    private static string FormatModeId(NumericFormatDefinition format)
    {
        if (format.IsSignMagnitude)
        {
            return "sign-magnitude";
        }

        return format.Weighting switch
        {
            WeightingMode.RadixComplement => "radix-complement",
            WeightingMode.DiminishedRadixComplement => "diminished-radix-complement",
            _ => "standard",
        };
    }

    private void RefreshFormatPreview()
    {
        if (_refreshingFormatForm)
        {
            return;
        }

        string id = _editingFormatId ?? "preview";
        if (!TryBuildFormatDraft(id, out NumericFormatDefinition? draft, out string error) || draft is null)
        {
            FormatPreviewText = "Preview: " + error;
            return;
        }

        INumericFormat previewFormat = draft.ToFormat();
        if (_editingFormatId is string editingId)
        {
            NumericLane? lane = Definition.NumericLanes.FirstOrDefault(candidate =>
                string.Equals(candidate.FormatId, editingId, StringComparison.Ordinal));
            if (lane is not null)
            {
                if (lane.PinIds.Count == 0)
                {
                    FormatPreviewText = $"Preview · {lane.Name}: this lane has no pins.";
                    return;
                }

                if (Simulator.Engine is null)
                {
                    FormatPreviewText = $"Preview · {previewFormat.Describe(lane.PinIds.Count)} · start the simulation to decode live values.";
                    return;
                }

                int[] values = new int[lane.PinIds.Count];
                for (int i = 0; i < lane.PinIds.Count; i++)
                {
                    if (!Simulator.TryRead(EditorPin.ForPort(lane.PinIds[i]), out int value, out _))
                    {
                        FormatPreviewText = $"Preview · {lane.Name}: pin values are not available.";
                        return;
                    }

                    values[i] = value;
                }

                if (!previewFormat.LeastSignificantFirst)
                {
                    Array.Reverse(values);
                }

                NumericDecodeResult decoded = previewFormat.Decode(values);
                FormatPreviewText = decoded.IsValid
                    ? $"Preview · {lane.Name} = {decoded.Value.ToString(CultureInfo.InvariantCulture)}"
                    : $"Preview · {lane.Name}: {decoded.Error}";
                return;
            }
        }

        FormatPreviewText = $"Preview · {previewFormat.Describe(Math.Max(1, draft.DigitValues.Count))}";
    }

    public void RemoveCustomNumericFormat(string formatId)
    {
        if (_project.FindCustomNumericFormat(formatId) is not NumericFormatDefinition format)
        {
            return;
        }

        int uses = _project.Definitions.Sum(definition =>
            definition.NumericLanes.Count(lane => string.Equals(lane.FormatId, formatId, StringComparison.Ordinal)));
        if (uses > 0)
        {
            StatusText = $"Format '{format.Name}' is used by {uses} numeric lane(s); remove those lanes first.";
            return;
        }

        bool wasEditing = string.Equals(_editingFormatId, formatId, StringComparison.Ordinal);
        PushUndo();
        _project.CustomNumericFormats.Remove(format);
        if (wasEditing)
        {
            _editingFormatId = null;
            ResetFormatForm();
            RaisePropertyChanged(nameof(IsEditingFormat));
            RaisePropertyChanged(nameof(FormatSaveButtonText));
        }

        AfterMutation(structural: true, invalidateSimulation: false);
        StatusText = $"Removed numeric format '{format.Name}'";
    }

    /// <summary>
    /// Adds a lane, or updates the one currently being edited. Both paths validate the
    /// full draft first and commit it as a single undo step.
    /// </summary>
    public void AddNumericLane()
    {
        NumericLane? editingLane = _editingLaneId is string editingId
            ? Definition.FindNumericLane(editingId)
            : null;
        if (_editingLaneId is not null && editingLane is null)
        {
            CancelNumericLaneEdit();
        }

        List<PinDeclaration> selected = Definition.Pins
            .Where(pin => LanePinChoices.Any(choice =>
                choice.IsSelected && string.Equals(choice.PinId, pin.Id, StringComparison.Ordinal)))
            .ToList();
        if (selected.Count == 0)
        {
            StatusText = "Select at least one interface pin for the numeric lane.";
            return;
        }

        if (selected.Select(pin => pin.DomainId).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            StatusText = "A numeric lane can only group pins from one signal domain.";
            return;
        }

        INumericFormat? chosenFormat = SelectedLaneFormat?.Format;
        string formatId = chosenFormat?.Id ?? editingLane?.FormatId ?? NumericFormats.UnsignedBinary.Id;
        bool updating = editingLane is not null;
        PushUndo();
        NumericLane? lane = updating
            ? (DefinitionEditor.UpdateNumericLane(
                Definition,
                editingLane!.Id,
                NewLaneName,
                formatId,
                selected.Select(pin => pin.Id))
                ? editingLane
                : null)
            : DefinitionEditor.AddNumericLane(Definition, NewLaneName, formatId, selected.Select(pin => pin.Id));
        if (lane is null)
        {
            _undo.Pop();
            StatusText = updating
                ? "Could not update the numeric lane."
                : "Could not add the numeric lane.";
            return;
        }

        if (updating)
        {
            _editingLaneId = null;
            ResetLaneForm();
            RaisePropertyChanged(nameof(IsEditingLane));
            RaisePropertyChanged(nameof(LaneSaveButtonText));
        }
        else
        {
            NewLaneName = string.Empty;
        }

        AfterMutation(structural: true, invalidateSimulation: false);
        string note = chosenFormat is null ||
            string.Equals(selected[0].DomainId, chosenFormat.DomainId, StringComparison.Ordinal)
            ? string.Empty
            : $" · note: {chosenFormat.Name} is intended for domain '{chosenFormat.DomainId}'.";
        StatusText = updating
            ? $"Updated numeric lane '{lane.Name}' ({lane.PinIds.Count} pins).{note}"
            : $"Added numeric lane '{lane.Name}' ({lane.PinIds.Count} pins).{note}";
    }

    public void BeginEditNumericLane(string laneId)
    {
        if (Definition.FindNumericLane(laneId) is not NumericLane lane)
        {
            StatusText = $"Numeric lane '{laneId}' no longer exists.";
            return;
        }

        _editingLaneId = lane.Id;
        NewLaneName = lane.Name;
        NumericDisplayOption? match = FormatChoices.FirstOrDefault(option =>
            string.Equals(option.Format.Id, lane.FormatId, StringComparison.Ordinal));
        SelectedLaneFormat = match;
        foreach (LanePinChoice choice in LanePinChoices)
        {
            choice.IsSelected = lane.PinIds.Contains(choice.PinId, StringComparer.Ordinal);
        }

        RaisePropertyChanged(nameof(IsEditingLane));
        RaisePropertyChanged(nameof(LaneSaveButtonText));
        StatusText = match is null
            ? $"Editing numeric lane '{lane.Name}' · format '{lane.FormatId}' is unknown; pick a known format to replace it."
            : $"Editing numeric lane '{lane.Name}'";
    }

    public void CancelNumericLaneEdit()
    {
        if (_editingLaneId is null)
        {
            return;
        }

        _editingLaneId = null;
        ResetLaneForm();
        RaisePropertyChanged(nameof(IsEditingLane));
        RaisePropertyChanged(nameof(LaneSaveButtonText));
        StatusText = "Lane edit cancelled";
    }

    private void ResetLaneForm()
    {
        NewLaneName = string.Empty;
        SelectedLaneFormat = FormatChoices.FirstOrDefault(option =>
            option.Format.Id == NumericFormats.UnsignedBinary.Id) ?? FormatChoices.FirstOrDefault();
        foreach (LanePinChoice choice in LanePinChoices)
        {
            choice.IsSelected = false;
        }
    }

    public void RemoveNumericLane(string laneId)
    {
        if (Definition.FindNumericLane(laneId) is not NumericLane lane)
        {
            return;
        }

        bool wasEditing = string.Equals(_editingLaneId, laneId, StringComparison.Ordinal);
        PushUndo();
        if (!DefinitionEditor.RemoveNumericLane(Definition, laneId))
        {
            _undo.Pop();
            return;
        }

        if (wasEditing)
        {
            _editingLaneId = null;
            ResetLaneForm();
            RaisePropertyChanged(nameof(IsEditingLane));
            RaisePropertyChanged(nameof(LaneSaveButtonText));
        }

        AfterMutation(structural: true, invalidateSimulation: false);
        StatusText = $"Removed numeric lane '{lane.Name}'";
    }

    public void RemovePin(string pinId)
    {
        string name = Definition.FindPin(pinId)?.Name ?? pinId;
        PushUndo();
        if (!DefinitionEditor.RemovePin(Definition, pinId))
        {
            _undo.Pop();
            return;
        }

        (int references, int nets) = DefinitionEditor.PruneInstancePinReferences(_project, Definition.Id, pinId);
        AfterMutation(structural: true);
        string extra = references == 0
            ? string.Empty
            : $" · removed {references} dangling connection(s)" + (nets > 0 ? $" and {nets} empty net(s)" : string.Empty);
        StatusText = $"Removed pin {name}{extra}";
    }

    /// <summary>
    /// Changes interface declaration order. Pin refs are stable IDs, so existing
    /// connections and lane pin lists are untouched; new lanes read the new order.
    /// </summary>
    public void MovePin(string pinId, int offset)
    {
        if (Definition.FindPin(pinId) is not PinDeclaration pin)
        {
            return;
        }

        PushUndo();
        if (!DefinitionEditor.MovePin(Definition, pinId, offset))
        {
            _undo.Pop();
            return;
        }

        AfterMutation(structural: true);
        StatusText = $"Moved interface pin '{pin.Name}' {(offset < 0 ? "up" : "down")}";
    }

    /// <summary>Pins or unpins the selected net for the waveform panel.</summary>
    public void ToggleSelectedNetWaveformPin()
    {
        if (_selectedNet is null)
        {
            return;
        }

        Net net = _selectedNet;
        List<string> pins = PinnedWaveformNetIds();
        if (pins.Remove(net.Id))
        {
            StatusText = $"Removed {(string.IsNullOrWhiteSpace(net.Label) ? net.Id : net.Label)} from the waveform";
        }
        else
        {
            pins.Add(net.Id);
            StatusText = $"Pinned {(string.IsNullOrWhiteSpace(net.Label) ? net.Id : net.Label)} to the waveform";
        }

        WaveformPinsChanged();
    }

    public void ClearWaveformPins()
    {
        if (!_waveformPins.TryGetValue(Definition.Id, out List<string>? pins) || pins.Count == 0)
        {
            return;
        }

        int count = pins.Count;
        pins.Clear();
        StatusText = $"Cleared {count} waveform pin(s) for {Definition.Name}";
        WaveformPinsChanged();
    }

    public bool IsWaveformPinned(string netId) =>
        _waveformPins.TryGetValue(Definition.Id, out List<string>? pins) && pins.Contains(netId);

    /// <summary>
    /// Pinned nets that still exist in the current definition, in pin order. Stale IDs
    /// are dropped so removing a net cannot leave a phantom waveform row.
    /// </summary>
    private List<string> PinnedWaveformNetIds()
    {
        if (!_waveformPins.TryGetValue(Definition.Id, out List<string>? pins))
        {
            pins = [];
            _waveformPins[Definition.Id] = pins;
        }

        pins.RemoveAll(id => Definition.FindNet(id) is null);
        return pins;
    }

    private void WaveformPinsChanged()
    {
        RaisePropertyChanged(nameof(SelectedNetOnWaveform));
        RaisePropertyChanged(nameof(WaveformPinButtonText));
        RaisePropertyChanged(nameof(WaveformPinCount));
        RaisePropertyChanged(nameof(HasWaveformPins));
        RaisePropertyChanged(nameof(WaveformPinSummary));
        RefreshNetList();
        RefreshWaveform();
        VisualInvalidated?.Invoke();
    }

    public void ChangeNetDomain(string domainId)
    {
        if (_selectedNet is null || _project.FindDomain(domainId) is null ||
            string.Equals(_selectedNet.DomainId, domainId, StringComparison.Ordinal))
        {
            return;
        }

        PushUndo();
        _selectedNet.DomainId = domainId;
        AfterMutation(structural: false);
    }

    public void SetNetWireColor(string netId, string? color)
    {
        Net? net = Definition.FindNet(netId);
        if (net is null)
        {
            return;
        }

        if (!WireAnnotation.TryNormalizeColor(color, out string? normalized))
        {
            StatusText = "Wire color must be #RRGGBB or auto.";
            return;
        }

        PushUndo();
        net.WireColor = normalized;
        AfterMutation(structural: false, invalidateSimulation: false);
        StatusText = normalized is null ? "Wire color set to automatic" : $"Wire color set to {normalized}";
    }

    public void AddNetWaypoint(string netId, Point point)
    {
        Net? net = Definition.FindNet(netId);
        if (net is null)
        {
            return;
        }

        if (net.RoutePoints.Count >= WireAnnotation.MaxRoutePoints)
        {
            StatusText = $"A wire can have at most {WireAnnotation.MaxRoutePoints} bendpoints.";
            return;
        }

        PushUndo();
        net.RoutePoints.Add(new NetWaypoint(Math.Round(point.X, 1), Math.Round(point.Y, 1)));
        SelectNet(net.Id);
        AfterMutation(structural: false, invalidateSimulation: false);
        StatusText = "Added wire bendpoint";
    }

    public void MoveNetWaypoint(string netId, int index, Point point)
    {
        Net? net = Definition.FindNet(netId);
        if (net is null || index < 0 || index >= net.RoutePoints.Count)
        {
            return;
        }

        if (_pendingDragUndo)
        {
            PushUndo();
            _pendingDragUndo = false;
        }

        net.RoutePoints[index] = new NetWaypoint(Math.Round(point.X, 1), Math.Round(point.Y, 1));
        IsDirty = true;
        RebuildLayout();
        VisualInvalidated?.Invoke();
    }

    public void RemoveNetWaypoint(string netId, int index)
    {
        Net? net = Definition.FindNet(netId);
        if (net is null || index < 0 || index >= net.RoutePoints.Count)
        {
            return;
        }

        PushUndo();
        net.RoutePoints.RemoveAt(index);
        AfterMutation(structural: false, invalidateSimulation: false);
        StatusText = "Removed wire bendpoint";
    }

    public void ClearNetRoute(string netId)
    {
        Net? net = Definition.FindNet(netId);
        if (net is null || net.RoutePoints.Count == 0)
        {
            return;
        }

        PushUndo();
        net.RoutePoints.Clear();
        AfterMutation(structural: false, invalidateSimulation: false);
        StatusText = "Wire route reset to automatic";
    }

    public void ClearSelectedNetRoute()
    {
        if (_selectedNet is not null)
        {
            ClearNetRoute(_selectedNet.Id);
        }
    }

    public void PlaceLibraryComponent(LibraryComponent component)
    {
        PushUndo();
        string id = UniquePlacementId(component.Name);
        var instance = new ComponentInstance
        {
            Id = id,
            Primitive = component.Spec,
            Label = component.Name,
            X = NextPlacementX(),
            Y = NextPlacementY(),
        };
        Definition.Instances.Add(instance);
        _selectedInstance = instance;
        _selectedInstanceIds.Clear();
        _selectedInstanceIds.Add(instance.Id);
        _selectedNet = null;
        AfterMutation(structural: true);
        FormatPlacement();
        StatusText = $"Placed {component.Name}";
    }

    public void PlaceDefinition(string definitionId)
    {
        CircuitDefinition? target = _project.FindDefinition(definitionId);
        if (target is null)
        {
            return;
        }

        if (HierarchyEditor.WouldCreateCycle(_project, Definition.Id, target.Id))
        {
            StatusText = $"Placing '{target.Name}' here would create a recursive hierarchy.";
            return;
        }

        PushUndo();
        string id = UniquePlacementId(target.Name);
        var instance = new ComponentInstance
        {
            Id = id,
            DefinitionId = target.Id,
            Label = target.Name,
            X = NextPlacementX(),
            Y = NextPlacementY(),
        };
        Definition.Instances.Add(instance);
        _selectedInstance = instance;
        _selectedInstanceIds.Clear();
        _selectedInstanceIds.Add(instance.Id);
        _selectedNet = null;
        AfterMutation(structural: true);
        FormatPlacement();
        StatusText = $"Placed {target.Name}";
    }

    /// <summary>
    /// Copies the selected parts plus every unbound net whose pins are all inside
    /// the selection. Wires to the outside are intentionally not copied, so a paste
    /// cannot silently join unrelated circuitry.
    /// </summary>
    public bool CopySelection()
    {
        ClipboardPayload? payload = CaptureSelection();
        if (payload is null)
        {
            StatusText = "Select one or more parts to copy.";
            return false;
        }

        _clipboard = payload;
        _pasteOffset = 0;
        _pasteCommand.RaiseCanExecuteChanged();
        StatusText = string.Create(
            CultureInfo.InvariantCulture,
            $"Copied {payload.Instances.Count} parts and {payload.Nets.Count} internal nets");
        return true;
    }

    public void Paste()
    {
        if (_clipboard is null)
        {
            StatusText = "Clipboard is empty.";
            return;
        }

        PastePayload(_clipboard, "Pasted");
    }

    /// <summary>
    /// Replaces the selected parts with one instance of a newly created definition.
    /// The undo snapshot is captured before the structural service runs, but only
    /// pushed after it succeeds, so a refused extraction does not disturb redo.
    /// </summary>
    public void ExtractSelectionToComponent()
    {
        if (_selectedInstanceIds.Count == 0)
        {
            StatusText = "Select one or more parts to extract.";
            return;
        }

        string[] ids = _selectedInstanceIds.ToArray();
        EditorSnapshot before = Capture();
        HierarchyExtractResult result = HierarchyEditor.ExtractSelection(
            _project,
            Definition,
            ids,
            NewComponentName);
        if (!result.Success || result.Instance is null)
        {
            StatusText = result.Message;
            return;
        }

        PushUndo(before);
        AfterMutation(structural: true);
        SelectInstance(result.Instance.Id);
        NewComponentName = "Component";
        StatusText = result.Message;
    }

    public void PromoteSelectedNet(PinDirection direction)
    {
        Net? net = _selectedNet;
        if (net is null || !CanPromoteSelectedNet)
        {
            StatusText = "Select an unbound net with at least one pin to expose it.";
            return;
        }

        PushUndo();
        PinDeclaration? pin = HierarchyEditor.PromoteNet(
            _project,
            Definition,
            net,
            direction,
            PromoteNetName);
        if (pin is null)
        {
            Undo();
            StatusText = "That net is already bound to an interface pin.";
            return;
        }

        AfterMutation(structural: true);
        SelectNet(net.Id);
        PromoteNetName = string.Empty;
        StatusText = $"Exposed '{net.Label ?? net.Id}' as {(pin.Direction == PinDirection.Input ? "input" : "output")} pin '{pin.Name}'";
    }

    public void DuplicateSelection()
    {
        ClipboardPayload? payload = CaptureSelection();
        if (payload is null)
        {
            StatusText = "Select one or more parts to duplicate.";
            return;
        }

        PastePayload(payload, "Duplicated");
    }

    private ClipboardPayload? CaptureSelection()
    {
        if (_selectedInstanceIds.Count == 0)
        {
            return null;
        }

        var instances = Definition.Instances
            .Where(instance => _selectedInstanceIds.Contains(instance.Id))
            .Select(CloneInstance)
            .ToList();

        HashSet<string> boundNets = Definition.Ports
            .Select(port => port.NetId)
            .ToHashSet(StringComparer.Ordinal);
        var nets = Definition.Nets
            .Where(net =>
                !boundNets.Contains(net.Id) &&
                net.Pins.Count > 0 &&
                net.Pins.All(pin => _selectedInstanceIds.Contains(pin.InstanceId)))
            .Select(CloneNet)
            .ToList();

        return new ClipboardPayload(instances, nets);
    }

    private void PastePayload(ClipboardPayload payload, string verb)
    {
        List<ComponentInstance> instances = payload.Instances
            .Where(instance => instance.DefinitionId is null || _project.FindDefinition(instance.DefinitionId) is not null)
            .ToList();
        if (instances.Count == 0)
        {
            StatusText = "Copied parts refer to definitions that are not in this project.";
            return;
        }

        PushUndo();
        double offset = 32 + (_pasteOffset * 24);
        _pasteOffset++;
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var pastedIds = new List<string>(instances.Count);
        foreach (ComponentInstance source in instances)
        {
            string id = UniquePlacementId(source.Label ?? source.DefinitionId ?? "part");
            idMap[source.Id] = id;
            pastedIds.Add(id);

            ComponentInstance clone = CloneInstance(source);
            clone.Id = id;
            clone.X += offset;
            clone.Y += offset;
            Definition.Instances.Add(clone);
        }

        int pastedNets = 0;
        foreach (Net source in payload.Nets)
        {
            if (_project.FindDomain(source.DomainId) is null)
            {
                continue;
            }

            var pins = source.Pins
                .Where(pin => idMap.ContainsKey(pin.InstanceId))
                .Select(pin => new PinRef(idMap[pin.InstanceId], pin.PinId))
                .ToList();
            if (pins.Count == 0)
            {
                continue;
            }

            Net net = CloneNet(source);
            net.Id = DefinitionEditor.UniqueNetId(Definition, Slug(source.Id));
            net.Pins.Clear();
            net.Pins.AddRange(pins);
            net.RoutePoints.Clear();
            foreach (NetWaypoint point in source.RoutePoints)
            {
                net.RoutePoints.Add(new NetWaypoint(point.X + offset, point.Y + offset));
            }

            Definition.Nets.Add(net);
            pastedNets++;
        }

        AfterMutation(structural: true);
        SelectInstances(pastedIds);
        StatusText = string.Create(
            CultureInfo.InvariantCulture,
            $"{verb} {pastedIds.Count} parts and {pastedNets} internal nets");
    }

    private static ComponentInstance CloneInstance(ComponentInstance source) => new()
    {
        Id = source.Id,
        DefinitionId = source.DefinitionId,
        Primitive = source.Primitive,
        Label = source.Label,
        X = source.X,
        Y = source.Y,
    };

    private static Net CloneNet(Net source)
    {
        var net = new Net
        {
            Id = source.Id,
            DomainId = source.DomainId,
            Label = source.Label,
            InitialValue = source.InitialValue,
            WireColor = source.WireColor,
        };
        net.RoutePoints.AddRange(source.RoutePoints);
        net.Pins.AddRange(source.Pins);
        return net;
    }

    public void Connect(EditorPin start, EditorPin end)
    {
        if (start == end)
        {
            StatusText = "A pin cannot connect to itself.";
            return;
        }

        Net? first = Simulator.NetFor(start);
        Net? second = Simulator.NetFor(end);
        string? firstDomain = DeclaredDomain(start);
        string? secondDomain = DeclaredDomain(end);
        if (firstDomain is not null && secondDomain is not null &&
            !string.Equals(firstDomain, secondDomain, StringComparison.Ordinal))
        {
            StatusText = $"Domain mismatch: {firstDomain} and {secondDomain}.";
            return;
        }

        string? domainId = firstDomain ?? secondDomain ?? first?.DomainId ?? second?.DomainId;
        if (domainId is null)
        {
            domainId = DefaultDomainId();
        }

        if (domainId is null)
        {
            StatusText = "No signal domain is available.";
            return;
        }

        if (first is not null && second is not null)
        {
            if (!string.Equals(first.DomainId, second.DomainId, StringComparison.Ordinal))
            {
                StatusText = $"Cannot merge nets on {first.DomainId} and {second.DomainId}.";
                return;
            }

            PushUndo();
            MergeNets(first, second);
            AttachInstancesToNet(start, first);
            AttachInstancesToNet(end, first);
            AfterMutation(structural: true);
            StatusText = $"Connected {Describe(start)} to {Describe(end)}";
            return;
        }

        PushUndo();
        Net target = first ?? second ?? new Net
        {
            Id = DefinitionEditor.UniqueNetId(Definition),
            DomainId = domainId,
        };

        if (!Definition.Nets.Contains(target))
        {
            Definition.Nets.Add(target);
        }

        AttachToNet(start, target);
        AttachToNet(end, target);
        AfterMutation(structural: true);
        StatusText = $"Wired {Describe(start)} to {Describe(end)}";
    }

    public void AttachPinToNet(EditorPin pin, string netId)
    {
        Net? net = Definition.FindNet(netId);
        if (net is null)
        {
            return;
        }

        string? declared = DeclaredDomain(pin);
        if (declared is not null && !string.Equals(declared, net.DomainId, StringComparison.Ordinal))
        {
            StatusText = $"Pin domain {declared} does not match net domain {net.DomainId}.";
            return;
        }

        PushUndo();
        AttachToNet(pin, net);
        AfterMutation(structural: true);
        StatusText = $"Attached {Describe(pin)} to {net.Id}";
    }

    public void Disconnect(EditorPin pin)
    {
        Net? net = Simulator.NetFor(pin);
        if (net is null)
        {
            return;
        }

        PushUndo();
        if (pin.Kind == EditorPinKind.Port)
        {
            Definition.Ports.RemoveAll(p => string.Equals(p.PinId, pin.PinId, StringComparison.Ordinal));
        }
        else
        {
            net.Pins.RemoveAll(p =>
                string.Equals(p.InstanceId, pin.OwnerId, StringComparison.Ordinal) &&
                string.Equals(p.PinId, pin.PinId, StringComparison.Ordinal));
        }

        CleanEmptyNets();
        AfterMutation(structural: true);
        StatusText = $"Disconnected {Describe(pin)}";
    }

    public bool ApplyField(EditableField field)
    {
        if (field.Apply is null || string.Equals(field.Value, field.LastCommitted, StringComparison.Ordinal))
        {
            return false;
        }

        bool applied = field.Apply(field.Value);
        if (applied)
        {
            field.LastCommitted = field.Value;
        }

        if (!applied)
        {
            StatusText = $"Could not apply '{field.Label}'.";
        }

        return applied;
    }

    public void BeginSimulation()
    {
        if (!EnsureSimulation())
        {
            return;
        }

        Simulator.Reset();
        RefreshSimulationPresentation();
        StatusText = $"Simulation reset to t=0 · {Simulator.EventsProcessed} events";
    }

    public void AdvanceSimulation()
    {
        if (!EnsureSimulation())
        {
            return;
        }

        if (!int.TryParse(AdvanceTicksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ticks) || ticks < 0)
        {
            ticks = 10;
            AdvanceTicksText = "10";
        }

        Simulator.Advance(ticks);
        RefreshSimulationPresentation();
        StatusText = $"Advanced {ticks} ticks";
    }

    public void StepEvent()
    {
        if (!EnsureSimulation())
        {
            return;
        }

        Simulator.StepEvent();
        RefreshSimulationPresentation();
        StatusText = "Stepped one event";
    }

    public void Settle()
    {
        if (!EnsureSimulation())
        {
            return;
        }

        Simulator.SettleCurrentTime();
        RefreshSimulationPresentation();
        StatusText = "Settled current timestamp";
    }

    public void StepClock()
    {
        if (!EnsureSimulation())
        {
            return;
        }

        string? clockNet = FindClockNet();
        if (clockNet is null)
        {
            Simulator.StepEvent();
            StatusText = "No clock net found; stepped one event";
        }
        else
        {
            Simulator.StepClock(clockNet);
            StatusText = $"Stepped clock {clockNet}";
        }

        RefreshSimulationPresentation();
    }

    public void SetPortValue(string pinId, int value)
    {
        if (!EnsureSimulation())
        {
            return;
        }

        if (Simulator.DrivePort(pinId, value))
        {
            RefreshSimulationPresentation();
            StatusText = $"Drove {pinId} = {SimulatorHost.Format(value, Simulator.DomainFor(EditorPin.ForPort(pinId)))}";
        }
    }

    public void ClearDrives()
    {
        if (!EnsureSimulation())
        {
            return;
        }

        Simulator.ClearDrives();
        RefreshSimulationPresentation();
        StatusText = "Cleared external drives";
    }

    /// <summary>
    /// Rebuilds presentation that cannot use DynamicResource directly (diagnostic
    /// rows and waveform colours) after a theme change.
    /// </summary>
    public void RefreshTheme()
    {
        RefreshInspector();
        if (Simulator.Engine is null)
        {
            RefreshDiagnosticsFromValidator();
        }
        else
        {
            RefreshDiagnosticsFromSimulator();
        }

        RefreshWaveform();
        RefreshRecentTransitions();
        VisualInvalidated?.Invoke();
    }

    public void RunEvent(string description)
    {
        if (!EnsureSimulation())
        {
            return;
        }

        Simulator.RunEventDescription(description);
        RefreshSimulationPresentation();
        StatusText = description;
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(Capture());
        EditorSnapshot snapshot = _undo.Pop();
        Restore(snapshot);
        UpdateCommandStates();
        StatusText = "Undo";
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(Capture());
        EditorSnapshot snapshot = _redo.Pop();
        Restore(snapshot);
        UpdateCommandStates();
        StatusText = "Redo";
    }

    public void PushUndo() => PushUndo(Capture());

    private void PushUndo(EditorSnapshot snapshot)
    {
        _undo.Push(snapshot);
        while (_undo.Count > 80)
        {
            EditorSnapshot[] items = _undo.ToArray();
            _undo.Clear();
            foreach (EditorSnapshot item in items.Take(80).Reverse())
            {
                _undo.Push(item);
            }
        }

        _redo.Clear();
        UpdateCommandStates();
    }

    private void EnsureProjectShape()
    {
        if (_project.Definitions.Count == 0)
        {
            var root = new CircuitDefinition { Id = "main", Name = "Main" };
            _project.Definitions.Add(root);
            _project.RootDefinitionId = root.Id;
        }

        if (_project.FindDefinition(_project.RootDefinitionId) is null)
        {
            _project.RootDefinitionId = _project.Definitions[0].Id;
        }

        if (_project.Domains.Count == 0)
        {
            _project.Domains.Add(StandardDomains.Binary());
        }
    }

    private void RefreshAll(bool fit)
    {
        EnsureProjectShape();
        if (_project.FindDefinition(_definitionId) is null)
        {
            _definitionId = _project.RootDefinitionId;
        }

        _project.View.ActiveDefinitionId = _definitionId;
        Simulator.Retarget(_project, Definition);
        RefreshDefinitions();
        RefreshPalette();
        RefreshDomains();
        RefreshCustomNumericFormats();
        RefreshFormatChoices();
        RefreshPins();
        RefreshIoPins();
        RefreshNumericLanes();
        RefreshNetList();
        RebuildLayout();
        RefreshInspector();
        RefreshDiagnosticsFromValidator();
        RefreshWaveform();
        RefreshRecentTransitions();
        RefreshSimStatus();
        RefreshEvents();
        UpdateCommandStates();
        RaisePropertyChanged(nameof(DefinitionTitle));
        RaisePropertyChanged(nameof(ProjectSubtitle));
        RaisePropertyChanged(nameof(ProjectTitle));
        VisualInvalidated?.Invoke();
        if (fit)
        {
            FitRequested?.Invoke();
        }
    }

    private void RefreshDefinitions()
    {
        Definitions.Clear();
        string query = _librarySearch.Trim();
        foreach (CircuitDefinition definition in _project.Definitions)
        {
            if (query.Length > 0 &&
                !definition.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !definition.Id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !(definition.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            string id = definition.Id;
            Definitions.Add(new DefinitionListItem(
                definition,
                definition.Id == _project.RootDefinitionId,
                definition.Id == Definition.Id,
                new RelayCommand(() => OpenDefinition(id)),
                new RelayCommand(() => PlaceDefinition(id)),
                new RelayCommand(() => DuplicateDefinitionOf(id))));
        }
    }

    private void RefreshPalette()
    {
        Palette.Clear();
        string query = _librarySearch.Trim();
        foreach (LibraryComponent component in ComponentCatalog.Components)
        {
            if (query.Length > 0 &&
                !component.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !component.Category.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !component.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Palette.Add(new PaletteItem(
                component.Name,
                component.Category,
                component.Description,
                () => PlaceLibraryComponent(component)));
        }

        RaisePropertyChanged(nameof(HasPaletteResults));
    }

    private void RefreshDomains()
    {
        Domains.Clear();
        foreach (SignalDomain domain in _project.Domains)
        {
            bool standard = StandardDomains.All().Any(d => string.Equals(d.Id, domain.Id, StringComparison.Ordinal));
            string summary = $"{string.Join(" ", domain.ValueNames)} · {domain.Drivers.Describe()}";
            var choice = new DomainChoice(
                domain.Id,
                domain.Name,
                standard,
                summary,
                new RelayCommand(() => BeginEditDomainPolicy(domain.Id)));
            Domains.Add(choice);
        }

        if (_editingDomainId is not null && _project.FindDomain(_editingDomainId) is null)
        {
            _editingDomainId = null;
            ResetDomainForm();
            RaiseDomainEditChanged();
        }

        NewPinDomain = Domains.FirstOrDefault(d => d.Id == NewPinDomain?.Id) ?? Domains.FirstOrDefault();
        NewFormatDomain = Domains.FirstOrDefault(d => d.Id == NewFormatDomain?.Id) ?? Domains.FirstOrDefault();
        if (_selectedNet is not null)
        {
            _refreshingDomainSelection = true;
            SelectedNetDomain = Domains.FirstOrDefault(d => d.Id == _selectedNet.DomainId);
            _refreshingDomainSelection = false;
        }
    }

    private void RefreshPins()
    {
        Pins.Clear();
        for (int i = 0; i < Definition.Pins.Count; i++)
        {
            PinDeclaration pin = Definition.Pins[i];
            string pinId = pin.Id;
            bool canMoveUp = i > 0;
            bool canMoveDown = i < Definition.Pins.Count - 1;
            Net? net = Definition.FindPortNet(pinId);
            Pins.Add(new PinListItem(
                pin,
                net?.Label ?? net?.Id ?? "unbound",
                Domains,
                new RelayCommand(() => MovePin(pinId, -1), () => canMoveUp),
                new RelayCommand(() => MovePin(pinId, 1), () => canMoveDown),
                new RelayCommand(() => RemovePin(pinId))));
        }
    }

    private void RefreshIoPins()
    {
        IoPins.Clear();
        foreach (PinDeclaration pin in Definition.Pins)
        {
            SignalDomain? domain = _project.FindDomain(pin.DomainId);
            if (domain is null)
            {
                continue;
            }

            var view = new IoPinViewModel(pin.Id, pin.Name, pin.Direction, domain, pin.Direction == PinDirection.Input);
            if (pin.Direction == PinDirection.Input)
            {
                for (int i = 0; i < domain.ValueCount; i++)
                {
                    int value = i;
                    view.Values.Add(new IoValueOption(domain.ValueName(i), i, chosen => SetPortValue(pin.Id, chosen)));
                }
            }

            IoPins.Add(view);
        }

        RefreshIoValues();
    }

    private void RefreshIoValues()
    {
        foreach (IoPinViewModel view in IoPins)
        {
            view.ValueText = Simulator.Format(EditorPin.ForPort(view.PinId));
        }

        RefreshNumericLaneValues();
        RefreshFormatPreview();
    }

    private void RefreshFormatChoices()
    {
        string? selectedId = _selectedLaneFormat?.Format.Id;
        FormatChoices.Clear();
        foreach (INumericFormat format in NumericFormatCatalog.All(_project))
        {
            FormatChoices.Add(new NumericDisplayOption(format));
        }

        SelectedLaneFormat = FormatChoices.FirstOrDefault(option =>
            string.Equals(option.Format.Id, selectedId, StringComparison.Ordinal)) ??
            FormatChoices.FirstOrDefault(option => option.Format.Id == NumericFormats.UnsignedBinary.Id) ??
            FormatChoices.FirstOrDefault();
    }

    private void RefreshCustomNumericFormats()
    {
        CustomNumericFormats.Clear();
        foreach (NumericFormatDefinition format in _project.CustomNumericFormats)
        {
            string formatId = format.Id;
            string weighting = format.IsSignMagnitude
                ? $"sign-magnitude, negative={format.NegativeSignValue}"
                : format.Weighting switch
                {
                    WeightingMode.RadixComplement => "radix complement",
                    WeightingMode.DiminishedRadixComplement => "diminished complement",
                    _ => "standard",
                };
            string summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{format.DomainId} · radix {format.Radix} · digits [{string.Join(",", format.DigitValues)}] · {weighting}");
            CustomNumericFormats.Add(new CustomNumericFormatListItem(
                format,
                summary,
                new RelayCommand(() => BeginEditCustomNumericFormat(formatId)),
                new RelayCommand(() => RemoveCustomNumericFormat(formatId))));
        }

        if (_editingFormatId is string editingId &&
            _project.FindCustomNumericFormat(editingId) is null)
        {
            _editingFormatId = null;
            ResetFormatForm();
            RaisePropertyChanged(nameof(IsEditingFormat));
            RaisePropertyChanged(nameof(FormatSaveButtonText));
        }

        RaisePropertyChanged(nameof(HasCustomNumericFormats));
        RefreshFormatPreview();
    }

    private const int MaxNetListRows = 200;

    /// <summary>
    /// Projects Definition.Nets into a filtered, read-only navigator. Selection only
    /// changes the editor's current net; connectivity and simulation are untouched.
    /// </summary>
    private void RefreshNetList()
    {
        NetList.Clear();
        string query = _netSearch.Trim();
        int matched = 0;
        foreach (Net net in Definition.Nets)
        {
            SignalDomain? domain = _project.FindDomain(net.DomainId);
            string name = string.IsNullOrWhiteSpace(net.Label) ? net.Id : net.Label;
            string summary = string.IsNullOrWhiteSpace(net.Label)
                ? $"{domain?.Name ?? net.DomainId} · {net.Pins.Count} pin(s)"
                : $"{net.Id} · {domain?.Name ?? net.DomainId} · {net.Pins.Count} pin(s)";
            if (IsWaveformPinned(net.Id))
            {
                summary += " · waveform";
            }
            if (query.Length > 0 &&
                !name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !net.Id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !net.DomainId.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !(domain?.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            matched++;
            if (NetList.Count >= MaxNetListRows)
            {
                continue;
            }

            string netId = net.Id;
            NetList.Add(new NetListItem(
                net,
                name,
                summary,
                new RelayCommand(() => SelectNet(netId))));
        }

        _netListSummary = query.Length == 0
            ? (matched > MaxNetListRows
                ? $"Showing first {MaxNetListRows} of {matched} nets"
                : $"{matched} net(s)")
            : $"{NetList.Count} of {matched} matching net(s)";
        RaisePropertyChanged(nameof(NetListSummary));
        RaisePropertyChanged(nameof(HasNetList));
        RefreshNetListValues();
    }

    private void RefreshNetListValues()
    {
        foreach (NetListItem item in NetList)
        {
            if (Simulator.Circuit is null || Simulator.Engine is null)
            {
                item.ValueText = "—";
                item.ValueHint = "Start or advance the simulation to read this net.";
                continue;
            }

            if (!Simulator.TryNetIndex(item.Id, out int netIndex, out SignalDomain? domain) || domain is null)
            {
                item.ValueText = "?";
                item.ValueHint = "This net is not part of the compiled circuit.";
                continue;
            }

            int value = Simulator.Engine.GetNetValue(netIndex);
            item.ValueText = value < 0 ? "z" : domain.ValueName(value);
            item.ValueHint = value < 0
                ? $"Net '{item.Name}' · domain {domain.Name} · undriven"
                : $"Net '{item.Name}' · domain {domain.Name} · value {domain.ValueName(value)}";
        }
    }

    private void RefreshNumericLanes()
    {
        NumericLanes.Clear();
        foreach (NumericLane lane in Definition.NumericLanes)
        {
            string laneId = lane.Id;
            INumericFormat? format = NumericFormatCatalog.Find(_project, lane.FormatId);
            string pins = string.Join(
                " · ",
                lane.PinIds.Select(pinId => Definition.FindPin(pinId)?.Name ?? $"{pinId} (missing)"));
            bool canDrive = lane.PinIds.Count > 0 && lane.PinIds.All(pinId =>
                Definition.FindPin(pinId)?.Direction == PinDirection.Input);
            NumericLanes.Add(new NumericLaneListItem(
                lane,
                format?.Name ?? $"{lane.FormatId} (unknown)",
                pins.Length == 0 ? "no pins" : pins,
                canDrive,
                new RelayCommand(() => BeginEditNumericLane(laneId)),
                new RelayCommand(() => RemoveNumericLane(laneId)),
                new RelayCommand(() => DriveNumericLane(laneId))));
        }

        LanePinChoices.Clear();
        NumericLane? editingLane = _editingLaneId is string editingId
            ? Definition.FindNumericLane(editingId)
            : null;
        var selectedPins = editingLane is null
            ? null
            : new HashSet<string>(editingLane.PinIds, StringComparer.Ordinal);
        foreach (PinDeclaration pin in Definition.Pins)
        {
            LanePinChoices.Add(new LanePinChoice(pin)
            {
                IsSelected = selectedPins?.Contains(pin.Id) ?? false,
            });
        }

        if (_editingLaneId is not null && editingLane is null)
        {
            _editingLaneId = null;
            ResetLaneForm();
            RaisePropertyChanged(nameof(IsEditingLane));
            RaisePropertyChanged(nameof(LaneSaveButtonText));
        }

        RaisePropertyChanged(nameof(HasNumericLanes));
        RefreshNumericLaneValues();
    }

    /// <summary>
    /// Encodes a typed number with the lane's format and applies every resulting
    /// digit as an external drive. All validation happens before any drive is applied.
    /// </summary>
    public void DriveNumericLane(string laneId)
    {
        NumericLane? lane = Definition.FindNumericLane(laneId);
        NumericLaneListItem? view = NumericLanes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, laneId, StringComparison.Ordinal));
        if (lane is null || view is null)
        {
            return;
        }

        string text = view.DriveText.Trim();
        if (!BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger value))
        {
            StatusText = $"Enter a whole number to drive lane '{lane.Name}'.";
            return;
        }

        INumericFormat? format = NumericFormatCatalog.Find(_project, lane.FormatId);
        if (format is null)
        {
            StatusText = $"Lane '{lane.Name}' uses unknown format '{lane.FormatId}'.";
            return;
        }

        int[] signals = new int[lane.PinIds.Count];
        if (!format.TryEncode(value, signals, out string? encodeError))
        {
            StatusText = encodeError ?? $"Value {value} cannot be encoded for lane '{lane.Name}'.";
            return;
        }

        if (!format.LeastSignificantFirst)
        {
            Array.Reverse(signals);
        }

        var drives = new List<(string PinId, int Value)>(signals.Length);
        string? domainId = null;
        for (int i = 0; i < lane.PinIds.Count; i++)
        {
            PinDeclaration? pin = Definition.FindPin(lane.PinIds[i]);
            if (pin is null || pin.Direction != PinDirection.Input)
            {
                StatusText = $"Lane '{lane.Name}' can only be driven when every pin is an input.";
                return;
            }

            if (domainId is null)
            {
                domainId = pin.DomainId;
            }
            else if (!string.Equals(domainId, pin.DomainId, StringComparison.Ordinal))
            {
                StatusText = $"Lane '{lane.Name}' contains pins from more than one domain.";
                return;
            }

            SignalDomain? domain = _project.FindDomain(pin.DomainId);
            if (domain is null || !domain.IsValidValue(signals[i]))
            {
                StatusText = $"Value {value} needs a digit outside domain '{pin.DomainId}' for lane '{lane.Name}'.";
                return;
            }

            drives.Add((pin.Id, signals[i]));
        }

        if (!EnsureSimulation())
        {
            return;
        }

        if (!Simulator.DrivePorts(drives))
        {
            StatusText = $"Could not drive lane '{lane.Name}' on the compiled circuit.";
            return;
        }

        RefreshSimulationPresentation();
        StatusText = $"Drove lane '{lane.Name}' with {value.ToString(CultureInfo.InvariantCulture)}";
    }

    private void RefreshNumericLaneValues()
    {
        foreach (NumericLaneListItem view in NumericLanes)
        {
            if (Definition.FindNumericLane(view.Id) is not NumericLane lane)
            {
                view.ValueText = "—";
                view.ValueHint = "This lane no longer exists.";
                continue;
            }

            (string text, string hint) = DecodeNumericLane(lane, view.FormatId);
            view.ValueText = text;
            view.ValueHint = hint;
        }
    }

    private (string Text, string Hint) DecodeNumericLane(NumericLane lane, string formatId)
    {
        if (Simulator.Circuit is null || Simulator.Engine is null)
        {
            return ("—", "Start or advance the simulation to decode this lane.");
        }

        INumericFormat? format = NumericFormatCatalog.Find(_project, formatId);
        if (format is null)
        {
            return ("invalid", $"Unknown numeric format '{formatId}'.");
        }

        if (lane.PinIds.Count == 0)
        {
            return ("invalid", "This lane has no interface pins.");
        }

        int[] values = new int[lane.PinIds.Count];
        for (int i = 0; i < lane.PinIds.Count; i++)
        {
            string pinId = lane.PinIds[i];
            if (!Simulator.TryRead(EditorPin.ForPort(pinId), out int value, out _))
            {
                return ("missing", $"Pin '{pinId}' is not available in the compiled circuit.");
            }

            values[i] = value;
        }

        // Lanes are declared least-significant first by contract. Formats that read
        // most-significant first need the sequence reversed before decoding.
        if (!format.LeastSignificantFirst)
        {
            Array.Reverse(values);
        }

        NumericDecodeResult result = format.Decode(values);
        return result.IsValid
            ? (result.Value.ToString(CultureInfo.InvariantCulture), format.Describe(values.Length))
            : ("invalid", result.Error ?? "The pin values cannot be decoded.");
    }

    private void RefreshEvents()
    {
        Events.Clear();
        Events.Add("begin simulation");
        Events.Add("advance");
        Events.Add("settle");
        foreach (PinDeclaration pin in Definition.Pins.Where(p => p.Direction == PinDirection.Input))
        {
            SignalDomain? domain = _project.FindDomain(pin.DomainId);
            if (domain is null)
            {
                continue;
            }

            for (int i = 0; i < domain.ValueCount; i++)
            {
                string text = $"append {pin.Name}={domain.ValueName(i)} @ t={Simulator.Now}";
                Events.Add(text);
            }
        }
    }

    private void RefreshInspector()
    {
        Fields.Clear();

        if (_selectedInstanceIds.Count > 1)
        {
            Fields.Add(new EditableField(
                "Selection",
                string.Create(CultureInfo.InvariantCulture, $"{_selectedInstanceIds.Count} parts"),
                isReadOnly: true));
            Fields.Add(new EditableField("Instances", string.Join(", ", _selectedInstanceIds.Order(StringComparer.Ordinal)), isReadOnly: true));
            Fields.Add(new EditableField(
                "Extract",
                "Use Create from selection in the Definitions panel to turn this group into a reusable component.",
                isReadOnly: true));
        }
        else if (_selectedInstance is not null)
        {
            ComponentInstance instance = _selectedInstance;
            string instanceId = instance.Id;
            Fields.Add(new EditableField("Instance", instance.Id, isReadOnly: true));
            Fields.Add(new EditableField("Type", TypeName(instance), isReadOnly: true));
            Fields.Add(new EditableField(
                "Label",
                instance.Label ?? string.Empty,
                hint: "Display name shown on the canvas",
                apply: value => MutateInstance(instanceId, i => DefinitionEditor.SetLabel(i, value), "label")));
            Fields.Add(new EditableField(
                "X",
                instance.X.ToString("0.#", CultureInfo.InvariantCulture),
                apply: value => ParseDoubleValue(value) is double x && MutateInstance(instanceId, i => i.X = x, "position")));
            Fields.Add(new EditableField(
                "Y",
                instance.Y.ToString("0.#", CultureInfo.InvariantCulture),
                apply: value => ParseDoubleValue(value) is double y && MutateInstance(instanceId, i => i.Y = y, "position")));
            Fields.Add(new EditableField(
                "Pins",
                string.Join(", ", _project.GetInstancePins(instance).Select(p => p.Name)),
                isReadOnly: true));

            AddPrimitiveFields(instance, instanceId);
        }
        else if (_selectedNet is not null)
        {
            Net net = _selectedNet;
            string netId = net.Id;
            Fields.Add(new EditableField("Net", net.Id, isReadOnly: true));
            Fields.Add(new EditableField("Domain", net.DomainId, isReadOnly: true));
            Fields.Add(new EditableField(
                "Label",
                net.Label ?? string.Empty,
                apply: value => MutateNet(netId, n => n.Label = string.IsNullOrWhiteSpace(value) ? null : value, "label")));
            Fields.Add(new EditableField(
                "Initial",
                net.InitialValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                hint: "Empty for undriven",
                apply: value =>
                {
                    if (value.Trim().Length == 0)
                    {
                        return MutateNet(netId, n => n.InitialValue = null, "initial value");
                    }

                    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                           MutateNet(netId, n => n.InitialValue = parsed, "initial value");
                }));
            Fields.Add(new EditableField(
                "Wire color",
                net.WireColor ?? WireAnnotation.AutoColor,
                hint: "#RRGGBB, or auto for domain-derived colors",
                apply: value =>
                {
                    if (!WireAnnotation.TryNormalizeColor(value, out string? normalized))
                    {
                        return false;
                    }

                    PushUndo();
                    net.WireColor = normalized;
                    AfterMutation(structural: false, invalidateSimulation: false);
                    return true;
                }));
            Fields.Add(new EditableField(
                "Bendpoints",
                net.RoutePoints.Count == 0 ? "automatic" : net.RoutePoints.Count.ToString(CultureInfo.InvariantCulture),
                isReadOnly: true));
            Fields.Add(new EditableField(
                "Pins",
                net.Pins.Count == 0 ? "none" : string.Join(", ", net.Pins.Select(p => p.ToString())),
                isReadOnly: true));
        }
        else
        {
            string definitionId = Definition.Id;
            Fields.Add(new EditableField(
                "Project name",
                _project.Name,
                apply: value =>
                {
                    RenameProject(value);
                    return true;
                }));
            Fields.Add(new EditableField("Id", Definition.Id, isReadOnly: true));
            Fields.Add(new EditableField(
                "Name",
                Definition.Name,
                apply: value =>
                {
                    PushUndo();
                    DefinitionEditor.RenameDefinition(Definition, value);
                    AfterMutation(structural: true, invalidateSimulation: false);
                    return true;
                }));
            Fields.Add(new EditableField(
                "Description",
                Definition.Description ?? string.Empty,
                apply: value =>
                {
                    PushUndo();
                    Definition.Description = string.IsNullOrWhiteSpace(value) ? null : value;
                    AfterMutation(structural: false, invalidateSimulation: false);
                    return true;
                }));
            Fields.Add(new EditableField("Instances", Definition.Instances.Count.ToString(CultureInfo.InvariantCulture), isReadOnly: true));
            Fields.Add(new EditableField("Nets", Definition.Nets.Count.ToString(CultureInfo.InvariantCulture), isReadOnly: true));
            Fields.Add(new EditableField("Pins", Definition.Pins.Count.ToString(CultureInfo.InvariantCulture), isReadOnly: true));
            Fields.Add(new EditableField(
                "Root",
                _project.RootDefinitionId == definitionId ? "yes" : "no",
                isReadOnly: true));
        }

        _refreshingDomainSelection = true;
        SelectedNetDomain = _selectedNet is null
            ? null
            : Domains.FirstOrDefault(d => d.Id == _selectedNet.DomainId);        _refreshingDomainSelection = false;
        RaisePropertyChanged(nameof(HasSelection));
        RaisePropertyChanged(nameof(HasSelectedInstance));
        RaisePropertyChanged(nameof(HasSelectedNet));
        RaisePropertyChanged(nameof(HasMultipleSelection));
        RaisePropertyChanged(nameof(SelectionCount));
        RaisePropertyChanged(nameof(SelectedInstanceIds));
        RaisePropertyChanged(nameof(IsRootDefinition));
        RaisePropertyChanged(nameof(SelectedNetHasRoute));
        RaisePropertyChanged(nameof(SelectedNetOnWaveform));
        RaisePropertyChanged(nameof(WaveformPinButtonText));
        RaisePropertyChanged(nameof(CanPromoteSelectedNet));
        RaisePropertyChanged(nameof(SelectedInstanceId));
        RaisePropertyChanged(nameof(SelectedNetId));
        RaisePropertyChanged(nameof(SelectionTitle));
        RaisePropertyChanged(nameof(SelectionSubtitle));
    }

    private void RefreshInspectorPosition(ComponentInstance instance)
    {
        if (_selectedInstance?.Id != instance.Id)
        {
            return;
        }

        foreach (EditableField field in Fields)
        {
            if (field.Label == "X")
            {
                field.Value = instance.X.ToString("0.#", CultureInfo.InvariantCulture);
            }
            else if (field.Label == "Y")
            {
                field.Value = instance.Y.ToString("0.#", CultureInfo.InvariantCulture);
            }
        }
    }

    private void AddPrimitiveFields(ComponentInstance instance, string instanceId)
    {
        switch (instance.Primitive)
        {
            case ConstantSpec constant:
                Fields.Add(new EditableField(
                    "Value",
                    constant.Value.ToString(CultureInfo.InvariantCulture),
                    apply: value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                                     MutatePrimitive(instanceId, _ => new ConstantSpec(parsed), "constant value")));
                break;

            case LookupSpec lookup:
                Fields.Add(new EditableField(
                    "Table",
                    string.Join(' ', lookup.Table),
                    hint: "Mixed-radix truth table, first input most significant",
                    apply: value => ParseInts(value) is int[] table &&
                                     MutatePrimitive(instanceId, spec => new LookupSpec(((LookupSpec)spec).InputCount, table, ((LookupSpec)spec).DelayTicks), "lookup table")));
                Fields.Add(new EditableField(
                    "Delay",
                    lookup.DelayTicks.ToString(CultureInfo.InvariantCulture),
                    hint: "Transport delay in ticks",
                    apply: value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) &&
                                     MutatePrimitive(instanceId, spec => new LookupSpec(((LookupSpec)spec).InputCount, ((LookupSpec)spec).Table, parsed), "lookup delay")));
                break;

            case DelaySpec delay:
                Fields.Add(new EditableField(
                    "Delay",
                    delay.DelayTicks.ToString(CultureInfo.InvariantCulture),
                    apply: value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) &&
                                     MutatePrimitive(instanceId, spec => new DelaySpec(parsed, ((DelaySpec)spec).InitialValue), "delay")));
                Fields.Add(new EditableField(
                    "Initial",
                    delay.InitialValue.ToString(CultureInfo.InvariantCulture),
                    apply: value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                                     MutatePrimitive(instanceId, spec => new DelaySpec(((DelaySpec)spec).DelayTicks, parsed), "initial value")));
                break;

            case WaveformSpec waveform:
                Fields.Add(new EditableField(
                    "Values",
                    string.Join(' ', waveform.Values),
                    hint: "Value indices, one per phase",
                    apply: value => ParseInts(value) is int[] values &&
                                     MutatePrimitive(instanceId, spec => new WaveformSpec(((WaveformSpec)spec).Durations, values), "waveform values")));
                Fields.Add(new EditableField(
                    "Durations",
                    string.Join(' ', waveform.Durations),
                    hint: "Ticks per phase",
                    apply: value => ParseLongs(value) is long[] durations &&
                                     MutatePrimitive(instanceId, spec => new WaveformSpec(durations, ((WaveformSpec)spec).Values), "waveform durations")));
                break;

            case ProbeSpec probe:
                Fields.Add(new EditableField(
                    "Probe label",
                    probe.Label,
                    apply: value => MutatePrimitive(instanceId, _ => new ProbeSpec(value), "probe label")));
                break;
        }
    }

    private bool MutateInstance(string instanceId, Action<ComponentInstance> mutate, string what)
    {
        ComponentInstance? instance = Definition.FindInstance(instanceId);
        if (instance is null)
        {
            return false;
        }

        PushUndo();
        mutate(instance);
        AfterMutation(structural: false, invalidateSimulation: false);
        StatusText = $"Updated {what} on {instanceId}";
        return true;
    }

    private bool MutateNet(string netId, Action<Net> mutate, string what)
    {
        Net? net = Definition.FindNet(netId);
        if (net is null)
        {
            return false;
        }

        PushUndo();
        mutate(net);
        AfterMutation(structural: false);
        StatusText = $"Updated {what} on net {netId}";
        return true;
    }

    private bool MutatePrimitive(string instanceId, Func<PrimitiveSpec, PrimitiveSpec> update, string what)
    {
        ComponentInstance? instance = Definition.FindInstance(instanceId);
        if (instance?.Primitive is null)
        {
            return false;
        }

        try
        {
            PrimitiveSpec updated = update(instance.Primitive);
            PushUndo();
            instance.Primitive = updated;
            AfterMutation(structural: false);
            StatusText = $"Updated {what}";
            return true;
        }
        catch (ArgumentException exception)
        {
            StatusText = "Invalid parameters: " + exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Commits an edit to the UI. <paramref name="invalidateSimulation"/> is false for
    /// presentation-only changes (positions, labels, colors, routes, numeric lanes),
    /// which must not discard the running engine or its drive history.
    /// </summary>
    private void AfterMutation(bool structural, bool invalidateSimulation = true)
    {
        IsDirty = true;
        if (invalidateSimulation)
        {
            Simulator.Retarget(_project, Definition);
        }

        RebuildLayout();
        if (structural)
        {
            RefreshDefinitions();
            RefreshPalette();
            RefreshDomains();
            RefreshCustomNumericFormats();
            RefreshFormatChoices();
            RefreshPins();
            RefreshIoPins();
            RefreshNumericLanes();
            RefreshNetList();
            RefreshEvents();
        }
        else
        {
            RefreshIoValues();
        }

        RefreshInspector();
        RefreshDiagnosticsFromValidator();
        RefreshWaveform();
        RefreshRecentTransitions();
        RefreshSimStatus();
        RaisePropertyChanged(nameof(DefinitionTitle));
        RaisePropertyChanged(nameof(ProjectSubtitle));
        RaisePropertyChanged(nameof(ProjectTitle));
        UpdateCommandStates();
        VisualInvalidated?.Invoke();
    }

    private void RebuildLayout(bool reuseRoutes = false)
    {
        CircuitLayout? previous = reuseRoutes && ReferenceEquals(Layout?.Definition, Definition)
            ? Layout
            : null;
        Layout = CircuitLayout.Build(Definition, _project, previous);
        RaisePropertyChanged(nameof(ProjectSubtitle));
        VisualInvalidated?.Invoke();
    }

    private void RefreshDiagnosticsFromValidator()
    {
        DiagnosticBag bag = CircuitValidator.Validate(_project);
        Diagnostics.Clear();
        foreach (Diagnostic diagnostic in bag.Take(250))
        {
            Diagnostics.Add(CreateDiagnosticItem(diagnostic));
        }

        RaisePropertyChanged(nameof(DiagnosticSummary));
    }

    private void RefreshDiagnosticsFromSimulator()
    {
        Diagnostics.Clear();
        foreach (Diagnostic diagnostic in Simulator.AllDiagnostics().Take(250))
        {
            Diagnostics.Add(CreateDiagnosticItem(diagnostic));
        }

        RaisePropertyChanged(nameof(DiagnosticSummary));
    }

    private DiagnosticItem CreateDiagnosticItem(Diagnostic diagnostic) =>
        new(diagnostic, new RelayCommand(() => NavigateToDiagnostic(diagnostic)));

    private void RefreshWaveform()
    {
        SimulationEngineSnapshot snapshot = CaptureSimulationSnapshot();
        Waveform = snapshot.Waveform;
        RaisePropertyChanged(nameof(WaveformPinCount));
        RaisePropertyChanged(nameof(HasWaveformPins));
        RaisePropertyChanged(nameof(WaveformPinSummary));
    }

    /// <summary>
    /// Waveform rows in display order: pinned nets first (in pin order), then the rest
    /// of the definition's nets in declaration order, capped at the row limit.
    /// </summary>
    private List<Net> OrderedWaveformNets()
    {
        var ordered = new List<Net>();
        foreach (string netId in PinnedWaveformNetIds())
        {
            if (ordered.Count >= MaxWaveformRows)
            {
                break;
            }

            if (Definition.FindNet(netId) is Net net && !ordered.Contains(net))
            {
                ordered.Add(net);
            }
        }

        foreach (Net net in Definition.Nets)
        {
            if (ordered.Count >= MaxWaveformRows)
            {
                break;
            }

            if (!ordered.Contains(net))
            {
                ordered.Add(net);
            }
        }

        return ordered;
    }

    private SimulationEngineSnapshot CaptureSimulationSnapshot()
    {
        if (Simulator.Circuit is null || Simulator.Engine is null || Definition.Nets.Count == 0)
        {
            return new SimulationEngineSnapshot(WaveformSnapshot.Empty);
        }

        var rows = new List<WaveformRow>();
        var targets = new Dictionary<int, (string Label, SignalDomain Domain, int Start, List<WaveValueChange> Changes)>();
        foreach (Net net in OrderedWaveformNets())
        {
            if (!Simulator.TryNetIndex(net.Id, out int netIndex, out SignalDomain? domain) || domain is null ||
                targets.ContainsKey(netIndex))
            {
                continue;
            }

            targets[netIndex] = (
                Simulator.NetLabel(netIndex),
                domain,
                Simulator.Circuit.Nets[netIndex].InitialValue,
                new List<WaveValueChange>());
        }

        // One pass over the trace, not one pass per visible net.
        foreach (NetChange change in Simulator.Engine.Trace)
        {
            if (targets.TryGetValue(change.Net, out var target))
            {
                target.Changes.Add(new WaveValueChange(change.Time, change.NewValue));
            }
        }

        foreach ((string label, SignalDomain domain, int start, List<WaveValueChange> changes) in targets.Values)
        {
            rows.Add(new WaveformRow(label, domain, start, changes));
        }

        long end = Math.Max(1, Simulator.Engine.Now);
        return new SimulationEngineSnapshot(new WaveformSnapshot(0, end, rows));
    }

    private void RefreshRecentTransitions()
    {
        RecentTransitions.Clear();
        if (Simulator.Circuit is null || Simulator.Engine is null)
        {
            return;
        }

        SimulationTrace trace = Simulator.Engine.Trace;
        int start = Math.Max(0, trace.Count - 60);
        for (int i = trace.Count - 1; i >= start; i--)
        {
            NetChange change = trace[i];
            string label = Simulator.NetLabel(change.Net);
            SignalDomain domain = Simulator.Circuit.Nets[change.Net].Domain;
            string value = SimulatorHost.Format(change.NewValue, domain);
            RecentTransitions.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"t={change.Time}  {label} → {value}"));
        }
    }

    private void RefreshSimStatus()
    {
        RaisePropertyChanged(nameof(SimTimeText));
        RaisePropertyChanged(nameof(SimStatusText));
        RefreshIoValues();
    }

    private void RefreshSimulationPresentation()
    {
        RefreshDiagnosticsFromSimulator();
        RefreshIoValues();
        RefreshNetListValues();
        RefreshWaveform();
        RefreshRecentTransitions();
        RaisePropertyChanged(nameof(SimTimeText));
        RaisePropertyChanged(nameof(SimStatusText));
        UpdateCommandStates();
        VisualInvalidated?.Invoke();
    }

    private bool EnsureSimulation()
    {
        if (Simulator.IsCompiled)
        {
            return true;
        }

        StatusText = "Compiling…";
        if (!Simulator.Compile())
        {
            RefreshDiagnosticsFromSimulator();
            StatusText = "Simulation blocked by validation errors";
            VisualInvalidated?.Invoke();
            return false;
        }

        RefreshDiagnosticsFromSimulator();
        RefreshSimulationPresentation();
        return true;
    }

    private string? FindClockNet()
    {
        PinDeclaration? port = Definition.Pins.FirstOrDefault(p =>
            p.Direction == PinDirection.Input &&
            (p.Name.Contains("clk", StringComparison.OrdinalIgnoreCase) ||
             p.Id.Contains("clk", StringComparison.OrdinalIgnoreCase) ||
             p.Name.Contains("clock", StringComparison.OrdinalIgnoreCase)));
        if (port is not null && Definition.FindPortNet(port.Id) is Net portNet)
        {
            return portNet.Id;
        }

        Net? labelled = Definition.Nets.FirstOrDefault(n =>
            (n.Label ?? string.Empty).Contains("clk", StringComparison.OrdinalIgnoreCase) ||
            n.Id.Contains("clk", StringComparison.OrdinalIgnoreCase));
        if (labelled is not null)
        {
            return labelled.Id;
        }

        foreach (ComponentInstance instance in Definition.Instances)
        {
            foreach (PinDescriptor pin in _project.GetInstancePins(instance))
            {
                if (!pin.Name.Contains("clk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Net? net = Simulator.NetFor(EditorPin.ForInstance(instance.Id, pin.Id));
                if (net is not null)
                {
                    return net.Id;
                }
            }
        }

        return null;
    }

    private string? DeclaredDomain(EditorPin pin)
    {
        if (pin.Kind == EditorPinKind.Port)
        {
            return Definition.FindPin(pin.PinId)?.DomainId;
        }

        ComponentInstance? instance = Definition.FindInstance(pin.OwnerId);
        return instance is null ? null : _project.FindInstancePin(instance, pin.PinId)?.DomainId;
    }

    private string? DefaultDomainId()
    {
        if (_project.FindDomain(StandardDomains.BinaryId) is not null)
        {
            return StandardDomains.BinaryId;
        }

        return _project.Domains.FirstOrDefault()?.Id;
    }

    private void AttachInstancesToNet(EditorPin pin, Net net)
    {
        if (pin.Kind == EditorPinKind.Instance &&
            Definition.FindInstance(pin.OwnerId) is not null &&
            !net.Pins.Any(p => string.Equals(p.InstanceId, pin.OwnerId, StringComparison.Ordinal) &&
                               string.Equals(p.PinId, pin.PinId, StringComparison.Ordinal)))
        {
            net.Pins.Add(new PinRef(pin.OwnerId, pin.PinId));
        }
    }

    private void AttachToNet(EditorPin pin, Net target)
    {
        Net? current = Simulator.NetFor(pin);
        if (current is not null && !ReferenceEquals(current, target))
        {
            if (pin.Kind == EditorPinKind.Port)
            {
                for (int i = 0; i < Definition.Ports.Count; i++)
                {
                    if (string.Equals(Definition.Ports[i].PinId, pin.PinId, StringComparison.Ordinal))
                    {
                        Definition.Ports[i] = new PortBinding(pin.PinId, target.Id);
                    }
                }
            }
            else
            {
                current.Pins.RemoveAll(p =>
                    string.Equals(p.InstanceId, pin.OwnerId, StringComparison.Ordinal) &&
                    string.Equals(p.PinId, pin.PinId, StringComparison.Ordinal));
            }
        }

        AttachInstancesToNet(pin, target);
    }

    private void MergeNets(Net target, Net source)
    {
        if (ReferenceEquals(target, source))
        {
            return;
        }

        foreach (PinRef pin in source.Pins)
        {
            if (!target.Pins.Any(p => p.InstanceId == pin.InstanceId && p.PinId == pin.PinId))
            {
                target.Pins.Add(pin);
            }
        }

        for (int i = 0; i < Definition.Ports.Count; i++)
        {
            if (string.Equals(Definition.Ports[i].NetId, source.Id, StringComparison.Ordinal))
            {
                Definition.Ports[i] = new PortBinding(Definition.Ports[i].PinId, target.Id);
            }
        }

        target.Label ??= source.Label;
        target.InitialValue ??= source.InitialValue;
        Definition.Nets.Remove(source);
    }

    private void CleanEmptyNets()
    {
        var bound = Definition.Ports.Select(p => p.NetId).ToHashSet(StringComparer.Ordinal);
        Definition.Nets.RemoveAll(n => n.Pins.Count == 0 && !bound.Contains(n.Id));
    }

    private string Describe(EditorPin pin) =>
        pin.Kind == EditorPinKind.Port ? $"port {pin.PinId}" : $"{pin.OwnerId}.{pin.PinId}";

    private string TypeName(ComponentInstance instance)
    {
        if (instance.Primitive is PrimitiveSpec spec)
        {
            return PrimitiveDisplay.Name(spec);
        }

        return _project.FindDefinition(instance.DefinitionId)?.Name ?? "missing definition";
    }

    private string UniquePlacementId(string label) =>
        DefinitionEditor.UniqueInstanceId(Definition, Slug(label));

    private static string Slug(string label)
    {
        string stem = new string(label
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');
        return stem.Length == 0 ? "part" : stem;
    }

    private double NextPlacementX()
    {
        _placementCascade = (_placementCascade + 1) % 8;
        return _placementPoint.X + (_placementCascade * 22);
    }

    private double NextPlacementY() => _placementPoint.Y + (_placementCascade * 16);

    private void FormatPlacement() => _placementCascade = 0;

    private static double? ParseDoubleValue(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
    }

    private static int[]? ParseInts(string text)
    {
        string[] parts = text.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        return values;
    }

    private static long[]? ParseLongs(string text)
    {
        string[] parts = text.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var values = new long[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        return values;
    }

    private void RaiseSelectionChanged()
    {
        RaisePropertyChanged(nameof(HasSelection));
        RaisePropertyChanged(nameof(HasSelectedInstance));
        RaisePropertyChanged(nameof(HasSelectedNet));
        RaisePropertyChanged(nameof(HasMultipleSelection));
        RaisePropertyChanged(nameof(SelectionCount));
        RaisePropertyChanged(nameof(SelectedInstanceIds));
        RaisePropertyChanged(nameof(IsRootDefinition));
        RaisePropertyChanged(nameof(SelectedNetHasRoute));
        RaisePropertyChanged(nameof(SelectedNetOnWaveform));
        RaisePropertyChanged(nameof(WaveformPinButtonText));
        RaisePropertyChanged(nameof(CanPromoteSelectedNet));
        RaisePropertyChanged(nameof(SelectedInstanceId));
        RaisePropertyChanged(nameof(SelectedNetId));
        RaisePropertyChanged(nameof(SelectionTitle));
        RaisePropertyChanged(nameof(SelectionSubtitle));
        UpdateCommandStates();
    }

    private EditorSnapshot Capture()
    {
        return new EditorSnapshot(
            ProjectSerializer.SaveToString(_project),
            Definition.Id,
            _selectedInstanceIds.Order(StringComparer.Ordinal).ToArray(),
            _selectedNet?.Id);
    }

    private void Restore(EditorSnapshot snapshot)
    {
        CancelNumericLaneEdit();
        CancelDomainEdit();
        try
        {
            double? savedScale = _project.View.Scale;
            double? savedOffsetX = _project.View.OffsetX;
            double? savedOffsetY = _project.View.OffsetY;
            _project = ProjectSerializer.LoadFromString(snapshot.Json, validate: false);
            EnsureProjectShape();
            _project.View.Scale = savedScale;
            _project.View.OffsetX = savedOffsetX;
            _project.View.OffsetY = savedOffsetY;
            _definitionId = _project.FindDefinition(snapshot.DefinitionId)?.Id ?? _project.RootDefinitionId;
            _selectedInstanceIds.Clear();
            CircuitDefinition? restored = _project.FindDefinition(_definitionId);
            if (restored is not null)
            {
                foreach (string id in snapshot.SelectedInstanceIds)
                {
                    if (restored.FindInstance(id) is not null)
                    {
                        _selectedInstanceIds.Add(id);
                    }
                }
            }

            _selectedInstance = FindSelectedInstance();
            _selectedNet = _project.FindDefinition(_definitionId)?.FindNet(snapshot.SelectedNetId ?? string.Empty);
            Simulator = new SimulatorHost(_project, Definition);
            RefreshAll(fit: false);
            RefreshDirtyFromSaved();
            AdvanceTicksText = _project.Settings.DefaultAdvanceTicks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is ProjectFormatException or CircuitValidationException)
        {
            StatusText = "Undo failed: " + exception.Message;
        }
    }

    private void UpdateCommandStates()
    {
        _undoCommand.RaiseCanExecuteChanged();
        _redoCommand.RaiseCanExecuteChanged();
        _deleteCommand.RaiseCanExecuteChanged();
        _copyCommand.RaiseCanExecuteChanged();
        _pasteCommand.RaiseCanExecuteChanged();
        _duplicateCommand.RaiseCanExecuteChanged();
        _selectAllCommand.RaiseCanExecuteChanged();
        _extractCommand.RaiseCanExecuteChanged();
        _alignLeftCommand.RaiseCanExecuteChanged();
        _alignTopCommand.RaiseCanExecuteChanged();
        _distributeHorizontallyCommand.RaiseCanExecuteChanged();
        _distributeVerticallyCommand.RaiseCanExecuteChanged();
        _promoteInputCommand.RaiseCanExecuteChanged();
        _promoteOutputCommand.RaiseCanExecuteChanged();
        (ClearNetRouteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(ProjectTitle));
    }

    private sealed record ClipboardPayload(
        IReadOnlyList<ComponentInstance> Instances,
        IReadOnlyList<Net> Nets);

    private void RefreshDirtyFromSaved()
    {
        if (_savedProjectJson is null)
        {
            IsDirty = true;
            return;
        }

        try
        {
            IsDirty = !string.Equals(
                ProjectSerializer.SaveToString(_project, includeView: false),
                _savedProjectJson,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ProjectFormatException or InvalidOperationException)
        {
            IsDirty = true;
        }
    }

    private sealed record EditorSnapshot(
        string Json,
        string DefinitionId,
        string[] SelectedInstanceIds,
        string? SelectedNetId);

    private readonly record struct SimulationEngineSnapshot(WaveformSnapshot Waveform);
}
