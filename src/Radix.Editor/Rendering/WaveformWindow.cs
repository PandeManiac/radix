namespace Radix.Editor.Rendering;

/// <summary>
/// Visible absolute tick range of the waveform panel. Pure presentation state:
/// zooming and panning never touch the snapshot, the trace, or simulation.
/// </summary>
public sealed class WaveformWindow
{
    /// <summary>Smallest visible span, in ticks, so zoom cannot collapse to a point.</summary>
    public const long MinSpan = 2;

    public long Start { get; private set; }

    public long End { get; private set; } = MinSpan;

    public long Span => End - Start;

    /// <summary>Shows the whole trace from tick zero to <paramref name="dataEnd"/>.</summary>
    public void Fit(long dataEnd) => Set(0, Math.Max(MinSpan, dataEnd));

    /// <summary>Sets the range, normalizing reversed input and enforcing the minimum span.</summary>
    public void Set(long start, long end)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        if (start < 0)
        {
            // Preserve the span when a pan runs past tick zero.
            decimal shifted = (decimal)end - start;
            end = shifted >= long.MaxValue ? long.MaxValue : (long)shifted;
            start = 0;
        }

        if (end - start < MinSpan)
        {
            end = start + MinSpan;
        }

        Start = start;
        End = end;
    }

    /// <summary>Keeps the same span inside <c>[0, dataEnd]</c>, shifting it when needed.</summary>
    public void ClampTo(long dataEnd)
    {
        long maxEnd = Math.Max(MinSpan, dataEnd);
        long span = Math.Min(Span, maxEnd);
        long start = Math.Clamp(Start, 0, maxEnd - span);
        Start = start;
        End = start + span;
    }

    /// <summary>
    /// Scales the visible span around <paramref name="anchorTick"/>, which keeps its
    /// relative horizontal position. The result is clamped to the data range.
    /// </summary>
    public void ZoomAt(long anchorTick, double factor, long dataEnd)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        long maxSpan = Math.Max(MinSpan, dataEnd);
        long span = Math.Max(MinSpan, Span);
        double target = span * factor;
        if (!double.IsFinite(target) || target > maxSpan)
        {
            target = maxSpan;
        }

        long newSpan = Math.Max(MinSpan, (long)Math.Round(target));
        long anchor = Math.Clamp(anchorTick, Start, End);
        double relative = (double)(anchor - Start) / span;
        long newStart = anchorTick - (long)Math.Round(relative * newSpan);
        Set(newStart, newStart + newSpan);
        ClampTo(dataEnd);
    }

    /// <summary>Shifts the window by ticks, clamping it to the data range.</summary>
    public void Pan(long deltaTicks, long dataEnd)
    {
        long limit = Math.Max(MinSpan, dataEnd);
        long delta = Math.Clamp(deltaTicks, -limit, limit);
        Set(Start + delta, End + delta);
        ClampTo(dataEnd);
    }

    public bool IsFull(long dataEnd) =>
        Start <= 0 && End >= Math.Max(MinSpan, dataEnd);
}
