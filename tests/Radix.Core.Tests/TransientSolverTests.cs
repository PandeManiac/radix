using Radix.Core.Electrical;
using Xunit;

namespace Radix.Core.Tests;

public sealed class TransientSolverTests
{
    [Fact]
    public void RcStepMatchesTheBackwardEulerRecurrenceExactly()
    {
        const double supply = 5;
        const double resistance = 1_000;
        const double capacitance = 1e-6;
        double tau = resistance * capacitance;
        var options = new TransientOptions { StopTime = 5 * tau, Steps = 250 };
        double h = options.TimeStep;
        double conductance = capacitance / h;

        // Start from rest with an explicit initial voltage rather than a drive step: a
        // breakpoint is now a step boundary, and a step at 1e-12 s would insert a point
        // this uniform-grid recurrence does not describe.
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", supply)
            .AddResistor("R1", "in", "out", resistance)
            .AddCapacitor("C1", "out", "gnd", capacitance)
            .SetCapacitorInitialVoltage("C1", 0);

        TransientResult result = netlist.Run(options);

        Assert.True(result.Completed, result.Message);
        Assert.Equal(options.Steps + 1, result.Points.Count);
        AssertClose(0, result.InitialPoint!.NodeVoltages["out"], 1e-12);
        Assert.Equal(options.TimeStep, result.Points[1].Time);
        Assert.Equal(options.StopTime, result.FinalPoint!.Time);

        double expected = 0;
        for (int step = 1; step <= options.Steps; step++)
        {
            // C dv/dt = (V - v)/R with backward Euler:
            // v_n = (V/R + (C/h) v_{n-1}) / (1/R + C/h).
            expected = ((supply / resistance) + (conductance * expected)) /
                ((1 / resistance) + conductance);
            AssertClose(expected, result.Points[step].NodeVoltages["out"], 1e-9);
            AssertClose(
                conductance * (expected - result.Points[step - 1].NodeVoltages["out"]),
                result.Points[step].ElementCurrents["C1"],
                1e-9);
        }
    }

    [Theory]
    [InlineData(TransientMethod.BackwardEuler, 2.0)]
    [InlineData(TransientMethod.Trapezoidal, 4.0)]
    public void RcStepConvergesToTheAnalyticSolutionAtTheExpectedOrder(
        TransientMethod method,
        double expectedOrder)
    {
        double coarse = RcStepError(method, 200);
        double fine = RcStepError(method, 400);

        Assert.True(fine < coarse, $"refining the step did not reduce error ({coarse:G6} -> {fine:G6}).");
        Assert.InRange(coarse / fine, expectedOrder * 0.8, expectedOrder * 1.25);
        Assert.True(fine < 1e-2, $"error at 400 steps was {fine:G6} V.");
    }

    [Fact]
    public void TrapezoidalIsMuchMoreAccurateThanBackwardEulerAtTheSameStep()
    {
        double backwardEuler = RcStepError(TransientMethod.BackwardEuler, 100);
        double trapezoidal = RcStepError(TransientMethod.Trapezoidal, 100);

        Assert.True(
            trapezoidal * 5 < backwardEuler,
            $"trapezoidal error {trapezoidal:G6} V was not clearly better than {backwardEuler:G6} V.");
    }

    [Fact]
    public void TrapezoidalRunsStillUseBackwardEulerForTheirFirstStep()
    {
        var options = new TransientOptions
        {
            StopTime = 1e-3,
            Steps = 20,
            Method = TransientMethod.Trapezoidal,
        };
        TransientNetlist netlist = RcStepNetlist(5, 1_000, 1e-6);

        TransientResult trapezoidal = netlist.Run(options);
        TransientResult backwardEuler = netlist.Run(options with { Method = TransientMethod.BackwardEuler });

        Assert.True(trapezoidal.Completed, trapezoidal.Message);
        Assert.Equal(
            backwardEuler.Points[1].NodeVoltages["out"],
            trapezoidal.Points[1].NodeVoltages["out"]);
        Assert.NotEqual(
            backwardEuler.Points[2].NodeVoltages["out"],
            trapezoidal.Points[2].NodeVoltages["out"]);
    }

