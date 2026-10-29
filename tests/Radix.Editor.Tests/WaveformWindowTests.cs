using Radix.Editor.Rendering;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class WaveformWindowTests
{
    [Fact]
    public void FitAndSetNormalizeRanges()
    {
        var window = new WaveformWindow();

        window.Fit(0);
        Assert.Equal(0, window.Start);
        Assert.Equal(WaveformWindow.MinSpan, window.End);

        window.Fit(160);
        Assert.Equal(0, window.Start);
        Assert.Equal(160, window.End);

        window.Set(50, 10);
        Assert.Equal(10, window.Start);
        Assert.Equal(50, window.End);

        window.Set(100, 100);
        Assert.Equal(100, window.Start);
        Assert.Equal(102, window.End);
    }

    [Fact]
    public void ZoomKeepsTheAnchorAtItsRelativePosition()
    {
        var window = new WaveformWindow();
        window.Fit(1000);

        window.ZoomAt(250, 0.5, 1000);

        Assert.Equal(125, window.Start);
        Assert.Equal(625, window.End);

        for (int i = 0; i < 20; i++)
        {
            window.ZoomAt(375, 0.5, 1000);
        }

        Assert.True(window.Span >= WaveformWindow.MinSpan);
        Assert.True(window.Start >= 0);
        Assert.True(window.End <= 1000);
    }

    [Fact]
    public void ZoomingOutClampsToTheFullTrace()
    {
        var window = new WaveformWindow();
        window.Fit(1000);
        window.Set(200, 700);

        window.ZoomAt(200, 4, 1000);

        Assert.True(window.IsFull(1000));
        Assert.Equal(0, window.Start);
        Assert.Equal(1000, window.End);
    }

    [Fact]
    public void PanStopsAtBothDataEdges()
    {
        var window = new WaveformWindow();
        window.Fit(1000);
        window.Set(100, 500);

        window.Pan(-1000, 1000);
        Assert.Equal(0, window.Start);
        Assert.Equal(400, window.End);

        window.Pan(2000, 1000);
        Assert.Equal(600, window.Start);
        Assert.Equal(1000, window.End);

        window.Pan(long.MinValue, 1000);
        Assert.Equal(0, window.Start);
        Assert.Equal(400, window.End);

        window.Pan(long.MaxValue, 1000);
        Assert.Equal(600, window.Start);
        Assert.Equal(1000, window.End);
    }

    [Fact]
    public void ClampToAShorterTraceKeepsTheSpanInsideIt()
    {
        var window = new WaveformWindow();
        window.Fit(1000);
        window.Set(800, 1000);

        window.ClampTo(400);

        Assert.Equal(200, window.Start);
        Assert.Equal(400, window.End);
        Assert.False(window.IsFull(400));
    }
}
