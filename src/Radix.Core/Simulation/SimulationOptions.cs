using Radix.Core.Model;

namespace Radix.Core.Simulation;

/// <summary>Deterministic simulation configuration.</summary>
public sealed class SimulationOptions
{
    /// <summary>
    /// Maximum evaluations of a single component at one simulated timestamp.
    /// Exceeding this indicates zero-delay feedback that does not converge.
    /// </summary>
    public int MaxEvaluationsPerComponentPerTimestamp { get; init; } = 64;

    /// <summary>
    /// Maximum number of events that may wait in the queue at once. Zero disables the
    /// limit. Exceeding it halts the run with a diagnostic instead of letting a circuit
    /// that produces events faster than they drain exhaust memory.
    /// </summary>
    public int MaxPendingEvents { get; init; } = 1_000_000;

    /// <summary>Capacity of the net-change trace ring buffer. Zero disables storage.</summary>
    public int MaxRecordedChanges { get; init; } = 500_000;

    /// <summary>Whether net transitions are recorded for waveforms and audits.</summary>
    public bool RecordTrace { get; init; } = true;

    public static SimulationOptions FromSettings(ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SimulationOptions
        {
            MaxEvaluationsPerComponentPerTimestamp = Math.Max(1, settings.MaxEvaluationsPerComponentPerTimestamp),
            MaxPendingEvents = Math.Max(0, settings.MaxPendingEvents),
            MaxRecordedChanges = Math.Max(0, settings.MaxRecordedNetChanges),
            RecordTrace = settings.MaxRecordedNetChanges > 0 && settings.TraceAllNets,
        };
    }
}