    [Fact]
    public void CapacitorDischargeFromAnInitialVoltageFollowsTheBackwardEulerRecurrence()
    {
        const double resistance = 1_000;
        const double capacitance = 1e-6;
        const double initial = 5;
        double tau = resistance * capacitance;
        var options = new TransientOptions { StopTime = 3 * tau, Steps = 60 };
        double h = options.TimeStep;

        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddResistor("R1", "out", "gnd", resistance)
            .AddCapacitor("C1", "out", "gnd", capacitance)
            .SetCapacitorInitialVoltage("C1", initial);

        TransientResult result = netlist.Run(options);

        Assert.True(result.Completed, result.Message);
        AssertClose(initial, result.InitialPoint!.NodeVoltages["out"], 1e-12);
        double expected = initial;
        for (int step = 1; step <= options.Steps; step++)
        {
            expected /= 1 + (h / tau);
            AssertClose(expected, result.Points[step].NodeVoltages["out"], 1e-9);
        }
    }

    [Fact]
    public void InductorCurrentStepMatchesTheBackwardEulerRecurrence()
    {
        const double supply = 10;
        const double resistance = 10;
        const double inductance = 1e-3;
        double tau = inductance / resistance;
        var options = new TransientOptions { StopTime = 5 * tau, Steps = 100 };
        double h = options.TimeStep;

        // Start from rest with an explicit inductor current rather than a drive step, so
        // the run keeps the uniform grid this recurrence is derived on.
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", supply)
            .AddResistor("R1", "in", "mid", resistance)
            .AddInductor("L1", "mid", "gnd", inductance)
            .SetInductorInitialCurrent("L1", 0);

        TransientResult result = netlist.Run(options);

        Assert.True(result.Completed, result.Message);
        AssertClose(0, result.InitialPoint!.ElementCurrents["L1"], 1e-12);

        double expected = 0;
        for (int step = 1; step <= options.Steps; step++)
        {
            // V = R i + L di/dt with backward Euler:
            // i_n = (i_{n-1} + V h / L) / (1 + R h / L).
            expected = (expected + (supply * h / inductance)) / (1 + (resistance * h / inductance));
            AssertClose(expected, result.Points[step].ElementCurrents["L1"], 1e-9);
        }

        AssertClose(supply / resistance, result.FinalPoint!.ElementCurrents["L1"], 2e-2);
    }

    [Fact]
    public void SeriesRlcStepOvershootsByTheAnalyticUnderdampedAmount()
    {
        const double supply = 10;
        const double resistance = 100;
        const double inductance = 0.01;
        const double capacitance = 1e-6;
        double damping = (resistance / 2) * Math.Sqrt(capacitance / inductance);
        Assert.Equal(0.5, damping, 1e-12);
        double expectedPeak = supply *
            (1 + Math.Exp(-Math.PI * damping / Math.Sqrt(1 - (damping * damping))));

        var options = new TransientOptions
        {
            StopTime = 5e-3,
            Steps = 500,
            Method = TransientMethod.Trapezoidal,
        };
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", supply)
            .SetWaveform("V1", [new TransientWaveformPoint(0, 0), new TransientWaveformPoint(1e-12, supply)])
            .AddResistor("R1", "in", "a", resistance)
            .AddInductor("L1", "a", "out", inductance)
            .AddCapacitor("C1", "out", "gnd", capacitance);

        TransientResult result = netlist.Run(options);

        Assert.True(result.Completed, result.Message);
        double peak = result.Points.Max(point => point.NodeVoltages["out"]);
        AssertClose(expectedPeak, peak, 1.5e-2);
        Assert.True(peak > supply, "an underdamped step must overshoot its final value.");
        AssertClose(supply, result.FinalPoint!.NodeVoltages["out"], 1e-2);
        AssertClose(0, result.FinalPoint.ElementCurrents["L1"], 1e-2);
    }

