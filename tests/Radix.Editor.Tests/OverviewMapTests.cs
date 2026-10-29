using Avalonia;
using Radix.Editor.Rendering;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class OverviewMapTests
{
    [Fact]
    public void ContentIsCenteredInThePlotWithoutDistortion()
    {
        OverviewMap map = Assert.IsType<OverviewMap>(OverviewMap.Create(
            new Rect(0, 0, 1000, 500),
            new Rect(10, 20, 250, 125)));

        Assert.Equal(0.25, map.Scale, precision: 9);
        Assert.Equal(new Rect(10, 20, 250, 125), map.ContentFrame);
        Assert.Equal(new Point(135, 82.5), map.ToScreen(new Point(500, 250)));
    }

    [Fact]
    public void PointerMappingRoundTripsAndClampsToContent()
    {
        OverviewMap map = Assert.IsType<OverviewMap>(OverviewMap.Create(
            new Rect(100, 200, 400, 300),
            new Rect(0, 0, 200, 150)));

        Point world = new(350, 330);
        Assert.Equal(world, map.ToWorld(map.ToScreen(world)));

        Point before = map.ToWorld(new Point(-100, -100));
        Assert.Equal(100, before.X);
        Assert.Equal(200, before.Y);
        Point after = map.ToWorld(new Point(500, 500));
        Assert.Equal(500, after.X);
        Assert.Equal(500, after.Y);
    }

    [Fact]
    public void ViewportFrameIsClippedToContent()
    {
        OverviewMap map = Assert.IsType<OverviewMap>(OverviewMap.Create(
            new Rect(0, 0, 1000, 500),
            new Rect(10, 20, 250, 125)));

        Rect partial = map.ViewportFrame(new Rect(-100, -100, 300, 300));
        Assert.Equal(10, partial.X, precision: 9);
        Assert.Equal(20, partial.Y, precision: 9);
        Assert.Equal(50, partial.Width, precision: 9);
        Assert.Equal(50, partial.Height, precision: 9);

        Rect all = map.ViewportFrame(new Rect(-10_000, -10_000, 20_000, 20_000));
        Assert.Equal(map.ContentFrame, all);
        Assert.Equal(default, map.ViewportFrame(new Rect(-500, -500, 100, 100)));
    }

    [Fact]
    public void ContentRectanglesStayInsideThePlot()
    {
        OverviewMap map = Assert.IsType<OverviewMap>(OverviewMap.Create(
            new Rect(50, 60, 800, 400),
            new Rect(0, 0, 200, 100)));

        Rect box = map.MapRect(new Rect(150, 110, 200, 100));
        Assert.Equal(25, box.X, precision: 9);
        Assert.Equal(12.5, box.Y, precision: 9);
        Assert.Equal(50, box.Width, precision: 9);
        Assert.Equal(25, box.Height, precision: 9);
        Assert.True(map.Plot.Contains(box.TopLeft));
        Assert.True(map.Plot.Contains(new Point(box.Right, box.Bottom)));
    }

    [Fact]
    public void EmptyOrInvalidInputProducesNoMap()
    {
        Assert.Null(OverviewMap.Create(default, new Rect(0, 0, 100, 100)));
        Assert.Null(OverviewMap.Create(new Rect(0, 0, 100, 100), default));
        Assert.Null(OverviewMap.Create(new Rect(double.NaN, 0, 100, 100), new Rect(0, 0, 100, 100)));
    }
}
