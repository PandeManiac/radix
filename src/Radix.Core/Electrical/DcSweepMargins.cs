using System.Globalization;
using System.Text;

namespace Radix.Core.Electrical;

/// <summary>A swept input range over which the circuit does not amplify a disturbance.</summary>
public sealed record DcInputWindow(double From, double To)
{
    /// <summary>How wide the window is, in the swept parameter's unit.</summary>
    public double Width => To - From;
}

/// <summary>
/// Turns a measured transfer curve into the input windows a logic level can occupy. A circuit
/// holds a level where its gain is below one: a disturbance there decays. Between two unity-gain
/// crossings the curve amplifies, so that band is a forbidden zone and the gaps are the windows.
///
/// For a binary inverter this yields the two rail bands and the transition between them. For the
/// ternary inverter it yields three windows — one per level — which is the property that decides
/// whether a three-level gate is usable, and which a table of voltages alone cannot show.
///
/// Crossings are found by interpolating the measured gain between adjacent solved points, so they
/// are consistent with the gain table by construction and never require another solve.
/// </summary>
public static class DcSweepMargins
{
    /// <summary>The swept values where |dV/dx| passes through one, in sweep order.</summary>
    public static IReadOnlyList<double> UnityGainCrossings(IReadOnlyList<DcSweepGainPoint> gains)
    {
        ArgumentNullException.ThrowIfNull(gains);

        var crossings = new List<double>();
        for (int index = 0; index + 1 < gains.Count; index++)
        {
            if (gains[index].Gain is not double left || gains[index + 1].Gain is not double right)
            {
                continue;
            }

            double leftExcess = Math.Abs(left) - 1;
            double rightExcess = Math.Abs(right) - 1;
            if (leftExcess == 0)
            {
                crossings.Add(gains[index].Value);
                continue;
            }

            // A crossing needs a sign change in |gain| - 1; two points on the same side of unity
            // do not bracket one, however steep the curve is.
            if (leftExcess * rightExcess > 0)
            {
                continue;
            }

            double span = rightExcess - leftExcess;
            crossings.Add(span == 0
                ? gains[index].Value
                : gains[index].Value +
                  ((gains[index + 1].Value - gains[index].Value) * (-leftExcess / span)));
        }

        if (gains.Count > 0 && gains[^1].Gain is double last && Math.Abs(last) == 1)
        {
            crossings.Add(gains[^1].Value);
        }

        return crossings;
    }

    /// <summary>
    /// The swept ranges where the curve does not amplify. A sweep that never crosses unity gain has
    /// one window covering the whole range, which is the honest report of a circuit that attenuates
    /// everywhere.
    /// </summary>
    public static IReadOnlyList<DcInputWindow> Windows(IReadOnlyList<DcSweepGainPoint> gains)
    {
        ArgumentNullException.ThrowIfNull(gains);
        var windows = new List<DcInputWindow>();
        if (gains.Count == 0)
        {
            return windows;
        }

        // The region before the first crossing is amplified exactly when the curve starts out
        // amplifying; a sweep can begin anywhere, so this cannot be assumed.
        bool amplified = FirstGain(gains) is double first && Math.Abs(first) > 1;
        double from = gains[0].Value;
        double end = gains[^1].Value;

        foreach (double crossing in UnityGainCrossings(gains))
        {
            if (!amplified && crossing > from)
            {
                windows.Add(new DcInputWindow(from, crossing));
            }

            amplified = !amplified;
            from = crossing;
        }

        if (!amplified && end > from)
        {
            windows.Add(new DcInputWindow(from, end));
        }

        return windows;
    }

    /// <summary>A readable report of the crossings and the windows they bound.</summary>
    public static string ToText(
        string elementId,
        string nodeId,
        IReadOnlyList<DcSweepGainPoint> gains)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(gains);

        IReadOnlyList<DcInputWindow> windows = Windows(gains);
        IReadOnlyList<double> crossings = UnityGainCrossings(gains);

        var text = new StringBuilder();
        text.Append("input windows for ").Append(nodeId)
            .Append(" over ").Append(elementId).Append(": ")
            .Append(windows.Count.ToString(CultureInfo.InvariantCulture))
            .Append(windows.Count == 1 ? " window" : " windows")
            .Append(" where |dV(").Append(nodeId).Append(")/d").Append(elementId).Append("| < 1")
            .AppendLine();
        foreach (DcInputWindow window in windows)
        {
            text.Append("  ").Append(Number(window.From))
                .Append(" -> ").Append(Number(window.To))
                .Append("  (").Append(Number(window.Width)).Append(" wide)")
                .AppendLine();
        }

        text.Append("unity-gain crossings: ")
            .Append(crossings.Count == 0 ? "none" : string.Join(", ", crossings.Select(Number)))
            .AppendLine();
        return text.ToString();
    }

    private static double? FirstGain(IReadOnlyList<DcSweepGainPoint> gains)
    {
        foreach (DcSweepGainPoint point in gains)
        {
            if (point.Gain is double gain)
            {
                return gain;
            }
        }

        return null;
    }

    private static string Number(double value) => value.ToString("G4", CultureInfo.InvariantCulture);
}