    [Fact]
    public void TrapezoidalPreservesLcTankEnergyWhileBackwardEulerDissipates()
    {
        const double inductance = 1e-3;
        const double capacitance = 1e-6;
        const double initialVolts = 1;
        double initialEnergy = 0.5 * capacitance * initialVolts * initialVolts;

        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddInductor("L1", "a", "gnd", inductance)
            .AddCapacitor("C1", "a", "gnd", capacitance)
            .SetCapacitorInitialVoltage("C1", initialVolts);

        var options = new TransientOptions
        {
            StopTime = 2e-3,
            Steps = 2_000,
            Method = TransientMethod.Trapezoidal,
        };
        TransientResult trapezoidal = netlist.Run(options);
        TransientResult backwardEuler = netlist.Run(options with { Method = TransientMethod.BackwardEuler });

        Assert.True(trapezoidal.Completed, trapezoidal.Message);
        Assert.True(backwardEuler.Completed, backwardEuler.Message);
        AssertClose(initialVolts, trapezoidal.InitialPoint!.NodeVoltages["a"], 1e-12);

        // The first step is backward Euler by design, so it dissipates a little of the
        // initial tank energy. From there the trapezoidal rule must conserve the energy
        // of this linear LC pair to rounding, which is the property being checked.
        double bootstrapEnergy = Energy(trapezoidal.Points[1]);
        Assert.True(
            Math.Abs(bootstrapEnergy - initialEnergy) / initialEnergy < 2e-3,
            $"the backward-Euler bootstrap step lost {Math.Abs(bootstrapEnergy - initialEnergy) / initialEnergy:G3} of the tank energy.");
        double trapezoidalDrift = Math.Abs(Energy(trapezoidal.FinalPoint!) - bootstrapEnergy) / bootstrapEnergy;
        Assert.True(
            trapezoidalDrift < 1e-9,
            $"trapezoidal energy drifted by {trapezoidalDrift:G3} of the tank energy after the first step.");

        double previous = initialEnergy;
        foreach (TransientPoint point in backwardEuler.Points)
        {
            double energy = Energy(point);
            Assert.True(
                energy <= previous * (1 + 1e-12),
                $"backward Euler gained energy at t={point.Time:G6}: {energy:G9} > {previous:G9}.");
            previous = energy;
        }

        Assert.True(
            previous < initialEnergy * 0.95,
            $"backward Euler must damp the tank, but energy ended at {previous / initialEnergy:G4} of the initial value.");

        static double Energy(TransientPoint point) =>
            (0.5 * capacitance * point.NodeVoltages["a"] * point.NodeVoltages["a"]) +
            (0.5 * inductance * point.ElementCurrents["L1"] * point.ElementCurrents["L1"]);
    }

    [Fact]
    public void AbsorbedPowerSumsToZeroAtEveryOperatingPoint()
    {
        var options = new TransientOptions { StopTime = 1e-3, Steps = 50 };
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "a", 100)
            .AddCapacitor("C1", "a", "gnd", 1e-6)
            .AddInductor("L1", "a", "b", 1e-3)
            .AddResistor("R2", "b", "gnd", 100);

        TransientResult result = netlist.Run(options);

