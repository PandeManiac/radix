using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Radix.Editor.Controls;
using Radix.Editor.Services;
using Radix.Editor.ViewModels;

namespace Radix.Editor;

public partial class MainWindow : Window
{
    private readonly bool _themeReady;
    private bool _closeConfirmed;
    private EditorViewModel? _watchedViewModel;
    private double _normalWorkspaceWidth = WorkspaceSettings.DefaultWidth;
    private double _normalWorkspaceHeight = WorkspaceSettings.DefaultHeight;

    public MainWindow()
    {
        InitializeComponent();
        _normalWorkspaceWidth = double.IsFinite(Width) ? Width : WorkspaceSettings.DefaultWidth;
        _normalWorkspaceHeight = double.IsFinite(Height) ? Height : WorkspaceSettings.DefaultHeight;
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal && Bounds.Width >= MinWidth && Bounds.Height >= MinHeight)
            {
                _normalWorkspaceWidth = Bounds.Width;
                _normalWorkspaceHeight = Bounds.Height;
            }
        };
        ThemeBox.SelectedIndex = (int)ThemeService.Current;
        _themeReady = true;
        ThemeService.Changed += ThemeService_Changed;
        Closed += (_, _) => ThemeService.Changed -= ThemeService_Changed;
        Closing += Window_Closing;
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => BindViewModel();
        BindViewModel();
    }

    private void BindViewModel()
    {
        if (_watchedViewModel is not null)
        {
            _watchedViewModel.RecentProjectOpenRequested -= ViewModel_RecentProjectOpenRequested;
            _watchedViewModel.SearchOpened -= ViewModel_SearchOpened;
        }

        _watchedViewModel = ViewModel;
        if (_watchedViewModel is not null)
        {
            _watchedViewModel.RecentProjectOpenRequested += ViewModel_RecentProjectOpenRequested;
            _watchedViewModel.SearchOpened += ViewModel_SearchOpened;
        }
    }

    private void ViewModel_RecentProjectOpenRequested(string path) => _ = OpenRecentProjectAsync(path);

    private void ViewModel_SearchOpened()
    {
        // The palette becomes visible during this event; post focus so layout has run.
        Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            ScrollSearchSelection();
        });
    }

    private void SearchBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && ViewModel is EditorViewModel viewModel)
        {
            viewModel.CloseSearch();
            e.Handled = true;
        }
    }

    private void SearchResults_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: ProjectSearchItem item })
        {
            item.ActivateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ScrollSearchSelection()
    {
        if (ViewModel is EditorViewModel { SelectedSearchItem: { } item })
        {
            SearchResultsList.ScrollIntoView(item);
        }
    }

    private async Task OpenRecentProjectAsync(string path)
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        ViewModel?.LoadProject(path);
    }

    private async void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || ViewModel is not EditorViewModel { IsDirty: true })
        {
            return;
        }

        e.Cancel = true;
        if (await ConfirmDiscardAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private async void NewProject_Click(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmDiscardAsync())
        {
            ViewModel?.NewProject();
        }
    }

    private void ThemeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_themeReady || ThemeBox.SelectedIndex is < 0 or > 2)
        {
            return;
        }

        ThemeService.SetMode((ThemeMode)ThemeBox.SelectedIndex);
    }

    private void ThemeService_Changed(object? sender, EventArgs e)
    {
        ThemeBox.SelectedIndex = (int)ThemeService.Current;
        ViewModel?.RefreshTheme();
    }

    /// <summary>Active inspector tab; used to restore and capture workspace state.</summary>
    public int InspectorTabIndex
    {
        get => RightTabs.SelectedIndex;
        set => RightTabs.SelectedIndex = Math.Clamp(value, 0, RightTabs.ItemCount - 1);
    }

    /// <summary>Snapshot for per-user preferences; never includes project content.</summary>
    public WorkspaceSettings CaptureWorkspace() => new()
    {
        WindowWidth = _normalWorkspaceWidth,
        WindowHeight = _normalWorkspaceHeight,
        Maximized = WindowState == WindowState.Maximized,
        InspectorTabIndex = RightTabs.SelectedIndex,
        WireLabelMode = ViewModel?.WireLabelMode ?? WireLabelModes.All,
        RecentProjects = ViewModel?.RecentProjectPaths.ToList() ?? [],
    };

    private EditorViewModel? ViewModel => DataContext as EditorViewModel;

    private static readonly FilePickerFileType ProjectFileType = new("RADIX project")
    {
        Patterns = ["*.radix"],
        MimeTypes = ["application/json"],
    };

    private static readonly FilePickerFileType LibraryFileType = new("RADIX component library")
    {
        Patterns = ["*.radixlib"],
        MimeTypes = ["application/json"],
    };

    private static readonly FilePickerFileType JsonFileType = new("JSON")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    private static readonly FilePickerFileType CsvFileType = new("CSV")
    {
        Patterns = ["*.csv"],
        MimeTypes = ["text/csv"],
    };

    private static readonly FilePickerFileType TestScriptFileType = new("RADIX test script")
    {
        Patterns = ["*.radixtest"],
        MimeTypes = ["application/json"],
    };

    private void FieldBox_LostFocus(object? sender, RoutedEventArgs e) => CommitField(sender);

    private void LookupCell_LostFocus(object? sender, RoutedEventArgs e) => CommitLookupCell(sender);

    private void LookupCell_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitLookupCell(sender);
            e.Handled = true;
        }
    }

    private void CommitLookupCell(object? sender)
    {
        if (sender is TextBox { DataContext: LookupTableCell cell } && ViewModel is EditorViewModel viewModel)
        {
            viewModel.ApplyLookupCell(cell);
        }
    }

    private void FieldBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitField(sender);
            e.Handled = true;
        }
    }

    private void CommitField(object? sender)
    {
        if (sender is TextBox { DataContext: EditableField field } && ViewModel is EditorViewModel viewModel)
        {
            viewModel.ApplyField(field);
        }
    }

    private void PinName_LostFocus(object? sender, RoutedEventArgs e) => CommitPinName(sender);

    private void PinName_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitPinName(sender);
            e.Handled = true;
        }
    }

    private void CommitPinName(object? sender)
    {
        if (sender is TextBox { DataContext: PinListItem pin } && ViewModel is EditorViewModel viewModel)
        {
            viewModel.RenamePin(pin.Id, pin.Name);
        }
    }

    private void PinDomain_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: PinListItem pin, SelectedItem: DomainChoice domain } &&
            ViewModel is EditorViewModel viewModel &&
            !string.Equals(pin.DomainId, domain.Id, StringComparison.Ordinal))
        {
            viewModel.ChangePinDomain(pin.Id, domain.Id);
        }
    }

    private void PinDirection_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PinListItem pin } && ViewModel is EditorViewModel viewModel)
        {
            viewModel.TogglePinDirection(pin.Id);
        }
    }

    private void ElectricalTerminalNode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: ElectricalTerminalListItem terminal, SelectedItem: ElectricalNodeChoice node } &&
            ViewModel is EditorViewModel viewModel)
        {
            viewModel.SetElectricalTerminalNode(terminal.ComponentId, terminal.Id, node.Id);
        }
    }

    private void LaneValue_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            sender is not TextBox { DataContext: NumericLaneListItem lane } ||
            lane.DriveCommand.CanExecute(null) == false)
        {
            return;
        }

        lane.DriveCommand.Execute(null);
        e.Handled = true;
    }

    private void EventButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string description } && ViewModel is EditorViewModel viewModel)
        {
            viewModel.RunEvent(description);
        }
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => CircuitCanvas.ZoomIn();

    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => CircuitCanvas.ZoomOut();

    private void ResetZoom_Click(object? sender, RoutedEventArgs e) => CircuitCanvas.ResetZoom();

    private void FitZoom_Click(object? sender, RoutedEventArgs e) => CircuitCanvas.FitToContent();

    private async void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        await OpenProjectAsync();
    }

    private async Task OpenProjectAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open RADIX project",
            AllowMultiple = false,
            FileTypeFilter = [ProjectFileType],
        });

        if (files.Count > 0)
        {
            ViewModel?.LoadProject(files[0].Path.LocalPath);
        }
    }

    private async void SaveProject_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not EditorViewModel viewModel)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(viewModel.ProjectPath))
        {
            viewModel.SaveProject(viewModel.ProjectPath);
            return;
        }

        await SaveAsAsync(viewModel);
    }

    private async void SaveProjectAs_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is EditorViewModel viewModel)
        {
            await SaveAsAsync(viewModel);
        }
    }

    private async void ElectricalCopyJson_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is EditorViewModel viewModel && viewModel.TryBuildElectricalSolveJson(out string json))
        {
            await CopyElectricalSolveTextAsync(json, "Copied DC solve result as JSON");
        }
    }

    private async void ElectricalCopyCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is EditorViewModel viewModel && viewModel.TryBuildElectricalSolveCsv(out string csv))
        {
            await CopyElectricalSolveTextAsync(csv, "Copied DC solve result as CSV");
        }
    }

    private async void ElectricalSaveJson_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is EditorViewModel viewModel && viewModel.TryBuildElectricalSolveJson(out string json))
        {
            await SaveElectricalSolveTextAsync(viewModel, json, "json", JsonFileType);
        }
    }

    private async void ElectricalSaveCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is EditorViewModel viewModel && viewModel.TryBuildElectricalSolveCsv(out string csv))
        {
            await SaveElectricalSolveTextAsync(viewModel, csv, "csv", CsvFileType);
        }
    }

    private async Task SaveElectricalSolveTextAsync(
        EditorViewModel viewModel,
        string text,
        string extension,
        FilePickerFileType fileType)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save DC solve result as {extension.ToUpperInvariant()}",
            SuggestedFileName = $"{SanitizeFileName(viewModel.Project.Name)}-dc-solve.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = [fileType],
            ShowOverwritePrompt = true,
        });

        if (file is null)
        {
            return;
        }

        try
        {
            await using Stream stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(text);
            await writer.FlushAsync();
            viewModel.StatusText = $"Saved DC solve result as {extension.ToUpperInvariant()}";
        }
        catch (Exception)
        {
            viewModel.StatusText = $"Could not save DC solve result as {extension.ToUpperInvariant()}.";
        }
    }

    private async Task CopyElectricalSolveTextAsync(string text, string status)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            if (ViewModel is EditorViewModel missingClipboard)
            {
                missingClipboard.StatusText = "Clipboard is not available.";
            }

            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            if (ViewModel is EditorViewModel failedClipboard)
            {
                failedClipboard.StatusText = "Could not copy to the clipboard.";
            }

            return;
        }

        if (ViewModel is EditorViewModel viewModel)
        {
            viewModel.StatusText = status;
        }
    }

    private async void RunTestScript_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not EditorViewModel viewModel)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Run RADIX test script",
            AllowMultiple = false,
            FileTypeFilter = [TestScriptFileType],
        });

        if (files.Count > 0)
        {
            await viewModel.RunTestScriptAsync(files[0].Path.LocalPath);
        }
    }

    private async void RunTestScriptSuite_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not EditorViewModel viewModel)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Run RADIX test scripts in folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            await viewModel.RunTestScriptSuiteAsync(folders[0].Path.LocalPath);
        }
    }

    private async void ImportLibrary_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not EditorViewModel viewModel)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import RADIX component library",
            AllowMultiple = false,
            FileTypeFilter = [LibraryFileType],
        });

        if (files.Count > 0)
        {
            viewModel.ImportLibrary(files[0].Path.LocalPath);
        }
    }

    private async void ExportLibrary_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not EditorViewModel viewModel)
        {
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export RADIX component library",
            SuggestedFileName = SanitizeFileName(viewModel.Definition.Name),
            DefaultExtension = "radixlib",
            FileTypeChoices = [LibraryFileType],
            ShowOverwritePrompt = true,
        });

        if (file is not null)
        {
            viewModel.ExportDefinition(file.Path.LocalPath, viewModel.Definition.Id);
        }
    }

    private async Task<bool> SaveAsAsync(EditorViewModel viewModel)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save RADIX project",
            SuggestedFileName = SanitizeFileName(viewModel.Project.Name),
            DefaultExtension = "radix",
            FileTypeChoices = [ProjectFileType],
            ShowOverwritePrompt = true,
        });

        if (file is null)
        {
            return false;
        }

        return viewModel.SaveProject(file.Path.LocalPath);
    }

    /// <summary>
    /// Returns true when it is safe to replace or close the project. Save failures
    /// and cancelled file pickers abort the action instead of silently discarding.
    /// </summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (ViewModel is not EditorViewModel { IsDirty: true } viewModel)
        {
            return true;
        }

        UnsavedChangesChoice choice = await UnsavedChangesDialog.AskAsync(this, viewModel.Project.Name);
        if (choice == UnsavedChangesChoice.Discard)
        {
            // The user explicitly declined these changes, so the crash snapshot is stale.
            RecoveryService.Clear();
            return true;
        }

        return choice == UnsavedChangesChoice.Save &&
               (string.IsNullOrWhiteSpace(viewModel.ProjectPath)
                   ? await SaveAsAsync(viewModel)
                   : viewModel.SaveProject(viewModel.ProjectPath));
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return cleaned.Trim().Length == 0 ? "circuit" : cleaned;
    }

    private async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is EditorViewModel palette && palette.IsSearchOpen)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && (e.Key is Key.P or Key.K))
            {
                e.Handled = true;
                palette.CloseSearch();
                return;
            }

            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    palette.CloseSearch();
                    return;
                case Key.Enter:
                    e.Handled = true;
                    palette.ActivateSelectedSearch();
                    return;
                case Key.Down:
                    e.Handled = true;
                    palette.MoveSearchSelection(1);
                    ScrollSearchSelection();
                    return;
                case Key.Up:
                    e.Handled = true;
                    palette.MoveSearchSelection(-1);
                    ScrollSearchSelection();
                    return;
            }
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && (e.Key is Key.P or Key.K) &&
            ViewModel is EditorViewModel openSearch)
        {
            e.Handled = true;
            openSearch.OpenSearch();
            return;
        }

        if (e.Key == Key.Delete && e.Source is not TextBox && ViewModel is EditorViewModel delete)
        {
            e.Handled = true;
            delete.DeleteSelection();
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        bool editingText = e.Source is TextBox;
        switch (e.Key)
        {
            case Key.Z:
                if (!editingText && ViewModel is EditorViewModel undo)
                {
                    e.Handled = true;
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        undo.Redo();
                    }
                    else
                    {
                        undo.Undo();
                    }
                }

                break;
            case Key.Y:
                if (!editingText && ViewModel is EditorViewModel redo)
                {
                    e.Handled = true;
                    redo.Redo();
                }

                break;
            case Key.C:
                if (!editingText && ViewModel is EditorViewModel copy)
                {
                    e.Handled = true;
                    copy.CopySelection();
                }

                break;
            case Key.V:
                if (!editingText && ViewModel is EditorViewModel paste)
                {
                    e.Handled = true;
                    paste.Paste();
                }

                break;
            case Key.D:
                if (!editingText && ViewModel is EditorViewModel duplicate)
                {
                    e.Handled = true;
                    duplicate.DuplicateSelection();
                }

                break;
            case Key.A:
                if (!editingText && ViewModel is EditorViewModel selectAll)
                {
                    e.Handled = true;
                    selectAll.SelectAllInstances();
                }

                break;
            case Key.S:
                e.Handled = true;
                if (ViewModel is EditorViewModel save && !string.IsNullOrWhiteSpace(save.ProjectPath) &&
                    !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    save.SaveProject(save.ProjectPath);
                }
                else if (ViewModel is EditorViewModel saveAs)
                {
                    await SaveAsAsync(saveAs);
                }

                break;
            case Key.O:
                e.Handled = true;
                await OpenProjectAsync();
                break;
            case Key.N:
                e.Handled = true;
                if (await ConfirmDiscardAsync())
                {
                    ViewModel?.NewProject();
                }

                break;
        }
    }
}
