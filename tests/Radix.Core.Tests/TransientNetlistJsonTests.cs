using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

public sealed class TransientNetlistJsonTests
{
    private const string RcDocument = """
        {
          "format": "radix-transient-netlist",
          "version": 1,
          "ground": "gnd",
          "elements": [
            { "id": "V1", "type": "voltage-source", "nodeA": "in", "nodeB": "gnd", "volts": 5,
              "waveform": [ { "time": 0, "value": 0 }, { "time": 0.0005, "value": 5 }, { "time": 0.001, "value": 0 } ] },
            { "id": "R1", "type": "resistor", "nodeA": "in", "nodeB": "out", "ohms": 1000 },
            { "id": "C1", "type": "capacitor", "nodeA": "out", "nodeB": "gnd", "farads": 1e-6, "initialVolts": 0 },
            { "id": "L1", "type": "inductor", "nodeA": "out", "nodeB": "gnd", "henries": 0.01, "initialAmperes": 0 }
          ],
          "analysis": { "start": 0, "stop": 0.001, "steps": 20, "method": "trapezoidal" }
        }
        """;

    [Fact]
    public void LoadsElementsAnalysisAndWaveform()
    {
        TransientDocument document = TransientNetlistJson.LoadFromString(RcDocument);

        Assert.Equal("gnd", document.Netlist.GroundNodeId);
        Assert.Equal(2, document.Netlist.Elements.Count);
        Assert.Single(document.Netlist.Capacitors);
        Assert.Single(document.Netlist.Inductors);
        Assert.Equal(1e-6, document.Netlist.Capacitors[0].Farads);
        Assert.Equal(0.01, document.Netlist.Inductors[0].Henries);

        TransientOptions options = Assert.IsType<TransientOptions>(document.Analysis);
        Assert.Equal(0.001, options.StopTime);
        Assert.Equal(20, options.Steps);
        Assert.Equal(TransientMethod.Trapezoidal, options.Method);

        TransientWaveform waveform = Assert.IsType<TransientWaveform>(document.Netlist.FindWaveform("V1"));
        Assert.Equal(3, waveform.Points.Count);
        Assert.Equal(2.5, waveform.ValueAt(0.00025), 1e-12);
    }

    [Fact]
    public void ShippedTransientExamplesLoadSolveAndMatchHandCalculations()
    {
        string root = RepositoryRoot();
        string rcPath = Path.Combine(root, "examples", "transient-rc.json");
        string rlcPath = Path.Combine(root, "examples", "transient-rlc.json");
        string pulsePath = Path.Combine(root, "examples", "transient-pulse.json");

        TransientDocument rc = TransientNetlistJson.Load(rcPath);
        TransientResult rcResult = rc.Netlist.Run(Assert.IsType<TransientOptions>(rc.Analysis));
        Assert.True(rcResult.Completed, rcResult.Message);
        Assert.Equal(rc.Analysis!.Steps + 1, rcResult.Points.Count);
        Assert.InRange(rcResult.FinalPoint!.NodeVoltages["out"], 3.5, 5);

        TransientDocument rlc = TransientNetlistJson.Load(rlcPath);
        TransientResult rlcResult = rlc.Netlist.Run(Assert.IsType<TransientOptions>(rlc.Analysis));
        Assert.True(rlcResult.Completed, rlcResult.Message);
        Assert.InRange(rlcResult.FinalPoint!.NodeVoltages["out"], 9.8, 10.2);
        Assert.True(
            rlcResult.Points.Max(point => point.NodeVoltages["out"]) > 10.5,
            "the shipped RLC example should visibly overshoot.");

        TransientDocument pulse = TransientNetlistJson.Load(pulsePath);
        TransientResult pulseResult = pulse.Netlist.Run(Assert.IsType<TransientOptions>(pulse.Analysis));
        Assert.True(pulseResult.Completed, pulseResult.Message);
        double peak = pulseResult.Points.Max(point => point.NodeVoltages["out"]);
        Assert.InRange(peak, 2.0, 3.6);
        Assert.True(pulseResult.FinalPoint!.NodeVoltages["out"] < peak);
    }

