using System.Globalization;
using System.Xml.Linq;
using Radix.Editor.Rendering;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The SVG export is the shareable form of the plot, so it is checked structurally: the file
/// must parse, every node must become exactly one polyline, all coordinates must stay inside
/// the view box, node names must be escaped, and the marker must match the panel's.
/// </summary>
public sealed class TransientPlotSvgTests
{
    [Fact]
    public void EveryNodeBecomesOnePolylineInsideTheViewBox()
    {
        TransientPlot plot = BuildPlot();
        string svg = TransientPlotSvg.Render(plot, title: "Transient: rc-charge");

        XDocument document = XDocument.Parse(svg);
        List<XElement> polylines = document.Descendants()
            .Where(element => element.Name.LocalName == "polyline")
            .ToList();
        Assert.Equal(3, polylines.Count);

        foreach (XElement polyline in polylines)
        {
            string[] points = polyline.Attribute("points")!.Value.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(points);
            foreach (string pair in points)
            {
                string[] coordinates = pair.Split(',');
                double x = double.Parse(coordinates[0], CultureInfo.InvariantCulture);
                double y = double.Parse(coordinates[1], CultureInfo.InvariantCulture);
                Assert.InRange(x, 48, 630);
                Assert.InRange(y, 18, 260);
            }
        }

        Assert.Contains("gnd", svg, StringComparison.Ordinal);
        Assert.Contains("out", svg, StringComparison.Ordinal);
        Assert.Contains("<title>Transient: rc-charge</title>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeNamesAreEscapedSoTheFileStaysWellFormed()
    {
        TransientPlot plot = Assert.IsType<TransientPlot>(TransientPlot.Create(
        [
            new TransientPlotSeries("a<b&c", [new(0, 0), new(1, 5)]),
        ]));

        string svg = TransientPlotSvg.Render(plot);

        Assert.Contains("a&lt;b&amp;c", svg, StringComparison.Ordinal);
        XDocument.Parse(svg);
    }

    [Fact]
    public void TheProbeMarkerIsDrawnOnlyInsideThePlottedRange()
    {
        TransientPlot plot = BuildPlot();

        string marked = TransientPlotSvg.Render(plot, markerTime: 0.0015);
        Assert.Contains("stroke-dasharray=\"3 3\"", marked, StringComparison.Ordinal);
        Assert.Contains("0.0015", marked, StringComparison.Ordinal);

        string outside = TransientPlotSvg.Render(plot, markerTime: 5);
        Assert.DoesNotContain("stroke-dasharray=\"3 3\"", outside, StringComparison.Ordinal);
    }

    [Fact]
    public void ASizeWithNoRoomForTheAxesIsRejected()
    {
        TransientPlot plot = BuildPlot();

        Assert.Throws<ArgumentOutOfRangeException>(() => TransientPlotSvg.Render(plot, width: 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransientPlotSvg.Render(plot, height: 80));
    }

    private static TransientPlot BuildPlot() => Assert.IsType<TransientPlot>(TransientPlot.Create(
    [
        new TransientPlotSeries("gnd", [new(0, 0), new(0.001, 0), new(0.002, 0)]),
        new TransientPlotSeries("in", [new(0, 0), new(0.001, 5), new(0.002, 5)]),
        new TransientPlotSeries("out", [new(0, 0), new(0.001, 3.2), new(0.002, 4.4)]),
    ]));
}
