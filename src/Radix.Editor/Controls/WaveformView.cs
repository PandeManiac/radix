using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Radix.Core.Signals;
using Radix.Editor.Rendering;
using Radix.Editor.ViewModels;

namespace Radix.Editor.Controls;

/// <summary>Digital waveform panel for a bounded simulation snapshot.</summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<WaveformSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<WaveformView, WaveformSnapshot?>(nameof(Snapshot));

    private IBrush _panel = Brushes.White;
    private IBrush _ink = Brushes.Black;
    private IBrush _muted = Brushes.Gray;
    private IBrush _grid = Brushes.LightGray;
    private IBrush _rowAlt = Brushes.WhiteSmoke;
    private IBrush _undriven = Brushes.LightGray;
    private IBrush _low = Brushes.SlateGray;
    private IBrush _high = Brushes.SeaGreen;
    private IBrush _one = Brushes.SteelBlue;
    private IBrush _two = Brushes.DarkOrange;
    private IBrush _three = Brushes.MediumPurple;
    private IBrush _four = Brushes.MediumVioletRed;
    private IBrush _accent = Brushes.SteelBlue;
    private long? _cursorTime;
    private bool _followEnd = true;
    private long _lastEnd = 1;
    private Rect _plotRect;
    private bool _panning;
    private Point _panOrigin;
    private long _panStart;
    private long _panEnd;

    /// <summary>Visible tick range. Wheel zooms, drag pans, and double-click refits.</summary>
    public WaveformWindow VisibleWindow { get; } = new();

    public WaveformSnapshot? Snapshot
    {
        get => GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    static WaveformView()
    {
        AffectsRender<WaveformView>(SnapshotProperty);
    }

    public WaveformView()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Shows the whole trace and resumes following the simulation end.</summary>
    public void FitAll()
    {
        _followEnd = true;
        _cursorTime = null;
        if (Snapshot is { } snapshot)
        {
            VisibleWindow.Fit(snapshot.End);
            _lastEnd = Math.Max(1, snapshot.End);
        }

        InvalidateVisual();
    }

    /// <summary>Shows an explicit absolute tick range, clamped to the snapshot.</summary>
    public void ZoomTo(long start, long end)
    {
        if (Snapshot is not { } snapshot)
        {
            return;
        }

        _followEnd = false;
        VisibleWindow.Set(start, end);
        VisibleWindow.ClampTo(snapshot.End);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        LoadTheme();
        context.FillRectangle(_panel, new Rect(Bounds.Size));
        WaveformSnapshot? snapshot = Snapshot;
        if (snapshot is null || snapshot.Rows.Count == 0)
        {
            _plotRect = default;
            FormattedText empty = Text("No nets to display — open a circuit with nets.", 12, _muted);
            context.DrawText(empty, new Point(
                Math.Max(12, (Bounds.Width - empty.Width) / 2),
                Math.Max(12, (Bounds.Height - empty.Height) / 2)));
            return;
        }

        SyncWindow(snapshot);
        double labelWidth = Math.Min(150, Math.Max(80, Bounds.Width * 0.26));
        double top = 16;
        double bottom = 24;
        var plot = new Rect(labelWidth, top, Math.Max(10, Bounds.Width - labelWidth - 46), Math.Max(10, Bounds.Height - top - bottom));
        _plotRect = plot;
        long start = VisibleWindow.Start;
        long end = Math.Max(VisibleWindow.End, start + 1);
        double rowHeight = plot.Height / snapshot.Rows.Count;

        context.DrawLine(new Pen(_grid, 1), new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        context.DrawLine(new Pen(_grid, 1), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));

        // Four evenly spaced time grid lines, labelled with simulated ticks.
        for (int i = 0; i <= 4; i++)
        {
            double x = plot.Left + (plot.Width * i / 4);
            context.DrawLine(new Pen(_grid, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            long tick = start + ((end - start) * i / 4);
            FormattedText label = Text($"t={tick.ToString(CultureInfo.InvariantCulture)}", 9, _muted);
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
                context.FillRectangle(_rowAlt, new Rect(plot.Left, rowTop, plot.Width, rowHeight));
            }

            double center = rowTop + (rowHeight / 2);
            FormattedText name = Text(row.Label, Math.Min(11, Math.Max(8.5, rowHeight * 0.45)), _ink);
            name.MaxTextWidth = labelWidth - 18;
            name.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(name, new Point(8, center - (name.Height / 2)));
            context.DrawLine(new Pen(_grid, 1), new Point(0, rowTop), new Point(plot.Right, rowTop));
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

        long? cursor = _cursorTime is long rawCursor
            ? Math.Clamp(rawCursor, start, end)
            : null;
        if (cursor is long cursorTime)
        {
            double cursorX = X(cursorTime, start, end, plot);
            context.DrawLine(
                new Pen(_accent, 1.4, new DashStyle([4, 2], 0)),
                new Point(cursorX, plot.Top),
                new Point(cursorX, plot.Bottom));
            FormattedText stamp = Text($"t={cursorTime.ToString(CultureInfo.InvariantCulture)}", 9, _ink);
            double stampX = Math.Clamp(cursorX + 4, plot.Left, Math.Max(plot.Left, plot.Right - stamp.Width - 4));
            context.FillRectangle(_panel, new Rect(stampX - 2, 1, stamp.Width + 4, stamp.Height + 2));
            context.DrawText(stamp, new Point(stampX, 2));
        }

        for (int rowIndex = 0; rowIndex < snapshot.Rows.Count; rowIndex++)
        {
            WaveformRow row = snapshot.Rows[rowIndex];
            double center = plot.Top + (rowIndex * rowHeight) + (rowHeight / 2);
            int value = cursor is long cursorAt
                ? row.ValueAt(cursorAt)
                : row.Changes.Count == 0
                    ? row.StartValue
                    : row.Changes[^1].Value;
            FormattedText last = Text(
                value < 0 ? "z" : Format(value, row.Domain),
                Math.Min(10, Math.Max(8, rowHeight * 0.4)),
                ValueBrush(row.Domain, value));
            context.DrawText(last, new Point(plot.Right + 4, center - (last.Height / 2)));
        }
    }

    /// <summary>
    /// Keeps the visible range coherent when a new snapshot arrives. Follow mode fits
    /// the whole trace; after zooming, the window is clamped instead. A simulation
    /// reset (a shorter trace) returns to follow mode.
    /// </summary>
    private void SyncWindow(WaveformSnapshot snapshot)
    {
        long end = Math.Max(1, snapshot.End);
        if (end < _lastEnd)
        {
            _followEnd = true;
        }

        _lastEnd = end;
        if (_followEnd)
        {
            VisibleWindow.Fit(end);
        }
        else
        {
            VisibleWindow.ClampTo(end);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Snapshot is not { } snapshot || _plotRect.Width <= 0 || e.Delta.Y == 0)
        {
            return;
        }

        Point position = e.GetPosition(this);
        if (!_plotRect.Contains(position))
        {
            return;
        }

        long span = Math.Max(1, VisibleWindow.Span);
        long anchor = VisibleWindow.Start +
            (long)Math.Round((position.X - _plotRect.Left) / _plotRect.Width * span);
        _followEnd = false;
        VisibleWindow.ZoomAt(anchor, e.Delta.Y > 0 ? 0.8 : 1.25, snapshot.End);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Snapshot is null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            FitAll();
            e.Handled = true;
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            _plotRect.Width <= 0 ||
            !_plotRect.Contains(e.GetPosition(this)))
        {
            return;
        }

        _cursorTime = null;
        _panning = true;
        _panOrigin = e.GetPosition(this);
        _panStart = VisibleWindow.Start;
        _panEnd = VisibleWindow.End;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Snapshot is not { } snapshot || _plotRect.Width <= 0)
        {
            return;
        }

        Point position = e.GetPosition(this);
        if (!_panning)
        {
            if (_plotRect.Contains(position))
            {
                SetCursorAt(position.X, snapshot);
            }
            else if (_cursorTime is not null)
            {
                _cursorTime = null;
                InvalidateVisual();
            }

            return;
        }

        double deltaX = position.X - _panOrigin.X;
        long span = Math.Max(1, _panEnd - _panStart);
        long deltaTicks = -(long)Math.Round(deltaX / _plotRect.Width * span);
        VisibleWindow.Set(_panStart + deltaTicks, _panEnd + deltaTicks);
        VisibleWindow.ClampTo(snapshot.End);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_panning)
        {
            return;
        }

        _panning = false;
        e.Pointer.Capture(null);
        if (Snapshot is { } snapshot && _plotRect.Contains(e.GetPosition(this)))
        {
            SetCursorAt(e.GetPosition(this).X, snapshot);
        }

        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_panning && _cursorTime is not null)
        {
            _cursorTime = null;
            InvalidateVisual();
        }
    }

    /// <summary>Maps a horizontal plot position to an absolute tick and redraws.</summary>
    private void SetCursorAt(double x, WaveformSnapshot snapshot)
    {
        long start = VisibleWindow.Start;
        long end = Math.Max(VisibleWindow.End, start + 1);
        double fraction = Math.Clamp((x - _plotRect.Left) / _plotRect.Width, 0, 1);
        long time = start + (long)Math.Round(fraction * (end - start));
        time = Math.Clamp(time, 0, Math.Max(0, snapshot.End));
        if (_cursorTime == time)
        {
            return;
        }

        _cursorTime = time;
        InvalidateVisual();
    }

    private static double X(long time, long start, long end, Rect plot) =>
        plot.Left + ((time - start) * plot.Width / Math.Max(1, end - start));

    private void DrawLevel(
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
            ? new Pen(_undriven, 1.2, new DashStyle([3, 3], 0))
            : new Pen(ValueBrush(domain, value), 1.8, lineCap: PenLineCap.Round);
        context.DrawLine(pen, new Point(fromX, y), new Point(toX, y));
    }

    private void DrawTransition(
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
            ? new Pen(_undriven, 1.2, new DashStyle([3, 3], 0))
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

    private IBrush ValueBrush(SignalDomain domain, int value)
    {
        if (value < 0)
        {
            return _undriven;
        }

        if (domain.ValueCount == 2)
        {
            return value == 0 ? _low : _high;
        }

        return value switch
        {
            0 => _low,
            1 => _one,
            2 => _two,
            3 => _three,
            _ => _four,
        };
    }

    private void LoadTheme()
    {
        _panel = ThemeBrushes.Get(this, "App.WavePanel", "#FBFCFD");
        _ink = ThemeBrushes.Get(this, "App.TextPrimary", "#263442");
        _muted = ThemeBrushes.Get(this, "App.TextMuted", "#758596");
        _grid = ThemeBrushes.Get(this, "App.WaveGrid", "#E3E9EF");
        _rowAlt = ThemeBrushes.Get(this, "App.WaveRowAlt", "#F4F7FA");
        _undriven = ThemeBrushes.Get(this, "App.SignalUndriven", "#B7C2CC");
        _low = ThemeBrushes.Get(this, "App.SignalLow", "#8091A3");
        _high = ThemeBrushes.Get(this, "App.SignalHigh", "#1F9D72");
        _one = ThemeBrushes.Get(this, "App.SignalOne", "#2B6CB0");
        _two = ThemeBrushes.Get(this, "App.SignalTwo", "#D97706");
        _three = ThemeBrushes.Get(this, "App.SignalThree", "#7C5CBF");
        _four = ThemeBrushes.Get(this, "App.SignalFour", "#B0427A");
        _accent = ThemeBrushes.Get(this, "App.Accent", "#2B6CB0");
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
