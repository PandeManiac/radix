using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Testing;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// This is the handoff the electrical model has been missing: a solved node's level, expressed as
/// the drives the engine already applies. The tests hold the conversion, the change-only rule and
/// the tick mapping, because each is a way the bridge could silently send the wrong value.
/// </summary>
public sealed class ElectricalDriveScheduleTests
{
    private static ElectricalCircuit Example()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix"),
            validate: false);
        return Assert.Single(project.ElectricalCircuits);
    }

    private static TransientResult Run(ElectricalCircuit circuit) => circuit.ToTransientNetlist()
        .Run(new TransientOptions { StartTime = 0, StopTime = 120e-9, Steps = 1280 });

    [Fact]
    public void TheOutputChangesLevelOnceOnItsWayToMidRail()
    {
        ElectricalLevelSampler sampler = ElectricalLevelSampler.TernaryFromRail(5);
        IReadOnlyList<ElectricalDriveStep> levels = ElectricalDriveSchedule.Levels(Run(Example()), "out", sampler);

        // 5 V is level 2 and 2.5 V is level 1, and the node crosses the 3.333 V boundary once.
        Assert.Equal(2, levels.Count);
        Assert.Equal(2, levels[0].Level);
        Assert.Equal(0, levels[0].Time);
        Assert.Equal(1, levels[1].Level);
        Assert.InRange(levels[1].Time, 1e-9, 120e-9);
        Assert.InRange(levels[1].Volts, 1.6, 3.4);
    }

    [Fact]
    public void RepeatedLevelsDoNotBecomeRepeatedDrives()
    {
        ElectricalLevelSampler sampler = ElectricalLevelSampler.TernaryFromRail(5);
        IReadOnlyList<ElectricalDriveStep> levels = ElectricalDriveSchedule.Levels(Run(Example()), "gnd", sampler);

        // Ground never leaves level 0, so one step describes the whole run.
        ElectricalDriveStep only = Assert.Single(levels);
        Assert.Equal(0, only.Level);
        Assert.Equal(0, only.Time);
    }

    [Fact]
    public void TheDrivesCarryTheLevelAsTheDomainValueIndex()
    {
        ElectricalLevelSampler sampler = ElectricalLevelSampler.TernaryFromRail(5);

        TransientResult result = Run(Example());

        // One tick per nanosecond resolves the crossing, which happens tens of nanoseconds in.
        IReadOnlyList<(long At, ScriptDrive Drive)> scheduled = ElectricalDriveSchedule.Scheduled(
            result, "out", sampler, "A", ticksPerSecond: 1e9);

        Assert.Equal(2, scheduled.Count);
        Assert.Equal(0, scheduled[0].At);
        Assert.Equal("A", scheduled[0].Drive.PinId);
        Assert.True(scheduled[0].Drive.Value.IsNumeric);
        Assert.Equal(2, scheduled[0].Drive.Value.Index);

        Assert.InRange(scheduled[1].At, 1, 120);
        Assert.Equal(1, scheduled[1].Drive.Value.Index);
        Assert.Equal("A", scheduled[1].Drive.PinId);

        // The unscheduled form keeps every change and its order, whatever the rate.
        IReadOnlyList<ScriptDrive> drives = ElectricalDriveSchedule.Drives(
            result, "out", sampler, "A", ticksPerSecond: 1e6);
        Assert.Equal([2, 1], drives.Select(drive => drive.Value.Index!.Value));
    }

    [Fact]
    public void ACoarseRateCollapsesChangesRatherThanDroppingThemSilently()
    {
        ElectricalLevelSampler sampler = ElectricalLevelSampler.TernaryFromRail(5);
        TransientResult result = Run(Example());

        // One tick per microsecond is far too coarse: both changes land on tick 0, and the level the
        // node ended on wins. The plain list still reports both changes.
        IReadOnlyList<(long At, ScriptDrive Drive)> scheduled = ElectricalDriveSchedule.Scheduled(
            result, "out", sampler, "A", ticksPerSecond: 1e6);
        Assert.Single(scheduled);
        Assert.Equal(1, scheduled[0].Drive.Value.Index);
        Assert.Equal(0, scheduled[0].At);

        Assert.Equal(2, ElectricalDriveSchedule.Levels(result, "out", sampler).Count);
    }

    [Fact]
    public void ARateIsRequiredAndBadNodesAreRefused()
    {
        ElectricalLevelSampler sampler = ElectricalLevelSampler.TernaryFromRail(5);
        TransientResult result = Run(Example());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ElectricalDriveSchedule.Scheduled(result, "out", sampler, "A", 0));
        Assert.Throws<ArgumentException>(() =>
            ElectricalDriveSchedule.Levels(result, "nope", sampler));
        Assert.Throws<ArgumentException>(() =>
            ElectricalDriveSchedule.Drives(result, "out", sampler, "  ", 1e6));
    }
}
