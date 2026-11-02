using System.Globalization;

namespace Radix.Core.Electrical;

/// <summary>
/// How far one node's trace moved when the step was halved, sampled at the finer
/// run's points with the coarser run interpolated between its own. The reference is
/// the largest magnitude the node reached, so the relative figure means the same
/// thing for a 5 V step and a millivolt ripple.
/// </summary>
public sealed record TransientNodeDeviation(
    string NodeId,
    double MaximumDeviationVolts,
    double WorstTimeSeconds,
    double ReferenceVolts)
{
    /// <summary>Deviation relative to the node's own peak magnitude.</summary>
    public double RelativeDeviation => Math.Abs(ReferenceVolts) > 0
        ? MaximumDeviationVolts / Math.Abs(ReferenceVolts)
        : MaximumDeviationVolts;
}

/// <summary>
/// One step size of a refinement study: the step count, its uniform step in seconds,
/// the largest deviation from the previous (coarser) level, and the convergence order
/// implied by that deviation and the one before it.
/// </summary>
public sealed record TransientRefinementLevel(
    int Steps,
    double TimeStep,
    double MaximumDeviationVolts,
    double? ObservedOrder);

/// <summary>
/// An error estimate that needs no closed-form solution: solve the same circuit at
/// two, three, or more step sizes, compare each finer run against the coarser one
/// sampled at the finer run's times, and infer the measured order of convergence from
/// the ratio of successive deviations. With order <c>p</c>, the finest run's error is
/// about <c>d / (2^p - 1)</c> where <c>d</c> is its deviation from the previous level,
/// which is the Richardson estimate.
///
/// This complements the energy residual: the residual says whether the reported powers
/// and reactive state agree, while this says whether halving the step would still
/// change the answer. A large estimate with an order near zero means the run has not
/// reached its asymptotic regime, so the numbers should not be trusted at that step.
/// </summary>
public sealed record TransientRefinement(
    IReadOnlyList<TransientRefinementLevel> Levels,
    IReadOnlyList<TransientNodeDeviation> Nodes,
    double? ObservedOrder,
    double? EstimatedRelativeError,
    DcSolveFailure? Failure)
{
    /// <summary>Whether every refined solve completed.</summary>
    public bool Completed => Failure is null;

    /// <summary>
    /// Solves <paramref name="options"/> at the requested step count and at each
    /// doubled count, comparing consecutive solutions. <paramref name="baseline"/> is
    /// reused as the coarsest level when it already solved those exact options, which
    /// keeps the printed table consistent with the run the caller just reported.
    /// </summary>
    public static TransientRefinement Analyze(
        TransientNetlist netlist,
        TransientOptions options,
        int levels = 3,
        TransientResult? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentNullException.ThrowIfNull(options);
        if (options.IsAdaptive)
        {
            throw new ArgumentException(
                "A refinement study halves a fixed step; adaptive runs already control their own step.",
                nameof(options));
        }

        if (levels < 2 || levels > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(levels), levels, "A refinement study needs between 2 and 5 levels.");
        }

        options.Validate();
        int finestSteps = options.Steps;
        for (int level = 1; level < levels; level++)
        {
            if (finestSteps > TransientOptions.MaximumSteps / 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.Steps,
                    $"Refining {options.Steps} steps {levels} times would exceed the " +
                    $"{TransientOptions.MaximumSteps}-step limit; start from fewer steps or fewer levels.");
            }

            finestSteps *= 2;
        }

        var summaries = new List<TransientRefinementLevel>(levels);
        TransientResult? coarse = null;
        double coarseDeviation = 0;
        IReadOnlyList<TransientNodeDeviation> finestNodes = [];
        double finestDeviation = 0;
        double? order = null;

        for (int level = 0; level < levels; level++)
        {
            int steps = options.Steps << level;
            TransientOptions levelOptions = options with { Steps = steps };
            TransientResult fine = level == 0 && baseline is not null &&
                ReferenceEquals(baseline.Options, options)
                    ? baseline
                    : netlist.Run(levelOptions);
            if (fine.Points.Count == 0 || fine.Options.Steps != steps)
            {
                fine = netlist.Run(levelOptions);
            }

            if (!fine.Completed)
            {
                return new TransientRefinement(summaries, [], null, null, fine.Failure);
            }

            if (coarse is null)
            {
                summaries.Add(new TransientRefinementLevel(steps, fine.TimeStep, 0, null));
                coarse = fine;
                continue;
            }

            (IReadOnlyList<TransientNodeDeviation> nodes, double maximum) = Compare(coarse, fine);
            order = coarseDeviation > 0 && maximum > 0
                ? Math.Log2(coarseDeviation / maximum)
                : null;
            summaries.Add(new TransientRefinementLevel(steps, fine.TimeStep, maximum, order));
            coarse = fine;
            coarseDeviation = maximum;
            finestNodes = nodes;
            finestDeviation = maximum;
        }

        double scale = finestNodes.Count == 0
            ? 0
            : finestNodes.Max(node => Math.Abs(node.ReferenceVolts));
        double estimate = finestDeviation;
        if (order is double measured && measured > 0.05)
        {
            estimate = finestDeviation / (Math.Pow(2, measured) - 1);
        }

        if (scale > 0)
        {
            estimate /= scale;
        }

        return new TransientRefinement(
            summaries,
            finestNodes,
            order,
            double.IsFinite(estimate) ? estimate : null,
            null);
    }

    private static (IReadOnlyList<TransientNodeDeviation> Nodes, double Maximum) Compare(
        TransientResult coarse,
        TransientResult fine)
    {
        string[] nodes = fine.Points
            .SelectMany(point => point.NodeVoltages.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var deviations = new List<TransientNodeDeviation>(nodes.Length);
        double maximum = 0;
        foreach (string node in nodes)
        {
            double worst = 0;
            double worstTime = fine.Points[0].Time;
            double reference = 0;
            foreach (TransientPoint point in fine.Points)
            {
                double value = point.NodeVoltages.TryGetValue(node, out double volts) ? volts : 0;
                reference = Math.Max(reference, Math.Abs(value));
                double previous = Interpolate(coarse.Points, node, point.Time);
                double deviation = Math.Abs(value - previous);
                if (deviation > worst)
                {
                    worst = deviation;
                    worstTime = point.Time;
                }
            }

            foreach (TransientPoint point in coarse.Points)
            {
                double value = point.NodeVoltages.TryGetValue(node, out double volts) ? volts : 0;
                reference = Math.Max(reference, Math.Abs(value));
            }

            deviations.Add(new TransientNodeDeviation(node, worst, worstTime, reference));
            maximum = Math.Max(maximum, worst);
        }

        return (deviations, maximum);
    }

    /// <summary>Piecewise-linear value of <paramref name="node"/> at <paramref name="time"/>.</summary>
    private static double Interpolate(
        IReadOnlyList<TransientPoint> points,
        string node,
        double time)
    {
        double ValueAt(TransientPoint point) =>
            point.NodeVoltages.TryGetValue(node, out double volts) ? volts : 0;

        if (time <= points[0].Time)
        {
            return ValueAt(points[0]);
        }

        if (time >= points[^1].Time)
        {
            return ValueAt(points[^1]);
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

        double span = points[high].Time - points[low].Time;
        if (!(span > 0))
        {
            return ValueAt(points[high]);
        }

        double weight = (time - points[low].Time) / span;
        double left = ValueAt(points[low]);
        double right = ValueAt(points[high]);
        double delta = right - left;
        return double.IsFinite(delta)
            ? left + (weight * delta)
            : (left * (1 - weight)) + (right * weight);
    }

    /// <summary>Human description of the estimate for reports and the CLI.</summary>
    public string Describe()
    {
        if (!Completed)
        {
            return $"refinement failed with {Failure}.";
        }

        string order = ObservedOrder is double measured
            ? string.Create(CultureInfo.InvariantCulture, $"measured order {measured:0.###}")
            : "no order measured";
        string estimate = EstimatedRelativeError is double relative
            ? string.Create(CultureInfo.InvariantCulture, $"{relative:0.###e+0} relative")
            : "unavailable";
        return $"estimated error at the finest step: {estimate} ({order}).";
    }
}
