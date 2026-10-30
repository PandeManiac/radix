using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

public sealed class DcNetlistJsonTests
{
    [Fact]
    public void LoadsAndSolvesVersionedNetlist()
    {
        DcNetlist netlist = DcNetlistJson.LoadFromString("""
            {
              "format": "radix-dc-netlist",
              "version": 1,
              "ground": "gnd",
              "elements": [
                { "id": "V1", "type": "voltage-source", "nodeA": "in", "nodeB": "gnd", "volts": 10 },
                { "id": "R1", "type": "resistor", "nodeA": "in", "nodeB": "out", "ohms": 1000 },
                { "id": "R2", "type": "resistor", "nodeA": "out", "nodeB": "gnd", "ohms": 1000 }
              ]
            }
            """);

        DcSolution solution = netlist.Solve();

        Assert.Equal(3, netlist.Elements.Count);
        Assert.Equal(5, solution.NodeVoltages["out"]);
        Assert.Equal(5e-3, solution.ElementCurrents["R1"]);
        Assert.Equal(-5e-3, solution.ElementCurrents["V1"]);
    }

    [Fact]
    public void ReportCarriesNodesElementsAndSignConventions()
    {
        DcNetlist netlist = new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 10)
            .AddResistor("R1", "in", "out", 1_000)
            .AddResistor("R2", "out", "gnd", 1_000);
        DcSolution solution = netlist.Solve();

        string json = DcNetlistJson.ToReportJson("divider.json", netlist, solution);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(DcNetlistJson.ReportFormatName, root.GetProperty("format").GetString());
        Assert.Equal(DcNetlistJson.FormatVersion, root.GetProperty("version").GetInt32());
        Assert.Equal("divider.json", root.GetProperty("source").GetString());
        Assert.Equal("gnd", root.GetProperty("ground").GetString());
        Assert.True(root.GetProperty("solved").GetBoolean());
        Assert.Equal(5, root.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("id").GetString() == "out")
            .GetProperty("volts").GetDouble());

        JsonElement r1 = root.GetProperty("elements").EnumerateArray()
            .Single(element => element.GetProperty("id").GetString() == "R1");
        Assert.Equal("resistor", r1.GetProperty("kind").GetString());
        Assert.Equal(5e-3, r1.GetProperty("amperes").GetDouble());
        Assert.Equal(25e-3, r1.GetProperty("absorbedPowerWatts").GetDouble());

        JsonElement v1 = root.GetProperty("elements").EnumerateArray()
            .Single(element => element.GetProperty("id").GetString() == "V1");
        Assert.Equal(-5e-3, v1.GetProperty("amperes").GetDouble());
        Assert.Equal(-50e-3, v1.GetProperty("absorbedPowerWatts").GetDouble());
    }

    [Fact]
    public void SolveFailureReportIsMachineReadable()
    {
        var netlist = new DcNetlist("gnd")
            .AddCurrentSource("I1", "gnd", "out", 1e-3);
        DcSolveException exception = Assert.Throws<DcSolveException>(netlist.Solve);

        string json = DcNetlistJson.ToFailureJson(
            "open.json",
            netlist.GroundNodeId,
            exception.Failure,
            exception.Message);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(DcNetlistJson.ReportFormatName, root.GetProperty("format").GetString());
        Assert.False(root.GetProperty("solved").GetBoolean());
        Assert.Equal("singular-matrix", root.GetProperty("failure").GetString());
        Assert.Equal("open.json", root.GetProperty("source").GetString());
        Assert.Contains("singular", root.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(root.GetProperty("nodes").EnumerateArray());
        Assert.Empty(root.GetProperty("elements").EnumerateArray());
    }

    [Fact]
    public void StrictParserRejectsUnsupportedOrAmbiguousNetlists()
    {
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString("""
            { "format": "wrong", "version": 1, "ground": "gnd", "elements": [] }
            """));
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString("""
            { "format": "radix-dc-netlist", "version": 2, "ground": "gnd", "elements": [] }
            """));
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString("""
            { "format": "radix-dc-netlist", "version": 1, "ground": "gnd" }
            """));
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString("""
            {
              "format": "radix-dc-netlist", "version": 1, "ground": "gnd",
              "elements": [
                { "id": "R1", "type": "resistor", "nodeA": "a", "nodeB": "gnd", "ohms": 1, "volts": 5 }
              ]
            }
            """));
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString("""
            {
              "format": "radix-dc-netlist", "version": 1, "ground": "gnd",
              "elements": [
                { "id": "X1", "type": "resistor", "nodeA": "a", "nodeB": "gnd", "ohms": 1, "extra": true }
              ]
            }
            """));
        Assert.Throws<ProjectFormatException>(() => DcNetlistJson.LoadFromString("""
            {
              "format": "radix-dc-netlist", "version": 1, "ground": "gnd",
              "elements": [
                { "id": "X1", "type": "mystery", "nodeA": "a", "nodeB": "gnd" }
              ]
            }
            """));
    }

    [Fact]
    public void LoadReadsFileAndEnforcesTheSizeBoundary()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {
                  "format": "radix-dc-netlist",
                  "version": 1,
                  "ground": "gnd",
                  "elements": [
                    { "id": "V1", "type": "voltage-source", "nodeA": "a", "nodeB": "gnd", "volts": 3 },
                    { "id": "R1", "type": "resistor", "nodeA": "a", "nodeB": "gnd", "ohms": 1000 }
                  ]
                }
                """);

            DcNetlist netlist = DcNetlistJson.Load(path);

            Assert.Equal(3, netlist.Solve().NodeVoltages["a"]);
            Assert.Throws<ProjectFormatException>(() => DcNetlistJson.Load(path, maxBytes: 8));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
