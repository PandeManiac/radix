using System.Globalization;
using System.Text;
using Radix.Core.Serialization;

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
        int height = 280,
        TransientStamp? stamp = null)
    {
        ArgumentNullException.ThrowIfNull(plot);
        if (plot.IsEmpty)
        {
            throw new ArgumentException("A plot with no series has nothing to render.", nameof(plot));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(width, 160);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 100);

        // A stamped document reserves a caption line below the axis labels, so the picture
        // carries the same provenance as the table without covering a tick.
        double captionSpace = stamp is null ? 0 : 14;
        double plotWidth = width - LeftMargin - RightMargin;
        double plotHeight = height - TopMargin - BottomMargin - captionSpace;
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
            $"viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\">");        svg.AppendLine($"  <title>{Escape(string.IsNullOrWhiteSpace(title) ? "Transient plot" : title)}</title>");
        if (stamp is not null)
        {
            var attributes = new StringBuilder();
            foreach (KeyValuePair<string, string> field in stamp.Fields)
            {
                attributes
                    .Append(' ').Append(field.Key)
                    .Append("=\"").Append(Escape(field.Value)).Append('"');
            }

            svg.AppendLine("  <metadata>");
            svg.AppendLine($"    <provenance xmlns=\"https://radix.local/ns/transient\"{attributes}/>");
            svg.AppendLine("  </metadata>");
        }
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

        for (int index = 0; index < plot.TimeTicks.Count; index++)
        {
            double tick = plot.TimeTicks[index];
            string x = N(X(tick));
            svg.AppendLine(
                $"  <line x1=\"{x}\" y1=\"{N(TopMargin)}\" x2=\"{x}\" y2=\"{N(TopMargin + plotHeight)}\" " +
                "stroke=\"#E9EEF3\" stroke-width=\"1\" stroke-dasharray=\"2 3\"/>");
            // The model owns the label, so a log-frequency axis exports as decades rather than
            // as the exponents it plots.
            svg.AppendLine(
                $"  <text x=\"{x}\" y=\"{N(TopMargin + plotHeight + 12)}\" font-size=\"8.5\" " +
                $"fill=\"#6F8092\" text-anchor=\"middle\">{Escape(plot.TimeTickLabel(index))}</text>");
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
                $"fill=\"{colour}\">{Escape(series.Label)}</text>");
        }

        // Axis units come from the model - volts for a transient, dB for a response - and the
        // horizontal unit sits inside the frame so it cannot collide with a tick label.
        svg.AppendLine(
            $"  <text x=\"2\" y=\"12\" font-size=\"8.5\" fill=\"#1F2A36\">{Escape(plot.VerticalUnit)}</text>");
        if (plot.HorizontalUnit.Length > 0)
        {
            svg.AppendLine(
                $"  <text x=\"{N(LeftMargin + plotWidth - 4)}\" y=\"{N(TopMargin + plotHeight - 6)}\" " +
                $"font-size=\"8.5\" fill=\"#1F2A36\" text-anchor=\"end\">{Escape(plot.HorizontalUnit)}</text>");
        }
        if (stamp is not null)
        {
            string caption = string.Join(
                " · ",
                new[] { stamp.Circuit, stamp.Method, $"{stamp.Steps} steps", $"{stamp.Points} points" }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            svg.AppendLine(
                $"  <text x=\"{N(LeftMargin)}\" y=\"{N(height - 4)}\" font-size=\"8.5\" " +
                $"fill=\"#6F8092\">{Escape(caption)}</text>");
        }

        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    /// <summary>
    /// Renders two plots as one document, stacked: a frequency response is gain and phase read
    /// together, and two files that must be kept in step are worse than one file.
    /// <para>
    /// Each panel keeps its own axes, ticks, labels and units; a translated group places the
    /// second below the first, so the layout is the only thing this method decides.
    /// </para>
    /// </summary>
    public static string RenderBode(
        TransientPlot gain,
        TransientPlot phase,
        string? title = null,
        int width = 640,
        int gainHeight = 210,
        int phaseHeight = 170)
    {
        ArgumentNullException.ThrowIfNull(gain);
        ArgumentNullException.ThrowIfNull(phase);

        string gainBody = Body(Render(gain, null, title, width, gainHeight));
        string phaseBody = Body(Render(phase, null, null, width, phaseHeight));
        int height = gainHeight + phaseHeight;

        var svg = new StringBuilder();
        svg.AppendLine(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" " +
            $"viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\">");
        svg.AppendLine(
            $"  <title>{Escape(string.IsNullOrWhiteSpace(title) ? "Frequency response" : title)}</title>");
        svg.AppendLine(
            $"  <rect x=\"0\" y=\"0\" width=\"{width}\" height=\"{height}\" fill=\"#F8FAFC\"/>");
        svg.AppendLine("  <g>");
        svg.Append(gainBody);
        svg.AppendLine("  </g>");
        svg.AppendLine($"  <g transform=\"translate(0,{gainHeight})\">");
        svg.Append(phaseBody);
        svg.AppendLine("  </g>");
        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    /// <summary>
    /// The drawing part of a rendered document: the outer element, its title and its background
    /// are dropped, because the caller supplies one of each for the composed document.
    /// </summary>
    private static string Body(string document)
    {
        var body = new StringBuilder();
        foreach (string line in document.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("<svg", StringComparison.Ordinal) ||
                trimmed.StartsWith("<title", StringComparison.Ordinal) ||
                trimmed.StartsWith("<rect x=\"0\" y=\"0\"", StringComparison.Ordinal) ||
                trimmed.StartsWith("</svg>", StringComparison.Ordinal))
            {
                continue;
            }

            body.AppendLine(line);
        }

        return body.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
