namespace Radix.Core.Electrical;

/// <summary>
/// The fraction of a MOSFET's authored gate capacitance that the channel presents at a bias point.
/// <para>
/// The square-law model divides the channel charge between the gate and the two terminals by
/// region, which is the Meyer charge model: in cut-off there is no channel, so neither capacitance
/// exists; in triode the channel connects gate to both terminals equally; in saturation the channel
/// is pinched off at the drain, so two thirds of the gate charge faces the source and none faces the
/// drain. A device's drain-gate capacitance therefore collapses when it saturates, which is why a
/// saturated stage stops feeding its output back into its own input.
/// </para>
/// <para>
/// These are fractions, not farads: the authored capacitance stays the zero-bias reference, and a
/// caller multiplies by it. Values are exact in the model (2/3 and 1/2) rather than fitted, so a
/// test can compare against the algebra instead of a tolerance.
/// </para>
/// </summary>
public static class MosfetCapacitance
{
    /// <summary>Gate-source fraction with the channel in saturation: two thirds.</summary>
    public const double SaturationGateFraction = 2.0 / 3.0;

    /// <summary>Gate fraction in either direction with the channel fully on: one half.</summary>
    public const double TriodeGateFraction = 0.5;

    public static double GateSourceFraction(DcMosfetRegion region) => region switch
    {
        DcMosfetRegion.Cutoff => 0,
        DcMosfetRegion.Triode => TriodeGateFraction,
        DcMosfetRegion.Saturation => SaturationGateFraction,
        _ => throw new ArgumentOutOfRangeException(
            nameof(region), region, "Unknown MOSFET region."),
    };

    public static double GateDrainFraction(DcMosfetRegion region) => region switch
    {
        DcMosfetRegion.Cutoff => 0,
        DcMosfetRegion.Triode => TriodeGateFraction,
        DcMosfetRegion.Saturation => 0,
        _ => throw new ArgumentOutOfRangeException(
            nameof(region), region, "Unknown MOSFET region."),
    };

    /// <summary>The effective gate-source capacitance at a bias point, in farads.</summary>
    public static double EffectiveGateSourceFarads(double authoredFarads, DcMosfetRegion region) =>
        authoredFarads * GateSourceFraction(region);

    /// <summary>The effective gate-drain capacitance at a bias point, in farads.</summary>
    public static double EffectiveGateDrainFarads(double authoredFarads, DcMosfetRegion region) =>
        authoredFarads * GateDrainFraction(region);
}
