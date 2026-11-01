using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A preset initial value is legitimate but can start the circuit away from its own DC
/// operating point, so the first samples move for a reason the sources do not explain.
/// The solver reports that as an informational note: it must appear when the two disagree,
/// stay silent when they agree, and never change the run itself.
/// </summary>
public sealed class TransientInitialConditionNoteTests
{
    [Fact]
    public void AChargedCapacitorAwayFromTheOperatingPointIsReported()
    {
        TransientNetlist netlist = RcNetlist().SetCapacitorInitialVoltage("C1", 2);

        TransientResult result = netlist.Run(new TransientOptions { StopTime = 1e-3, Steps = 100 });

        Assert.True(result.Completed, result.Message);
        string note = Assert.Single(result.Notes);
        Assert.Contains("C1", note, StringComparison.Ordinal);
        Assert.Contains("starts at 2 V", note, StringComparison.Ordinal);
        Assert.Contains("DC operating point is 5 V", note, StringComparison.Ordinal);
        Assert.Contains("3 V discontinuity", note, StringComparison.Ordinal);
    }

    [Fact]
    public void AMatchingInitialConditionIsNotReported()
    {
        TransientNetlist netlist = RcNetlist().SetCapacitorInitialVoltage("C1", 5);

        TransientResult result = netlist.Run(new TransientOptions { StopTime = 1e-3, Steps = 100 });

        Assert.True(result.Completed, result.Message);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void AnInductorPresetAwayFromItsDcCurrentIsReported()
    {
        // At DC the inductor is a short, so it carries the 5 mA the resistor sets. Starting it
        // at 0 A is a real discontinuity at the first step.
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddInductor("L1", "out", "gnd", 1e-3)
            .SetInductorInitialCurrent("L1", 0);

        TransientResult result = netlist.Run(new TransientOptions { StopTime = 1e-4, Steps = 100 });

        Assert.True(result.Completed, result.Message);
        string note = Assert.Single(result.Notes);
        Assert.Contains("L1", note, StringComparison.Ordinal);
        Assert.Contains("starts at 0 A", note, StringComparison.Ordinal);
        Assert.Contains("0.005 A", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ANoteNeverChangesTheRunOrItsGrid()
    {
        TransientResult noted = RcNetlist()
            .SetCapacitorInitialVoltage("C1", 2)
            .Run(new TransientOptions { StopTime = 1e-3, Steps = 100 });
        TransientResult steady = RcNetlist()
            .SetCapacitorInitialVoltage("C1", 5)
            .Run(new TransientOptions { StopTime = 1e-3, Steps = 100 });

        Assert.Equal(101, noted.Points.Count);
        Assert.Equal(steady.Points.Count, noted.Points.Count);
        Assert.Equal(steady.FinalPoint!.Time, noted.FinalPoint!.Time);
        // The note is why the traces differ, not evidence that the run is wrong.
        Assert.True(noted.InitialPoint!.NodeVoltages["out"] < steady.InitialPoint!.NodeVoltages["out"]);
    }

    [Fact]
    public void TheReportCarriesNotesAndOmitsThemWhenThereAreNone()
    {
        TransientNetlist netlist = RcNetlist();
        TransientOptions options = new() { StopTime = 1e-3, Steps = 100 };

        TransientResult noted = netlist.Run(options);
        TransientResult quiet = RcNetlist()
            .SetCapacitorInitialVoltage("C1", 5)
            .Run(options);

        using JsonDocument notedJson = JsonDocument.Parse(
            TransientNetlistJson.ToReportJson("rc", netlist, noted));
        JsonElement notes = notedJson.RootElement.GetProperty("notes");
        Assert.Equal(1, notes.GetArrayLength());
        Assert.Contains("C1", notes[0].GetString()!, StringComparison.Ordinal);

        using JsonDocument quietJson = JsonDocument.Parse(
            TransientNetlistJson.ToReportJson("rc", netlist, quiet));
        Assert.False(quietJson.RootElement.TryGetProperty("notes", out _));
    }

    private static TransientNetlist RcNetlist() => new TransientNetlist("gnd")
        .AddVoltageSource("V1", "in", "gnd", 5)
        .AddResistor("R1", "in", "out", 1_000)
        .AddCapacitor("C1", "out", "gnd", 1e-6)
        .SetCapacitorInitialVoltage("C1", 0);
}
