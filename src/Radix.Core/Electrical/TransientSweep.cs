namespace Radix.Core.Electrical;

/// <summary>One sweep point: the value that was set and the run it produced.</summary>
public sealed record TransientSweepRow(double Value, TransientResult Result);

/// <summary>
/// Design-space exploration: solve the same circuit with one element's value replaced, so a
/// designer can read a metric against a component instead of guessing a value, re-running, and
/// comparing by hand. Every row is a full run of its own netlist, and the swept value is the only
/// difference between them, so a difference in the results belongs to the value and not to a
/// rebuilt circuit.
/// </summary>
public static class TransientSweep
{
    /// <summary>
    /// Runs <paramref name="netlist"/> once per value with <paramref name="elementId"/> set to it.
    /// Failed runs are reported too, so a value that cannot solve is visible as a row instead of
    /// silently disappearing from the study.
    /// </summary>
    public static IReadOnlyList<TransientSweepRow> Run(
        TransientNetlist netlist,
        string elementId,
        IEnumerable<double> values,
        TransientOptions options)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(options);

        List<TransientSweepRow> rows = [];
        foreach (double value in values)
        {
            rows.Add(new TransientSweepRow(value, netlist.WithElementValue(elementId, value).Run(options)));
        }

        if (rows.Count == 0)
        {
            throw new ArgumentException("A sweep needs at least one value.", nameof(values));
        }

        return rows;
    }

    /// <summary>
    /// <paramref name="count"/> evenly spaced values from <paramref name="from"/> to
    /// <paramref name="to"/>, both inclusive, with the last value returned exactly rather than
    /// accumulated. A descending range is allowed: a sweep is a list of values, and nothing about
    /// the study requires it to increase.
    /// </summary>
    public static double[] Linspace(double from, double to, int count)
    {
        if (!double.IsFinite(from) || !double.IsFinite(to))
        {
            throw new ArgumentOutOfRangeException(
                nameof(from), "Sweep bounds must be finite.");
        }

        if (count < 2 || count > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, "A sweep needs between 2 and 64 values.");
        }

        double[] values = new double[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = index == count - 1
                ? to
                : from + ((to - from) * index / (count - 1));
        }

        return values;
    }
}
