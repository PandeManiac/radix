using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class RenderingTests
{
    [AvaloniaFact]
    public void MainWindowRendersCounterWithoutDisplay()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        viewModel.BeginSimulation();
        viewModel.AdvanceTicksText = "100";
        viewModel.AdvanceSimulation();

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1480,
            Height = 920,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(1480, 920));
        window.Arrange(new Rect(0, 0, 1480, 920));
        Dispatcher.UIThread.RunJobs();

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);

        string path = Path.Combine(ArtifactDirectory(), "workbench-counter.png");
        using FileStream stream = File.Create(path);
        bitmap.Save(stream);
        stream.Flush();

        window.Close();

        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
    }

    [AvaloniaFact]
    public void WaveformTabRendersRecordedTrace()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        viewModel.BeginSimulation();
        viewModel.AdvanceTicksText = "160";
        viewModel.AdvanceSimulation();

        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.FindControl<TabControl>("RightTabs");
        Assert.NotNull(tabs);
        tabs!.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-waveform.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        window.Close();
        Assert.NotEmpty(viewModel.Waveform.Rows);
        Assert.True(new FileInfo(path).Length > 10_000);
    }

    [AvaloniaFact]
    public void EmptyProjectCanvasRendersHint()
    {
        var project = Core.Model.CircuitProject.CreateEmpty("Empty");
        project.Definitions.Add(new Core.Model.CircuitDefinition { Id = "main", Name = "Main" });
        project.RootDefinitionId = "main";
        var viewModel = new EditorViewModel(project);
        var window = new MainWindow { DataContext = viewModel, Width = 1100, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var bitmap = new RenderTargetBitmap(new PixelSize(1100, 700), new Vector(96, 96));
        bitmap.Render(window);

        Assert.Empty(viewModel.Definition.Instances);
        Assert.Empty(viewModel.Diagnostics);
        window.Close();
    }

    private static string ArtifactDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName ?? Environment.CurrentDirectory;
        string path = Path.Combine(root, "artifacts", "editor");
        Directory.CreateDirectory(path);
        return path;
    }
}
