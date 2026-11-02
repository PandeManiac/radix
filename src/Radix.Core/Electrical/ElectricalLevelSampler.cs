using Radix.Core.Electrical;

namespace Radix.Core.Electrical;

/// <summary>
/// A solved node voltage read as a discrete level. This is the electrical-to-digital half of the
/// bridge: the solver produces volts, a domain's values are an ordered list, and the thresholds are
/// where one value stops being the nearest description of the voltage.
/// </summary>
/// <remarks>
/// Thresholds are ascending boundaries and a voltage exactly on one reads as the level above it,
/// which matches how a comparator with zero offset behaves. A level is the index of a value in a
/// domain's value list, so a table with two thresholds has three levels and describes a ternary
/// signal. Voltages outside the authored thresholds clamp to the first or last level rather than
/// failing, because a rail that overshoots is still that rail.
/// </remarks>
public sealed class ElectricalLevelSampler
{
    /// <summary>Levels a ternary domain has.</summary>
    public const int TernaryLevelCount = 3;

    private readonly double[] _thresholds;

    public ElectricalLevelSampler(IReadOnlyList<double> thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        _thresholds = [.. thresholds];
        for (int index = 0; index < _thresholds.Length; index++)
        {
            if (!double.IsFinite(_thresholds[index]))
            {
                throw new ArgumentException(
                    $"Threshold {index} is not a finite number of volts.", nameof(thresholds));
            }

            if (index > 0 && _thresholds[index] <= _thresholds[index - 1])
            {
                throw new ArgumentException(
                    $"Thresholds must ascend: {_thresholds[index - 1]} then {_thresholds[index]}.",
                    nameof(thresholds));
            }
        }
    }

    /// <summary>
    /// The boundaries of a ternary domain using equal spacing, one third and two thirds of the
    /// supplied rail. A 5 V rail gives 1.667 V and 3.333 V.
    /// </summary>
    public static ElectricalLevelSampler TernaryFromRail(double rail)
    {
        if (!double.IsFinite(rail) || rail <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rail), rail, "Rail must be a positive voltage.");
        }

        return new ElectricalLevelSampler([rail / 3.0, rail * 2.0 / 3.0]);
    }

    /// <summary>The ascending boundaries in volts.</summary>
    public IReadOnlyList<double> Thresholds => _thresholds;

    /// <summary>How many values the sampled domain has: one more than the boundaries.</summary>
    public int LevelCount => _thresholds.Length + 1;

    /// <summary>
    /// The level a voltage reads as: the number of boundaries at or below it. Exactly on a
    /// boundary reads as the level above, as a comparator with no offset would.
    /// </summary>
    public int LevelFor(double volts)
    {
        if (!double.IsFinite(volts))
        {
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "Voltage must be finite.");
        }

        int level = 0;
        foreach (double threshold in _thresholds)
        {
            if (volts < threshold)
            {
                break;
            }

            level++;
        }

        return level;
    }

    /// <summary>
    /// Reads a node at a time the run may not have landed on, by interpolating between the two
    /// bracketing points. A time outside the run clamps to its ends, because asking what the circuit
    /// reads before it started should answer with what it started at rather than throw.
    /// </summary>
    public SampledLevel SampleAt(TransientResult result, string nodeId, double time)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (!double.IsFinite(time))
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, "Time must be finite.");
        }

        IReadOnlyList<TransientPoint> points = result.Points;
        if (points.Count == 0)
        {
            throw new ArgumentException("The run has no points to sample.", nameof(result));
        }

        if (!points[0].NodeVoltages.ContainsKey(nodeId))
        {
            throw new ArgumentException(
                $"Node '{nodeId}' is not in the run.", nameof(nodeId));
        }

        double volts = Interpolate(points, nodeId, time);
        return new SampledLevel(time, volts, LevelFor(volts));
    }

    private static double Interpolate(
        IReadOnlyList<TransientPoint> points,
        string nodeId,
        double time)
    {
        if (time <= points[0].Time)
        {
            return points[0].NodeVoltages[nodeId];
        }

        if (time >= points[^1].Time)
        {
            return points[^1].NodeVoltages[nodeId];
        }

        int low = 0;
        int high = points.Count - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (points[middle].Time <= time)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        TransientPoint before = points[low];
        TransientPoint after = points[high];
        double span = after.Time - before.Time;
        if (span <= 0)
        {
            return before.NodeVoltages[nodeId];
        }

        double fraction = (time - before.Time) / span;
        return before.NodeVoltages[nodeId] +
            (after.NodeVoltages[nodeId] - before.NodeVoltages[nodeId]) * fraction;
    }
}

/// <summary>A node voltage read at a time, with the level it reads as.</summary>
/// <param name="Time">The requested time in seconds.</param>
/// <param name="Volts">The voltage there, interpolated when the run did not land on it.</param>
/// <param name="Level">The index of the value the voltage reads as, zero-based.</param>
public readonly record struct SampledLevel(double Time, double Volts, int Level);
