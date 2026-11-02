namespace Radix.Core.Electrical;

/// <summary>One row of a gain measurement: the swept value and dV/dx there, when it is defined.</summary>
public sealed record DcSweepGainPoint(double Value, double? Gain);

/// <summary>
/// Measures the local small-signal gain of a swept transfer characteristic by differentiating the
/// curve the solver already produced. A gate is decided by its slope — a ternary inverter's
/// shoulders are exactly where that slope is large — so a sweep table that lists only voltages
/// leaves the most important property of the design unmeasured.
///
/// The derivative is a difference quotient of solved points, not a second solve, so it cannot
/// disagree with the values the caller already has. Neighbours that failed to solve are skipped to
/// the next solved point, and a row that has no solved neighbour to bracket it reports no slope
/// rather than a fabricated zero.
/// </summary>
public static class DcSweepGain
{
    /// <summary>Local dV(<paramref name="nodeId"/>)/d(swept), one entry per row in input order.</summary>
    public static IReadOnlyList<DcSweepGainPoint> Along(
        IReadOnlyList<DcSweepRow> rows,
        string nodeId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        var volts = new double?[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            volts[index] = rows[index].Solution is { } solution &&
                solution.NodeVoltages.TryGetValue(nodeId, out double value)
                    ? value
                    : null;
        }

        var points = new List<DcSweepGainPoint>(rows.Count);
        foreach (DcSweepRow row in rows)
        {
            points.Add(new DcSweepGainPoint(row.Value, SlopeAt(rows, volts, points.Count)));
        }

        return points;
    }

    private static double? SlopeAt(IReadOnlyList<DcSweepRow> rows, double?[] volts, int index)
    {
        if (volts[index] is null)
        {
            return null;
        }

        int left = NearestSolved(volts, index, -1);
        int right = NearestSolved(volts, index, +1);
        if (left < 0 && right < 0)
        {
            return null;
        }

        // A central difference uses solved neighbours on both sides; at either end of the swept
        // range, or past a value that failed, the one-sided difference is the honest estimate.
        int first = left >= 0 ? left : index;
        int last = right >= 0 ? right : index;
        double span = rows[last].Value - rows[first].Value;
        if (span == 0)
        {
            // Repeated swept values give no interval to divide by.
            return null;
        }

        return (volts[last]!.Value - volts[first]!.Value) / span;
    }

    private static int NearestSolved(double?[] volts, int from, int step)
    {
        for (int index = from + step; index >= 0 && index < volts.Length; index += step)
        {
            if (volts[index] is not null)
            {
                return index;
            }
        }

        return -1;
    }
}
