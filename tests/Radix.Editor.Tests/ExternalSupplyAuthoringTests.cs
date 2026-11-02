using Radix.Core.Serialization;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// Declaring that a pin is supplied from outside is a statement about the interface, so the panel
/// accepts only outputs and only pins that exist, and every accepted edit is undoable like the rest.
/// The refusals matter more than the acceptance: a flag on an input would say nothing while looking
/// like it said something.
/// </summary>
public sealed class ExternalSupplyAuthoringTests
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

    private static EditableField Field(EditorViewModel viewModel) =>
        Assert.Single(
            viewModel.Fields,
            candidate => candidate.Label == "Externally supplied pins");

    [Fact]
    public void AnOutputCanBeDeclaredAndTheFieldShowsIt()
    {
        EditorViewModel viewModel = Load("electrical-ternary-drive.radix");

        EditableField field = Field(viewModel);
        Assert.NotNull(field.Apply);
        Assert.True(field.Apply!("y"));

        Assert.Equal("y", Field(viewModel).Value);
    }

    [Fact]
    public void AnInputIsRefusedBecauseItIsAlreadySupplied()
    {
        EditorViewModel viewModel = Load("electrical-ternary-drive.radix");

        Assert.False(Field(viewModel).Apply!("a"));
        Assert.Contains("is an input", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Field(viewModel).Value);
    }

    [Fact]
    public void APinTheDefinitionDoesNotHaveIsRefused()
    {
        EditorViewModel viewModel = Load("electrical-ternary-drive.radix");

        Assert.False(Field(viewModel).Apply!("zzz"));
        Assert.Contains("has no pin 'zzz'", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Field(viewModel).Value);
    }

    [Fact]
    public void ClearingTheDeclarationCanBeUndone()
    {
        EditorViewModel viewModel = Load("electrical-ternary-drive.radix");
        Assert.True(Field(viewModel).Apply!("y"));

        Assert.True(Field(viewModel).Apply!(string.Empty));
        Assert.Equal(string.Empty, Field(viewModel).Value);

        viewModel.UndoCommand.Execute(null);

        Assert.Equal("y", Field(viewModel).Value);
    }
}
