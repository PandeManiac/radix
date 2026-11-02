using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The panel can author a MOSFET, but one operating point does not show a transfer
/// characteristic. These tests drive the DC sweep through the same command the button calls,
/// so a panel that cannot measure a curve shows up as a failure rather than as a missing feature.
/// </summary>
public sealed class ElectricalDcSweepTests
{
    /// <summary>A 10 V source from n1 to ground, with a 1 kOhm load from n1 to ground.</summary>
    private static EditorViewModel DividerWithSource()
    {
        var project = CircuitProject.CreateEmpty("DC sweep test");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;
        StandardLibrary.Install(project);

        var viewModel = new EditorViewModel(project);
        viewModel.AddElectricalCircuit();
        viewModel.AddElectricalComponent();
        viewModel.SetElectricalTerminalNode("r1", "a", "n1");
        viewModel.SetElectricalTerminalNode("r1", "b", "gnd");
        viewModel.SelectedElectricalComponentKind = viewModel.ElectricalComponentKinds
            .Single(choice => choice.Kind == ElectricalComponentKind.VoltageSource);
        EditableField volts = viewModel.ElectricalComponentFields
            .Single(field => field.Label.StartsWith("Value", StringComparison.Ordinal));
        volts.Value = "10";
        Assert.True(viewModel.ApplyField(volts));
        viewModel.AddElectricalComponent();
        return viewModel;
    }

    [Fact]
    public void ASweepMeasuresEveryPointOfTheTransferCurve()
    {
        EditorViewModel viewModel = DividerWithSource();
        viewModel.ElectricalDcSweepElementText = "r1";
        viewModel.ElectricalDcSweepValuesText = "0:10:3";

        Assert.True(viewModel.RunElectricalDcSweep());
        Assert.True(viewModel.HasElectricalDcSweepResult);

        // Ground is the reference and is omitted, so each swept value contributes one row.
        Assert.Equal(3, viewModel.ElectricalDcSweepRows.Count);
        Assert.Equal(
            ["0 V", "5 V", "10 V"],
            viewModel.ElectricalDcSweepRows.Select(row => row.VoltsText).ToArray());
        // Each value has exactly one non-ground node, so every row is the first row of its
        // value and prints it; a value spanning several nodes prints it once.
        Assert.Equal(["0", "5", "10"], viewModel.ElectricalDcSweepRows.Select(row => row.ValueText).ToArray());
        Assert.Contains("Swept 'r1'", viewModel.ElectricalDcSweepSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ASweepOfAMissingComponentExplainsItself()
    {
        EditorViewModel viewModel = DividerWithSource();
        viewModel.ElectricalDcSweepElementText = "nope";
        viewModel.ElectricalDcSweepValuesText = "0:5:2";

        Assert.False(viewModel.RunElectricalDcSweep());
        Assert.False(viewModel.HasElectricalDcSweepResult);
        Assert.Contains("nope", viewModel.ElectricalDcSweepSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedSweepValuesAreRejectedWithoutThrowing()
    {
        EditorViewModel viewModel = DividerWithSource();
        viewModel.ElectricalDcSweepElementText = "r1";
        viewModel.ElectricalDcSweepValuesText = "1,,2";

        Assert.False(viewModel.RunElectricalDcSweep());
        Assert.Contains("empty entry", viewModel.ElectricalDcSweepSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSweepCsvUsesTheSameWriterAsTheCommandLine()
    {
        EditorViewModel viewModel = DividerWithSource();
        viewModel.ElectricalDcSweepElementText = "r1";
        viewModel.ElectricalDcSweepValuesText = "0,5";
        Assert.True(viewModel.RunElectricalDcSweep());

        Assert.True(viewModel.TryBuildElectricalDcSweepCsv(out string csv));
        // Node ids are quoted only when they would break the table, exactly like the CLI writer.
        Assert.StartsWith("value,gnd,n1,failure", csv, StringComparison.Ordinal);
        Assert.Contains("\n0,0,0,\n", csv, StringComparison.Ordinal);
        Assert.Contains("\n5,0,5,\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingTheCircuitDropsTheSweep()
    {
        EditorViewModel viewModel = DividerWithSource();
        viewModel.ElectricalDcSweepElementText = "r1";
        viewModel.ElectricalDcSweepValuesText = "0:5:2";
        Assert.True(viewModel.RunElectricalDcSweep());

        viewModel.AddElectricalComponent();

        // A curve that describes an older circuit would be worse than no curve at all.
        Assert.False(viewModel.HasElectricalDcSweepResult);
        Assert.False(viewModel.TryBuildElectricalDcSweepCsv(out _));
    }
}
