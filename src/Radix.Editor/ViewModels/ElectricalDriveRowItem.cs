using System.Globalization;
using Radix.Core.Electrical;

namespace Radix.Editor.ViewModels;

/// <summary>
/// One change a drive binding would produce, formatted the way the command line prints it so a reader
/// sees the same numbers in both places. A single text property keeps the panel simple: the row is a
/// line of a report, not a set of editable cells.
/// </summary>
public sealed class ElectricalDriveRowItem
{
    public ElectricalDriveRowItem(ElectricalDriveBinding binding, ElectricalDriveStep step, long at)
    {
        ArgumentNullException.ThrowIfNull(binding);
        NodeId = binding.NodeId;
        PinId = binding.PinId;
        Kind = binding.Checks ? "claim" : "drive";
        At = at;
        Text = string.Create(
            CultureInfo.InvariantCulture,
            $"  {binding.NodeId,-10}  {binding.PinId,-4}  {Kind,-5}  {at,-8}  {step.Time:E3}  {step.Volts,8:G4}  L{step.Level}");
    }

    public string NodeId { get; }

    public string PinId { get; }

    /// <summary>"drive" or "claim", matching how the binding is applied.</summary>
    public string Kind { get; }

    public long At { get; }

    /// <summary>The whole row, already formatted.</summary>
    public string Text { get; }
}
