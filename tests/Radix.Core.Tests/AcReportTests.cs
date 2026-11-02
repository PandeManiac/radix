using System.Numerics;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Recorded sweeps and comparisons. The quantitative checks are closed forms: a single-pole response
/// with the capacitance doubled is 6.02 dB down once well above both corners, so a comparison that
/// merely "found a difference" cannot pass as correct.
/// </summary>
public sealed class AcReportTests
{
    private static TransientNetlist Rc(double farads) =>
        new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 1)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", farads);

    private static double[] Grid() =>
        [.. AcFrequencySweep.Logarithmic(10, 10_000, 9)];

    [Fact]
    public void AReportRoundTripsThroughJson()
    {
        AcResponse response = AcAnalysis.Run(Rc(1e-6), "V1", Grid());
        string json = AcReport.From(response, "examples/rc.json", "main").ToJson();

        AcReport restored = AcReport.Parse(json);

        Assert.Equal("V1", restored.Drive);
        Assert.Equal(1, restored.Amplitude);
        Assert.Equal("examples/rc.json", restored.Source);
        Assert.Equal("main", restored.Circuit);
        Assert.Equal(response.Points.Count, restored.Points.Count);

        foreach ((AcPoint before, AcPoint after) in response.Points.Zip(restored.Points))
        {
            Assert.Equal(before.FrequencyHertz, after.FrequencyHertz, 12);
            foreach ((string node, Complex value) in before.NodeVoltages)
            {
                Assert.Equal(value.Real, after.NodeVoltages[node].Real, 12);
                Assert.Equal(value.Imaginary, after.NodeVoltages[node].Imaginary, 12);
            }
        }

        Assert.Equal(response.Nodes, restored.Nodes);
    }

    [Fact]
    public void TheCsvHasOneRowPerNodePerFrequency()
    {
        AcResponse response = AcAnalysis.Run(Rc(1e-6), "V1", Grid());
        string csv = AcReport.From(response, "examples/rc.json", null).ToCsv();

        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] rows = [.. lines.Where(line => !line.StartsWith('#'))];

        Assert.Contains("# format: radix-ac-report", csv);
        Assert.Contains("# drive: V1", csv);
        Assert.Equal("hz,node,gain_db,phase_deg", rows[0]);
        Assert.Equal(1 + (response.Points.Count * response.Nodes.Count), rows.Length);
    }

    [Fact]
    public void AnUnchangedSweepDeviationIsZero()
    {
        AcResponse response = AcAnalysis.Run(Rc(1e-6), "V1", Grid());
        IReadOnlyList<AcDeviation> deviations =
            AcComparison.Compare(AcReport.From(response), response);

        // Both nodes are compared: the driven node stays at 0 dB, the output does not move either.
        Assert.Equal(2, deviations.Count);
        AcDeviation output = deviations.Single(deviation => deviation.Node == "out");
        Assert.Equal(0, output.GainDeviationDb, 9);
        Assert.Equal(0, output.MaximumPhaseDeviationDegrees, 9);
    }

    [Fact]
    public void DoublingTheCapacitanceIsSixDecibelsWellAboveTheCorner()
    {
        // Both corners (159 Hz and 80 Hz) are far below 10 kHz, so the ratio of the two responses
        // there is the ratio of the capacitances: 20*log10(2) = 6.0206 dB. Phase is compared where
        // the two stages differ most, near the corners, where the difference approaches 45 degrees.
        AcResponse before = AcAnalysis.Run(Rc(1e-6), "V1", Grid());
        AcResponse after = AcAnalysis.Run(Rc(2e-6), "V1", Grid());

        AcDeviation deviation = AcComparison.Compare(AcReport.From(before), after)
            .Single(entry => entry.Node == "out");

        Assert.InRange(deviation.GainDeviationDb, 5.7, 6.03);
        // The phase gap between two single poles peaks near their geometric mean, at
        // atan(1/sqrt(2)) - atan(sqrt(2)/2) ... or simply: 19.4 degrees, which is what a measured
        // 19.2 must sit against rather than an invented bound.
        Assert.InRange(deviation.MaximumPhaseDeviationDegrees, 19.0, 19.5);
        Assert.Equal(10_000, deviation.WorstGainFrequencyHz, 6);
    }

    [Fact]
    public void DifferentGridsAreRejected()
    {
        AcResponse response = AcAnalysis.Run(Rc(1e-6), "V1", Grid());
        AcReport archived = AcReport.From(
            AcAnalysis.Run(Rc(1e-6), "V1", AcFrequencySweep.Logarithmic(10, 10_000, 5)));

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => AcComparison.Compare(archived, response));

        Assert.Contains("5 frequencies", exception.Message);
        Assert.Contains("this run has 9", exception.Message);
    }

    [Fact]
    public void AReportThatNamesAnotherDriveIsRejected()
    {
        AcResponse response = AcAnalysis.Run(Rc(1e-6), "V1", Grid());
        AcReport archived = AcReport.From(response with { DriveSourceId = "V2" });

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => AcComparison.Compare(archived, response));

        Assert.Contains("V2", exception.Message);
        Assert.Contains("V1", exception.Message);
    }

    [Fact]
    public void AReportFromAnotherFormatIsRejected()
    {
        ProjectFormatException exception = Assert.Throws<ProjectFormatException>(
            () => AcReport.Parse("""{"format":"radix-dc-sweep","version":1,"drive":"V1","amplitude":1,"points":[]}"""));

        Assert.Contains("radix-ac-report", exception.Message);
    }
}
