using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The energy ledger is an independent physical check on a run: whatever the
/// sources deliver must be dissipated or stored. These tests pin the analytic
/// energy of a first-order charge, the conservation of a lossless tank, and the
/// order at which the ledger closes, so a regression in the companion models or
/// in the power bookkeeping cannot slip through.
/// </summary>
public sealed class TransientEnergyTests
{
    [Fact]
    public void RcChargeLedgerMatchesTheAnalyticEnergy()
    {
        // 5 V through 1 kOhm into 1 uF from rest: the source delivers C*V^2 = 25 uJ,
        // of which half is stored in the capacitor and half dissipated in the resistor.
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 0);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 0.05,
            Steps = 5_000,
            Method = TransientMethod.Trapezoidal,
        });

        Assert.True(result.Completed, result.Message);
        TransientEnergyBalance energy = Assert.IsType<TransientEnergyBalance>(result.Energy);
        AssertClose(25e-6, energy.DeliveredJoules, 2e-3);
        AssertClose(12.5e-6, energy.DissipatedJoules, 2e-3);
        AssertClose(12.5e-6, energy.FinalStoredJoules, 1e-3);
        AssertClose(12.5e-6, energy.StoredChangeJoules, 1e-3);
        // At h/tau = 0.01 the sampled-power integral is accurate to about 1e-5 relative.
        Assert.True(
            energy.RelativeResidual < 5e-5,
            $"the ledger did not close: {energy.ResidualJoules} J ({energy.RelativeResidual} relative)");
    }

    [Fact]
    public void EnergyLedgerAveragesLargeFinitePowersWithoutOverflow()
    {
        // The source and resistor each carry 1e308 W, but across a 1 ms interval
        // the delivered and dissipated energies are both a representable 1e305 J.
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 1e154)
            .AddResistor("R1", "in", "gnd", 1);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 1e-3,
            Steps = 1,
        });

        Assert.True(result.Completed, result.Message);
        TransientEnergyBalance energy = Assert.IsType<TransientEnergyBalance>(result.Energy);
        Assert.True(double.IsFinite(energy.DeliveredJoules));
        Assert.True(double.IsFinite(energy.DissipatedJoules));
        AssertClose(1e305, energy.DeliveredJoules, 1e-12);
        AssertClose(1e305, energy.DissipatedJoules, 1e-12);
        Assert.Equal(0, energy.StoredChangeJoules);
        Assert.Equal(0, energy.ResidualJoules);
    }

    [Fact]
    public void LosslessTankConservesEnergyUnderTheTrapezoidalRule()
    {
        double initial = 0.5 * 1e-6 * 25;
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddInductor("L1", "out", "gnd", 1e-3)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 5);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 1e-3,
            Steps = 1_000,
            Method = TransientMethod.Trapezoidal,
        });

        Assert.True(result.Completed, result.Message);
        TransientEnergyBalance energy = Assert.IsType<TransientEnergyBalance>(result.Energy);
        AssertClose(0, energy.DeliveredJoules, 1, absolute: initial * 1e-9);
        AssertClose(0, energy.DissipatedJoules, 1, absolute: initial * 1e-9);
        AssertClose(initial, energy.FinalStoredJoules, 1e-3);
        Assert.True(
            Math.Abs(energy.StoredChangeJoules) < initial * 2e-3,
            $"a lossless tank changed its stored energy by {energy.StoredChangeJoules} J");
        // At h*omega = 0.032 the sampled-power integral is second-order accurate,
        // so the residual is about 1e-3 rather than round-off.
        Assert.True(
            energy.RelativeResidual < 2e-3,
            $"the ledger did not close: {energy.ResidualJoules} J ({energy.RelativeResidual} relative)");
    }

    [Fact]
    public void TrapezoidalLedgerResidualFallsWithTheSquareOfTheStep()
    {
        // Second order in the step is the trapezoidal rule's signature; a wrong
        // companion model or a mis-integrated power term would break the ratio.
        double coarse = TankResidual(250);
        double fine = TankResidual(1_000);

        Assert.True(coarse > fine, $"coarse residual {coarse} should exceed fine residual {fine}");
        double ratio = coarse / fine;
        Assert.True(
            ratio is > 8 and < 40,
            $"refining the step 4x should cut the residual about 16x, measured {ratio}x");
    }

    [Fact]
    public void BackwardEulerLedgerResidualIsFirstOrderInTheStep()
    {
        double coarse = RcResidual(100);
        double fine = RcResidual(400);

        Assert.True(coarse > fine, $"coarse residual {coarse} should exceed fine residual {fine}");
        double ratio = coarse / fine;
        Assert.True(
            ratio is > 2 and < 8,
            $"refining the step 4x should cut the residual about 4x, measured {ratio}x");
    }

    [Fact]
    public void FailedInitialStateHasNoLedger()
    {
        // A capacitor with no resistive DC path cannot start a run: the operating
        // point is floating, so there are no points to integrate.
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddCapacitor("C1", "out", "gnd", 1e-6);

        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 1e-3,
            Steps = 10,
        });

        Assert.False(result.Completed);
        Assert.Empty(result.Points);
        Assert.Null(result.Energy);
    }

    [Fact]
    public void ReportJsonIncludesTheEnergyLedger()
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 0);
        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 0.02,
            Steps = 200,
        });

        using JsonDocument document = JsonDocument.Parse(
            TransientNetlistJson.ToReportJson("inline", netlist, result));
        JsonElement energy = document.RootElement.GetProperty("energy");

        Assert.True(energy.GetProperty("deliveredJoules").GetDouble() > 0);
        Assert.True(energy.GetProperty("dissipatedJoules").GetDouble() > 0);
        Assert.True(energy.GetProperty("finalStoredJoules").GetDouble() > 0);
        Assert.True(energy.GetProperty("relativeResidual").GetDouble() < 0.05);
    }

    private static double TankResidual(int steps)
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddInductor("L1", "out", "gnd", 1e-3)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 5);
        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 1e-3,
            Steps = steps,
            Method = TransientMethod.Trapezoidal,
        });

        Assert.True(result.Completed, result.Message);
        return Assert.IsType<TransientEnergyBalance>(result.Energy).RelativeResidual;
    }

    private static double RcResidual(int steps)
    {
        TransientNetlist netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 5)
            .AddResistor("R1", "in", "out", 1_000)
            .AddCapacitor("C1", "out", "gnd", 1e-6)
            .SetCapacitorInitialVoltage("C1", 0);
        TransientResult result = netlist.Run(new TransientOptions
        {
            StopTime = 0.05,
            Steps = steps,
            Method = TransientMethod.BackwardEuler,
        });

        Assert.True(result.Completed, result.Message);
        return Assert.IsType<TransientEnergyBalance>(result.Energy).RelativeResidual;
    }

    private static void AssertClose(
        double expected,
        double actual,
        double relativeTolerance,
        double absolute = 0)
    {
        double tolerance = Math.Max(Math.Abs(expected) * relativeTolerance, absolute);
        Assert.True(
            Math.Abs(actual - expected) <= tolerance,
            $"expected {expected:G6}, measured {actual:G6} (tolerance {tolerance:G6})");
    }
}
