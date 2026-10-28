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

    public MainWindow()
    {
        InitializeComponent();
        ThemeBox.SelectedIndex = (int)ThemeService.Current;
        _themeReady = true;
        ThemeService.Changed += ThemeService_Changed;
        Closed += (_, _) => ThemeService.Changed -= ThemeService_Changed;
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
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

    private async Task SaveAsAsync(EditorViewModel viewModel)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save RADIX project",
            SuggestedFileName = SanitizeFileName(viewModel.Project.Name),
            DefaultExtension = "radix",
            FileTypeChoices = [ProjectFileType],
            ShowOverwritePrompt = true,
        });

        if (file is not null)
        {
            viewModel.SaveProject(file.Path.LocalPath);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return cleaned.Trim().Length == 0 ? "circuit" : cleaned;
    }

    private async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.S:
                e.Handled = true;
                if (ViewModel is EditorViewModel save && !string.IsNullOrWhiteSpace(save.ProjectPath))
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
                OpenProject_Click(sender, new RoutedEventArgs());
                break;
            case Key.N:
                e.Handled = true;
                ViewModel?.NewProject();
                break;
        }
    }
}
