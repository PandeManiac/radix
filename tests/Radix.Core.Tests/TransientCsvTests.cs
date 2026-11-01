using System.Globalization;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The CSV is the tabular export, so it must be a faithful table: one header with the time and
/// every node in ordinal order, one row per solved point, round-trip numbers, and quoting that
/// survives a node id containing a comma or a quote.
/// </summary>
public sealed class TransientCsvTests
{
    [Fact]
    public void TheTableHasAHeaderAndOneRowPerPoint()
    {
        TransientResult result = RcNetlist().Run(new TransientOptions { StopTime = 1e-3, Steps = 10 });

        string[] lines = TransientCsv.Write(result).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("time_s,\"gnd\",\"in\",\"out\"", lines[0]);
        Assert.Equal(result.Points.Count + 1, lines.Length);
        string[] first = lines[1].Split(',');
        Assert.Equal(4, first.Length);
        Assert.Equal(0, double.Parse(first[0], CultureInfo.InvariantCulture));
        Assert.Equal(0, double.Parse(first[1], CultureInfo.InvariantCulture));
        Assert.Equal(5, double.Parse(first[2], CultureInfo.InvariantCulture));
        Assert.Equal(0, double.Parse(first[3], CultureInfo.InvariantCulture));
        // The last row is the final solved point, whose time must round-trip exactly.
        string[] last = lines[^1].Split(',');
        Assert.Equal(
            result.FinalPoint!.Time,
            double.Parse(last[0], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NumbersRoundTripAndNodeNamesAreQuoted()
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "a,b", "gnd", 1.0 / 3)
            .AddResistor("R1", "a,b", "gnd", 1_000);

        TransientResult result = netlist.Run(new TransientOptions { StopTime = 1e-3, Steps = 4 });

        string csv = TransientCsv.Write(result);
        string header = csv.Split('\n')[0];
        Assert.Contains("\"a,b\"", header, StringComparison.Ordinal);
        Assert.Equal(1.0 / 3, result.InitialPoint!.NodeVoltages["a,b"]);
        // A round-trip format means the text parses back to the same double, not a rounded one.
        // Quoting applies to the header; a data row has one field per node, so "a,b" is field 1.
        string[] row = csv.Split('\n')[1].Split(',');
        Assert.Equal(3, row.Length);
        Assert.Equal(
            result.InitialPoint.NodeVoltages["a,b"],
            double.Parse(row[1], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AQuotedNodeNameSurvivesItsOwnQuotes()
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "odd\"node", "gnd", 5)
            .AddResistor("R1", "odd\"node", "gnd", 1_000);

        string header = TransientCsv.Write(
                netlist.Run(new TransientOptions { StopTime = 1e-3, Steps = 2 }))
            .Split('\n')[0];

        Assert.Contains("\"odd\"\"node\"", header, StringComparison.Ordinal);
    }

    private static TransientNetlist RcNetlist() => new TransientNetlist("gnd")
        .AddVoltageSource("V1", "in", "gnd", 5)
        .AddResistor("R1", "in", "out", 1_000)
        .AddCapacitor("C1", "out", "gnd", 1e-6)
        .SetCapacitorInitialVoltage("C1", 0);
}
