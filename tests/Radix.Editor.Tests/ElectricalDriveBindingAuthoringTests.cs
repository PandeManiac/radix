using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The inspector authors a binding, and that includes refusing one the project cannot use: a node the
/// circuit lacks, or a pin no definition declares. The paired example is the fixture because it is
/// the case where both exist, so a valid line and an invalid one differ by one character.
/// </summary>
public sealed class ElectricalDriveBindingAuthoringTests
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
            Path.Combine(directory!.FullName, "examples", "electrical-ternary-drive.radix"),
            validate: false));
    }

    private static EditableField Field(EditorViewModel viewModel)
    {
        EditableField? field = Assert.Single(
            viewModel.ElectricalCircuitFields,
            candidate => candidate.Label == "Drive bindings");
        Assert.NotNull(field);
        Assert.NotNull(field!.Apply);
        return field;
    }

    private static ElectricalCircuit Circuit(EditorViewModel viewModel) =>
        viewModel.SelectedElectricalCircuitModel!;

    [Fact]
    public void ADriveAndAClaimAreBothAuthorableFromOneLine()
    {
        EditorViewModel viewModel = Load();
        Assert.Equal(2, Circuit(viewModel).DriveBindings.Count);

        Assert.True(Field(viewModel).Apply!("out:a@2e9; out:y@2e9?"));

        ElectricalCircuit circuit = Circuit(viewModel);
        Assert.Equal(2, circuit.DriveBindings.Count);
        Assert.Equal("a", circuit.DriveBindings[0].PinId);
        Assert.Equal(2e9, circuit.DriveBindings[0].TicksPerSecond);
        Assert.False(circuit.DriveBindings[0].Checks);
        Assert.Equal("y", circuit.DriveBindings[1].PinId);
        Assert.True(circuit.DriveBindings[1].Checks);

        // Applying the same line again is accepted and does not append a third binding.
        Assert.True(Field(viewModel).Apply!("out:a@2e9; out:y@2e9?"));
        Assert.Equal(2, circuit.DriveBindings.Count);
    }

    [Fact]
    public void APinNoDefinitionDeclaresIsRefused()
    {
        EditorViewModel viewModel = Load();
        int before = Circuit(viewModel).DriveBindings.Count;

        EditableField field = Field(viewModel);
        Assert.False(field.Apply!("out:zzz@1e9"));
        Assert.Contains("No definition declares pin(s) zzz", viewModel.StatusText, StringComparison.Ordinal);

        // The refusal leaves the bindings the file had, so a typo cannot drop a handoff.
        Assert.Equal(before, Circuit(viewModel).DriveBindings.Count);
    }

    [Fact]
    public void ANodeTheCircuitDoesNotHaveIsRefusedAndChangesNothing()
    {
        EditorViewModel viewModel = Load();
        int before = Circuit(viewModel).DriveBindings.Count;
        EditableField field = Field(viewModel);

        Assert.False(field.Apply!("nope:a@1e9"));
        Assert.Contains("not a node of this circuit", viewModel.StatusText, StringComparison.Ordinal);

        // Malformed lines are refused too, and none of the attempts removes a binding that exists.
        Assert.False(field.Apply!("out:a"));
        Assert.False(field.Apply!("out:a@0"));
        Assert.Equal(before, Circuit(viewModel).DriveBindings.Count);
    }

    [Fact]
    public void ClearingTheFieldRemovesEveryBindingAndUndoRestoresIt()
    {
        EditorViewModel viewModel = Load();
        Assert.Equal(2, Circuit(viewModel).DriveBindings.Count);

        Assert.True(Field(viewModel).Apply!(string.Empty));
        Assert.Empty(Circuit(viewModel).DriveBindings);

        // Undo restores a snapshot, so the circuit is a new object: the assertion has to read the
        // one the view model now exposes rather than a reference captured before the edit.
        viewModel.UndoCommand.Execute(null);
        Assert.Equal(2, Circuit(viewModel).DriveBindings.Count);
    }
}
