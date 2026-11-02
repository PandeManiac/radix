using System.Globalization;

namespace Radix.Editor.Rendering;

/// <summary>One sample drawn on a transient plot, in circuit units.</summary>
public readonly record struct TransientPlotPoint(double Time, double Value);

/// <summary>One node's samples, already reduced to what the plot draws.</summary>
public sealed class TransientPlotSeries
{
    public TransientPlotSeries(
        string nodeId,
        IReadOnlyList<TransientPlotPoint> points,
        int colorIndex = 0,
        string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfNegative(colorIndex);
        NodeId = nodeId;
        Label = string.IsNullOrWhiteSpace(label) ? nodeId : label;
        Points = points;
        ColorIndex = colorIndex;
    }

    public string NodeId { get; }

    /// <summary>Display label for the legend; defaults to the node ID.</summary>
    public string Label { get; }

    public IReadOnlyList<TransientPlotPoint> Points { get; }

    /// <summary>
    /// Palette slot for this node, assigned from the full node list rather than the drawn
    /// order, so hiding one trace does not repaint the others.
    /// </summary>
    public int ColorIndex { get; }
}

/// <summary>
/// Screen-independent geometry for an analog transient plot: the time and value range,
/// the decimated series, axis ticks, and the normalized mapping the control uses.
/// Keeping this free of Avalonia types lets the mapping be verified headlessly, so the
/// drawing code only has to place what the model already computed.
/// </summary>
public sealed class TransientPlot
{
    /// <summary>Points kept per series; more than a few hundred cannot be resolved on screen.</summary>
    public const int DefaultMaximumPointsPerSeries = 600;

    /// <summary>Tick count on both axes, including both ends.</summary>
    public const int TickCount = 5;

    private TransientPlot(
        double timeStart,
        double timeEnd,
        double valueMin,
        double valueMax,
        IReadOnlyList<TransientPlotSeries> series,
        IReadOnlyList<double> timeTicks,
        IReadOnlyList<double> valueTicks,
        IReadOnlyList<string>? timeTickLabels,
        string horizontalUnit,
        string verticalUnit)
    {
        TimeStart = timeStart;
        TimeEnd = timeEnd;
        ValueMin = valueMin;
        ValueMax = valueMax;
        Series = series;
        TimeTicks = timeTicks;
        ValueTicks = valueTicks;
        TimeTickLabels = timeTickLabels;
        HorizontalUnit = horizontalUnit;
        VerticalUnit = verticalUnit;
    }

    public double TimeStart { get; }

    public double TimeEnd { get; }

    public double ValueMin { get; }

    public double ValueMax { get; }

    public IReadOnlyList<TransientPlotSeries> Series { get; }

    public IReadOnlyList<double> TimeTicks { get; }

    public IReadOnlyList<double> ValueTicks { get; }

    /// <summary>
    /// Labels for <see cref="TimeTicks" /> when the horizontal axis is not a plain number line —
    /// a log-frequency axis reads as decades (1k, 10k, …) rather than as the exponents it plots.
    /// Null means each tick is formatted from its value.
    /// </summary>
    public IReadOnlyList<string>? TimeTickLabels { get; }

    /// <summary>Unit of the horizontal axis, empty when it carries no unit.</summary>
    public string HorizontalUnit { get; }

    /// <summary>Unit of the vertical axis, drawn as a short hint.</summary>
    public string VerticalUnit { get; }

    public bool IsEmpty => Series.Count == 0;

    /// <summary>The label for one horizontal tick, using the supplied labels when present.</summary>
    public string TimeTickLabel(int index) =>
        TimeTickLabels is { } labels && index >= 0 && index < labels.Count
            ? labels[index]
            : FormatTick(TimeTicks[index]);

    /// <summary>
    /// Builds the plot from per-node samples, or null when there is nothing to draw. The
    /// value range is padded by 10% so a trace never touches the frame, and a flat trace
    /// (every sample equal) still gets a usable range instead of a degenerate one.
    /// </summary>
    public static TransientPlot? Create(
        IEnumerable<TransientPlotSeries> series,
        int maximumPointsPerSeries = DefaultMaximumPointsPerSeries) =>
        Create(series, maximumPointsPerSeries, null, null, string.Empty, "V");

