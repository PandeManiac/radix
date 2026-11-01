using Radix.Editor.Rendering;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The plot's scaling, padding, and decimation live in a plain geometry model, so the
/// numbers that reach the screen can be checked without a window: a trace must fill the
/// frame, a flat trace must still get a usable range, and decimation must keep the last
/// sample because that is the settled value.
/// </summary>
public sealed class TransientPlotTests
{
    [Fact]
    public void SeriesShareTheTimeAxisAndAreDecimated()
    {
        TransientPlot plot = Assert.IsType<TransientPlot>(TransientPlot.Create(
        [
            Series("a", 1_000, time => time * 1e-6, time => 5 * time),
            Series("b", 500, time => time * 1e-6, time => -2),
        ]));

        Assert.Equal(2, plot.Series.Count);
        Assert.All(plot.Series, series => Assert.True(
            series.Points.Count <= TransientPlot.DefaultMaximumPointsPerSeries + 1,
            $"{series.NodeId} kept {series.Points.Count} points"));
        Assert.Equal(0, plot.Series[0].Points[0].Time);
        Assert.Equal(999 * 1e-6, plot.Series[0].Points[^1].Time, 12);
        Assert.Equal(-2, plot.Series[1].Points[^1].Value);
    }

    [Fact]
    public void AFlatSeriesStillGetsAUsableRange()
    {
        TransientPlot plot = Assert.IsType<TransientPlot>(TransientPlot.Create(
        [
            Series("flat", 10, time => time * 1e-3, _ => 5),
        ]));

        Assert.True(plot.ValueMin < 5, $"min {plot.ValueMin} should be below the series");
        Assert.True(plot.ValueMax > 5, $"max {plot.ValueMax} should be above the series");
        Assert.Equal(0.5, plot.NormalizeY(5), 9);
    }

    [Fact]
    public void NormalizedMappingPutsTheCornersAtTheFrameEdges()
    {
        TransientPlot plot = Assert.IsType<TransientPlot>(TransientPlot.Create(
        [
            Series("ramp", 20, time => time, time => time),
        ]));

        Assert.Equal(0, plot.NormalizeX(0), 12);
        Assert.Equal(1, plot.NormalizeX(19), 12);
        Assert.Equal(0, plot.NormalizeY(plot.ValueMin), 12);
        Assert.Equal(1, plot.NormalizeY(plot.ValueMax), 12);
    }

    [Fact]
    public void TicksCoverBothAxisEnds()
    {
        TransientPlot plot = Assert.IsType<TransientPlot>(TransientPlot.Create(
        [
            Series("ramp", 20, time => time, time => time),
        ]));

        Assert.Equal(TransientPlot.TickCount, plot.TimeTicks.Count);
        Assert.Equal(plot.TimeStart, plot.TimeTicks[0], 12);
        Assert.Equal(plot.TimeEnd, plot.TimeTicks[^1], 12);
        Assert.Equal(plot.ValueMin, plot.ValueTicks[0], 12);
        Assert.Equal(plot.ValueMax, plot.ValueTicks[^1], 12);
        Assert.Equal("0", TransientPlot.FormatTick(0));
    }

    [Fact]
    public void NothingToDrawProducesNoPlot()
    {
        Assert.Null(TransientPlot.Create([]));
        Assert.Null(TransientPlot.Create([Series("single", 1, _ => 0, _ => 1)]));
        Assert.Null(TransientPlot.Create([
            new TransientPlotSeries("nan", [new TransientPlotPoint(double.NaN, 1)]),
        ]));
    }

    private static TransientPlotSeries Series(
        string nodeId,
        int count,
        Func<int, double> time,
        Func<double, double> value)
    {
        var points = new TransientPlotPoint[count];
        for (int index = 0; index < count; index++)
        {
            double t = time(index);
            points[index] = new TransientPlotPoint(t, value(t));
        }

        return new TransientPlotSeries(nodeId, points);
    }
}
