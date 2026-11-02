using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The gate-charge fractions are exact model constants, so they are checked against the algebra
/// rather than a tolerance: no channel means no capacitance, a full channel splits the gate charge
/// equally between both terminals, and a pinched-off channel leaves two thirds facing the source
/// and nothing facing the drain.
/// </summary>
public sealed class MosfetCapacitanceTests
{
    [Fact]
    public void ACutOffChannelPresentsNoGateCapacitance()
    {
        Assert.Equal(0, MosfetCapacitance.GateSourceFraction(DcMosfetRegion.Cutoff));
        Assert.Equal(0, MosfetCapacitance.GateDrainFraction(DcMosfetRegion.Cutoff));
    }

    [Fact]
    public void AChannelInTriodeSplitsTheGateChargeEvenly()
    {
        Assert.Equal(0.5, MosfetCapacitance.GateSourceFraction(DcMosfetRegion.Triode));
        Assert.Equal(0.5, MosfetCapacitance.GateDrainFraction(DcMosfetRegion.Triode));
    }

    [Fact]
    public void ASaturatedChannelFacesTheSourceAndNotTheDrain()
    {
        // 2/3 and 0: the pinch-off point is the whole reason a saturated stage stops feeding its
        // output back into its own gate. A model that kept the drain coupling would predict Miller
        // feedback exactly where the device is supposed to have none.
        Assert.Equal(2.0 / 3.0, MosfetCapacitance.GateSourceFraction(DcMosfetRegion.Saturation));
        Assert.Equal(0, MosfetCapacitance.GateDrainFraction(DcMosfetRegion.Saturation));
    }

    [Fact]
    public void TheEffectiveFaradsFollowTheFraction()
    {
        const double authored = 3e-12;

        Assert.Equal(2e-12, MosfetCapacitance.EffectiveGateSourceFarads(authored, DcMosfetRegion.Saturation), 15);
        Assert.Equal(1.5e-12, MosfetCapacitance.EffectiveGateDrainFarads(authored, DcMosfetRegion.Triode), 15);
        Assert.Equal(0, MosfetCapacitance.EffectiveGateDrainFarads(authored, DcMosfetRegion.Saturation), 15);
        Assert.Equal(0, MosfetCapacitance.EffectiveGateSourceFarads(authored, DcMosfetRegion.Cutoff), 15);
    }

    [Fact]
    public void TheDeviceReportCarriesTheFractionsOfItsOwnRegion()
    {
        static TransientNetlist Stage(double gateVolts) =>
            new TransientNetlist("gnd")
                .AddVoltageSource("VDD", "vdd", "gnd", 5)
                .AddVoltageSource("VIN", "in", "gnd", gateVolts)
                .AddResistor("RD", "vdd", "out", 1_000)
                .AddMosfet("M1", "out", "in", "gnd", DcMosfetChannel.NChannel, 1, 1e-3, 0, 1e-12, 1e-12);

        // Saturated: two thirds facing the source, none facing the drain.
        AcDeviceReport on = Assert.Single(AcAnalysis.Run(Stage(2), "VIN", [10]).Devices);
        Assert.Equal("Saturation", on.Region);
        Assert.Equal(2.0 / 3.0, on.GateSourceFraction);
        Assert.Equal(0, on.GateDrainFraction);

        // Off: no channel, so neither capacitance is presented.
        AcDeviceReport off = Assert.Single(AcAnalysis.Run(Stage(0.5), "VIN", [10]).Devices);
        Assert.Equal(0, off.GateSourceFraction);
        Assert.Equal(0, off.GateDrainFraction);
    }
}
