using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Radix.Editor.Controls;
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
    public void NumericLaneDriveControlsRenderInInspector()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateAccumulatorCpuDemo());
        viewModel.BeginSimulation();

        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var inspector = window.FindControl<ScrollViewer>("InspectorScroll");
        Assert.NotNull(inspector);
        var dataLane = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text => text.Text == "Data");
        Assert.NotNull(dataLane);
        dataLane!.BringIntoView();
        Dispatcher.UIThread.RunJobs();

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-accumulator-lanes.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        window.Close();

        Assert.Equal(3, viewModel.NumericLanes.Count);
        Assert.Contains(viewModel.NumericLanes, lane => lane.CanDrive);
        Assert.Contains(viewModel.NumericLanes, lane => !lane.CanDrive);
        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
    }

    [AvaloniaFact]
    public void AccumulatorCanvasRendersReadableNumericLaneOverlay()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateAccumulatorCpuDemo());
        viewModel.BeginSimulation();
        viewModel.AdvanceTicksText = "100";
        viewModel.AdvanceSimulation();

        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(1480, 920));
        window.Arrange(new Rect(0, 0, 1480, 920));
        Dispatcher.UIThread.RunJobs();

        var canvas = Assert.IsType<CircuitCanvas>(window.FindControl<CircuitCanvas>("CircuitCanvas"));
        Assert.Equal(3, viewModel.NumericLanes.Count);

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-accumulator-lane-overlay.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        Assert.True(canvas.LaneOverlayBounds.Width > 200, "the overlay should reserve a readable width");
        Assert.True(canvas.LaneOverlayBounds.Height > 100, "three lanes should produce a readable legend");
        Assert.True(canvas.LaneOverlayBounds.Bottom <= canvas.Bounds.Height);

        window.Close();
        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
    }

    [AvaloniaFact]
    public void CustomNumericFormatEditorRendersEditAndPreviewState()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateAccumulatorCpuDemo());
        viewModel.BeginSimulation();
        viewModel.BeginEditCustomNumericFormat("signed-4bit");

        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Button update = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.Content is "Update numeric format");
        Button cancel = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.Content is "Cancel edit");
        update.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(update.IsVisible);
        Assert.True(cancel.IsVisible);
        Assert.Contains("Preview", viewModel.FormatPreviewText, StringComparison.Ordinal);

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-custom-format-editor.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        window.Close();
        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
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

    [AvaloniaFact]
    public void DarkThemeRendersDarkSurfacesAndCircuit()
    {
        Application? application = Application.Current;
        Assert.NotNull(application);
        ThemeVariant? previous = application.RequestedThemeVariant;
        application.RequestedThemeVariant = ThemeVariant.Dark;

        try
        {
            var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
            viewModel.BeginSimulation();
            viewModel.AdvanceTicksText = "100";
            viewModel.AdvanceSimulation();

            var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(1480, 920));
            window.Arrange(new Rect(0, 0, 1480, 920));
            Dispatcher.UIThread.RunJobs();

            using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
            bitmap.Render(window);
            string path = Path.Combine(ArtifactDirectory(), "workbench-counter-dark.png");
            using (FileStream stream = File.Create(path))
            {
                bitmap.Save(stream);
            }

            (byte r, byte g, byte b) = SamplePixel(bitmap, 5, 5);
            double luminance = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

            var tabs = window.FindControl<TabControl>("RightTabs");
            Assert.NotNull(tabs);
            tabs!.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            using (var waveform = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96)))
            {
                waveform.Render(window);
                using FileStream stream = File.Create(Path.Combine(ArtifactDirectory(), "workbench-waveform-dark.png"));
                waveform.Save(stream);
            }

            window.Close();

            Assert.True(luminance < 90, $"expected a dark window background, sampled {r},{g},{b}");
            Assert.True(new FileInfo(path).Length > 10_000);
        }
        finally
        {
            application.RequestedThemeVariant = previous;
        }
    }

    [AvaloniaFact]
    public void SavedViewportIsAppliedInsteadOfFitting()
    {
        var project = ExampleProjects.CreateCounterDemo();
        project.View.Scale = 0.7;
        project.View.OffsetX = 123.5;
        project.View.OffsetY = -45;

        var viewModel = new EditorViewModel(project);
        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(1480, 920));
        window.Arrange(new Rect(0, 0, 1480, 920));
        Dispatcher.UIThread.RunJobs();

        CircuitCanvas canvas = Assert.IsType<CircuitCanvas>(window.FindControl<CircuitCanvas>("CircuitCanvas"));
        Assert.Equal(0.7, canvas.Viewport.Scale, 3);
        Assert.Equal(123.5, canvas.Viewport.Offset.X, 3);
        Assert.Equal(-45, canvas.Viewport.Offset.Y, 3);

        window.Close();
    }

    private static (byte R, byte G, byte B) SamplePixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), buffer.Length, 4);
        }
        finally
        {
            handle.Free();
        }

        return (buffer[2], buffer[1], buffer[0]);
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
