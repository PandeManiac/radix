using System.Globalization;
using System.Text;

namespace Radix.Editor.Rendering;

/// <summary>
/// Renders a <see cref="TransientPlot"/> as a standalone SVG document so a run can be dropped
/// into a note or a report without a screenshot. The margin and mapping match the control, so
/// the file shows what the panel shows, and colours are the light palette's because a document
/// has no theme to follow. Node names are escaped, so an unusual node id cannot produce a
/// malformed file.
/// </summary>
public static class TransientPlotSvg
{
    private const double LeftMargin = 48;
    private const double RightMargin = 10;
    private const double TopMargin = 18;
    private const double BottomMargin = 20;

    private static readonly string[] Palette =
    [
        "#2B6CB0",
        "#C53030",
        "#2F855A",
        "#6B46C1",
        "#B7791F",
        "#2C7A7B",
    ];

    /// <summary>
    /// Draws <paramref name="plot"/> as SVG. <paramref name="markerTime"/> adds the same dashed
    /// probe line the panel shows, clamped into the plotted range.
    /// </summary>
    public static string Render(
        TransientPlot plot,
        double? markerTime = null,
        string? title = null,
        int width = 640,
        int height = 280)
    {
        ArgumentNullException.ThrowIfNull(plot);
        if (plot.IsEmpty)
        {
            throw new ArgumentException("A plot with no series has nothing to render.", nameof(plot));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(width, 160);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 100);

        double plotWidth = width - LeftMargin - RightMargin;
        double plotHeight = height - TopMargin - BottomMargin;
        if (plotWidth < 40 || plotHeight < 30)
        {
            throw new ArgumentException("The requested size leaves no room for the axes.", nameof(width));
        }

        double X(double time) => LeftMargin + (plot.NormalizeX(time) * plotWidth);
        double Y(double value) => TopMargin + ((1 - plot.NormalizeY(value)) * plotHeight);
        string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        var svg = new StringBuilder();
        svg.AppendLine(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" " +
            $"viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\">");
        svg.AppendLine(
            $"  <title>{Escape(string.IsNullOrWhiteSpace(title) ? "Transient plot" : title)}</title>");
        svg.AppendLine(
            $"  <rect x=\"0\" y=\"0\" width=\"{width}\" height=\"{height}\" fill=\"#F8FAFC\"/>");

        foreach (double tick in plot.ValueTicks)
        {
            string y = N(Y(tick));
            svg.AppendLine(
                $"  <line x1=\"{N(LeftMargin)}\" y1=\"{y}\" x2=\"{N(LeftMargin + plotWidth)}\" y2=\"{y}\" " +
                "stroke=\"#E9EEF3\" stroke-width=\"1\" stroke-dasharray=\"2 3\"/>");
            svg.AppendLine(
                $"  <text x=\"{N(LeftMargin - 3)}\" y=\"{N(Y(tick) + 3)}\" font-size=\"8.5\" " +
                $"fill=\"#6F8092\" text-anchor=\"end\">{Escape(TransientPlot.FormatTick(tick))}</text>");
        }

        foreach (double tick in plot.TimeTicks)
        {
            string x = N(X(tick));
            svg.AppendLine(
                $"  <line x1=\"{x}\" y1=\"{N(TopMargin)}\" x2=\"{x}\" y2=\"{N(TopMargin + plotHeight)}\" " +
                "stroke=\"#E9EEF3\" stroke-width=\"1\" stroke-dasharray=\"2 3\"/>");
            svg.AppendLine(
                $"  <text x=\"{x}\" y=\"{N(TopMargin + plotHeight + 12)}\" font-size=\"8.5\" " +
                $"fill=\"#6F8092\" text-anchor=\"middle\">{Escape(TransientPlot.FormatTick(tick))}</text>");
        }

        svg.AppendLine(
            $"  <line x1=\"{N(LeftMargin)}\" y1=\"{N(TopMargin)}\" x2=\"{N(LeftMargin)}\" " +
            $"y2=\"{N(TopMargin + plotHeight)}\" stroke=\"#E9EEF3\" stroke-width=\"1\"/>");
        svg.AppendLine(
            $"  <line x1=\"{N(LeftMargin)}\" y1=\"{N(TopMargin + plotHeight)}\" " +
            $"x2=\"{N(LeftMargin + plotWidth)}\" y2=\"{N(TopMargin + plotHeight)}\" " +
            "stroke=\"#E9EEF3\" stroke-width=\"1\"/>");

        if (plot.ValueMin < 0 && plot.ValueMax > 0)
        {
            svg.AppendLine(
                $"  <line x1=\"{N(LeftMargin)}\" y1=\"{N(Y(0))}\" x2=\"{N(LeftMargin + plotWidth)}\" " +
                $"y2=\"{N(Y(0))}\" stroke=\"#6F8092\" stroke-width=\"1\"/>");
        }

        if (markerTime is double marker && double.IsFinite(marker) &&
            marker >= plot.TimeStart && marker <= plot.TimeEnd)
        {
            string markerX = N(X(marker));
            svg.AppendLine(
                $"  <line x1=\"{markerX}\" y1=\"{N(TopMargin)}\" x2=\"{markerX}\" " +
                $"y2=\"{N(TopMargin + plotHeight)}\" stroke=\"#2B6CB0\" stroke-width=\"1\" " +
                "stroke-dasharray=\"3 3\"/>");
            svg.AppendLine(
                $"  <text x=\"{markerX}\" y=\"{N(TopMargin + 10)}\" font-size=\"8.5\" fill=\"#1F2A36\" " +
                $"text-anchor=\"middle\">{Escape(TransientPlot.FormatTick(marker))}</text>");
        }

        for (int index = 0; index < plot.Series.Count; index++)
        {
            TransientPlotSeries series = plot.Series[index];
            string colour = Palette[series.ColorIndex % Palette.Length];
            var points = new StringBuilder();
            foreach (TransientPlotPoint point in series.Points)
            {
                points.Append(CultureInfo.InvariantCulture, $"{N(X(point.Time))},{N(Y(point.Value))} ");
            }

            svg.AppendLine(
                $"  <polyline points=\"{points.ToString().TrimEnd()}\" fill=\"none\" stroke=\"{colour}\" " +
                "stroke-width=\"1.4\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
            svg.AppendLine(
                $"  <text x=\"{N(LeftMargin + (index * 56))}\" y=\"11\" font-size=\"8.5\" " +
                $"fill=\"{colour}\">{Escape(series.NodeId)}</text>");
        }

        svg.AppendLine(
            $"  <text x=\"2\" y=\"12\" font-size=\"8.5\" fill=\"#1F2A36\">V</text>");
        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