        Assert.True(result.Completed, result.Message);
        foreach (TransientPoint point in result.Points)
        {
            double total = point.ElementPowers.Values.Sum();
            double scale = Math.Max(point.ElementPowers.Values.Sum(power => Math.Abs(power)), 1e-12);
            Assert.True(
                Math.Abs(total) <= 1e-9 * scale,
                $"absorbed power summed to {total:G6} W at t={point.Time:G6} (scale {scale:G6} W).");
        }
    }

    [Fact]
    public void DiodeAndCapacitorChargeMonotonicallyWithoutLosingNewtonConvergence()
    {
        const double supply = 5;
        var options = new TransientOptions { StopTime = 0.02, Steps = 200 };
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", supply)
            .SetWaveform("V1", [new TransientWaveformPoint(0, 0), new TransientWaveformPoint(1e-12, supply)])
            .AddResistor("R1", "in", "out", 100)
            .AddDiode("D1", "out", "cap", 1e-12)
            .AddCapacitor("C1", "cap", "gnd", 10e-6)
            .AddResistor("R2", "cap", "gnd", 1e6);

        TransientResult result = netlist.Run(options);

        Assert.True(result.Completed, result.Message);
        Assert.All(result.Points, point => Assert.True(point.RelativeResidual <= 1));
        double previous = result.InitialPoint!.NodeVoltages["cap"];
        foreach (TransientPoint point in result.Points)
        {
            Assert.True(
                point.NodeVoltages["cap"] >= previous - 1e-12,
                $"capacitor voltage fell at t={point.Time:G6}: {point.NodeVoltages["cap"]:G9} < {previous:G9}.");
            previous = point.NodeVoltages["cap"];
        }

        double final = result.FinalPoint!.NodeVoltages["cap"];
        Assert.InRange(final, 4.0, supply);
    }

    [Fact]
    public void ElementOrderAndGroundNodeNameDoNotChangeResults()
    {
        var options = new TransientOptions { StopTime = 2e-3, Steps = 40 };
        TransientWaveformPoint[] drive =
        [
            new(0, 0),
            new(5e-4, 5),
            new(1e-3, 0),
        ];

        var first = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .SetWaveform("V1", drive)
            .AddResistor("R1", "in", "a", 100)
            .AddCapacitor("C1", "a", "gnd", 1e-6)
            .AddInductor("L1", "a", "b", 1e-3)
            .AddResistor("R2", "b", "gnd", 100);
        var second = new TransientNetlist("ground")
            .AddResistor("R2", "b", "ground", 100)
            .AddInductor("L1", "a", "b", 1e-3)
            .AddCapacitor("C1", "a", "ground", 1e-6)
            .AddResistor("R1", "in", "a", 100)
            .AddVoltageSource("V1", "in", "ground", 5)
            .SetWaveform("V1", drive);

        TransientResult left = first.Run(options);
        TransientResult right = second.Run(options);

        Assert.True(left.Completed, left.Message);
        Assert.True(right.Completed, right.Message);
        Assert.Equal(left.Points.Count, right.Points.Count);
        for (int index = 0; index < left.Points.Count; index++)
        {
            Assert.Equal(left.Points[index].Time, right.Points[index].Time);
            foreach (string node in new[] { "in", "a", "b" })
            {
                AssertClose(left.Points[index].NodeVoltages[node], right.Points[index].NodeVoltages[node], 1e-12);
            }

            foreach (string element in new[] { "V1", "R1", "C1", "L1", "R2" })
            {
                AssertClose(
                    left.Points[index].ElementCurrents[element],
                    right.Points[index].ElementCurrents[element],
                    1e-12);
            }
        }
    }

    [Fact]
    public void InitialOperatingPointHoldsCapacitorsOpenAndInductorsShorted()
    {
        TransientResult capacitive = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 10)
            .AddResistor("R1", "in", "out", 1_000)
            .AddResistor("R2", "out", "gnd", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .Run(new TransientOptions { StopTime = 1e-6, Steps = 1 });

        TransientResult inductive = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 10)
            .AddResistor("R1", "in", "out", 1_000)
            .AddInductor("L1", "out", "gnd", 0.01)
            .Run(new TransientOptions { StopTime = 1e-6, Steps = 1 });

        Assert.True(capacitive.Completed, capacitive.Message);
        Assert.True(inductive.Completed, inductive.Message);
        AssertClose(5, capacitive.InitialPoint!.NodeVoltages["out"], 1e-9);
        AssertClose(0, capacitive.InitialPoint.ElementCurrents["C1"], 1e-9);
        AssertClose(0, inductive.InitialPoint!.NodeVoltages["out"], 1e-9);
        AssertClose(0.01, inductive.InitialPoint.ElementCurrents["L1"], 1e-9);
    }

    [Fact]
    public void CapacitorOnlyNodeIsReportedAsFloatingInsteadOfGuessingAnOperatingPoint()
    {
        var netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "gnd", 1_000)
            .AddCapacitor("C1", "iso", "gnd", 1e-6);

        TransientResult result = netlist.Run(new TransientOptions { StopTime = 1e-3, Steps = 10 });

        Assert.False(result.Completed);
        Assert.Equal(DcSolveFailure.FloatingNetwork, result.Failure);
        Assert.Empty(result.Points);
        Assert.Contains("'iso'", result.Message, StringComparison.Ordinal);
        Assert.Null(result.FailedStep);
    }

    [Fact]
    public void RunsAreDeterministic()
    {
        var options = new TransientOptions { StopTime = 1e-3, Steps = 25 };
        TransientNetlist netlist = RcStepNetlist(5, 1_000, 1e-6);

        TransientResult first = netlist.Run(options);
        TransientResult second = netlist.Run(options);

        Assert.True(first.Completed, first.Message);
        Assert.Equal(first.Points.Count, second.Points.Count);
        for (int index = 0; index < first.Points.Count; index++)
        {
            Assert.Equal(first.Points[index].Time, second.Points[index].Time);
            Assert.Equal(
                first.Points[index].NodeVoltages["out"],
                second.Points[index].NodeVoltages["out"]);
            Assert.Equal(
                first.Points[index].ElementCurrents["C1"],
                second.Points[index].ElementCurrents["C1"]);
        }
    }

    [Fact]
    public void WaveformInterpolatesBetweenBreakpointsAndHoldsItsEndpoints()
    {
        TransientWaveform waveform = TransientWaveform.Create(
        [
            new TransientWaveformPoint(2, 3),
            new TransientWaveformPoint(0, 1),
            new TransientWaveformPoint(4, 5),
        ]);

        Assert.Equal(3, waveform.Points.Count);
        Assert.Equal(1, waveform.ValueAt(-1));
        Assert.Equal(1, waveform.ValueAt(0));
        Assert.Equal(2, waveform.ValueAt(1));
        Assert.Equal(3, waveform.ValueAt(2));
        Assert.Equal(4, waveform.ValueAt(3));
        Assert.Equal(5, waveform.ValueAt(4));
        Assert.Equal(5, waveform.ValueAt(100));
    }

    [Fact]
    public void InvalidElementsWaveformsAndOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientNetlist("gnd").AddCapacitor("C1", "a", "gnd", 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientNetlist("gnd").AddInductor("L1", "a", "gnd", double.NaN));
        Assert.Throws<ArgumentException>(
            () => new TransientNetlist("gnd").AddCapacitor("~C1", "a", "gnd", 1e-6));
        Assert.Throws<ArgumentException>(
            () => new TransientNetlist("gnd").AddCapacitor("C1", "a", "a", 1e-6));

        var duplicate = new TransientNetlist("gnd").AddResistor("R1", "a", "gnd", 1);
        Assert.Throws<ArgumentException>(() => duplicate.AddCapacitor("R1", "a", "gnd", 1e-6));

        var netlist = new TransientNetlist("gnd").AddResistor("R1", "a", "gnd", 1);
        Assert.Throws<ArgumentException>(() => netlist.SetCapacitorInitialVoltage("missing", 1));
        Assert.Throws<ArgumentException>(() => netlist.SetInductorInitialCurrent("missing", 1));
        Assert.Throws<ArgumentException>(() => netlist.SetWaveform("R1", [new TransientWaveformPoint(0, 1)]));
        Assert.Throws<ArgumentException>(
            () => TransientWaveform.Create(
                [new TransientWaveformPoint(1, 0), new TransientWaveformPoint(1, 5)]));
        Assert.Throws<ArgumentException>(() => TransientWaveform.Create([]));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientOptions { StopTime = 0, Steps = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientOptions { StopTime = 1, Steps = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientOptions
            {
                StartTime = double.NaN,
                StopTime = 1,
                Steps = 1,
            }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientOptions
            {
                StopTime = 1,
                Steps = TransientOptions.MaximumSteps + 1,
            }.Validate());
    }

    [Fact]
    public void AdaptiveRcChargeMeetsItsToleranceWithFewerPointsThanAFixedRun()
    {
        const double supply = 5;
        const double resistance = 1_000;
        const double capacitance = 1e-6;
        double tau = resistance * capacitance;
        TransientNetlist netlist = RcStepNetlist(supply, resistance, capacitance);

        TransientResult adaptive = netlist.Run(new TransientOptions
        {
            StopTime = 5 * tau,
            AdaptiveTolerance = 1e-6,
        });
        TransientResult fixedStep = netlist.Run(new TransientOptions
        {
            StopTime = 5 * tau,
            Steps = 100,
        });

        Assert.True(adaptive.Completed, adaptive.Message);
        double exact = supply * (1 - Math.Exp(-5));
        double adaptiveError = Math.Abs(adaptive.FinalPoint!.NodeVoltages["out"] - exact);
        double fixedError = Math.Abs(fixedStep.FinalPoint!.NodeVoltages["out"] - exact);
        Assert.True(adaptiveError < 1e-3, $"adaptive error was {adaptiveError:G6} V.");
        Assert.True(
            adaptiveError * 5 < fixedError,
            $"adaptive error {adaptiveError:G6} V was not clearly better than the 100-step error {fixedError:G6} V.");
        Assert.True(adaptive.Points.Count < 1_000, $"adaptive run used {adaptive.Points.Count} points.");
        Assert.Equal(0, adaptive.InitialPoint!.Time);
        Assert.Equal(5 * tau, adaptive.FinalPoint.Time, 1e-15);
        for (int index = 1; index < adaptive.Points.Count; index++)
        {
            Assert.True(adaptive.Points[index].Time > adaptive.Points[index - 1].Time);
        }

        foreach (TransientPoint point in adaptive.Points)
        {
            double total = point.ElementPowers.Values.Sum();
            double scale = Math.Max(point.ElementPowers.Values.Sum(power => Math.Abs(power)), 1e-12);
            Assert.True(Math.Abs(total) <= 1e-9 * scale);
        }
    }

    [Fact]
    public void TighterAdaptiveToleranceReducesErrorAndAddsPoints()
    {
        double looseError = AdaptiveRcError(1e-3, out int loosePoints);
        double tightError = AdaptiveRcError(1e-9, out int tightPoints);

        Assert.True(tightError < looseError, $"tight tolerance error {tightError:G6} was not below {looseError:G6}.");
        Assert.True(tightPoints > loosePoints, "a tighter tolerance must not use fewer points.");
    }

    [Fact]
    public void AdaptiveControlHandlesATimeConstantFarBelowTheWholeInterval()
    {
        // tau = 1 us over a 10 ms interval: a fixed run with the default step count
        // cannot resolve the charge at all, while adaptive control must reach the
        // analytic final value.
        var options = new TransientOptions
        {
            StopTime = 1e-2,
            AdaptiveTolerance = 1e-4,
        };

        TransientResult result = RcStepNetlist(5, 1_000, 1e-9).Run(options);

        Assert.True(result.Completed, result.Message);
        AssertClose(5, result.FinalPoint!.NodeVoltages["out"], 1e-4);
        Assert.True(result.Points.Count < options.MaximumStepCount);
    }

    [Fact]
    public void AdaptiveRunsAreDeterministic()
    {
        var options = new TransientOptions { StopTime = 2e-3, AdaptiveTolerance = 1e-7 };
        TransientNetlist netlist = RcStepNetlist(5, 1_000, 1e-6);

        TransientResult first = netlist.Run(options);
        TransientResult second = netlist.Run(options);

        Assert.True(first.Completed, first.Message);
        Assert.Equal(first.Points.Count, second.Points.Count);
        for (int index = 0; index < first.Points.Count; index++)
        {
            Assert.Equal(first.Points[index].Time, second.Points[index].Time);
            Assert.Equal(
                first.Points[index].NodeVoltages["out"],
                second.Points[index].NodeVoltages["out"]);
        }
    }

    [Fact]
    public void AdaptiveControlReportsWhenTheMinimumStepCannotMeetTheTolerance()
    {
        var options = new TransientOptions
        {
            StopTime = 1e-3,
            AdaptiveTolerance = 1e-12,
            InitialTimeStep = 1e-3,
            MinimumTimeStep = 1e-3,
            MaximumStepCount = 1_000,
        };

        // tau = 10 us, so a 1 ms step cannot meet a 1e-12 tolerance and shrinking is
        // forbidden by the minimum step.
        TransientResult result = RcStepNetlist(5, 1_000, 1e-8).Run(options);

        Assert.False(result.Completed);
        Assert.Equal(DcSolveFailure.NonConvergent, result.Failure);
        Assert.Contains("minimum step", result.Message, StringComparison.Ordinal);
        Assert.NotEmpty(result.Points);
    }

    [Fact]
    public void AdaptiveOptionsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientOptions { StopTime = 1, AdaptiveTolerance = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientOptions { StopTime = 1, AdaptiveTolerance = double.NaN }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientOptions { StopTime = 1, AdaptiveTolerance = 1e-6, MaximumStepCount = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientOptions { StopTime = 1, AdaptiveTolerance = 1e-6, InitialTimeStep = double.NaN }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientOptions { StopTime = 1, AdaptiveTolerance = 1e-6, MinimumTimeStep = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientOptions
            {
                StopTime = 1,
                AdaptiveTolerance = 1e-6,
                InitialTimeStep = 0.1,
                MaximumTimeStep = 0.01,
            }.Validate());
    }

    private static double AdaptiveRcError(double tolerance, out int points)
    {
        const double supply = 5;
        const double resistance = 1_000;
        const double capacitance = 1e-6;
        double tau = resistance * capacitance;
        TransientResult result = RcStepNetlist(supply, resistance, capacitance).Run(new TransientOptions
        {
            StopTime = 3 * tau,
            AdaptiveTolerance = tolerance,
        });
        Assert.True(result.Completed, result.Message);
        points = result.Points.Count;
        double exact = supply * (1 - Math.Exp(-3));
        return Math.Abs(result.FinalPoint!.NodeVoltages["out"] - exact);
    }

    private static TransientNetlist RcStepNetlist(double supply, double resistance, double capacitance) =>
        new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", supply)
            .SetWaveform("V1", [new TransientWaveformPoint(0, 0), new TransientWaveformPoint(1e-12, supply)])
            .AddResistor("R1", "in", "out", resistance)
            .AddCapacitor("C1", "out", "gnd", capacitance);

    private static double RcStepError(TransientMethod method, int steps)
    {
        const double supply = 5;
        const double resistance = 1_000;
        const double capacitance = 1e-6;
        double tau = resistance * capacitance;
        var options = new TransientOptions
        {
            StopTime = tau,
            Steps = steps,
            Method = method,
        };

        TransientResult result = RcStepNetlist(supply, resistance, capacitance).Run(options);
        Assert.True(result.Completed, result.Message);
        double exact = supply * (1 - Math.Exp(-1));
        return Math.Abs(result.FinalPoint!.NodeVoltages["out"] - exact);
    }

    private static void AssertClose(double expected, double actual, double tolerance)
    {
        Assert.True(
            Math.Abs(expected - actual) <= tolerance * Math.Max(1, Math.Abs(expected)),
            $"expected {expected:G12} but found {actual:G12} (tolerance {tolerance:G3}).");
    }
}
