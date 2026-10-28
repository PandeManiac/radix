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
            MaxRecordedChanges = Math.Max(0, settings.MaxRecordedNetChanges),
            RecordTrace = settings.MaxRecordedNetChanges > 0 && settings.TraceAllNets,
        };
    }
}
