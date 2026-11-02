using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Radix.Editor.Rendering;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The panel can solve an operating point and sweep one parameter, but a gain and a corner are
/// frequency-domain facts. These tests drive the AC sweep through the command the button calls, so a
/// panel that cannot measure a response fails here rather than being missing.
/// </summary>
public sealed class ElectricalAcSweepTests
{
    private static string ExamplePath(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "examples", name);
    }

    /// <summary>The ternary inverter example, which is the circuit these panels exist to inspect.</summary>
    private static EditorViewModel TernaryInverter()
    {
        EditorViewModel viewModel = new(
            ProjectSerializer.Load(ExamplePath("transient-ternary-inverter.radix"), validate: false));

        Assert.True(viewModel.HasSelectedElectricalCircuit);
        return viewModel;
    }

    [Fact]
    public void ASweepMeasuresTheGateGainAndItsCorner()
    {
        EditorViewModel viewModel = TernaryInverter();

        // Biased on the lower transition, where the gate amplifies: 0 V is off, 2.5 V is the plateau.
        viewModel.ElectricalAcSweepDriveText = "VIN";
        viewModel.ElectricalAcSweepBiasText = "VIN=1.3";
        viewModel.ElectricalAcSweepRangeText = "1e3:1e7:9";

        Assert.True(viewModel.RunElectricalAcSweep());
        Assert.True(viewModel.HasElectricalAcSweepResult);
        Assert.True(viewModel.HasElectricalAcSweepPlot);

        // The gate inverts and amplifies, so its gain is tens of decibels and its phase is near 180.
        ElectricalAcSweepRowItem first = viewModel.ElectricalAcSweepRows.First(row => row.NodeId == "out");
        Assert.EndsWith("dB", first.GainText, StringComparison.Ordinal);
        double gain = double.Parse(
            first.GainText[..^3], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(gain, 20, 40);
        Assert.EndsWith("°", first.PhaseText, StringComparison.Ordinal);

        Assert.Contains("out", viewModel.ElectricalAcSweepSummary, StringComparison.Ordinal);
        Assert.Contains("dB", viewModel.ElectricalAcSweepSummary, StringComparison.Ordinal);
        Assert.Contains("3 dB down by", viewModel.ElectricalAcSweepSummary, StringComparison.Ordinal);

        // Every frequency carries a row, and the frequency prints once per block: one block per
        // solved frequency, each holding one row per node.
        int nodes = viewModel.SelectedElectricalCircuitModel!.Nodes
            .Count(node => !string.Equals(node.Id, "gnd", StringComparison.Ordinal));
        Assert.Equal(9 * nodes, viewModel.ElectricalAcSweepRows.Count);
        Assert.Equal(9, viewModel.ElectricalAcSweepRows.Count(row => row.FrequencyText.Length > 0));
    }

    [Fact]
    public void TheCurvePutsLogFrequencyOnTheHorizontalAxis()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.ElectricalAcSweepDriveText = "VIN";
        viewModel.ElectricalAcSweepBiasText = "VIN=1.3";
        viewModel.ElectricalAcSweepRangeText = "1e3:1e7:9";
        Assert.True(viewModel.RunElectricalAcSweep());

        TransientPlot plot = Assert.IsType<TransientPlot>(viewModel.ElectricalAcSweepPlot);
        Assert.Equal(3, plot.TimeStart, 9);
        Assert.Equal(7, plot.TimeEnd, 9);

        TransientPlotSeries output = plot.Series.Single(series => series.NodeId == "out");
        // Nine log-spaced points from 1 kHz to 10 MHz, one decade per step.
        Assert.Equal(9, output.Points.Count);
        Assert.Equal(3, output.Points[0].Time, 9);
        Assert.Equal(7, output.Points[^1].Time, 9);
        // The first sample is the gain the summary names.
        Assert.InRange(output.Points[0].Value, 20, 40);

        // The axis plots exponents but reads in decades, so a reader never sees "3" for 1 kHz.
        Assert.Equal([3d, 4d, 5d, 6d, 7d], plot.TimeTicks);
        Assert.Equal(
            ["1k", "10k", "100k", "1M", "10M"],
            Enumerable.Range(0, plot.TimeTicks.Count)
                .Select(plot.TimeTickLabel)
                .ToArray());
        Assert.Equal("Hz", plot.HorizontalUnit);
        Assert.Equal("dB", plot.VerticalUnit);
    }

    [Fact]
    public void ThePhaseIsDrawnOnItsOwnAxis()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.ElectricalAcSweepDriveText = "VIN";
        viewModel.ElectricalAcSweepBiasText = "VIN=1.3";
        viewModel.ElectricalAcSweepRangeText = "1e3:1e7:9";
        Assert.True(viewModel.RunElectricalAcSweep());

        // Degrees and decibels share no scale, so the phase gets its own geometry and its own
        // unit rather than being squeezed onto the gain curve's axis.
        TransientPlot phase = Assert.IsType<TransientPlot>(viewModel.ElectricalAcSweepPhasePlot);
        Assert.Equal("°", phase.VerticalUnit);
        Assert.Equal("Hz", phase.HorizontalUnit);

        TransientPlotSeries output = phase.Series.Single(series => series.NodeId == "out");
        Assert.Equal(9, output.Points.Count);
        // The gate inverts at low frequency and rolls off towards zero phase at high frequency.
        Assert.InRange(output.Points[0].Value, 170, 190);
        Assert.InRange(output.Points[^1].Value, 0, 120);
    }

    [Fact]
    public void AxisTicksAndLabelsMustPairUp()
    {
        var series = new[]
        {
            new TransientPlotSeries("out", [new TransientPlotPoint(0, 1), new TransientPlotPoint(1, 2)]),
        };

        // A tick without a label would render a number where a decade belongs, so the model
        // refuses the pair instead of silently formatting the wrong axis.
        ArgumentException exception = Assert.Throws<ArgumentException>(() => TransientPlot.Create(
            series,
            TransientPlot.DefaultMaximumPointsPerSeries,
            [3, 4, 5],
            ["1k", "10k"],
            "Hz",
            "dB"));

        Assert.Contains("3 ticks", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2 labels", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSweepCsvUsesTheSameWriterAsTheCommandLine()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.ElectricalAcSweepDriveText = "VIN";
        viewModel.ElectricalAcSweepBiasText = "VIN=1.3";
        viewModel.ElectricalAcSweepRangeText = "1e3:1e7:5";
        Assert.True(viewModel.RunElectricalAcSweep());

        Assert.True(viewModel.TryBuildElectricalAcSweepCsv(out string csv));
        Assert.Contains("# format: radix-ac-report", csv, StringComparison.Ordinal);
        Assert.Contains("# drive: VIN", csv, StringComparison.Ordinal);
        string[] rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("hz,node,gain_db,phase_deg", rows);

        // One row per node per frequency, with the node in the second column and a numeric gain.
        IEnumerable<string[]> data = rows
            .Where(row => !row.StartsWith('#') && !row.StartsWith("hz,"))
            .Select(row => row.Split(','));
        Assert.Equal(5 * viewModel.SelectedElectricalCircuitModel!.Nodes.Count(node => node.Id != "gnd"), data.Count());
        Assert.All(data, cells => Assert.True(double.TryParse(
            cells[2], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _)));
        Assert.Contains(data, cells => cells[1] == "out");
    }

    [Fact]
    public void TheResponseSvgCarriesBothCurvesAndDecadeLabels()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.ElectricalAcSweepDriveText = "VIN";
        viewModel.ElectricalAcSweepBiasText = "VIN=1.3";
        viewModel.ElectricalAcSweepRangeText = "1e3:1e7:9";
        Assert.True(viewModel.RunElectricalAcSweep());

        Assert.True(viewModel.TryBuildElectricalAcSweepSvg(out string svg));

        // One document, two panels: the phase is translated below the gain instead of being a
        // second file that has to be kept in step with the first.
        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("translate(0,210)", svg, StringComparison.Ordinal);
        Assert.Contains("AC analysis", svg, StringComparison.Ordinal);

        // The exported axis says decades and units, not the exponents it plots.
        Assert.Contains(">1k<", svg, StringComparison.Ordinal);
        Assert.Contains(">10M<", svg, StringComparison.Ordinal);
        Assert.Contains(">dB<", svg, StringComparison.Ordinal);
        Assert.Contains(">°<", svg, StringComparison.Ordinal);
        Assert.Contains(">Hz<", svg, StringComparison.Ordinal);
        Assert.DoesNotContain(">3<", svg, StringComparison.Ordinal);

        // Every responding node is drawn twice: once as gain, once as phase.
        Assert.True(
            System.Text.RegularExpressions.Regex.Matches(svg, "<polyline").Count >= 4,
            "both panels should carry curves");
        Assert.Contains("stroke-linejoin=\"round\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void DrivingSomethingThatIsNotASourceIsRejected()
    {
        EditorViewModel viewModel = TernaryInverter();
        string transistor = viewModel.SelectedElectricalCircuitModel!.Components
            .First(component => component.Kind != ElectricalComponentKind.VoltageSource).Id;
        viewModel.ElectricalAcSweepDriveText = transistor;
        viewModel.ElectricalAcSweepRangeText = "1e3:1e7:5";

        // A transfer function is measured from a source, so another component is refused by name
        // rather than producing a plausible-looking curve driven from nothing.
        Assert.False(viewModel.RunElectricalAcSweep());
        Assert.False(viewModel.HasElectricalAcSweepResult);
        Assert.Contains(transistor, viewModel.ElectricalAcSweepSummary, StringComparison.Ordinal);
        Assert.Contains("voltage source", viewModel.ElectricalAcSweepSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedRangeIsRejectedWithoutThrowing()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.ElectricalAcSweepDriveText = "VIN";
        viewModel.ElectricalAcSweepRangeText = "10:1";

        Assert.False(viewModel.RunElectricalAcSweep());
        Assert.False(viewModel.HasElectricalAcSweepResult);
        Assert.Contains("from:to:points", viewModel.ElectricalAcSweepSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingTheCircuitDropsTheResponse()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.ElectricalAcSweepDriveText = "VIN";
        viewModel.ElectricalAcSweepRangeText = "1e3:1e7:5";
        Assert.True(viewModel.RunElectricalAcSweep());

        viewModel.AddElectricalComponent();

        // A response describing an older circuit would be worse than none at all.
        Assert.False(viewModel.HasElectricalAcSweepResult);
        Assert.False(viewModel.HasElectricalAcSweepPlot);
        Assert.False(viewModel.HasElectricalAcSweepPhasePlot);
        Assert.False(viewModel.TryBuildElectricalAcSweepCsv(out _));
        Assert.False(viewModel.TryBuildElectricalAcSweepSvg(out _));
    }
}
