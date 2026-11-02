using System.Globalization;
using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Editor.Rendering;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class ElectricalEditorTests
{
    [Fact]
    public void AuthoredCircuitCanBeCreatedAndEditedThroughThePanel()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        Assert.False(viewModel.HasElectricalCircuits);
        Assert.False(viewModel.HasSelectedElectricalCircuit);

        viewModel.AddElectricalCircuit();

        Assert.True(viewModel.HasElectricalCircuits);
        Assert.True(viewModel.HasSelectedElectricalCircuit);
        Assert.Equal("dc1", viewModel.SelectedElectricalCircuitId);
        Assert.Equal(2, viewModel.ElectricalNodes.Count);
        Assert.Equal("gnd", viewModel.SelectedElectricalCircuitModel!.GroundNodeId);
        Assert.Equal("Valid authored circuit.", viewModel.ElectricalValidationSummary);
        Assert.True(viewModel.IsDirty);

        EditableField name = viewModel.ElectricalCircuitFields.Single(field => field.Label == "Name");
        name.Value = "Bench supply";
        Assert.True(viewModel.ApplyField(name));
        Assert.Equal("Bench supply", viewModel.SelectedElectricalCircuitModel.Name);

        EditableField description = viewModel.ElectricalCircuitFields.Single(field => field.Label == "Description");
        description.Value = "A small divider";
        Assert.True(viewModel.ApplyField(description));
        Assert.Equal("A small divider", viewModel.SelectedElectricalCircuitModel.Description);

        viewModel.SelectedElectricalNode = viewModel.ElectricalNodes.Single(node => node.Id == "n1");
        EditableField nodeName = viewModel.ElectricalNodeFields.Single(field => field.Label == "Node name");
        nodeName.Value = "Output";
        Assert.True(viewModel.ApplyField(nodeName));
        Assert.Equal("Output", viewModel.SelectedElectricalCircuitModel.FindNode("n1")!.Name);
    }

    [Fact]
    public void CapacitorKindExposesItsUnitAndInitialVoltageField()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.Capacitor);

        ElectricalComponent component = Assert.Single(viewModel.SelectedElectricalCircuitModel!.Components);
        Assert.Equal(ElectricalComponentKind.Capacitor, component.Kind);
        Assert.Equal("F", component.Unit);
        Assert.Equal(ElectricalTerminalPolarity.Passive, component.FindTerminal("a")!.Polarity);

        EditableField initial = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Initial voltage (V)");
        initial.Value = "2.5";
        Assert.True(viewModel.ApplyField(initial));
        Assert.Equal(2.5, component.InitialValue);

        EditableField cleared = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Initial voltage (V)");
        cleared.Value = string.Empty;
        Assert.True(viewModel.ApplyField(cleared));
        Assert.Null(component.InitialValue);
    }

    [Fact]
    public void TransientInitialConditionsAndWaveformsAcceptEngineeringSuffixesAndSerializeAsNumbers()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.Capacitor);
        ElectricalComponent capacitor = Assert.Single(viewModel.SelectedElectricalCircuitModel!.Components);

        EditableField initial = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Initial voltage (V)");
        initial.Value = "2.5m";
        Assert.True(viewModel.ApplyField(initial));
        Assert.Equal(0.0025, capacitor.InitialValue);

        viewModel.AddElectricalComponent();
        ElectricalComponent source = viewModel.SelectedElectricalCircuitModel!.Components
            .Single(component => component.Id != capacitor.Id);
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.VoltageSource);
        EditableField waveform = viewModel.ElectricalComponentFields.Single(field => field.Label == "Waveform");
        waveform.Value = "0=0, 250u=2.5, 1m=5";
        Assert.True(viewModel.ApplyField(waveform));
        Assert.Equal([0d, 250e-6, 1e-3], source.Waveform.Select(point => point.Time));
        Assert.Equal([0d, 2.5, 5d], source.Waveform.Select(point => point.Value));

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            Assert.True(viewModel.SaveProject(path));
            using JsonDocument project = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement components = project.RootElement.GetProperty("electricalCircuits")[0]
                .GetProperty("components");
            JsonElement savedCapacitor = components.EnumerateArray()
                .Single(component => component.GetProperty("id").GetString() == capacitor.Id);
            Assert.Equal(JsonValueKind.Number, savedCapacitor.GetProperty("initialValue").ValueKind);
            Assert.Equal(0.0025, savedCapacitor.GetProperty("initialValue").GetDouble());
            JsonElement savedSource = components.EnumerateArray()
                .Single(component => component.GetProperty("id").GetString() == source.Id);
            JsonElement points = savedSource.GetProperty("waveform");
            Assert.Equal(JsonValueKind.Number, points[1].GetProperty("time").ValueKind);
            Assert.Equal(250e-6, points[1].GetProperty("time").GetDouble());
            Assert.Equal(2.5, points[1].GetProperty("value").GetDouble());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("2.5x")]
    [InlineData("1e308G")]
    public void InvalidEngineeringSuffixesAreRejectedForInitialConditionsAndWaveforms(string text)
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.Capacitor);
        ElectricalComponent capacitor = Assert.Single(viewModel.SelectedElectricalCircuitModel!.Components);
        EditableField initial = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Initial voltage (V)");
        initial.Value = text;
        Assert.False(viewModel.ApplyField(initial));
        Assert.Null(capacitor.InitialValue);

        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.VoltageSource);
        ElectricalComponent source = Assert.Single(viewModel.SelectedElectricalCircuitModel.Components);
        EditableField waveform = viewModel.ElectricalComponentFields.Single(field => field.Label == "Waveform");
        waveform.Value = $"0=0, {text}=5";
        Assert.False(viewModel.ApplyField(waveform));
        Assert.Empty(source.Waveform);
    }

    [Fact]
    public void EditingAValuePreservesAnAuthoredWaveform()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        ElectricalComponent source = Assert.Single(viewModel.SelectedElectricalCircuitModel!.Components);
        source.Waveform.Add(new ElectricalWaveformPoint { Time = 0, Value = 0 });
        source.Waveform.Add(new ElectricalWaveformPoint { Time = 1e-3, Value = 5 });

        EditableField value = viewModel.ElectricalComponentFields
            .Single(field => field.Label.StartsWith("Value (", StringComparison.Ordinal));
        value.Value = "3";

        Assert.True(viewModel.ApplyField(value));
        Assert.Equal(3, source.Value);
        Assert.Equal(2, source.Waveform.Count);
        Assert.Equal(5, source.Waveform[1].Value);
    }

    [Fact]
    public void TransientRunReportsPointsEnergyAndExportsJson()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        ElectricalComponent capacitor =
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = 0;
        circuit.Components.Add(capacitor);

        EditableField method = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Transient method");
        method.Value = "trap";
        Assert.True(viewModel.ApplyField(method));

        Assert.True(viewModel.SolveElectricalTransient());
        Assert.True(viewModel.HasElectricalTransientResult);
        Assert.False(viewModel.HasElectricalTransientError);
        Assert.Equal(201, viewModel.ElectricalTransientResults.Count);
        Assert.Contains("trapezoidal", viewModel.ElectricalTransientSummary);
        Assert.Contains("residual", viewModel.ElectricalTransientEnergyText);
        Assert.Equal(0.005, viewModel.ElectricalTransientResults[^1].Time, 9);

        Assert.True(viewModel.TryBuildElectricalTransientJson(out string json));
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(circuit.Id, document.RootElement.GetProperty("circuit").GetString());
        Assert.True(document.RootElement.GetProperty("completed").GetBoolean());
        Assert.True(
            document.RootElement.GetProperty("energy").GetProperty("deliveredJoules").GetDouble() > 0);

        EditableField steps = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Transient steps");
        steps.Value = "abc";
        Assert.False(viewModel.ApplyField(steps));
        Assert.Contains("whole number", viewModel.StatusText);

        EditableField stop = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Transient stop (s)");
        stop.Value = "0.01";
        Assert.True(viewModel.ApplyField(stop));
        Assert.False(viewModel.HasElectricalTransientResult);
        Assert.False(viewModel.TryBuildElectricalTransientJson(out _));
    }

    [Fact]
    public void TransientRunCanUseAdaptiveControlAndExportCsv()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        ElectricalComponent capacitor =
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = 0;
        circuit.Components.Add(capacitor);

        EditableField tolerance = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Transient tolerance");
        tolerance.Value = "-1";
        Assert.False(viewModel.ApplyField(tolerance));
        Assert.Contains("positive", viewModel.StatusText);

        EditableField adaptive = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Transient tolerance");
        adaptive.Value = "1e-6";
        Assert.True(viewModel.ApplyField(adaptive));
        Assert.True(viewModel.SolveElectricalTransient());
        Assert.Contains("adaptive rtol", viewModel.ElectricalTransientSummary);
        Assert.True(viewModel.ElectricalTransientResults.Count > 2);
        Assert.Equal(0.005, viewModel.ElectricalTransientResults[^1].Time, 9);

        Assert.True(viewModel.TryBuildElectricalTransientCsv(out string csv));
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // The file names the circuit it came from before the table, so a saved export can be
        // traced back to the run that produced it.
        Assert.Equal("# circuit: dc1", lines[0]);
        int header = Array.FindIndex(
            lines, line => line.StartsWith("time_s", StringComparison.Ordinal));
        Assert.Equal("time_s,\"gnd\",\"in\",\"out\"", lines[header]);
        Assert.Equal(viewModel.ElectricalTransientPointCount + 1, lines.Length - header);
        // The table is sampled for the panel; the export always carries every point.
        Assert.True(lines.Length - header > viewModel.ElectricalTransientResults.Count);
        Assert.StartsWith("0,0,5,0", lines[header + 1]);

        EditableField fixedStep = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Transient tolerance");
        fixedStep.Value = string.Empty;
        Assert.True(viewModel.ApplyField(fixedStep));
        Assert.True(viewModel.SolveElectricalTransient());
        Assert.Contains("steps", viewModel.ElectricalTransientSummary);
        Assert.Equal(201, viewModel.ElectricalTransientResults.Count);
    }

    [Fact]
    public void TransientMetricsAndSettlingBandAreEditableInThePanel()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        ElectricalComponent capacitor =
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = 0;
        circuit.Components.Add(capacitor);

        EditableField invalid = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Settling band");
        invalid.Value = "2";
        Assert.False(viewModel.ApplyField(invalid));
        Assert.Contains("between 0 and 1", viewModel.StatusText);

        EditableField loose = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Settling band");
        loose.Value = "0.1";
        Assert.True(viewModel.ApplyField(loose));

        Assert.True(viewModel.SolveElectricalTransient());
        // The capacitor starts at 0 V against a 5 V operating point, and the panel says so
        // instead of leaving the first samples unexplained.
        Assert.Contains("C1", viewModel.ElectricalTransientNotesText, StringComparison.Ordinal);
        Assert.Contains(
            "discontinuity",
            viewModel.ElectricalTransientNotesText,
            StringComparison.Ordinal);

        ElectricalTransientMetricItem output = viewModel.ElectricalTransientMetrics
            .Single(item => item.NodeId == "out");
        Assert.Contains("settled at", output.SettleText);
        Assert.Contains("max", output.ExtremesText);
        Assert.Contains("peak @", output.PeakText);
        Assert.Contains(
            viewModel.ElectricalTransientMetrics,
            item => item.NodeId == "in" && item.SettleText == "settled at 0 s");

        TransientPlot plot = Assert.IsType<TransientPlot>(viewModel.ElectricalTransientPlot);
        Assert.Equal(3, plot.Series.Count);
        Assert.All(plot.Series, series => Assert.True(
            series.Points.Count > 1,
            $"{series.NodeId} needs a drawable polyline"));
        Assert.Equal(0.005, plot.TimeEnd, 12);

        // The SVG export is the shareable form of the same plot.
        Assert.True(viewModel.TryBuildElectricalTransientSvg(out string svg));
        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Equal(3, svg.Split("<polyline").Length - 1);
        // The picture names the circuit it came from, like the CSV export does.
        Assert.Contains("provenance", svg, StringComparison.Ordinal);

        EditableField tighter = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Settling band");
        tighter.Value = "0.02";
        Assert.True(viewModel.ApplyField(tighter));
        Assert.Empty(viewModel.ElectricalTransientMetrics);
        Assert.Equal(string.Empty, viewModel.ElectricalTransientNotesText);
        Assert.Null(viewModel.ElectricalTransientPlot);
        Assert.False(viewModel.TryBuildElectricalTransientSvg(out _));
        Assert.False(viewModel.HasElectricalTransientResult);
    }

    [Fact]
    public void ThePanelChecksTransientAccuracyAndClearsItWithTheRun()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        ElectricalComponent capacitor =
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = 0;
        circuit.Components.Add(capacitor);

        // Nothing has run yet, so there is nothing to refine.
        Assert.False(viewModel.CheckElectricalTransientAccuracy());
        Assert.Contains("Run a transient", viewModel.StatusText);
        Assert.Equal(string.Empty, viewModel.ElectricalTransientAccuracyText);

        Assert.True(viewModel.SolveElectricalTransient());
        Assert.True(viewModel.CheckElectricalTransientAccuracy());
        Assert.Contains("measured order", viewModel.ElectricalTransientAccuracyText);
        Assert.Contains(
            "Worst node out",
            viewModel.ElectricalTransientAccuracyText,
            StringComparison.Ordinal);

        // The per-level table shows where the measured order stops improving.
        Assert.True(viewModel.HasElectricalTransientRefinement);
        Assert.Equal(3, viewModel.ElectricalTransientRefinementLevels.Count);
        ElectricalTransientRefinementItem coarse = viewModel.ElectricalTransientRefinementLevels[0];
        Assert.Equal("200 steps", coarse.StepsText);
        Assert.Equal("reference", coarse.DeviationText);
        Assert.Equal("-", coarse.OrderText);

        ElectricalTransientRefinementItem finest = viewModel.ElectricalTransientRefinementLevels[^1];
        Assert.Equal("800 steps", finest.StepsText);
        Assert.True(double.TryParse(
            finest.OrderText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double measuredOrder));
        Assert.InRange(measuredOrder, 0.5, 1.5);

        // The exported report carries the study, so a script sees what the panel shows.
        Assert.True(viewModel.TryBuildElectricalTransientJson(out string report));
        Assert.Contains("\"refinement\"", report, StringComparison.Ordinal);
        Assert.Contains("\"observedOrder\"", report, StringComparison.Ordinal);

        // The check describes the displayed run, so a settings change invalidates both.
        EditableField band = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Settling band");
        band.Value = "0.1";
        Assert.True(viewModel.ApplyField(band));
        Assert.Equal(string.Empty, viewModel.ElectricalTransientAccuracyText);
        // With the run gone there is nothing to export, so the study cannot leak into a
        // later report either.
        Assert.False(viewModel.TryBuildElectricalTransientJson(out _));
        Assert.Empty(viewModel.ElectricalTransientRefinementLevels);
        Assert.False(viewModel.HasElectricalTransientRefinement);
        Assert.False(viewModel.HasElectricalTransientResult);
    }

    [Fact]
    public void TheAccuracyCheckUsesTheRequestedLevelCount()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        ElectricalComponent capacitor =
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = 0;
        circuit.Components.Add(capacitor);

        Assert.True(viewModel.SolveElectricalTransient());

        // One level cannot imply an order, so it is rejected before any solve.
        viewModel.ElectricalTransientRefineLevelsText = "1";
        Assert.False(viewModel.CheckElectricalTransientAccuracy());
        Assert.Contains("2 to 5", viewModel.StatusText);
        Assert.Empty(viewModel.ElectricalTransientRefinementLevels);

        // Four levels refine 200 steps to 1600 and report one row per level.
        viewModel.ElectricalTransientRefineLevelsText = "4";
        Assert.True(viewModel.CheckElectricalTransientAccuracy());
        Assert.Equal(4, viewModel.ElectricalTransientRefinementLevels.Count);
        Assert.Equal("200 steps", viewModel.ElectricalTransientRefinementLevels[0].StepsText);
        Assert.Equal("1600 steps", viewModel.ElectricalTransientRefinementLevels[^1].StepsText);
        Assert.Equal("reference", viewModel.ElectricalTransientRefinementLevels[0].DeviationText);
    }

    [Fact]
    public void NodesCanBeHiddenFromThePlotWithoutRepaintingTheOthers()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        ElectricalComponent capacitor =
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = 0;
        circuit.Components.Add(capacitor);

        Assert.True(viewModel.SolveElectricalTransient());
        Assert.Equal(3, viewModel.ElectricalTransientSeries.Count);

        ElectricalTransientSeriesItem ground =
            viewModel.ElectricalTransientSeries.Single(item => item.NodeId == "gnd");
        ground.IsVisible = false;

        TransientPlot plot = Assert.IsType<TransientPlot>(viewModel.ElectricalTransientPlot);
        Assert.Equal(2, plot.Series.Count);
        Assert.DoesNotContain(plot.Series, series => series.NodeId == "gnd");
        // The surviving traces keep their palette slots, so hiding one does not repaint the rest.
        Assert.Equal(1, plot.Series.Single(series => series.NodeId == "in").ColorIndex);
        Assert.Equal(2, plot.Series.Single(series => series.NodeId == "out").ColorIndex);

        foreach (ElectricalTransientSeriesItem item in viewModel.ElectricalTransientSeries)
        {
            item.IsVisible = false;
        }

        Assert.Null(viewModel.ElectricalTransientPlot);

        ground.IsVisible = true;
        viewModel.ElectricalTransientSeries.Single(item => item.NodeId == "in").IsVisible = true;
        viewModel.ElectricalTransientSeries.Single(item => item.NodeId == "out").IsVisible = true;
        Assert.Equal(3, Assert.IsType<TransientPlot>(viewModel.ElectricalTransientPlot).Series.Count);

        // A new run starts with every trace shown again.
        Assert.True(viewModel.SolveElectricalTransient());
        Assert.Equal(3, viewModel.ElectricalTransientSeries.Count);
        Assert.All(viewModel.ElectricalTransientSeries, item => Assert.True(item.IsVisible));
    }

    [Fact]
    public void ThePanelProbesNodeVoltagesAtAChosenTime()
    {
        const double supply = 5;
        const double tau = 1_000 * 1e-6;
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, supply, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        ElectricalComponent capacitor =
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = 0;
        circuit.Components.Add(capacitor);

        viewModel.ElectricalTransientProbeInput = "0.0012345";
        Assert.False(viewModel.ProbeElectricalTransient());
        Assert.Contains("Run a transient", viewModel.StatusText);

        Assert.True(viewModel.SolveElectricalTransient());
        Assert.True(viewModel.ProbeElectricalTransient());

        // 200 steps over 0.005 s puts samples at 2.5e-5 s, so this time is interpolated.
        Assert.Contains("interpolated", viewModel.ElectricalTransientProbeText);
        Assert.Equal(0.0012345, viewModel.ElectricalTransientProbeTime!.Value, 15);
        Assert.Equal(3, viewModel.ElectricalTransientProbeValues.Count);

        // The probe reports the solved trace, so it sits within the scheme's own error of
        // the analytic charge 5*(1 - e^(-t/tau)) at this time.
        double analytic = supply * (1 - Math.Exp(-0.0012345 / tau));
        Assert.InRange(viewModel.ElectricalTransientProbeValues["out"], analytic * 0.98, analytic * 1.02);
        Assert.Equal(supply, viewModel.ElectricalTransientProbeValues["in"], 9);

        viewModel.ElectricalTransientProbeInput = "abc";
        Assert.False(viewModel.ProbeElectricalTransient());
        Assert.Contains("number of seconds", viewModel.StatusText);

        viewModel.ElectricalTransientProbeInput = "0.01";
        Assert.False(viewModel.ProbeElectricalTransient());
        Assert.Contains("between", viewModel.StatusText);

        viewModel.ElectricalTransientProbeInput = "0.00125";
        Assert.True(viewModel.ProbeElectricalTransient());
        Assert.Contains("solved point", viewModel.ElectricalTransientProbeText);

        EditableField band = viewModel.ElectricalCircuitFields
            .Single(field => field.Label == "Settling band");
        band.Value = "0.1";
        Assert.True(viewModel.ApplyField(band));
        Assert.Empty(viewModel.ElectricalTransientProbeValues);
        Assert.Null(viewModel.ElectricalTransientProbeTime);
        Assert.Equal(string.Empty, viewModel.ElectricalTransientProbeText);
    }

    [Fact]
    public void SourceWaveformsCanBeAuthoredThroughThePanel()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.VoltageSource);
        ElectricalComponent source = Assert.Single(viewModel.SelectedElectricalCircuitModel!.Components);
        Assert.Equal(ElectricalComponentKind.VoltageSource, source.Kind);

        EditableField empty = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Waveform");
        Assert.Equal(string.Empty, empty.Value);

        empty.Value = "0=0, 0.001=5";
        Assert.True(viewModel.ApplyField(empty));
        Assert.Equal(2, source.Waveform.Count);
        Assert.Equal(0.001, source.Waveform[1].Time);
        Assert.Equal(5, source.Waveform[1].Value);

        EditableField formatted = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Waveform");
        Assert.Equal("0=0, 0.001=5", formatted.Value);

        EditableField decreasing = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Waveform");
        decreasing.Value = "0.002=1, 0.001=2";
        Assert.False(viewModel.ApplyField(decreasing));
        Assert.Equal(2, source.Waveform.Count);
        Assert.Equal(0, source.Waveform[0].Time);

        EditableField single = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Waveform");
        single.Value = "0=0";
        Assert.False(viewModel.ApplyField(single));
        Assert.Equal(2, source.Waveform.Count);

        EditableField garbled = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Waveform");
        garbled.Value = "0=0, nope";
        Assert.False(viewModel.ApplyField(garbled));
        Assert.Equal(2, source.Waveform.Count);

        EditableField cleared = viewModel.ElectricalComponentFields
            .Single(field => field.Label == "Waveform");
        cleared.Value = string.Empty;
        Assert.True(viewModel.ApplyField(cleared));
        Assert.Empty(source.Waveform);

        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.Resistor);
        Assert.DoesNotContain(
            viewModel.ElectricalComponentFields,
            field => field.Label == "Waveform");
    }

    [Fact]
    public void ComponentMetadataAndExplicitTerminalsAreEditable()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();

        ElectricalComponent component = Assert.Single(viewModel.SelectedElectricalCircuitModel!.Components);
        Assert.Equal("r1", component.Id);
        Assert.Equal(ElectricalComponentKind.Resistor, component.Kind);
        Assert.Equal(1_000, component.Value);
        Assert.Equal(2, viewModel.ElectricalTerminals.Count);
        Assert.Equal("gnd", component.FindTerminal("a")!.NodeId);
        Assert.Equal("n1", component.FindTerminal("b")!.NodeId);
        Assert.Equal(ElectricalTerminalPolarity.Passive, component.FindTerminal("a")!.Polarity);
        Assert.Equal(ElectricalTerminalPolarity.Passive, component.FindTerminal("b")!.Polarity);
        Assert.Equal("ohm", viewModel.SelectedElectricalComponentKind!.Unit);

        EditableField value = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));
        value.Value = "2200";
        Assert.True(viewModel.ApplyField(value));
        Assert.Equal(2_200, component.Value);

        EditableField componentName = viewModel.ElectricalComponentFields.Single(field => field.Label == "Name");
        componentName.Value = "R_LOAD";
        Assert.True(viewModel.ApplyField(componentName));
        Assert.Equal("R_LOAD", component.Name);

        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds.Single(choice =>
            choice.Kind == ElectricalComponentKind.VoltageSource);

        Assert.Equal(ElectricalComponentKind.VoltageSource, component.Kind);
        Assert.Equal(2_200, component.Value);
        Assert.Equal("V", viewModel.SelectedElectricalComponentKind!.Unit);
        Assert.Equal(ElectricalTerminalPolarity.Positive, component.FindTerminal("a")!.Polarity);
        Assert.Equal(ElectricalTerminalPolarity.Negative, component.FindTerminal("b")!.Polarity);
        Assert.Equal("gnd", component.FindTerminal("a")!.NodeId);
        Assert.Equal("n1", component.FindTerminal("b")!.NodeId);
        Assert.Equal(2, viewModel.ElectricalTerminals.Count);
    }

    [Theory]
    [InlineData("2.2k", 2_200)]
    [InlineData("4.7M", 4_700_000)]
    [InlineData("3.3meg", 3_300_000)]
    [InlineData("3m", 3e-3)]
    [InlineData("1u", 1e-6)]
    [InlineData("1µ", 1e-6)]
    [InlineData("1n", 1e-9)]
    [InlineData("1G", 1e9)]
    public void ComponentValuesAcceptInvariantSiEngineeringSuffixes(string text, double expected)
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        ElectricalComponent component = viewModel.SelectedElectricalCircuitModel!.Components.Single();
        EditableField value = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));

        value.Value = text;
        Assert.True(viewModel.ApplyField(value));
        Assert.InRange(component.Value, expected * (1 - 1e-12), expected * (1 + 1e-12));
        Assert.Contains("SI suffixes", value.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.2x")]
    [InlineData("1e308G")]
    public void InvalidOrOverflowingEngineeringValuesAreRejected(string text)
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        ElectricalComponent component = viewModel.SelectedElectricalCircuitModel!.Components.Single();
        EditableField value = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));

        value.Value = text;

        Assert.False(viewModel.ApplyField(value));
        Assert.Equal(1_000, component.Value);
        Assert.Contains("SI suffix", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalBindingsDriveValidationAndUndoRedo()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.AddElectricalNode();
        Assert.Equal("n2", viewModel.SelectedElectricalNodeId);

        viewModel.SetElectricalTerminalNode("r1", "b", "n2");
        ElectricalComponent component = viewModel.SelectedElectricalCircuitModel!.FindComponent("r1")!;
        Assert.Equal("n2", component.FindTerminal("b")!.NodeId);
        Assert.Empty(viewModel.ElectricalDiagnostics);

        viewModel.SetElectricalTerminalNode("r1", "a", "n2");
        Assert.Contains(
            viewModel.ElectricalDiagnostics,
            diagnostic => diagnostic.Code == "RAD-ELEC-013");

        viewModel.Undo();
        Assert.Equal("gnd", viewModel.SelectedElectricalCircuitModel!.FindComponent("r1")!
            .FindTerminal("a")!.NodeId);
        Assert.Empty(viewModel.ElectricalDiagnostics);

        viewModel.Redo();
        Assert.Equal("n2", viewModel.SelectedElectricalCircuitModel!.FindComponent("r1")!
            .FindTerminal("a")!.NodeId);
        Assert.Contains(
            viewModel.ElectricalDiagnostics,
            diagnostic => diagnostic.Code == "RAD-ELEC-013");
    }

    [Fact]
    public void ReferencedNodesAndGroundCannotBeRemoved()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();

        viewModel.SelectedElectricalNode = viewModel.ElectricalNodes.Single(node => node.Id == "gnd");
        viewModel.RemoveElectricalNode();
        Assert.Contains("ground", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, viewModel.SelectedElectricalCircuitModel!.Nodes.Count);

        viewModel.SelectedElectricalNode = viewModel.ElectricalNodes.Single(node => node.Id == "n1");
        viewModel.RemoveElectricalNode();
        Assert.Contains("bound", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, viewModel.SelectedElectricalCircuitModel!.Nodes.Count);

        viewModel.AddElectricalNode();
        string freeNode = viewModel.SelectedElectricalNodeId!;
        viewModel.RemoveElectricalNode();
        Assert.DoesNotContain(viewModel.SelectedElectricalCircuitModel!.Nodes, node => node.Id == freeNode);
    }

    [Fact]
    public void AuthoredCircuitsRoundTripWithTheirTerminals()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.AddElectricalNode();
        viewModel.SetElectricalTerminalNode("r1", "b", "n2");

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            Assert.True(viewModel.SaveProject(path));
            Assert.False(viewModel.IsDirty);

            EditorViewModel opened = CreateEmptyEditor();
            Assert.True(opened.LoadProject(path));

            ElectricalCircuit circuit = Assert.Single(opened.Project.ElectricalCircuits);
            Assert.Equal("dc1", circuit.Id);
            ElectricalComponent component = Assert.Single(circuit.Components);
            Assert.Equal("r1", component.Id);
            Assert.Equal("n2", component.FindTerminal("b")!.NodeId);
            Assert.NotNull(opened.SelectedElectricalCircuit);
            Assert.Empty(opened.ElectricalDiagnostics);
            Assert.False(opened.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SolveShowsNodeVoltagesCurrentsAndPower()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SetElectricalTerminalNode("r1", "a", "n1");
        viewModel.SetElectricalTerminalNode("r1", "b", "gnd");
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds.Single(choice =>
            choice.Kind == ElectricalComponentKind.VoltageSource);
        EditableField voltage = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));
        voltage.Value = "5";
        Assert.True(viewModel.ApplyField(voltage));

        viewModel.AddElectricalComponent();
        Assert.Equal("r2", viewModel.SelectedElectricalComponentId);
        Assert.True(viewModel.SolveElectricalCircuit());

        Assert.True(viewModel.HasElectricalSolveResult);
        Assert.False(viewModel.HasElectricalSolveError);
        Assert.Equal("5 V", viewModel.ElectricalNodeResults.Single(node => node.Id == "n1").VoltageText);
        Assert.Equal("0 V", viewModel.ElectricalNodeResults.Single(node => node.Id == "gnd").VoltageText);

        ElectricalElementResultItem source = viewModel.ElectricalElementResults.Single(element => element.Id == "r1");
        Assert.Equal("5 V", source.VoltageText);
        Assert.Equal("-0.005 A", source.CurrentText);
        Assert.Equal("-0.025 W", source.PowerText);

        ElectricalElementResultItem load = viewModel.ElectricalElementResults.Single(element => element.Id == "r2");
        Assert.Equal("-0.005 A", load.CurrentText);
        Assert.Equal("0.025 W", load.PowerText);
        Assert.Contains("Solved 'dc1'", viewModel.ElectricalSolveSummary);
    }

    [Fact]
    public void SolveExportsDisplayedValuesAsJsonAndCsv()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        Assert.False(viewModel.TryBuildElectricalSolveJson(out _));
        Assert.False(viewModel.TryBuildElectricalSolveCsv(out _));

        viewModel.SetElectricalTerminalNode("r1", "a", "n1");
        viewModel.SetElectricalTerminalNode("r1", "b", "gnd");
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds.Single(choice =>
            choice.Kind == ElectricalComponentKind.VoltageSource);
        EditableField voltage = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));
        voltage.Value = "5";
        Assert.True(viewModel.ApplyField(voltage));
        viewModel.AddElectricalComponent();
        Assert.Equal("r2", viewModel.SelectedElectricalComponentId);
        Assert.True(viewModel.SolveElectricalCircuit());

        Assert.True(viewModel.TryBuildElectricalSolveJson(out string json));
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            JsonElement root = document.RootElement;
            Assert.Equal("radix-dc-solve", root.GetProperty("format").GetString());
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal("dc1", root.GetProperty("circuit").GetString());
            Assert.Equal("gnd", root.GetProperty("ground").GetString());

            JsonElement n1 = root.GetProperty("nodes").EnumerateArray()
                .Single(node => node.GetProperty("id").GetString() == "n1");
            Assert.True(n1.GetProperty("solved").GetBoolean());
            Assert.Equal(5, n1.GetProperty("volts").GetDouble(), 12);

            JsonElement source = root.GetProperty("elements").EnumerateArray()
                .Single(element => element.GetProperty("id").GetString() == "r1");
            Assert.Equal("voltage-source", source.GetProperty("kind").GetString());
            Assert.Equal("n1", source.GetProperty("nodeA").GetString());
            Assert.Equal("gnd", source.GetProperty("nodeB").GetString());
            Assert.Equal(5, source.GetProperty("value").GetDouble(), 12);
            Assert.Equal("V", source.GetProperty("unit").GetString());
            Assert.Equal(5, source.GetProperty("volts").GetDouble(), 12);
            Assert.Equal(-0.005, source.GetProperty("amperes").GetDouble(), 12);
            Assert.Equal(-0.025, source.GetProperty("absorbedPowerWatts").GetDouble(), 12);
        }

        Assert.True(viewModel.TryBuildElectricalSolveCsv(out string csv));
        Assert.DoesNotContain('\r', csv);
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Equal(
            "circuit,type,id,name,kind,node_a,node_b,value,unit,volts,amperes,absorbed_power_watts,solved",
            lines[0]);
        Assert.Contains(lines, line => line.Contains("\"dc1\",node,\"n1\",\"Node 1\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.EndsWith(",5,\"\",\"\",true", StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains(
                ",element,\"r1\",\"R1\",\"voltage-source\",\"n1\",\"gnd\",5,\"V\",5,-0.005,-0.025,true",
                StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains(
                ",element,\"r2\",\"R2\",\"resistor\",\"gnd\",\"n1\",1000,\"ohm\",-5,-0.005,0.025,true",
                StringComparison.Ordinal));

        voltage = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));
        voltage.Value = "2000";
        Assert.True(viewModel.ApplyField(voltage));
        Assert.False(viewModel.HasElectricalSolveResult);
        Assert.False(viewModel.TryBuildElectricalSolveJson(out _));
        Assert.False(viewModel.TryBuildElectricalSolveCsv(out _));
    }

    [Fact]
    public void SolveReportsStructuralAndSingularFailuresAndClearsOnEdit()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SetElectricalTerminalNode("r1", "b", "gnd");

        Assert.False(viewModel.SolveElectricalCircuit());
        Assert.True(viewModel.HasElectricalSolveError);
        Assert.False(viewModel.HasElectricalSolveResult);
        Assert.Contains("structural", viewModel.ElectricalSolveSummary, StringComparison.OrdinalIgnoreCase);

        viewModel.SetElectricalTerminalNode("r1", "b", "n1");
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds.Single(choice =>
            choice.Kind == ElectricalComponentKind.CurrentSource);
        Assert.False(viewModel.SolveElectricalCircuit());
        Assert.True(viewModel.HasElectricalSolveError);
        Assert.Contains("singular", viewModel.ElectricalSolveSummary, StringComparison.OrdinalIgnoreCase);

        viewModel.AddElectricalComponent();
        Assert.True(viewModel.SolveElectricalCircuit());
        Assert.True(viewModel.HasElectricalSolveResult);
        EditableField value = viewModel.ElectricalComponentFields.Single(field =>
            field.Label.StartsWith("Value", StringComparison.Ordinal));
        value.Value = "2000";
        Assert.True(viewModel.ApplyField(value));
        Assert.False(viewModel.HasElectricalSolveResult);
        Assert.Empty(viewModel.ElectricalNodeResults);
        Assert.Empty(viewModel.ElectricalElementResults);
    }

    [Fact]
    public void ThePanelSweepsOneElementAndListsEachNodeMetric()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        circuit.Components.Add(
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd"));
        Assert.True(viewModel.SolveElectricalTransient());

        viewModel.ElectricalSweepElementText = "R1";
        viewModel.ElectricalSweepValuesText = "500,1000,2000";

        Assert.True(viewModel.RunElectricalSweep());
        Assert.True(viewModel.HasElectricalSweepResult);
        // Two measured nodes per value: ground is the reference, not a row.
        Assert.Equal(6, viewModel.ElectricalSweepRows.Count);
        Assert.Contains("R1", viewModel.ElectricalSweepSummary, StringComparison.Ordinal);
        Assert.All(viewModel.ElectricalSweepRows, row =>
            Assert.DoesNotContain("failed", row.NodeId, StringComparison.Ordinal));
        // The value is printed once per block, not on every node row.
        Assert.Equal("500", viewModel.ElectricalSweepRows[0].ValueText);
        Assert.Equal(string.Empty, viewModel.ElectricalSweepRows[1].ValueText);
    }

    [Fact]
    public void TheSweepExplainsAnUnknownElementOrUnusableValues()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        circuit.Components.Add(
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd"));
        Assert.True(viewModel.SolveElectricalTransient());

        viewModel.ElectricalSweepElementText = "R9";
        viewModel.ElectricalSweepValuesText = "500,1000";
        Assert.False(viewModel.RunElectricalSweep());
        Assert.Empty(viewModel.ElectricalSweepRows);
        Assert.Contains("R9", viewModel.ElectricalSweepSummary, StringComparison.Ordinal);

        viewModel.ElectricalSweepElementText = "R1";
        viewModel.ElectricalSweepValuesText = "500";
        Assert.False(viewModel.RunElectricalSweep());
        Assert.Contains("800:1200:5", viewModel.ElectricalSweepSummary, StringComparison.Ordinal);

        viewModel.ElectricalSweepValuesText = "500,abc";
        Assert.False(viewModel.RunElectricalSweep());
        Assert.Contains("abc", viewModel.ElectricalSweepSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditClearsTheSweepWithTheRunItCameFrom()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        circuit.Components.Add(
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd"));
        Assert.True(viewModel.SolveElectricalTransient());
        viewModel.ElectricalSweepElementText = "R1";
        viewModel.ElectricalSweepValuesText = "500,1000";
        Assert.True(viewModel.RunElectricalSweep());
        Assert.NotEmpty(viewModel.ElectricalSweepRows);

        // The stop time is part of the run the sweep reused, so changing it drops both.
        EditableField stop = viewModel.ElectricalCircuitFields
            .Single(field => field.Label.StartsWith("Transient stop", StringComparison.Ordinal));
        stop.Value = "0.02";
        Assert.True(viewModel.ApplyField(stop));

        Assert.Empty(viewModel.ElectricalSweepRows);
        Assert.False(viewModel.HasElectricalSweepResult);
        Assert.False(viewModel.HasElectricalTransientResult);
    }

    [Fact]
    public void ThePanelExportsItsSweepWithTheSharedWriters()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        circuit.Components.Add(
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd"));
        Assert.True(viewModel.SolveElectricalTransient());
        viewModel.ElectricalSweepElementText = "R1";
        viewModel.ElectricalSweepValuesText = "500,1000";
        Assert.True(viewModel.RunElectricalSweep());

        Assert.True(viewModel.TryBuildElectricalSweepCsv(out string csv));
        Assert.Contains("# format: radix-transient-sweep", csv, StringComparison.Ordinal);
        Assert.Contains("# element: R1", csv, StringComparison.Ordinal);
        Assert.Contains("# values: 2", csv, StringComparison.Ordinal);
        // The export is complete: two values times three nodes, ground included.
        Assert.Contains("node,peak_v,final_v,settle_s,energy_residual", csv, StringComparison.Ordinal);

        Assert.True(viewModel.TryBuildElectricalSweepJson(out string json));
        using JsonDocument report = JsonDocument.Parse(json);
        Assert.Equal("radix-transient-sweep", report.RootElement.GetProperty("format").GetString());
        Assert.Equal("R1", report.RootElement.GetProperty("element").GetString());
        Assert.Equal(circuit.Id, report.RootElement.GetProperty("circuit").GetString());
        Assert.Equal(2, report.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void SweepExportIsUnavailableUntilAStudyExists()
    {
        EditorViewModel viewModel = CreateEmptyEditor();

        Assert.False(viewModel.TryBuildElectricalSweepCsv(out string csv));
        Assert.False(viewModel.TryBuildElectricalSweepJson(out string json));
        Assert.Empty(csv);
        Assert.Empty(json);
        // A sweep without a run explains the prerequisite instead of failing silently.
        Assert.False(viewModel.RunElectricalSweep());
        Assert.Contains("Run a transient", viewModel.ElectricalSweepSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSweepExportDisappearsWithAnInvalidatingEdit()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        circuit.Components.Add(
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd"));
        Assert.True(viewModel.SolveElectricalTransient());
        viewModel.ElectricalSweepElementText = "R1";
        viewModel.ElectricalSweepValuesText = "500,1000";
        Assert.True(viewModel.RunElectricalSweep());
        Assert.True(viewModel.TryBuildElectricalSweepCsv(out _));

        EditableField stop = viewModel.ElectricalCircuitFields
            .Single(field => field.Label.StartsWith("Transient stop", StringComparison.Ordinal));
        stop.Value = "0.02";
        Assert.True(viewModel.ApplyField(stop));

        // No study, no export: the file would describe a circuit the panel no longer shows.
        Assert.False(viewModel.TryBuildElectricalSweepCsv(out _));
        Assert.False(viewModel.TryBuildElectricalSweepJson(out _));
    }

    [Fact]
    public void ThePanelComparesItsSweepWithAnArchivedStudy()
    {
        EditorViewModel viewModel = RcSweepEditor(out _);
        Assert.True(viewModel.TryBuildElectricalSweepJson(out string archived));

        Assert.True(viewModel.CompareElectricalSweepWithArchived(archived, "base.json"));
        Assert.Contains("Identical", viewModel.ElectricalSweepComparisonSummary, StringComparison.Ordinal);
        Assert.Empty(viewModel.ElectricalSweepDifferences);
        Assert.False(viewModel.HasElectricalSweepComparison);

        // A study over a different set of values is a different study, and the panel says which
        // values each side holds. (Physics changes are covered by the Core comparison tests; at a
        // 250 us step a doubled capacitance leaves the node metrics bit-identical here.)
        viewModel.ElectricalSweepValuesText = "500,1500";
        Assert.True(viewModel.RunElectricalSweep());

        Assert.True(viewModel.CompareElectricalSweepWithArchived(archived, "base.json"));
        Assert.True(viewModel.HasElectricalSweepComparison);
        Assert.Contains("difference", viewModel.ElectricalSweepComparisonSummary, StringComparison.Ordinal);
        Assert.Contains(
            viewModel.ElectricalSweepDifferences,
            row => row.Metric == "removed" && row.ValueText == "1000");
        Assert.Contains(
            viewModel.ElectricalSweepDifferences,
            row => row.Metric == "added" && row.ValueText == "1500");
    }

    [Fact]
    public void AComparisonRefusesAForeignReportOrADifferentElement()
    {
        EditorViewModel viewModel = RcSweepEditor(out _);
        Assert.True(viewModel.TryBuildElectricalSweepJson(out string archived));

        Assert.False(viewModel.CompareElectricalSweepWithArchived(
            "{\"format\":\"radix-transient-netlist\",\"version\":1}"));
        Assert.Contains("radix-transient-sweep", viewModel.ElectricalSweepComparisonSummary, StringComparison.Ordinal);

        string otherElement = archived.Replace(
            "\"element\": \"R1\"", "\"element\": \"C9\"", StringComparison.Ordinal);
        Assert.NotEqual(archived, otherElement);
        Assert.False(viewModel.CompareElectricalSweepWithArchived(otherElement));
        Assert.Contains("C9", viewModel.ElectricalSweepComparisonSummary, StringComparison.Ordinal);
        Assert.Empty(viewModel.ElectricalSweepDifferences);

        // An invalid tolerance is refused before any report is read.
        viewModel.ElectricalSweepToleranceText = "2";
        Assert.False(viewModel.CompareElectricalSweepWithArchived(archived));
        Assert.Contains("below 1", viewModel.ElectricalSweepComparisonSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditClearsTheComparisonWithTheStudy()
    {
        EditorViewModel viewModel = RcSweepEditor(out _);
        Assert.True(viewModel.TryBuildElectricalSweepJson(out string archived));
        viewModel.ElectricalSweepValuesText = "500,1500";
        Assert.True(viewModel.RunElectricalSweep());
        Assert.True(viewModel.CompareElectricalSweepWithArchived(archived));
        Assert.True(viewModel.HasElectricalSweepComparison);

        EditableField stop = viewModel.ElectricalCircuitFields
            .Single(field => field.Label.StartsWith("Transient stop", StringComparison.Ordinal));
        stop.Value = "0.02";
        Assert.True(viewModel.ApplyField(stop));

        Assert.Empty(viewModel.ElectricalSweepDifferences);
        Assert.False(viewModel.HasElectricalSweepComparison);
        Assert.Empty(viewModel.ElectricalSweepComparisonSummary);
    }

    [Fact]
    public void ThePanelExportsItsComparison()
    {
        EditorViewModel viewModel = RcSweepEditor(out _);
        Assert.False(viewModel.TryBuildElectricalSweepComparisonCsv(out _));
        Assert.False(viewModel.TryBuildElectricalSweepComparisonJson(out _));

        Assert.True(viewModel.TryBuildElectricalSweepJson(out string archived));
        viewModel.ElectricalSweepValuesText = "500,1500";
        Assert.True(viewModel.RunElectricalSweep());
        Assert.True(viewModel.CompareElectricalSweepWithArchived(archived, "base.json"));

        Assert.True(viewModel.TryBuildElectricalSweepComparisonCsv(out string csv));
        Assert.Contains("# format: radix-transient-sweep-comparison", csv, StringComparison.Ordinal);
        Assert.Contains("# element: R1", csv, StringComparison.Ordinal);
        Assert.Contains("# against: base.json", csv, StringComparison.Ordinal);
        Assert.Contains("value,node,metric,before,after", csv, StringComparison.Ordinal);

        Assert.True(viewModel.TryBuildElectricalSweepComparisonJson(out string json));
        using JsonDocument report = JsonDocument.Parse(json);
        Assert.Equal("radix-transient-sweep-comparison", report.RootElement.GetProperty("format").GetString());
        Assert.Equal("base.json", report.RootElement.GetProperty("against").GetString());
        Assert.False(report.RootElement.GetProperty("identical").GetBoolean());
    }

    [Fact]
    public void TheComparisonExportDisappearsWithTheStudy()
    {
        EditorViewModel viewModel = RcSweepEditor(out _);
        Assert.True(viewModel.TryBuildElectricalSweepJson(out string archived));
        viewModel.ElectricalSweepValuesText = "500,1500";
        Assert.True(viewModel.RunElectricalSweep());
        Assert.True(viewModel.CompareElectricalSweepWithArchived(archived, "base.json"));
        Assert.True(viewModel.TryBuildElectricalSweepComparisonCsv(out _));

        EditableField stop = viewModel.ElectricalCircuitFields
            .Single(field => field.Label.StartsWith("Transient stop", StringComparison.Ordinal));
        stop.Value = "0.02";
        Assert.True(viewModel.ApplyField(stop));

        // The study the diff described is gone, so the diff must not be exportable either.
        Assert.False(viewModel.TryBuildElectricalSweepComparisonCsv(out _));
        Assert.False(viewModel.TryBuildElectricalSweepComparisonJson(out _));
    }

    [Fact]
    public void MultipleCircuitsKeepIndependentSelectionsAndUndoRemoval()
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.AddElectricalCircuit();

        Assert.Equal("dc2", viewModel.SelectedElectricalCircuitId);
        Assert.Null(viewModel.SelectedElectricalComponent);
        Assert.Equal(2, viewModel.ElectricalCircuits.Count);

        viewModel.SelectedElectricalCircuit = viewModel.ElectricalCircuits.Single(item => item.Id == "dc1");
        Assert.Equal("dc1", viewModel.SelectedElectricalCircuitId);
        Assert.Single(viewModel.ElectricalComponents);
        viewModel.SelectedElectricalComponent = viewModel.ElectricalComponents.Single();
        Assert.Equal(2, viewModel.ElectricalTerminals.Count);

        viewModel.RemoveElectricalCircuit();
        Assert.Single(viewModel.ElectricalCircuits);
        Assert.Equal("dc2", viewModel.SelectedElectricalCircuitId);

        viewModel.Undo();
        Assert.Equal(2, viewModel.ElectricalCircuits.Count);
        Assert.NotNull(viewModel.Project.FindElectricalCircuit("dc1"));
    }

    /// <summary>A run RC circuit with a two-value sweep of R1 already taken.</summary>
    private static EditorViewModel RcSweepEditor(out ElectricalCircuit circuit)
    {
        EditorViewModel viewModel = CreateEmptyEditor();
        viewModel.AddElectricalCircuit();
        circuit = viewModel.SelectedElectricalCircuitModel!;
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Clear();
        circuit.Components.Add(
            ElectricalTestComponent("V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd"));
        circuit.Components.Add(
            ElectricalTestComponent("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        circuit.Components.Add(
            ElectricalTestComponent("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd"));
        // Long enough for the RC to settle; at the 5 ms default nothing settles, so a comparison
        // would only see the energy ledger move and the test would not exercise the metrics.
        EditableField stop = viewModel.ElectricalCircuitFields
            .Single(field => field.Label.StartsWith("Transient stop", StringComparison.Ordinal));
        stop.Value = "0.05";
        Assert.True(viewModel.ApplyField(stop));
        Assert.True(viewModel.SolveElectricalTransient());
        viewModel.ElectricalSweepElementText = "R1";
        viewModel.ElectricalSweepValuesText = "500,1000";
        Assert.True(viewModel.RunElectricalSweep());
        return viewModel;
    }

    private static ElectricalComponent ElectricalTestComponent(
        string id,
        ElectricalComponentKind kind,
        double value,
        string nodeA,
        string nodeB)
    {
        var component = new ElectricalComponent { Id = id, Name = id, Kind = kind, Value = value };
        bool source = !ElectricalComponentMetadata.HasPassiveTerminals(kind);
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "a",
            Name = source ? "Positive" : "A",
            Polarity = source ? ElectricalTerminalPolarity.Positive : ElectricalTerminalPolarity.Passive,
            NodeId = nodeA,
        });
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "b",
            Name = source ? "Negative" : "B",
            Polarity = source ? ElectricalTerminalPolarity.Negative : ElectricalTerminalPolarity.Passive,
            NodeId = nodeB,
        });
        return component;
    }

    private static EditorViewModel CreateEmptyEditor()
    {
        var project = CircuitProject.CreateEmpty("Electrical editor test");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);
        return new EditorViewModel(project);
    }
}
