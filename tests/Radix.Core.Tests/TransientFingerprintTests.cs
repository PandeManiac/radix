using Radix.Core.Electrical;
using Radix.Core.Serialization;
using System.Text.Json;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A fingerprint is a claim about identity, so it is tested where identity is unambiguous: the
/// same equations built in a different order must match, and any change to a value, a preset, a
/// waveform or the ground node must not. Without this, an export can name a circuit id that two
/// different revisions share.
/// </summary>
public sealed class TransientFingerprintTests
{
    [Fact]
    public void TheSameCircuitBuiltInADifferentOrderSharesItsFingerprint()
    {
        TransientNetlist forward = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6);
        TransientNetlist reversed = new TransientNetlist("gnd")
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .AddResistor("R1", "in", "out", 1_000)
            .AddVoltageSource("V1", "in", "gnd", 5);

        Assert.Equal(forward.Fingerprint(), reversed.Fingerprint());
        Assert.Equal(16, forward.Fingerprint().Length);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1_001)]
    public void AChangedValueChangesTheFingerprint(double ohms)
    {
        string baseline = Rc(1_000).Fingerprint();

        Assert.NotEqual(baseline, Rc(ohms).Fingerprint());
    }

    [Fact]
    public void APresetInitialValueIsPartOfTheFingerprint()
    {
        Assert.NotEqual(
            Rc(1_000).Fingerprint(),
            Rc(1_000).SetCapacitorInitialVoltage("C1", 2).Fingerprint());
    }

    [Fact]
    public void AWaveformAndTheGroundNodeArePartOfTheFingerprint()
    {
        TransientNetlist plain = Rc(1_000);
        TransientNetlist driven = Rc(1_000).SetWaveform(
            "V1",
            [new TransientWaveformPoint(0, 0), new TransientWaveformPoint(1e-3, 5)]);

        Assert.NotEqual(plain.Fingerprint(), driven.Fingerprint());
        // The same equations referenced to a differently named ground are a different netlist.
        Assert.NotEqual(
            plain.Fingerprint(),
            new TransientNetlist("ground")
                .AddVoltageSource("V1", "in", "ground", 5)
                .AddResistor("R1", "in", "out", 1_000)
                .AddCapacitor("C1", "out", "ground", 1e-6)
                .Fingerprint());
    }

    [Fact]
    public void AResultCarriesTheFingerprintAndItReachesTheStamp()
    {
        TransientNetlist netlist = Rc(1_000);
        TransientResult result = netlist.Run(new TransientOptions { StopTime = 1e-3, Steps = 4 });

        Assert.Equal(netlist.Fingerprint(), result.NetlistFingerprint);

        TransientStamp stamp = TransientStamp.From(result, source: "rc.json", circuit: "rc");
        Dictionary<string, string> fields = stamp.Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(netlist.Fingerprint(), fields["netlist"]);

        string csv = TransientCsv.Write(result, stamp);
        Assert.Contains($"# netlist: {netlist.Fingerprint()}", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsonReportCarriesTheFingerprint()
    {
        TransientNetlist netlist = Rc(1_000);
        TransientResult result = netlist.Run(new TransientOptions { StopTime = 1e-3, Steps = 2 });

        using JsonDocument report = JsonDocument.Parse(
            TransientNetlistJson.ToReportJson("rc.json", netlist, result, circuit: "rc"));

        Assert.Equal(netlist.Fingerprint(), report.RootElement.GetProperty("netlist").GetString());
    }

    private static TransientNetlist Rc(double ohms) => new TransientNetlist("gnd")
        .AddVoltageSource("V1", "in", "gnd", 5)
        .AddResistor("R1", "in", "out", ohms)
        .AddCapacitor("C1", "out", "gnd", 1e-6);
}
