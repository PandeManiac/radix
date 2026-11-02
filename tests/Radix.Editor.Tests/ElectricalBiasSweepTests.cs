using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Radix.Editor.Rendering;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The panel can say what one operating point is; a bias sweep says where the gate switches. These
/// tests drive the command the button calls and check the curve against what the solver measured:
/// a cut-off device must start at zero transconductance and rise where the gate turns on.
/// </summary>
public sealed class ElectricalBiasSweepTests
{
    private static EditorViewModel TernaryInverter()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        EditorViewModel viewModel = new(ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix"),
            validate: false));

        Assert.True(viewModel.HasSelectedElectricalCircuit);
        return viewModel;
    }

    private static EditorViewModel Swept(int count = 11)
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.ElectricalAcSweepDriveText = "VIN";
        viewModel.ElectricalBiasSweepRangeText = $"0:5:{count}";
        Assert.True(viewModel.RunElectricalBiasSweep());
        return viewModel;
    }

    [Fact]
    public void TheCurveShowsEachDeviceTurningOn()
    {
        EditorViewModel viewModel = Swept();

        Assert.True(viewModel.HasElectricalBiasSweepResult);
        Assert.True(viewModel.HasElectricalBiasSweepPlot);

        TransientPlot plot = Assert.IsType<TransientPlot>(viewModel.ElectricalBiasSweepPlot);
        Assert.Equal(0, plot.TimeStart, 9);
        Assert.Equal(5, plot.TimeEnd, 9);
        Assert.Equal("V", plot.HorizontalUnit);
        Assert.Equal("S", plot.VerticalUnit);
        Assert.Equal(4, plot.Series.Count);

        // MNL drives the output: quiet at 0 V, then a transconductance of hundreds of microsiemens
        // once the gate is on. A curve that never left zero would mean the sweep solved the rest
        // state eleven times.
        TransientPlotSeries driven = plot.Series.Single(series => series.NodeId == "MNL");
        Assert.Equal(0, driven.Points[0].Value, 12);
        Assert.InRange(driven.Points.Max(point => point.Value), 1e-4, 1e-2);
    }

    [Fact]
    public void TheSummaryNamesTheDeviceThatConductsHardest()
    {
        EditorViewModel viewModel = Swept();

        string summary = viewModel.ElectricalBiasSweepSummary;
        Assert.Contains("stepped 'VIN' over 11 values", summary, StringComparison.Ordinal);
        Assert.Contains("4 devices", summary, StringComparison.Ordinal);
        Assert.Contains("peaks at", summary, StringComparison.Ordinal);

        // The named device must be one the sweep actually measured; which one conducts hardest in
        // this range is a property of the circuit, not something to pin in a test.
        Assert.Contains(
            viewModel.SelectedElectricalCircuitModel!.Components.Select(component => component.Id),
            id => summary.Contains(id + " peaks at", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSweepCsvHasOneRowPerDevicePerBias()
    {
        EditorViewModel viewModel = Swept(count: 5);

        Assert.True(viewModel.TryBuildElectricalBiasSweepCsv(out string csv));
        string[] rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("# format: radix-mosfet-bias-sweep", rows);
        Assert.Contains("bias_v,device,region,vgs_v,vds_v,id_a,gm_s,gds_s", rows);

        string[] data = [.. rows.Where(row => !row.StartsWith('#'))];
        Assert.Equal(1 + (5 * 4), data.Length);
        Assert.Contains(data, row => row.StartsWith("0,MNL,Cutoff,", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonSourceIsRejectedByTheSweep()
    {
        EditorViewModel viewModel = TernaryInverter();
        string transistor = viewModel.SelectedElectricalCircuitModel!.Components
            .First(component => component.Kind != ElectricalComponentKind.VoltageSource).Id;
        viewModel.ElectricalAcSweepDriveText = transistor;
        viewModel.ElectricalBiasSweepRangeText = "0:5:5";

        Assert.False(viewModel.RunElectricalBiasSweep());
        Assert.False(viewModel.HasElectricalBiasSweepResult);
        Assert.Contains("voltage source", viewModel.ElectricalBiasSweepSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedRangeIsRejectedWithoutThrowing()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.ElectricalAcSweepDriveText = "VIN";
        viewModel.ElectricalBiasSweepRangeText = "0:5";

        Assert.False(viewModel.RunElectricalBiasSweep());
        Assert.Contains("from:to:count", viewModel.ElectricalBiasSweepSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingTheCircuitDropsTheSweep()
    {
        EditorViewModel viewModel = Swept(count: 5);
        Assert.True(viewModel.HasElectricalBiasSweepResult);

        viewModel.AddElectricalComponent();

        Assert.False(viewModel.HasElectricalBiasSweepResult);
        Assert.False(viewModel.HasElectricalBiasSweepPlot);
        Assert.False(viewModel.TryBuildElectricalBiasSweepCsv(out _));
    }
}
