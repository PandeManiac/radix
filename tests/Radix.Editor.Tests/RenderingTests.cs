using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Editor.Controls;
using Radix.Editor.Rendering;
using Radix.Editor.Services;
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
        tabs!.SelectedIndex = 3;
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
    public void WaveformZoomRendersASubrangeAndKeepsTheFullTraceReachable()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        viewModel.BeginSimulation();
        viewModel.AdvanceTicksText = "200";
        viewModel.AdvanceSimulation();

        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.FindControl<TabControl>("RightTabs");
        Assert.NotNull(tabs);
        tabs!.SelectedIndex = 3;
        Dispatcher.UIThread.RunJobs();

        WaveformView waveform = window.GetVisualDescendants().OfType<WaveformView>().Single();
        waveform.ZoomTo(60, 120);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(60, waveform.VisibleWindow.Start);
        Assert.Equal(120, waveform.VisibleWindow.End);

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-waveform-zoom.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        waveform.FitAll();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, waveform.VisibleWindow.Start);
        Assert.Equal(200, waveform.VisibleWindow.End);

        window.Close();
        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
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
    public void NumericLaneEditorRendersEditState()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        NumericLaneListItem lane = Assert.Single(viewModel.NumericLanes);
        viewModel.BeginEditNumericLane(lane.Id);
        Assert.True(viewModel.IsEditingLane);

        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Button update = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.Content is "Update numeric lane");
        Button cancel = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.Content is "Cancel lane edit");
        update.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(update.IsVisible);
        Assert.True(cancel.IsVisible);

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-lane-editor.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        window.Close();
        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
    }

    [AvaloniaFact]
    public void InterfacePinReorderControlsRenderAndExecute()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("LeftScroll"));
        scroll.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();

        List<Button> up = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Content is "↑")
            .ToList();
        List<Button> down = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Content is "↓")
            .ToList();
        Assert.Equal(viewModel.Definition.Pins.Count, up.Count);
        Assert.Equal(viewModel.Definition.Pins.Count, down.Count);
        Assert.True(up[0].Command?.CanExecute(null) == false);
        Assert.True(down[^1].Command?.CanExecute(null) == false);
        Assert.True(up[1].Command?.CanExecute(null) == true);

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-interface-reorder.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        window.Close();
        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
    }

    [AvaloniaFact]
    public void NetNavigatorRendersFilteredRowsAndLiveValues()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        viewModel.BeginSimulation();
        viewModel.AdvanceTicksText = "100";
        viewModel.AdvanceSimulation();
        viewModel.NetSearch = "q";

        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var filter = Assert.IsType<TextBox>(window.FindControl<TextBox>("NetFilterBox"));
        filter.BringIntoView();
        Dispatcher.UIThread.RunJobs();

        Assert.True(filter.IsVisible);
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "q0_net");
        Assert.Contains(viewModel.NetList, item => item.Id == "q0_net" && item.ValueText == "1");

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-net-navigator.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        window.Close();
        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
    }

    [AvaloniaFact]
    public void DomainPolicyEditorRendersPriorityFields()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        viewModel.SelectedDriverPolicy = viewModel.DriverPolicies.Single(policy => policy.Id == "priority");
        viewModel.NewDomainName = "Wired";
        viewModel.NewDomainValues = "0 1 X Z";
        viewModel.NewDomainPriorities = "1 1 3 0";
        viewModel.NewDomainConflictValue = "2";

        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var inspector = window.FindControl<ScrollViewer>("InspectorScroll");
        Assert.NotNull(inspector);
        inspector!.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();

        ComboBox policyBox = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("DomainPolicyBox"));
        TextBox priorities = Assert.IsType<TextBox>(window.FindControl<TextBox>("DomainPrioritiesBox"));
        TextBox conflict = Assert.IsType<TextBox>(window.FindControl<TextBox>("DomainConflictBox"));
        Assert.True(policyBox.IsVisible);
        Assert.True(priorities.IsVisible);
        Assert.True(conflict.IsVisible);
        Assert.Equal("1 1 3 0", priorities.Text);
        Assert.Equal("2", conflict.Text);

        priorities.BringIntoView();
        Dispatcher.UIThread.RunJobs();

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-domain-policy.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        viewModel.SelectedDriverPolicy = viewModel.DriverPolicies.Single(candidate => candidate.Id == "single");
        Dispatcher.UIThread.RunJobs();
        Assert.False(priorities.IsEffectivelyVisible);
        Assert.False(conflict.IsEffectivelyVisible);

        window.Close();
        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
    }

    [AvaloniaFact]
    public void DiagnosticsTabRendersLocateActionForLocatedProblems()
    {
        var project = CircuitProject.CreateEmpty("broken");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);
        var broken = new Net { Id = "broken_net", DomainId = StandardDomains.BinaryId };
        broken.Pins.Add(new PinRef("missing", "out"));
        root.Nets.Add(broken);

        var viewModel = new EditorViewModel(project);
        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.FindControl<TabControl>("RightTabs");
        Assert.NotNull(tabs);
        tabs!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Button locate = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.Content is "Locate");
        Assert.True(locate.IsVisible);

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-diagnostics-locate.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        locate.Command!.Execute(null);
        Assert.Equal("broken_net", viewModel.SelectedNetId);

        window.Close();
        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
    }

    [AvaloniaFact]
    public void LookupTableEditorRendersAddressesAndValues()
    {
        var builder = new ProjectBuilder("lookup render");
        DefinitionBuilder root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net b = root.AddPin("b", "B", PinDirection.Input, StandardDomains.BinaryId);
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("g", new LookupSpec(2, [0, 0, 0, 1], 1));
        root.Connect(a, root.Pin("g", "in0"));
        root.Connect(b, root.Pin("g", "in1"));
        root.Connect(y, root.Pin("g", "out"));

        var viewModel = new EditorViewModel(builder.Build("root"));
        viewModel.SelectInstance("g");
        Assert.True(viewModel.HasLookupTable);

        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.FindControl<TabControl>("RightTabs");
        Assert.NotNull(tabs);
        tabs!.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        TextBlock heading = Assert.Single(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "LOOKUP TABLE");
        Assert.True(heading.IsEffectivelyVisible);

        TextBox cell = Assert.Single(
            window.GetVisualDescendants().OfType<TextBox>(),
            box => box.DataContext is LookupTableCell item && item.Address.StartsWith("1 ·", StringComparison.Ordinal));
        Assert.True(cell.IsEffectivelyVisible);

        using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
        bitmap.Render(window);
        string path = Path.Combine(ArtifactDirectory(), "workbench-lookup-table.png");
        using (FileStream stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        window.Close();
        Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
    }

    [AvaloniaFact]
    public void DiagnosticsTabRendersInEditorTestScriptResult()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateDFlipFlopDemo());
        string scriptPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radixtest");
        File.WriteAllText(
            scriptPath,
            """
            {
              "format": "radix-test",
              "version": 1,
              "name": "rendered failure",
              "definition": "demo.dff",
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 0 }, "expect": { "q": 1 } }
              ]
            }
            """);

        MainWindow? window = null;
        try
        {
            Assert.False(viewModel.RunTestScript(scriptPath));

            window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tabs = window.FindControl<TabControl>("RightTabs");
            Assert.NotNull(tabs);
            tabs!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();

            Button run = Assert.Single(
                window.GetVisualDescendants().OfType<Button>(),
                button => button.Content is "Run script…");
            Assert.True(run.IsEffectivelyVisible);

            TextBlock summary = Assert.Single(
                window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.StartsWith("FAIL", StringComparison.Ordinal) == true);
            Assert.True(summary.IsEffectivelyVisible);

            TextBlock failure = Assert.Single(
                window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("pin 'q'", StringComparison.Ordinal) == true);
            Assert.True(failure.IsEffectivelyVisible);

            using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
            bitmap.Render(window);
            string path = Path.Combine(ArtifactDirectory(), "workbench-test-script.png");
            using (FileStream stream = File.Create(path))
            {
                bitmap.Save(stream);
            }

            Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
        }
        finally
        {
            window?.Close();
            File.Delete(scriptPath);
        }
    }

    [AvaloniaFact]
    public async Task SweepTabRendersTableInDarkTheme()
    {
        Application? application = Application.Current;
        Assert.NotNull(application);
        ThemeVariant? previous = application.RequestedThemeVariant;
        application.RequestedThemeVariant = ThemeVariant.Dark;

        MainWindow? window = null;
        try
        {
            var builder = new ProjectBuilder("sweep render");
            DefinitionBuilder root = builder.Define("root", "Root");
            Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
            Net b = root.AddPin("b", "B", PinDirection.Input, StandardDomains.BinaryId);
            Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
            root.AddPrimitive("g", new LookupSpec(2, [0, 0, 0, 1], 1));
            root.Connect(a, root.Pin("g", "in0"));
            root.Connect(b, root.Pin("g", "in1"));
            root.Connect(y, root.Pin("g", "out"));
            var viewModel = new EditorViewModel(builder.Build("root"));
            viewModel.SweepSettleTicksText = "2";
            Assert.True(await viewModel.RunSweepAsync());

            window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(1480, 920));
            window.Arrange(new Rect(0, 0, 1480, 920));
            Dispatcher.UIThread.RunJobs();

            var tabs = window.FindControl<TabControl>("RightTabs");
            Assert.NotNull(tabs);
            tabs!.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();

            Button run = Assert.Single(
                window.GetVisualDescendants().OfType<Button>(),
                button => button.Content is "Run sweep");
            Assert.True(run.IsEffectivelyVisible);

            TextBlock table = Assert.Single(
                window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("1 | 1 | 1", StringComparison.Ordinal) == true);
            Assert.True(table.IsEffectivelyVisible);

            using var bitmap = new RenderTargetBitmap(new PixelSize(1480, 920), new Vector(96, 96));
            bitmap.Render(window);
            string path = Path.Combine(ArtifactDirectory(), "workbench-sweep-dark.png");
            using (FileStream stream = File.Create(path))
            {
                bitmap.Save(stream);
            }

            Assert.True(new FileInfo(path).Length > 10_000, $"expected a non-trivial screenshot at {path}");
        }
        finally
        {
            window?.Close();
            application.RequestedThemeVariant = previous;
        }
    }

    [AvaloniaFact]
    public void ProjectSettingsPanelBindsAndApplies()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        var window = new MainWindow { DataContext = viewModel, Width = 1480, Height = 920 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.FindControl<TabControl>("RightTabs");
        Assert.NotNull(tabs);
        tabs!.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        TextBox advance = Assert.IsType<TextBox>(window.FindControl<TextBox>("DefaultAdvanceTicksBox"));
        Assert.True(advance.IsEffectivelyVisible);
        Assert.Equal("10", advance.Text);

        viewModel.DefaultAdvanceTicksText = "30";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("30", advance.Text);

        Button apply = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.Content is "Apply settings");
        Assert.True(apply.IsEffectivelyVisible);

        window.Close();
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
            tabs!.SelectedIndex = 3;
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
    public void MainWindowCapturesWorkspacePreferences()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        var window = new MainWindow { DataContext = viewModel, Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.InspectorTabIndex = 3;
        WorkspaceSettings workspace = window.CaptureWorkspace();

        Assert.Equal(3, workspace.InspectorTabIndex);
        Assert.False(workspace.Maximized);
        Assert.InRange(workspace.WindowWidth, 1000, 1300);
        Assert.InRange(workspace.WindowHeight, 700, 900);

        window.InspectorTabIndex = 99;
        Assert.Equal(WorkspacePreferences.MaxInspectorTabIndex, window.InspectorTabIndex);

        window.Close();
    }

    [Fact]
    public void ViewportCenterOnPlacesWorldCenterAtSurfaceCenter()
    {
        var viewport = new Viewport();
        viewport.Restore(2.0, new Point(10, 20));

        viewport.CenterOn(new Rect(100, 50, 40, 20), new Size(800, 600));

        Point screen = viewport.WorldToScreen(new Point(120, 60));
        Assert.Equal(400, screen.X, 3);
        Assert.Equal(300, screen.Y, 3);
        Assert.Equal(2.0, viewport.Scale, 3);
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
