using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The report is what a script or CI run reads, so a refinement study must be either absent
/// or complete and self-describing: levels, order, estimate, and the node that moved most,
/// with a failure named rather than silently dropped.
/// </summary>
public sealed class TransientRefinementJsonTests
{
    [Fact]
    public void AStudyAppearsInTheReportWithItsLevelsAndEstimate()
    {
        TransientNetlist netlist = RcNetlist();
        TransientOptions options = new()
        {
            StopTime = 5e-3,
            Steps = 40,
            Method = TransientMethod.Trapezoidal,
        };
        TransientResult result = netlist.Run(options);
        TransientRefinement study = TransientRefinement.Analyze(netlist, options, baseline: result);

        string json = TransientNetlistJson.ToReportJson(
            "rc", netlist, result, "rc-charge", study);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement refinement = document.RootElement.GetProperty("refinement");
        Assert.True(refinement.GetProperty("completed").GetBoolean());
        // Null fields are omitted rather than written as null, so a failure only appears
        // when there is one.
        Assert.False(refinement.TryGetProperty("failure", out _));

        JsonElement[] levels = refinement.GetProperty("levels").EnumerateArray().ToArray();
        Assert.Equal(3, levels.Length);
        Assert.Equal(40, levels[0].GetProperty("steps").GetInt32());
        Assert.True(levels[0].GetProperty("maximumDeviationVolts").GetDouble() == 0);
        // The reference level has no earlier level to compare against, so it has no order.
        Assert.False(levels[0].TryGetProperty("observedOrder", out _));

        double order = levels[^1].GetProperty("observedOrder").GetDouble();
        Assert.InRange(order, 1.6, 2.4);
        Assert.Equal(
            order,
            refinement.GetProperty("observedOrder").GetDouble(),
            12);
        Assert.True(refinement.GetProperty("estimatedRelativeError").GetDouble() > 0);

        JsonElement worst = refinement.GetProperty("worstNode");
        Assert.Equal("out", worst.GetProperty("id").GetString());
        Assert.True(worst.GetProperty("maximumDeviationVolts").GetDouble() >= 0);
        Assert.True(worst.GetProperty("referenceVolts").GetDouble() > 0);
        Assert.True(worst.GetProperty("relativeDeviation").GetDouble() >= 0);
    }

    [Fact]
    public void ARunWithoutAStudyReportsNoRefinementBlock()
    {
        TransientNetlist netlist = RcNetlist();
        TransientResult result = netlist.Run(new TransientOptions { StopTime = 5e-3, Steps = 40 });

        string json = TransientNetlistJson.ToReportJson("rc", netlist, result);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("refinement", out _));
    }

    private static TransientNetlist RcNetlist() => new TransientNetlist("gnd")
        .AddVoltageSource("V1", "in", "gnd", 5)
        .AddResistor("R1", "in", "out", 1_000)
        .AddCapacitor("C1", "out", "gnd", 1e-6)
        .SetCapacitorInitialVoltage("C1", 0);
}
