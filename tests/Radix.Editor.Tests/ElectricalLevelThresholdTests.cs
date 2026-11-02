using Radix.Core.Serialization;
using Radix.Core.Electrical;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The inspector field is where a circuit's boundaries are authored, so it has to be able to set
/// them, clear them back to the rail fallback, and refuse a list no reading could use.
/// </summary>
public sealed class ElectricalLevelThresholdTests
{
    private static EditorViewModel WithRun()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var viewModel = new EditorViewModel(ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix"),
            validate: false));

        SetField(viewModel, "Transient stop (s)", "120e-9");
        SetField(viewModel, "Transient steps", "1280");
        Assert.True(viewModel.SolveElectricalTransient());
        return viewModel;
    }

    private static EditableField Field(EditorViewModel viewModel, string label)
    {
        EditableField? field = Assert.Single(
            viewModel.ElectricalCircuitFields,
            candidate => candidate.Label == label);
        Assert.NotNull(field);
        Assert.NotNull(field!.Apply);
        return field;
    }

    private static void SetField(EditorViewModel viewModel, string label, string value) =>
        Assert.True(Field(viewModel, label).Apply!(value));

    private static int Probe(EditorViewModel viewModel, string node)
    {
        // Changing a circuit setting invalidates the run, which is correct; a probe after one has
        // to run again rather than read a stale result.
        if (!viewModel.HasElectricalTransientResult)
        {
            Assert.True(viewModel.SolveElectricalTransient());
        }

        viewModel.ElectricalTransientProbeInput = "120e-9";
        Assert.True(viewModel.ProbeElectricalTransient());
        return viewModel.ElectricalTransientProbeLevels[node];
    }

    [Fact]
    public void AuthoredBoundariesChangeWhatTheProbeReadsAndClearingThemRestoresTheRail()
    {
        EditorViewModel viewModel = WithRun();

        // The rail gives 1.667 V and 3.333 V, so 2.5 V reads as the middle level.
        Assert.Equal(1, Probe(viewModel, "out"));

        SetField(viewModel, "Level thresholds (V)", "2.6, 3.0");
        Assert.Equal(0, Probe(viewModel, "out"));

        SetField(viewModel, "Level thresholds (V)", string.Empty);
        Assert.Equal(1, Probe(viewModel, "out"));
    }

    [Fact]
    public void ADescendingOrUnreadableListIsRefusedAndChangesNothing()
    {
        EditorViewModel viewModel = WithRun();
        SetField(viewModel, "Level thresholds (V)", "2.6, 3.0");

        EditableField field = Field(viewModel, "Level thresholds (V)");
        Assert.False(field.Apply!("3.0, 2.6"));
        Assert.Contains("ascend", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(field.Apply!("2.6, wide"));
        Assert.False(field.Apply!("2.6, 2.6"));

        // Refusals leave the authored boundaries and therefore the reading as they were.
        Assert.Equal(0, Probe(viewModel, "out"));
    }

    [Fact]
    public void ApplyingTheSameListTwiceChangesNothing()
    {
        EditorViewModel viewModel = WithRun();
        SetField(viewModel, "Level thresholds (V)", "2.6, 3.0");

        // A repeated apply is accepted rather than treated as a failure, and must not append a
        // second copy of the boundaries - which would leave a table no reading could use.
        EditableField field = Field(viewModel, "Level thresholds (V)");
        Assert.True(field.Apply!("2.6, 3.0"));
        Assert.Equal(0, Probe(viewModel, "out"));
    }
}