    [Fact]
    public void ReportCarriesPointsCompletionAndElementValues()
    {
        TransientDocument document = TransientNetlistJson.LoadFromString(RcDocument);
        TransientResult result = document.Netlist.Run(document.Analysis!);

        string json = TransientNetlistJson.ToReportJson("rc.json", document.Netlist, result);
        using JsonDocument report = JsonDocument.Parse(json);
        JsonElement root = report.RootElement;

        Assert.Equal(TransientNetlistJson.ReportFormatName, root.GetProperty("format").GetString());
        Assert.Equal(TransientNetlistJson.FormatVersion, root.GetProperty("version").GetInt32());
        Assert.Equal("rc.json", root.GetProperty("source").GetString());
        Assert.Equal("gnd", root.GetProperty("ground").GetString());
        Assert.Equal("trapezoidal", root.GetProperty("method").GetString());
        Assert.True(root.GetProperty("completed").GetBoolean());
        Assert.False(root.TryGetProperty("failure", out _));
        Assert.Equal(result.Options.Steps, root.GetProperty("steps").GetInt32());

        JsonElement[] points = root.GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal(result.Points.Count, points.Length);
        Assert.Equal(0, points[0].GetProperty("time").GetDouble());
        Assert.Equal(
            document.Analysis!.StopTime,
            points[^1].GetProperty("time").GetDouble(),
            1e-12);

        JsonElement lastOutNode = points[^1].GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("id").GetString() == "out");
        Assert.Equal(
            result.FinalPoint!.NodeVoltages["out"],
            lastOutNode.GetProperty("volts").GetDouble());

        JsonElement capacitor = points[^1].GetProperty("elements").EnumerateArray()
            .Single(element => element.GetProperty("id").GetString() == "C1");
        Assert.Equal(
            result.FinalPoint.ElementCurrents["C1"],
            capacitor.GetProperty("amperes").GetDouble());
    }

    [Fact]
    public void FailureReportUsesTheStableFailureNameAndKeepsSolvedPoints()
    {
        string json = """
            {
              "format": "radix-transient-netlist",
              "version": 1,
              "ground": "gnd",
              "elements": [
                { "id": "V1", "type": "voltage-source", "nodeA": "in", "nodeB": "gnd", "volts": 5 },
                { "id": "R1", "type": "resistor", "nodeA": "in", "nodeB": "gnd", "ohms": 1000 },
                { "id": "C1", "type": "capacitor", "nodeA": "iso", "nodeB": "gnd", "farads": 1e-6 }
              ]
            }
            """;
        TransientDocument document = TransientNetlistJson.LoadFromString(json);
        TransientOptions options = document.Analysis ?? new TransientOptions { StopTime = 1e-3, Steps = 4 };

        TransientResult result = document.Netlist.Run(options);
        string reportJson = TransientNetlistJson.ToReportJson(null, document.Netlist, result);

        using JsonDocument report = JsonDocument.Parse(reportJson);
        JsonElement root = report.RootElement;
        Assert.False(root.GetProperty("completed").GetBoolean());
        Assert.Equal("floating-network", root.GetProperty("failure").GetString());
        Assert.Contains("iso", root.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(root.GetProperty("points").EnumerateArray());
    }

    [Theory]
    [InlineData("""{ "format": "radix-dc-netlist", "version": 1, "ground": "gnd", "elements": [] }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 2, "ground": "gnd", "elements": [] }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "elements": [] }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "ground": "gnd" }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "ground": "gnd", "elements": [], "extra": 1 }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "ground": "gnd", "elements": [ { "id": "C1", "type": "capacitor", "nodeA": "a", "nodeB": "gnd" } ] }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "ground": "gnd", "elements": [ { "id": "C1", "type": "capacitor", "nodeA": "a", "nodeB": "gnd", "farads": 1e-6, "ohms": 5 } ] }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "ground": "gnd", "elements": [ { "id": "V1", "type": "switch", "nodeA": "a", "nodeB": "gnd" } ] }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "ground": "gnd", "elements": [ { "id": "V1", "type": "voltage-source", "nodeA": "a", "nodeB": "gnd", "volts": 5, "waveform": [ { "time": 0, "value": 0 }, { "time": 0, "value": 5 } ] } ] }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "ground": "gnd", "elements": [ { "id": "V1", "type": "voltage-source", "nodeA": "a", "nodeB": "gnd", "volts": 5, "waveform": [ { "time": 0 } ] } ] }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "ground": "gnd", "elements": [], "analysis": { "start": 0, "stop": 0, "steps": 10 } }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "ground": "gnd", "elements": [], "analysis": { "method": "gear" } }""")]
    [InlineData("""{ "format": "radix-transient-netlist", "version": 1, "ground": "gnd", "elements": [], "analysis": { "steps": 0 } }""")]
    [InlineData("not json")]
    public void RejectsInvalidDocuments(string json)
    {
        Assert.Throws<ProjectFormatException>(() => TransientNetlistJson.LoadFromString(json));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "examples")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository's examples directory.");
    }
}
