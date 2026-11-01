using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// An exported file has to be traceable to the run that produced it, so the stamp names the
/// source, circuit, method and interval, and does so deterministically: no clock means two
/// exports of one run are byte-identical.
/// </summary>
public sealed class TransientStampTests
{
    [Fact]
    public void TheStampDescribesTheRunAndIsDeterministic()
    {
        TransientNetlist netlist = RcNetlist();
        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 1e-3,
            Steps = 8,
            Method = TransientMethod.Trapezoidal,
            SettlingBand = 0.05,
        });

        TransientStamp stamp = TransientStamp.From(result, source: "rc.json", circuit: "rc-charge");

        Assert.Equal("rc.json", stamp.Source);
        Assert.Equal("rc-charge", stamp.Circuit);
        Assert.Equal("trapezoidal", stamp.Method);
        Assert.Equal(8, stamp.Steps);
        Assert.Equal(9, stamp.Points);
        Assert.True(stamp.Completed);
        Assert.False(stamp.Adaptive);
        Assert.Equal(0.05, stamp.SettlingBand);

        Assert.Equal(
            TransientStamp.From(result, "rc.json", "rc-charge").Fields,
            stamp.Fields);

        Dictionary<string, string> fields = stamp.Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("rc.json", fields["source"]);
        Assert.Equal("rc-charge", fields["circuit"]);
        Assert.Equal("trapezoidal", fields["method"]);
        // Round-trip text, so a reader recovers exactly the interval that was solved.
        Assert.Equal(1e-3, double.Parse(fields["stop_s"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ACsvStartsWithTheStampAsCommentsThenTheHeader()
    {
        TransientResult result = RcNetlist().Run(new TransientOptions { StopTime = 1e-3, Steps = 4 });

        string csv = TransientCsv.Write(
            result, TransientStamp.From(result, source: "rc.json", circuit: "rc"));

        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("# source: rc.json", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line == "# circuit: rc");
        Assert.Contains(lines, line => line == "# steps: 4");
        // The header is still findable without comment awareness, and the body is untouched.
        int header = Array.FindIndex(lines, line => line.StartsWith("time_s", StringComparison.Ordinal));
        Assert.True(header > 0);
        Assert.Equal(result.Points.Count + 1, lines.Length - header);

        // Without a stamp the file is exactly the table it always was.
        Assert.StartsWith("time_s", TransientCsv.Write(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ALineBreakInALabelCannotForgeAHeaderRow()
    {
        TransientResult result = RcNetlist().Run(new TransientOptions { StopTime = 1e-3, Steps = 2 });

        string csv = TransientCsv.Write(
            result, TransientStamp.From(result, source: "a\ntime_s,b"));

        string[] lines = csv.Split('\n');
        // The newline is folded into the comment, so only one header row exists.
        Assert.Single(lines, line => line.StartsWith("time_s", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("# source: a time_s,b", StringComparison.Ordinal));
    }

    private static TransientNetlist RcNetlist() => new TransientNetlist("gnd")
        .AddVoltageSource("V1", "in", "gnd", 5)
        .AddResistor("R1", "in", "out", 1_000)
        .AddCapacitor("C1", "out", "gnd", 1e-6)
        .SetCapacitorInitialVoltage("C1", 0);
}
