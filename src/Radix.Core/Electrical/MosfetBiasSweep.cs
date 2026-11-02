using System.Globalization;

namespace Radix.Core.Electrical;

/// <summary>Every transistor's operating point at one value of the drive.</summary>
public sealed record MosfetBiasPoint(double BiasVolts, IReadOnlyList<AcDeviceReport> Devices);

/// <summary>
/// Measures each transistor's region and small-signal slopes at a solved bias point. It lives apart
/// from the frequency analysis because a bias sweep has no frequencies: both callers read the same
/// numbers from the same device model, so a table and a curve cannot disagree.
/// </summary>
public static class MosfetReports
{
    public static IReadOnlyList<AcDeviceReport> Measure(TransientNetlist netlist, DcSolution bias)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentNullException.ThrowIfNull(bias);

        var reports = new List<AcDeviceReport>();
        foreach (DcMosfet mosfet in netlist.Elements.OfType<DcMosfet>())
        {
            double drainSource = NodeBias(bias, mosfet.Drain) - NodeBias(bias, mosfet.Source);
            double gateSource = NodeBias(bias, mosfet.Gate) - NodeBias(bias, mosfet.Source);
            DcMosfetOperatingPoint point = mosfet.Evaluate(drainSource, gateSource);
            reports.Add(new AcDeviceReport(
                mosfet.Id,
                point.Region.ToString(),
                gateSource,
                drainSource,
                point.Current,
                point.GateTransconductance,
                point.DrainConductance));
        }

        return reports;
    }

    /// <summary>The bias a node sits at, treating ground and unknown nodes as 0 V.</summary>
    public static double NodeBias(DcSolution bias, string node) =>
        bias.NodeVoltages.TryGetValue(node, out double volts) ? volts : 0;
}

/// <summary>
/// Steps one source across a range and reports every transistor at each value.
/// <para>
/// A single operating point says where a device sits; a sweep says where it switches. The ternary
/// gate's two transitions are the interesting part of it, and they only exist as a sequence of
/// regions and transconductances across the input range.
/// </para>
/// </summary>
public static class MosfetBiasSweep
{
    /// <summary>Upper bound on values in one sweep, to keep a run bounded.</summary>
    public const int MaximumCount = 256;

    public static IReadOnlyList<MosfetBiasPoint> Run(
        TransientNetlist netlist,
        string sourceId,
        IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(values);

        if (!netlist.Elements
                .OfType<DcVoltageSource>()
                .Any(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Netlist has no voltage source '{sourceId}' to sweep.", nameof(sourceId));
        }

        double[] biases = values.ToArray();
        if (biases.Length == 0)
        {
            throw new ArgumentException("A bias sweep needs at least one value.", nameof(values));
        }

        if (biases.Length > MaximumCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values), biases.Length, $"A bias sweep takes at most {MaximumCount} values.");
        }

        foreach (double bias in biases)
        {
            if (!double.IsFinite(bias))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values), bias, "Every bias value must be finite.");
            }
        }

        var points = new List<MosfetBiasPoint>(biases.Length);
        foreach (double bias in biases)
        {
            // The source is held at the value under study: a waveform would otherwise bias the
            // circuit at its value at t = 0 and every point would describe the rest state.
            TransientNetlist held = netlist.Clone().WithElementValue(sourceId, bias);
            held.SetWaveform(sourceId, [new TransientWaveformPoint(0, bias)]);
            DcSolution solution = held.SolveDc();
            points.Add(new MosfetBiasPoint(bias, MosfetReports.Measure(held, solution)));
        }

        return points;
    }

    /// <summary>Evenly spaced biases, which is how a transfer characteristic is usually read.</summary>
    public static IReadOnlyList<double> Linear(double fromVolts, double toVolts, int count)
    {
        if (!double.IsFinite(fromVolts) || !double.IsFinite(toVolts))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fromVolts), fromVolts, "Both ends of a bias sweep must be finite.");
        }

        if (count < 2 || count > MaximumCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, $"A bias sweep needs between 2 and {MaximumCount} values.");
        }

        var values = new double[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = fromVolts + ((toVolts - fromVolts) * index / (count - 1));
        }

        return values;
    }
}
