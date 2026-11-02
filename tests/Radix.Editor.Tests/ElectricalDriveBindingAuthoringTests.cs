using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// A binding used to be JSON-only. The inspector field has to author both kinds, refuse a node the
/// circuit does not have, and clear back to none - with the undo path intact, since it mutates the
/// circuit like any other edit.
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
            Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix"),
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

    [Fact]
    public void ADriveAndAClaimAreBothAuthorableFromOneLine()
    {
        EditorViewModel viewModel = Load();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        Assert.Empty(circuit.DriveBindings);

        Assert.True(Field(viewModel).Apply!("out:a@1e9; out:y@1e9?"));

        Assert.Equal(2, circuit.DriveBindings.Count);
        Assert.Equal("out", circuit.DriveBindings[0].NodeId);
        Assert.Equal("a", circuit.DriveBindings[0].PinId);
        Assert.False(circuit.DriveBindings[0].Checks);
        Assert.Equal(1e9, circuit.DriveBindings[0].TicksPerSecond);
        Assert.True(circuit.DriveBindings[1].Checks);

        // Applying the same line again is accepted and does not append a third binding.
        Assert.True(Field(viewModel).Apply!("out:a@1e9; out:y@1e9?"));
        Assert.Equal(2, circuit.DriveBindings.Count);
    }

    [Fact]
    public void ANodeTheCircuitDoesNotHaveIsRefusedAndChangesNothing()
    {
        EditorViewModel viewModel = Load();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        Assert.True(Field(viewModel).Apply!("out:a@1e9"));

        EditableField field = Field(viewModel);
        Assert.False(field.Apply!("nope:a@1e9"));
        Assert.Contains("not a node of this circuit", viewModel.StatusText, StringComparison.Ordinal);

        // Malformed lines are refused too, and neither attempt removes the binding that exists.
        Assert.False(field.Apply!("out:a"));
        Assert.False(field.Apply!("out:a@0"));
        Assert.Single(circuit.DriveBindings);
    }

    [Fact]
    public void ClearingTheFieldRemovesEveryBindingAndUndoRestoresIt()
    {
        EditorViewModel viewModel = Load();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;
        Assert.True(Field(viewModel).Apply!("out:a@1e9; out:y@1e9?"));

        Assert.True(Field(viewModel).Apply!(string.Empty));
        Assert.Empty(circuit.DriveBindings);

        // Undo restores a snapshot, so the circuit is a new object: the assertion has to read the
        // one the view model now exposes rather than the reference captured before the edit.
        viewModel.UndoCommand.Execute(null);
        Assert.Equal(2, viewModel.SelectedElectricalCircuitModel!.DriveBindings.Count);
    }
}
