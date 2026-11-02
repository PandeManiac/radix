using Radix.Core.Serialization;
using Radix.Editor.Rendering;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The comparison is an inspection tool, so what matters is that it plots two runs of the same node,
/// that the runs really differ, and that it leaves the circuit saying what it said before. All three
/// are checked without a window, through the inspector fields the panel edits.
/// </summary>
public sealed class ElectricalCapacitanceComparisonTests
{
    private static EditorViewModel Load(string file)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return new EditorViewModel(ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", file),
            validate: false));
    }

    private static void SetField(EditorViewModel viewModel, string label, string value)
    {
        EditableField? field = Assert.Single(
            viewModel.ElectricalCircuitFields,
            candidate => candidate.Label == label);
        Assert.NotNull(field);
        Assert.NotNull(field!.Apply);
        Assert.True(field.Apply!(value), $"applying '{value}' to '{label}' was refused");
    }

    private static EditorViewModel WithRunSettings(string file)
    {
        EditorViewModel viewModel = Load(file);
        // Only the fields that differ from the panel's defaults: a setter rightly refuses to
        // record a value it already holds, and the default method is already backward-euler.
        SetField(viewModel, "Transient stop (s)", "120e-9");
        SetField(viewModel, "Transient steps", "1280");
        return viewModel;
    }

    [Fact]
    public void TheComparisonPlotsBothModelsOnTheNodeTheySeparate()
    {
        EditorViewModel viewModel = WithRunSettings("transient-ternary-inverter.radix");

        Assert.True(viewModel.CompareElectricalCapacitanceModels());

        Assert.NotNull(viewModel.ElectricalTransientPlot);
        Assert.Equal(2, viewModel.ElectricalTransientPlot!.Series.Count);
        Assert.Contains(viewModel.ElectricalTransientPlot.Series, series =>
            series.NodeId.EndsWith("authored C", StringComparison.Ordinal));
        Assert.Contains(viewModel.ElectricalTransientPlot.Series, series =>
            series.NodeId.EndsWith("bias-dependent C", StringComparison.Ordinal));

        // Both curves cover the same span: they were integrated on one grid.
        TransientPlotSeries authored = viewModel.ElectricalTransientPlot.Series[0];
        TransientPlotSeries dependent = viewModel.ElectricalTransientPlot.Series[1];
        Assert.Equal(authored.Points.Count, dependent.Points.Count);
        Assert.Equal(authored.Points[0].Time, dependent.Points[0].Time);
        Assert.Equal(authored.Points[^1].Time, dependent.Points[^1].Time);

        // Which node separates most is a property of the circuit, so it is not pinned here; what
        // must hold is that the text names the node the plot shows and the curves really separate
        // - two identical traces would make the tool look like it worked.
        string text = viewModel.ElectricalTransientComparisonText;
        string node = text[(text.IndexOf('\'') + 1)..text.IndexOf('\'', text.IndexOf('\'') + 1)];
        Assert.Contains("separates the two models most", text, StringComparison.Ordinal);
        Assert.Contains("V apart", text, StringComparison.Ordinal);
        Assert.Equal($"{node} · authored C", authored.NodeId);
        Assert.Equal($"{node} · bias-dependent C", dependent.NodeId);
        double largest = Math.Max(
            authored.Points.Zip(dependent.Points, (left, right) => Math.Abs(left.Value - right.Value))
                .Max(),
            0);
        Assert.True(largest > 1e-3, $"largest separation {largest:G3} V");
    }

    [Fact]
    public void TheComparisonRestoresTheAuthoredFlagsSoItCanBeTakenTwice()
    {
        EditorViewModel viewModel = WithRunSettings("transient-ternary-inverter-bias-dependent.radix");

        Assert.True(viewModel.CompareElectricalCapacitanceModels());
        string first = viewModel.ElectricalTransientComparisonText;
        Assert.True(viewModel.CompareElectricalCapacitanceModels());

        // The file asks for the model on every device. If the comparison left that forced true,
        // the second run would compare the model against itself and the difference would collapse.
        Assert.Equal(first, viewModel.ElectricalTransientComparisonText);
        TransientPlotSeries authored = viewModel.ElectricalTransientPlot!.Series[0];
        TransientPlotSeries dependent = viewModel.ElectricalTransientPlot.Series[1];
        Assert.True(
            authored.Points.Zip(dependent.Points, (left, right) => Math.Abs(left.Value - right.Value))
                .Max() > 1e-3);
    }
}