    /// <summary>
    /// Builds a plot whose horizontal axis carries its own ticks, labels and units, which is what
    /// a frequency response needs: the axis is logarithmic, so evenly spaced numbers would be
    /// unreadable and decade labels are the only honest ticks.
    /// </summary>
    public static TransientPlot? Create(
        IEnumerable<TransientPlotSeries> series,
        int maximumPointsPerSeries,
        IReadOnlyList<double>? timeTicks,
        IReadOnlyList<string>? timeTickLabels,
        string horizontalUnit,
        string verticalUnit)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPointsPerSeries, 2);
        if (timeTicks is not null &&
            timeTickLabels is not null &&
            timeTicks.Count != timeTickLabels.Count)
        {
            throw new ArgumentException(
                $"Axis ticks and labels must pair up: {timeTicks.Count} ticks but {timeTickLabels.Count} labels.",
                nameof(timeTickLabels));
        }

        var prepared = new List<TransientPlotSeries>();
        double timeStart = double.PositiveInfinity;
        double timeEnd = double.NegativeInfinity;
        double valueMin = double.PositiveInfinity;
        double valueMax = double.NegativeInfinity;
        foreach (TransientPlotSeries candidate in series)
        {
            if (candidate is null || candidate.Points.Count == 0)
            {
                continue;
            }

            TransientPlotPoint[] points = candidate.Points
                .Where(point => double.IsFinite(point.Time) && double.IsFinite(point.Value))
                .OrderBy(point => point.Time)
                .ToArray();
            if (points.Length == 0)
            {
                continue;
            }

            timeStart = Math.Min(timeStart, points[0].Time);
            timeEnd = Math.Max(timeEnd, points[^1].Time);
            foreach (TransientPlotPoint point in points)
            {
                valueMin = Math.Min(valueMin, point.Value);
                valueMax = Math.Max(valueMax, point.Value);
            }

            prepared.Add(new TransientPlotSeries(
                candidate.NodeId, points, candidate.ColorIndex, candidate.Label));
        }

        if (prepared.Count == 0 || !(timeEnd > timeStart))
        {
            return null;
        }

        if (valueMax > valueMin)
        {
            double padding = (valueMax - valueMin) * 0.1;
            valueMin -= padding;
            valueMax += padding;
        }
        else
        {
            double padding = Math.Max(Math.Abs(valueMax) * 0.1, 0.5);
            valueMin -= padding;
            valueMax += padding;
        }

        int longest = prepared.Max(entry => entry.Points.Count);
        int stride = Math.Max(1, (int)Math.Ceiling(longest / (double)maximumPointsPerSeries));
        TransientPlotSeries[] decimated = prepared
            .Select(entry => new TransientPlotSeries(
                entry.NodeId, Decimate(entry.Points, stride), entry.ColorIndex, entry.Label))
            .ToArray();

        return new TransientPlot(
            timeStart,
            timeEnd,
            valueMin,
            valueMax,
            decimated,
            timeTicks ?? Ticks(timeStart, timeEnd),
            Ticks(valueMin, valueMax),
            timeTickLabels,
            horizontalUnit,
            verticalUnit);
    }

    /// <summary>Horizontal position in [0, 1] for a time inside the range.</summary>
    public double NormalizeX(double time) => (time - TimeStart) / (TimeEnd - TimeStart);

    /// <summary>Vertical position in [0, 1] for a value inside the range; 1 is the top.</summary>
    public double NormalizeY(double value) => (value - ValueMin) / (ValueMax - ValueMin);

    /// <summary>
    /// Horizontal position in [0, 1] for any time, clamped to the plotted range. A probe
    /// or marker therefore always lands inside the frame, and a non-finite time places
    /// nothing rather than throwing during a render pass.
    /// </summary>
    public double FractionOf(double time) =>
        double.IsFinite(time) ? Math.Clamp(NormalizeX(time), 0, 1) : 0;

    /// <summary>A short invariant label for an axis tick.</summary>
    public static string FormatTick(double value) =>
        value == 0 ? "0" : value.ToString("G3", CultureInfo.InvariantCulture);

    private static IReadOnlyList<TransientPlotPoint> Decimate(
        IReadOnlyList<TransientPlotPoint> points,
        int stride)
    {
        if (stride <= 1)
        {
            return points;
        }

        var reduced = new List<TransientPlotPoint>((points.Count / stride) + 2);
        for (int index = 0; index < points.Count; index += stride)
        {
            reduced.Add(points[index]);
        }

        // The last sample is the settled value, so it must survive decimation.
        if (reduced[^1].Time < points[^1].Time)
        {
            reduced.Add(points[^1]);
        }

        return reduced;
    }

    private static IReadOnlyList<double> Ticks(double start, double end)
    {
        var ticks = new double[TickCount];
        for (int index = 0; index < TickCount; index++)
        {
            ticks[index] = start + ((end - start) * index / (TickCount - 1));
        }

        return ticks;
    }
}
