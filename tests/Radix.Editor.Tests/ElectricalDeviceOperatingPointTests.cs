using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The inspector names the operating point of the transistor being edited. The numbers must agree
/// with what the solver measured — the same gate that reads 344 for |gm/gds| on the command line
/// must read 344 here — and must disappear when the selection or the circuit changes.
/// </summary>
public sealed class ElectricalDeviceOperatingPointTests
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


    [Fact]
    public void TheSelectedTransistorShowsItsRegionAndSlopes()
    {
        EditorViewModel viewModel = TernaryInverter();
        // The circuit's rest state has VIN at 0 V, where this device is off, so the bias is what
        // puts it on the transition being inspected.
        viewModel.ElectricalDeviceBiasText = "VIN=1.3";
        viewModel.SelectedElectricalComponent =
            viewModel.ElectricalComponents.Single(item => item.Id == "MNL");

        Assert.True(viewModel.HasElectricalDeviceOperatingPoint);
        string text = viewModel.ElectricalDeviceOperatingPointText;

        // MNL is the n-channel device that drives the output at this bias, so it must be saturated
        // with a transconductance far above its drain conductance.
        Assert.Contains("Saturation", text, StringComparison.Ordinal);
        Assert.Contains("gm", text, StringComparison.Ordinal);
        Assert.Contains("gds", text, StringComparison.Ordinal);

        // The same ratio the CLI reports for this device at the same bias: the panel and the
        // command line evaluate one device model, so they cannot disagree.
        string ratio = text[(text.IndexOf("|gm/gds| ", StringComparison.Ordinal) + 9)..];
        double intrinsic = double.Parse(
            ratio[..ratio.IndexOf(' ')], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(intrinsic, 300, 400);

        // A saturated channel presents two thirds of its gate-source capacitance and none of its
        // gate-drain capacitance: the inspector says so where the capacitances are edited.
        Assert.Contains("Cgs ×0.6667", text, StringComparison.Ordinal);
        Assert.Contains("Cgd ×0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOffTransistorReportsItsOwnRegionAndNoGain()
    {
        EditorViewModel viewModel = TernaryInverter();

        // MPH is pinned in triode at this bias, which is why that half of the pair contributes
        // almost nothing to the output. Its intrinsic gain is far below unity.
        viewModel.ElectricalDeviceBiasText = "VIN=1.3";
        viewModel.SelectedElectricalComponent =
            viewModel.ElectricalComponents.Single(item => item.Id == "MPH");
        Assert.Contains("Triode", viewModel.ElectricalDeviceOperatingPointText, StringComparison.Ordinal);

        string text = viewModel.ElectricalDeviceOperatingPointText;
        string ratio = text[(text.IndexOf("|gm/gds| ", StringComparison.Ordinal) + 9)..];
        Assert.InRange(
            double.Parse(ratio[..ratio.IndexOf(' ')], System.Globalization.CultureInfo.InvariantCulture),
            0,
            0.01);

        // Triode, so the channel connects gate to both terminals equally.
        Assert.Contains("Cgs ×0.5", text, StringComparison.Ordinal);
        Assert.Contains("Cgd ×0.5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutABiasTheGateIsReportedAtRest()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.SelectedElectricalComponent =
            viewModel.ElectricalComponents.Single(item => item.Id == "MNL");

        // The rest state is a real operating point, not a missing one: VIN sits at its waveform's
        // value at t = 0, so the device is cut off and says so instead of borrowing a bias.
        Assert.Contains("Cutoff", viewModel.ElectricalDeviceOperatingPointText, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedBiasIsReportedInsteadOfGuessed()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.ElectricalDeviceBiasText = "VIN=notanumber";
        viewModel.SelectedElectricalComponent =
            viewModel.ElectricalComponents.Single(item => item.Id == "MNL");

        Assert.Contains(
            "Operating point unavailable",
            viewModel.ElectricalDeviceOperatingPointText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ANonTransistorSelectionHasNoOperatingPoint()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.SelectedElectricalComponent =
            viewModel.ElectricalComponents.Single(item => item.Id == "RG");

        // A resistor has no region or transconductance, so the panel shows nothing rather than
        // inventing a row of zeros.
        Assert.False(viewModel.HasElectricalDeviceOperatingPoint);
        Assert.Equal(string.Empty, viewModel.ElectricalDeviceOperatingPointText);
    }

    [Fact]
    public void EditingTheCircuitDropsTheOperatingPoint()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.SelectedElectricalComponent =
            viewModel.ElectricalComponents.Single(item => item.Id == "MNL");
        Assert.True(viewModel.HasElectricalDeviceOperatingPoint);

        viewModel.AddElectricalComponent();

        // The slopes describe the circuit as it was; an edit makes them stale, so they go away.
        Assert.False(viewModel.HasElectricalDeviceOperatingPoint);
    }
}
