using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

public sealed class TransientWaveformInterpolationTests
{
    [Fact]
    public void InterpolatesOppositeExtremeValuesWithoutOverflow()
    {
        TransientWaveform waveform = TransientWaveform.Create(
        [
            new TransientWaveformPoint(0, -1e308),
            new TransientWaveformPoint(1, 1e308),
        ]);

        Assert.Equal(0, waveform.ValueAt(0.5));
        Assert.Equal(-5e307, waveform.ValueAt(0.25));
        Assert.Equal(5e307, waveform.ValueAt(0.75));
    }

    [Fact]
    public void InterpolatesAcrossExtremeSignedTimesWithoutLosingTheMidpoint()
    {
        TransientWaveform waveform = TransientWaveform.Create(
        [
            new TransientWaveformPoint(-1e308, 0),
            new TransientWaveformPoint(1e308, 10),
        ]);

        Assert.Equal(5, waveform.ValueAt(0));
    }
}
