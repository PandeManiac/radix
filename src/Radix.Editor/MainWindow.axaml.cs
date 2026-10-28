using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Radix.Editor.Controls;
using Radix.Editor.Services;
using Radix.Editor.ViewModels;

namespace Radix.Editor;

public partial class MainWindow : Window
{
    private readonly bool _themeReady;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        ThemeBox.SelectedIndex = (int)ThemeService.Current;
        _themeReady = true;
        ThemeService.Changed += ThemeService_Changed;
        Closed += (_, _) => ThemeService.Changed -= ThemeService_Changed;
        Closing += Window_Closing;
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
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

    private void FieldBox_LostFocus(object? sender, RoutedEventArgs e) => CommitField(sender);

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
        return choice switch
        {
            UnsavedChangesChoice.Discard => true,
            UnsavedChangesChoice.Save => string.IsNullOrWhiteSpace(viewModel.ProjectPath)
                ? await SaveAsAsync(viewModel)
                : viewModel.SaveProject(viewModel.ProjectPath),
            _ => false,
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return cleaned.Trim().Length == 0 ? "circuit" : cleaned;
    }

    private async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
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
