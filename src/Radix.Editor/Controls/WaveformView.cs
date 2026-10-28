using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Radix.Core.Signals;
using Radix.Editor.ViewModels;

namespace Radix.Editor.Controls;

/// <summary>Digital waveform panel for a bounded simulation snapshot.</summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<WaveformSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<WaveformView, WaveformSnapshot?>(nameof(Snapshot));

    private static readonly IBrush Panel = Brush.Parse("#FBFCFD");
    private static readonly IBrush Ink = Brush.Parse("#263442");
    private static readonly IBrush Muted = Brush.Parse("#758596");
    private static readonly IBrush Grid = Brush.Parse("#E3E9EF");
    private static readonly IBrush RowAlt = Brush.Parse("#F4F7FA");
    private static readonly IBrush Undriven = Brush.Parse("#B7C2CC");

    public WaveformSnapshot? Snapshot
    {
        get => GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    static WaveformView()
    {
        AffectsRender<WaveformView>(SnapshotProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Panel, new Rect(Bounds.Size));
        WaveformSnapshot? snapshot = Snapshot;
        if (snapshot is null || snapshot.Rows.Count == 0)
        {
            FormattedText empty = Text("No nets to display — open a circuit with nets.", 12, Muted);
            context.DrawText(empty, new Point(
                Math.Max(12, (Bounds.Width - empty.Width) / 2),
                Math.Max(12, (Bounds.Height - empty.Height) / 2)));
            return;
        }

        double labelWidth = Math.Min(150, Math.Max(80, Bounds.Width * 0.26));
        double top = 16;
        double bottom = 24;
        var plot = new Rect(labelWidth, top, Math.Max(10, Bounds.Width - labelWidth - 46), Math.Max(10, Bounds.Height - top - bottom));
        long start = snapshot.Start;
        long end = Math.Max(snapshot.End, start + 1);
        double rowHeight = plot.Height / snapshot.Rows.Count;

        context.DrawLine(new Pen(Grid, 1), new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        context.DrawLine(new Pen(Grid, 1), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));

        // Four evenly spaced time grid lines, labelled with simulated ticks.
        for (int i = 0; i <= 4; i++)
        {
            double x = plot.Left + (plot.Width * i / 4);
            context.DrawLine(new Pen(Grid, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            long tick = start + ((end - start) * i / 4);
            FormattedText label = Text($"t={tick.ToString(CultureInfo.InvariantCulture)}", 9, Muted);
            context.DrawText(label, new Point(x - (label.Width / 2), plot.Bottom + 4));
        }

        // Labels and row bands sit outside the waveform clip so the left gutter can
        // stay a fixed width at every zoom level.
        for (int rowIndex = 0; rowIndex < snapshot.Rows.Count; rowIndex++)
        {
            WaveformRow row = snapshot.Rows[rowIndex];
            double rowTop = plot.Top + (rowIndex * rowHeight);
            if (rowIndex % 2 == 1)
            {
                context.FillRectangle(RowAlt, new Rect(plot.Left, rowTop, plot.Width, rowHeight));
            }

            double center = rowTop + (rowHeight / 2);
            FormattedText name = Text(row.Label, Math.Min(11, Math.Max(8.5, rowHeight * 0.45)), Ink);
            name.MaxTextWidth = labelWidth - 18;
            name.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(name, new Point(8, center - (name.Height / 2)));
            context.DrawLine(new Pen(Grid, 1), new Point(0, rowTop), new Point(plot.Right, rowTop));
        }

        using (context.PushClip(plot))
        {
            for (int rowIndex = 0; rowIndex < snapshot.Rows.Count; rowIndex++)
            {
                WaveformRow row = snapshot.Rows[rowIndex];
                double rowTop = plot.Top + (rowIndex * rowHeight);
                double center = rowTop + (rowHeight / 2);
                double lastX = plot.Left;
                int value = row.StartValue;
                foreach (WaveValueChange change in row.Changes)
                {
                    double x = X(change.Time, start, end, plot);
                    DrawLevel(context, row.Domain, value, lastX, x, center, rowHeight);
                    DrawTransition(context, row.Domain, value, change.Value, x, center, rowHeight);
                    lastX = x;
                    value = change.Value;
                }

                DrawLevel(context, row.Domain, value, lastX, plot.Right, center, rowHeight);
            }
        }

        for (int rowIndex = 0; rowIndex < snapshot.Rows.Count; rowIndex++)
        {
            WaveformRow row = snapshot.Rows[rowIndex];
            double center = plot.Top + (rowIndex * rowHeight) + (rowHeight / 2);
            int value = row.Changes.Count == 0 ? row.StartValue : row.Changes[^1].Value;
            FormattedText last = Text(
                value < 0 ? "z" : Format(value, row.Domain),
                Math.Min(10, Math.Max(8, rowHeight * 0.4)),
                ValueBrush(row.Domain, value));
            context.DrawText(last, new Point(plot.Right + 4, center - (last.Height / 2)));
        }
    }

    private static double X(long time, long start, long end, Rect plot) =>
        plot.Left + ((time - start) * plot.Width / Math.Max(1, end - start));

    private static void DrawLevel(
        DrawingContext context,
        SignalDomain domain,
        int value,
        double fromX,
        double toX,
        double center,
        double rowHeight)
    {
        if (toX <= fromX + 0.2)
        {
            return;
        }

        double y = LevelY(value, domain, center, rowHeight);
        IPen pen = value < 0
            ? new Pen(Undriven, 1.2, new DashStyle([3, 3], 0))
            : new Pen(ValueBrush(domain, value), 1.8, lineCap: PenLineCap.Round);
        context.DrawLine(pen, new Point(fromX, y), new Point(toX, y));
    }

    private static void DrawTransition(
        DrawingContext context,
        SignalDomain domain,
        int from,
        int to,
        double x,
        double center,
        double rowHeight)
    {
        double fromY = LevelY(from, domain, center, rowHeight);
        double toY = LevelY(to, domain, center, rowHeight);
        IPen pen = to < 0
            ? new Pen(Undriven, 1.2, new DashStyle([3, 3], 0))
            : new Pen(ValueBrush(domain, to), 1.8, lineCap: PenLineCap.Round);
        context.DrawLine(pen, new Point(x, fromY), new Point(x, toY));
    }

    private static double LevelY(int value, SignalDomain domain, double center, double rowHeight)
    {
        if (value < 0)
        {
            return center;
        }

        double amplitude = Math.Max(3, Math.Min(rowHeight * 0.26, 8));
        double fraction = domain.ValueCount <= 1 ? 0 : (double)value / (domain.ValueCount - 1);
        fraction = Math.Clamp(fraction, 0, 1);
        return center + amplitude - (2 * amplitude * fraction);
    }

    private static string Format(int value, SignalDomain domain) =>
        domain.IsValidValue(value) ? domain.ValueName(value) : "!";

    private static IBrush ValueBrush(SignalDomain domain, int value)
    {
        if (value < 0)
        {
            return Undriven;
        }

        if (domain.ValueCount == 2)
        {
            return value == 0 ? Brush.Parse("#8091A3") : Brush.Parse("#1F9D72");
        }

        return value switch
        {
            0 => Brush.Parse("#8091A3"),
            1 => Brush.Parse("#2B6CB0"),
            2 => Brush.Parse("#D97706"),
            _ => Brush.Parse("#7C5CBF"),
        };
    }

    private static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight, FontStretch.Normal),
            size,
            brush);
    }
}
