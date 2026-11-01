namespace Radix.Core.Electrical;

/// <summary>
/// One point of a DC voltage-source sweep. A sweep keeps going when an operating
/// point has no solution, so a failure at one bias does not discard the curve.
/// </summary>
public sealed record DcSweepPoint(
    double SourceValue,
    DcSolution? Solution,
    DcSolveFailure? Failure,
    string? Message)
{
    public bool Solved => Solution is not null;
}

/// <summary>
/// Independent DC operating-point sweeps. Every point solves a fresh copy of the
/// netlist, so point results are reproducible and never depend on the previous
/// bias. This is intentionally a small design tool, not a continuation-based
/// transient or AC analysis.
/// </summary>
public static class DcSweep
{
    /// <summary>Upper bound on requested points, to keep one sweep bounded.</summary>
    public const int MaximumPoints = 10_000;

    /// <summary>
    /// Sweeps one ideal voltage source from start to stop inclusive over
    /// <paramref name="points"/> uniform steps. A point that cannot be solved is
    /// recorded with its failure instead of aborting the sweep.
    /// </summary>
    public static IReadOnlyList<DcSweepPoint> VoltageSource(
        DcNetlist netlist,
        string sourceId,
        double start,
        double stop,
        int points)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (!double.IsFinite(start))
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "Sweep start must be finite.");
        }

        if (!double.IsFinite(stop))
        {
            throw new ArgumentOutOfRangeException(nameof(stop), stop, "Sweep stop must be finite.");
        }

        if (points < 2 || points > MaximumPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(points),
                points,
                $"A sweep needs between 2 and {MaximumPoints} points.");
        }

        if (!netlist.Elements.OfType<DcVoltageSource>().Any(element =>
                string.Equals(element.Id, sourceId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Netlist has no voltage source '{sourceId}'.",
                nameof(sourceId));
        }

        var results = new DcSweepPoint[points];
        double step = (stop - start) / (points - 1);
        for (int index = 0; index < points; index++)
        {
            double value = index == points - 1 ? stop : start + (step * index);
            try
            {
                DcSolution solution = netlist.WithVoltageSource(sourceId, value).Solve();
                results[index] = new DcSweepPoint(value, solution, null, null);
            }
            catch (DcSolveException exception)
            {
                results[index] = new DcSweepPoint(
                    value,
                    null,
                    exception.Failure,
                    exception.Message);
            }
        }

        return results;
    }
}
