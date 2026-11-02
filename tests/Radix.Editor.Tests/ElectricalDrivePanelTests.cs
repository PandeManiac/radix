using Radix.Core.Serialization;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The panel reports what the command line reports: the changes a circuit would drive, the claims it
/// makes, and whether the definition agreed. Both halves matter - a table with no verdict would look
/// successful whether or not the digital side followed.
/// </summary>
public sealed class ElectricalDrivePanelTests
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
        Assert.True(field.Apply!(value));
    }

    [Fact]
    public void RunningBindingsShowsEveryChangeAndTheDefinitionsVerdict()
    {
        EditorViewModel viewModel = Load("electrical-ternary-drive.radix");
        SetField(viewModel, "Transient stop (s)", "120e-9");
        SetField(viewModel, "Transient steps", "1280");

        Assert.True(viewModel.RunElectricalDriveBindings());

        // Two bindings with one change each: the drive and the claim on the same two ticks.
        Assert.Equal(4, viewModel.ElectricalDriveRows.Count);
        Assert.Contains(viewModel.ElectricalDriveRows, row => row.Kind == "drive");
        Assert.Contains(viewModel.ElectricalDriveRows, row => row.Kind == "claim");

        string summary = viewModel.ElectricalDriveSummary;
        Assert.Contains("demo.follow", summary, StringComparison.Ordinal);
        Assert.Contains("agreed on 45 claim tick(s)", summary, StringComparison.Ordinal);
        Assert.Contains("across 1 pin(s)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefinitionFieldPicksWhatTheBindingsAreAppliedTo()
    {
        EditorViewModel viewModel = Load("electrical-ternary-drive.radix");
        SetField(viewModel, "Transient stop (s)", "120e-9");
        SetField(viewModel, "Transient steps", "1280");
        SetField(viewModel, "Drive definition", "demo.follow");

        Assert.True(viewModel.RunElectricalDriveBindings());
        Assert.Contains("'demo.follow' agreed", viewModel.ElectricalDriveSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ADefinitionTheProjectDoesNotHaveIsRefused()
    {
        EditorViewModel viewModel = Load("electrical-ternary-drive.radix");
        EditableField? field = Assert.Single(
            viewModel.ElectricalCircuitFields,
            candidate => candidate.Label == "Drive definition");
        Assert.NotNull(field);
        Assert.NotNull(field!.Apply);

        Assert.False(field.Apply("not-a-definition"));
        Assert.Contains("No definition 'not-a-definition'", viewModel.StatusText, StringComparison.Ordinal);

        // Blank is always allowed and returns the panel to the root definition.
        Assert.True(field.Apply(string.Empty));
        Assert.Contains("root definition", viewModel.ElectricalDriveSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryAuditsWhatTheDefinitionWouldHaveComputed()
    {
        // The supply example hands Y to the network, so the claim count is zero and the audit is the
        // only thing that can say whether the definition would have agreed.
        EditorViewModel viewModel = Load("electrical-ternary-supply.radix");
        SetField(viewModel, "Transient stop (s)", "120e-9");
        SetField(viewModel, "Transient steps", "1280");

        Assert.True(viewModel.RunElectricalDriveBindings());
        // Two ticks, not the whole window: an audit compares the end of each input plateau, where a
        // zero-delay table can be held against a circuit that takes time to settle.
        Assert.Contains(
            "would have produced the same levels on 1 supplied pin(s) over 2 tick(s)",
            viewModel.ElectricalDriveSummary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryReportsTheMeasuredDelay()
    {
        // The sweep drives the input and supplies the output, so it has both halves the timing needs.
        EditorViewModel viewModel = Load("electrical-ternary-sweep.radix");
        SetField(viewModel, "Transient stop (s)", "250e-9");
        SetField(viewModel, "Transient steps", "2500");

        Assert.True(viewModel.RunElectricalDriveBindings());

        string summary = viewModel.ElectricalDriveSummary;
        Assert.Contains("Timing: 2->1 in 23 tick(s)", summary, StringComparison.Ordinal);
        Assert.Contains("1->0 in 44 tick(s)", summary, StringComparison.Ordinal);
        Assert.Contains("declares 0 tick(s) within 44 tick(s)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ACircuitWithoutBindingsSaysSoInsteadOfReportingSuccess()
    {
        EditorViewModel viewModel = Load("transient-ternary-inverter.radix");

        Assert.False(viewModel.RunElectricalDriveBindings());
        Assert.Empty(viewModel.ElectricalDriveRows);
        Assert.Contains("no drive bindings", viewModel.ElectricalDriveSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingTheCircuitClearsTheReportWithTheRun()
    {
        EditorViewModel viewModel = Load("electrical-ternary-drive.radix");
        SetField(viewModel, "Transient stop (s)", "120e-9");
        SetField(viewModel, "Transient steps", "1280");
        Assert.True(viewModel.RunElectricalDriveBindings());
        Assert.NotEmpty(viewModel.ElectricalDriveRows);

        // A changed binding invalidates the solve the report described.
        SetField(viewModel, "Drive bindings", "out:a@1e9");

        Assert.Empty(viewModel.ElectricalDriveRows);
        Assert.Contains("Run the bindings", viewModel.ElectricalDriveSummary, StringComparison.Ordinal);
    }
}
