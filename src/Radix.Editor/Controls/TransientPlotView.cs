using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Radix.Editor.Rendering;

namespace Radix.Editor.Controls;

/// <summary>
/// Draws an analog transient plot from <see cref="TransientPlot"/>: axes, ticks, a zero
/// line when the range crosses zero, and one polyline per node. All scaling, padding,
/// and decimation happen in the geometry model, so this control only places points.
/// </summary>
public sealed class TransientPlotView : Control
{
    public static readonly StyledProperty<TransientPlot?> PlotProperty =
        AvaloniaProperty.Register<TransientPlotView, TransientPlot?>(nameof(Plot));

    private static readonly string[] Palette =
    [
        "#2B6CB0",
        "#C53030",
        "#2F855A",
        "#6B46C1",
        "#B7791F",
        "#2C7A7B",
    ];

    private const double LeftMargin = 48;
    private const double RightMargin = 10;
    private const double TopMargin = 18;
    private const double BottomMargin = 20;

    static TransientPlotView()
    {
        AffectsRender<TransientPlotView>(PlotProperty);
    }

    public TransientPlot? Plot
    {
        get => GetValue(PlotProperty);
        set => SetValue(PlotProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        IBrush ink = ThemeBrushes.Get(this, "App.TextPrimary", "#1F2A36");
        IBrush muted = ThemeBrushes.Get(this, "App.TextMuted", "#6F8092");
        IBrush grid = ThemeBrushes.Get(this, "App.CanvasGrid", "#E9EEF3");
        IBrush background = ThemeBrushes.Get(this, "App.PanelAltBackground", "#F8FAFC");
        context.FillRectangle(background, new Rect(Bounds.Size));

        double width = Bounds.Width - LeftMargin - RightMargin;
        double height = Bounds.Height - TopMargin - BottomMargin;
        if (Plot is not { } plot || plot.IsEmpty || width < 40 || height < 30)
        {
            context.DrawText(
                Text("No transient points to plot.", 10, muted),
                new Point(6, 6));
            return;
        }

        Point Map(double time, double value) => new(
            LeftMargin + (plot.NormalizeX(time) * width),
            TopMargin + ((1 - plot.NormalizeY(value)) * height));

        var axisPen = new Pen(grid, 1);
        context.DrawLine(
            axisPen,
            new Point(LeftMargin, TopMargin),
            new Point(LeftMargin, TopMargin + height));
        context.DrawLine(
            axisPen,
            new Point(LeftMargin, TopMargin + height),
            new Point(LeftMargin + width, TopMargin + height));

        var gridPen = new Pen(grid, 1, new DashStyle([2, 3], 0));
        foreach (double tick in plot.ValueTicks)
        {
            double y = Map(plot.TimeStart, tick).Y;
            context.DrawLine(gridPen, new Point(LeftMargin, y), new Point(LeftMargin + width, y));
            FormattedText label = Text(TransientPlot.FormatTick(tick), 8.5, muted);
            context.DrawText(label, new Point(LeftMargin - label.Width - 3, y - (label.Height / 2)));
        }

        foreach (double tick in plot.TimeTicks)
        {
            double x = Map(tick, plot.ValueMin).X;
            context.DrawLine(gridPen, new Point(x, TopMargin), new Point(x, TopMargin + height));
            FormattedText label = Text(TransientPlot.FormatTick(tick), 8.5, muted);
            context.DrawText(label, new Point(x - (label.Width / 2), TopMargin + height + 3));
        }

        if (plot.ValueMin < 0 && plot.ValueMax > 0)
        {
            double zeroY = Map(plot.TimeStart, 0).Y;
            context.DrawLine(
                new Pen(muted, 1),
                new Point(LeftMargin, zeroY),
                new Point(LeftMargin + width, zeroY));
        }

        for (int index = 0; index < plot.Series.Count; index++)
        {
            TransientPlotSeries series = plot.Series[index];
            IBrush brush = Brush.Parse(Palette[index % Palette.Length]);
            var pen = new Pen(brush, 1.4, lineCap: PenLineCap.Round);
            Point previous = Map(series.Points[0].Time, series.Points[0].Value);
            for (int point = 1; point < series.Points.Count; point++)
            {
                Point current = Map(series.Points[point].Time, series.Points[point].Value);
                context.DrawLine(pen, previous, current);
                previous = current;
            }

            FormattedText name = Text(series.NodeId, 8.5, brush);
            context.DrawText(name, new Point(LeftMargin + (index * 56), 2));
        }

        // A short unit hint keeps the value axis unambiguous; voltages are the only node quantity.
        context.DrawText(Text("V", 8.5, ink), new Point(2, 2));
    }

    private static FormattedText Text(string text, double size, IBrush brush) => new(
        text,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        Typeface.Default,
        size,
        brush);
}
