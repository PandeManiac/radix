using Radix.Core.Serialization;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The probe is where a reader meets the electrical-to-digital conversion, so the level has to
/// travel with the voltage and has to be the level of that voltage, not of some other node.
/// </summary>
public sealed class ElectricalProbeLevelTests
{
    private static EditorViewModel Load()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return new EditorViewModel(ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix"),
            validate: false));
    }

    private static void SetField(EditorViewModel viewModel, string label, string value)
    {
        EditableField? field = Assert.Single(
            viewModel.ElectricalCircuitFields,
            candidate => candidate.Label == label);
        Assert.NotNull(field);
        Assert.NotNull(field!.Apply);
        Assert.True(field.Apply!(value));
    }

    private static EditorViewModel WithRun()
    {
        EditorViewModel viewModel = Load();
        SetField(viewModel, "Transient stop (s)", "120e-9");
        SetField(viewModel, "Transient steps", "1280");
        Assert.True(viewModel.SolveElectricalTransient());
        return viewModel;
    }

    [Fact]
    public void TheSettledOutputProbesAsTheMiddleLevel()
    {
        EditorViewModel viewModel = WithRun();
        viewModel.ElectricalTransientProbeInput = "120e-9";

        Assert.True(viewModel.ProbeElectricalTransient());

        // The rail is 5 V, so the boundaries are 1.667 V and 3.333 V: 2.5 V is the middle level.
        Assert.Equal(1, viewModel.ElectricalTransientProbeLevels["out"]);
        Assert.Equal(2, viewModel.ElectricalTransientProbeLevels["vdd"]);
        Assert.Equal(0, viewModel.ElectricalTransientProbeLevels["gnd"]);

        // Every level must belong to its own voltage, so the readout carries both.
        Assert.Contains("out 2.50", viewModel.ElectricalTransientProbeText, StringComparison.Ordinal);
        Assert.Contains("(L1)", viewModel.ElectricalTransientProbeText, StringComparison.Ordinal);
        Assert.Contains("vdd 5", viewModel.ElectricalTransientProbeText, StringComparison.Ordinal);
        Assert.Contains("(L2)", viewModel.ElectricalTransientProbeText, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEarlyProbeReadsTheRailItIsStillOn()
    {
        EditorViewModel viewModel = WithRun();
        viewModel.ElectricalTransientProbeInput = "0";

        Assert.True(viewModel.ProbeElectricalTransient());

        // Before the drive moves, the output sits on the 5 V rail: level 2, not the middle level.
        Assert.Equal(2, viewModel.ElectricalTransientProbeLevels["out"]);
    }

    [Fact]
    public void ClearingTheRunClearsTheLevelsWithTheVoltages()
    {
        EditorViewModel viewModel = WithRun();
        viewModel.ElectricalTransientProbeInput = "120e-9";
        Assert.True(viewModel.ProbeElectricalTransient());
        Assert.NotEmpty(viewModel.ElectricalTransientProbeLevels);

        // A new stop time invalidates the run, so the probe must not keep stale levels.
        SetField(viewModel, "Transient stop (s)", "100e-9");

        Assert.Empty(viewModel.ElectricalTransientProbeLevels);
        Assert.Empty(viewModel.ElectricalTransientProbeValues);
        Assert.Empty(viewModel.ElectricalTransientProbeText);
    }
}
