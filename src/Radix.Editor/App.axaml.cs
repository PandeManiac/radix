using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Radix.Editor.Services;
using Radix.Editor.ViewModels;
using Radix.StdLib;

namespace Radix.Editor;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ThemeService.Initialize();
            WorkspaceSettings workspace = WorkspacePreferences.Load();
            var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
            viewModel.SetRecentProjects(workspace.RecentProjects);
            string? startupProject = desktop.Args?.FirstOrDefault(
                argument => argument.EndsWith(".radix", StringComparison.OrdinalIgnoreCase));
            bool openStartupProject = startupProject is not null && File.Exists(startupProject);
            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = workspace.WindowWidth,
                Height = workspace.WindowHeight,
                InspectorTabIndex = workspace.InspectorTabIndex,
            };
            if (workspace.Maximized)
            {
                window.WindowState = WindowState.Maximized;
            }

            if (openStartupProject)
            {
                // Load after the window is attached so the saved viewport can be restored.
                window.Opened += (_, _) => viewModel.LoadProject(startupProject!);
            }

            window.Closed += (_, _) => WorkspacePreferences.Save(window.CaptureWorkspace());
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
