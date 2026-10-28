using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Radix.Editor;

public enum UnsavedChangesChoice
{
    Cancel,
    Discard,
    Save,
}

/// <summary>Small themed modal for protecting unsaved work. No platform message box.</summary>
public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog()
    {
        InitializeComponent();
    }

    public UnsavedChangesDialog(string projectName)
        : this()
    {
        MessageText.Text = $"“{projectName}” has changes that have not been saved.";
    }

    public static Task<UnsavedChangesChoice> AskAsync(Window owner, string projectName)
    {
        var dialog = new UnsavedChangesDialog(projectName);
        return dialog.ShowDialog<UnsavedChangesChoice>(owner);
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Save);

    private void Discard_Click(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Discard);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Cancel);
}
