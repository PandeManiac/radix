using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A refinement study is an accuracy claim made without a closed-form solution, so it
/// is checked where a closed form exists: halving the step of a first-order charge
/// must report order one for backward Euler and two for trapezoidal, and the
/// Richardson estimate must land within an order of magnitude of the true error
/// against the analytic solution.
/// </summary>
public sealed class TransientRefinementTests
{
    private const double Supply = 5;
    private const double Resistance = 1_000;
    private const double Capacitance = 1e-6;
    private const double Tau = Resistance * Capacitance;

    [Fact]
    public void BackwardEulerMeasuresFirstOrderConvergence()
    {
        TransientRefinement study = TransientRefinement.Analyze(
            RcNetlist(), Options(40, TransientMethod.BackwardEuler));

        Assert.True(study.Completed);
        Assert.Equal(3, study.Levels.Count);
        Assert.Equal([40, 80, 160], study.Levels.Select(level => level.Steps));
        Assert.InRange(study.ObservedOrder!.Value, 0.75, 1.3);
        Assert.True(
            study.EstimatedRelativeError > 0,
            "a first-order run at h/tau = 0.125 must be visibly off");
    }

    [Fact]
    public void TrapezoidalMeasuresSecondOrderConvergence()
    {
        TransientRefinement study = TransientRefinement.Analyze(
            RcNetlist(), Options(40, TransientMethod.Trapezoidal));

        Assert.True(study.Completed);
        Assert.InRange(study.ObservedOrder!.Value, 1.6, 2.4);
        Assert.True(
            study.EstimatedRelativeError < 0.02,
            $"second-order error should be small, got {study.EstimatedRelativeError}");
    }

    [Theory]
    [InlineData(TransientMethod.BackwardEuler)]
    [InlineData(TransientMethod.Trapezoidal)]
    public void TheEstimateIsTheRightSizeAgainstTheAnalyticSolution(TransientMethod method)
    {
        TransientOptions options = Options(40, method);
        TransientRefinement study = TransientRefinement.Analyze(RcNetlist(), options);

        TransientResult finest = study.Levels.Count == 0
            ? throw new Xunit.Sdk.XunitException("no levels solved")
            : RcNetlist().Run(options with { Steps = study.Levels[^1].Steps });
        Assert.True(finest.Completed, finest.Message);

        double actual = finest.Points.Max(point =>
            Math.Abs(point.NodeVoltages["out"] - Supply * (1 - Math.Exp(-point.Time / Tau)))) / Supply;

        Assert.True(actual > 0, "the discrete run cannot be exact");
        Assert.InRange(study.EstimatedRelativeError!.Value, actual * 0.1, actual * 10);
    }

    [Fact]
    public void TheBaselineRunIsReusedRatherThanSolvedAgain()
    {
        TransientOptions options = Options(40, TransientMethod.Trapezoidal);
        TransientResult baseline = RcNetlist().Run(options);

        TransientRefinement study = TransientRefinement.Analyze(
            RcNetlist(), options, baseline: baseline);

        Assert.True(study.Completed);
        Assert.Equal(baseline.Options.Steps, study.Levels[0].Steps);
        Assert.Equal(baseline.TimeStep, study.Levels[0].TimeStep, 15);
        // An untouched coarse level has nothing to compare against yet.
        Assert.Equal(0, study.Levels[0].MaximumDeviationVolts);
        Assert.Null(study.Levels[0].ObservedOrder);
    }

    [Fact]
    public void ADivergingRunIsReportedAsAFailureNotAnException()
    {
        var netlist = new TransientNetlist("gnd")
            .AddResistor("R1", "a", "b", 1_000)
            .AddCapacitor("C1", "b", "a", 1e-6);

        TransientRefinement study = TransientRefinement.Analyze(
            netlist, Options(40, TransientMethod.BackwardEuler));

        Assert.False(study.Completed);
        Assert.Equal(DcSolveFailure.NoGround, study.Failure);
        Assert.Empty(study.Levels);
        Assert.Null(study.EstimatedRelativeError);
        Assert.Contains("NoGround", study.Describe());
    }

    [Fact]
    public void AdaptiveOptionsAreRejectedBecauseThereIsNoStepToHalve()
    {
        TransientOptions adaptive = Options(40, TransientMethod.BackwardEuler) with
        {
            AdaptiveTolerance = 1e-5,
        };

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => TransientRefinement.Analyze(RcNetlist(), adaptive));
        Assert.Contains("adaptive", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StepCountsThatWouldExceedTheLimitAreRejectedBeforeSolving()
    {
        TransientOptions options = Options(TransientOptions.MaximumSteps / 2, TransientMethod.BackwardEuler);

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => TransientRefinement.Analyze(RcNetlist(), options, levels: 3));
        Assert.Contains("exceed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LevelsOutsideTheSupportedRangeAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransientRefinement.Analyze(RcNetlist(), Options(40, TransientMethod.BackwardEuler), levels: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransientRefinement.Analyze(RcNetlist(), Options(40, TransientMethod.BackwardEuler), levels: 6));
    }

    [Fact]
    public void AWithBreakpointsStillComparesEveryLevel()
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 0)
            .SetWaveform(
                "V1",
                [
                    new TransientWaveformPoint(0, 0),
                    new TransientWaveformPoint(1e-3, 5),
                    new TransientWaveformPoint(2e-3, 0),
                ])
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6);

        TransientRefinement study = TransientRefinement.Analyze(
            netlist,
            new TransientOptions
            {
                StopTime = 4e-3,
                Steps = 100,
                Method = TransientMethod.Trapezoidal,
            });

        Assert.True(study.Completed);
        Assert.Equal(3, study.Levels.Count);
        Assert.All(
            study.Levels.Skip(1),
            level => Assert.True(
                level.MaximumDeviationVolts >= 0 && double.IsFinite(level.MaximumDeviationVolts)));
        Assert.Contains(study.Nodes, node => node.NodeId == "out" && node.ReferenceVolts > 0);
    }

    [Fact]
    public void OppositeExtremeLinearTraceRefinesWithoutOverflowingCoarseInterpolation()
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddCurrentSource("I1", "gnd", "out", -1)
            .SetWaveform("I1", [
                new TransientWaveformPoint(0, -1),
                new TransientWaveformPoint(1, 1),
            ])
            .AddResistor("R1", "out", "gnd", 1e308);

        TransientRefinement study = TransientRefinement.Analyze(
            netlist,
            new TransientOptions { StopTime = 1, Steps = 1 },
            levels: 2);

        Assert.True(study.Completed);
        Assert.All(study.Levels, level =>
            Assert.True(double.IsFinite(level.MaximumDeviationVolts)));
        Assert.Equal(0, study.Levels[^1].MaximumDeviationVolts);
        Assert.Equal(0, study.EstimatedRelativeError);
    }

    private static TransientOptions Options(int steps, TransientMethod method) => new()
    {
        StopTime = 5 * Tau,
        Steps = steps,
        Method = method,
    };

    private static TransientNetlist RcNetlist() => new TransientNetlist("gnd")
        .AddVoltageSource("V1", "in", "gnd", Supply)
        .AddResistor("R1", "in", "out", Resistance)
        .AddCapacitor("C1", "out", "gnd", Capacitance)
        .SetCapacitorInitialVoltage("C1", 0);
}
