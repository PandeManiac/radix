using Radix.Core.Electrical;
using Radix.Core.Testing;

namespace Radix.Core.Electrical;

/// <summary>
/// A level a solved circuit held from one time onwards: the first such step and every change
/// after it. Only changes are recorded, because a drive that repeats a value the simulator already
/// holds would add events without adding information.
/// </summary>
/// <param name="Time">When the level was read, in seconds.</param>
/// <param name="At">The tick that time maps to.</param>
/// <param name="Volts">The voltage the level was read from.</param>
/// <param name="Level">The level index, which is also the index of the value in the domain.</param>
public readonly record struct ElectricalDriveStep(double Time, long At, double Volts, int Level);

/// <summary>
/// The electrical-to-digital handoff: a solved node read as levels over time, and those levels
/// expressed as the drives the simulation engine already accepts.
/// </summary>
/// <remarks>
/// The simulator counts abstract ticks while the solver counts seconds, so a scheduled conversion
/// needs a rate. It is supplied rather than guessed because a wrong rate would produce a script
/// that runs and means something else. When scheduling, two changes landing on the same tick
/// collapse to the later one - the engine drives a pin once per step, and the level the node
/// actually ended on should win - but the plain drive list keeps every change, so a rate too coarse
/// to schedule loses ordering rather than values.
/// </remarks>
public static class ElectricalDriveSchedule
{
    /// <summary>Every level the node held, one entry per change, starting at the run's first point.</summary>
    public static IReadOnlyList<ElectricalDriveStep> Levels(
        TransientResult result,
        string nodeId,
        ElectricalLevelSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (result.Points.Count == 0)
        {
            throw new ArgumentException("The run has no points to read.", nameof(result));
        }

        if (!result.Points[0].NodeVoltages.ContainsKey(nodeId))
        {
            throw new ArgumentException($"Node '{nodeId}' is not in the run.", nameof(nodeId));
        }

        var steps = new List<ElectricalDriveStep>();
        int? current = null;
        foreach (TransientPoint point in result.Points)
        {
            double volts = point.NodeVoltages[nodeId];
            int level = sampler.LevelFor(volts);
            if (current == level)
            {
                continue;
            }

            current = level;
            steps.Add(new ElectricalDriveStep(point.Time, 0, volts, level));
        }

        return steps;
    }

    /// <summary>
    /// The same changes as scripted drives. A value is emitted as its level index, which is how a
    /// domain identifies a value, so the number of levels and the number of domain values must
    /// match for the drives to be accepted.
    /// </summary>
    public static IReadOnlyList<ScriptDrive> Drives(
        TransientResult result,
        string nodeId,
        ElectricalLevelSampler sampler,
        string pinId,
        double ticksPerSecond)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pinId);
        if (!double.IsFinite(ticksPerSecond) || ticksPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ticksPerSecond), ticksPerSecond, "Ticks per second must be a positive number.");
        }

        // Order and value, not timing: this form is for a caller that supplies its own schedule.
        // The rate still has to be usable, because a caller asking for drives at a nonsense rate
        // has misunderstood what it is building.
        return [.. Levels(result, nodeId, sampler).Select(step =>
            new ScriptDrive(pinId, new ScriptValue(ValueName(step.Level), step.Level)))];
    }

    /// <summary>
    /// The same drives with the tick each applies at, so a caller can schedule them on a script
    /// step instead of applying them all at time zero.
    /// </summary>
    public static IReadOnlyList<(long At, ScriptDrive Drive)> Scheduled(
        TransientResult result,
        string nodeId,
        ElectricalLevelSampler sampler,
        string pinId,
        double ticksPerSecond)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pinId);
        if (!double.IsFinite(ticksPerSecond) || ticksPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ticksPerSecond), ticksPerSecond, "Ticks per second must be a positive number.");
        }

        var byTick = new SortedDictionary<long, int>();
        foreach (ElectricalDriveStep step in Levels(result, nodeId, sampler))
        {
            double scaled = step.Time * ticksPerSecond;
            byTick[(long)Math.Min(long.MaxValue / 2, Math.Max(0, Math.Round(scaled, MidpointRounding.ToEven)))] =
                step.Level;
        }

        return [.. byTick.Select(pair =>
            (pair.Key, new ScriptDrive(pinId, new ScriptValue(ValueName(pair.Value), pair.Value))))];
    }

    private static string ValueName(int level) =>
        level.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
