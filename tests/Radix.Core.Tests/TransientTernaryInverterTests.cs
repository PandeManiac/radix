using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The ternary inverter in the time domain. Every capacitance in the shipped circuit belongs to a
/// device, so the output's speed is the transistors' own: gate charge through the drive resistor and
/// drain charge through the summing resistors. Each claim is compared with something the circuit
/// states independently — a DC solve, a summing identity, a first-order closed form — rather than a
/// literal a step size could drift away from.
/// </summary>
public sealed class TransientTernaryInverterTests
{
    private const double StepSeconds = 0.25e-9;

    /// <summary>Midpoint of the 20 ns to 21 ns drive ramp, the reference for gate charge times.</summary>
    private const double RampMidpoint = 20.5e-9;

    private static readonly string[] Devices = ["MPL", "MNL", "MPH", "MNH"];

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    /// <summary>
    /// The shipped circuit with every device's drain-source capacitance set to one value and its
    /// gate-source capacitance scalable, so a test can change what the transistors present without
    /// adding a component the example omits.
    /// </summary>
    private static ElectricalCircuit Load(double deviceFarads = 1e-12, double gateSourceScale = 1)
    {
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(RepositoryRoot(), "examples", "transient-ternary-inverter.radix"));
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        foreach (string id in Devices)
        {
            ElectricalComponent device = circuit.FindComponent(id)!;
            device.DrainSourceCapacitance = deviceFarads;
            device.GateSourceCapacitance *= gateSourceScale;
        }

        return circuit;
    }

    private static TransientResult Run(
        double deviceFarads = 1e-12,
        double stopSeconds = 900e-9,
        double gateSourceScale = 1)
    {
        TransientResult result = Load(deviceFarads, gateSourceScale).ToTransientNetlist().Run(
            new TransientOptions
            {
                StartTime = 0,
                StopTime = stopSeconds,
                Steps = (int)Math.Round(stopSeconds / StepSeconds),
            });

        Assert.True(result.Failure is null, $"{result.Failure}: {result.Message}");
        Assert.True(result.Completed);
        return result;
    }

    /// <summary>When a node last leaves the 2 % band of its own swing, in seconds.</summary>
    private static double SettlingTime(TransientResult result, string node)
    {
        double final = result.Points[^1].NodeVoltages[node];
        double band = 0.02 * Math.Abs(result.Points[0].NodeVoltages[node] - final);
        double settled = 0;
        foreach (TransientPoint point in result.Points)
        {
            if (Math.Abs(point.NodeVoltages[node] - final) > band)
            {
                settled = point.Time;
            }
        }

        return settled;
    }

    /// <summary>
    /// How long the gate bus takes to reach half of its drive, measured from the midpoint of the
    /// ramp so the ramp's own duration cancels and only the charge time remains.
    /// </summary>
    private static double GateChargeTime(TransientResult result)
    {
        double drive = result.Points[^1].NodeVoltages["g"];
        TransientPoint half = result.Points.First(point => point.NodeVoltages["g"] >= drive / 2);
        return half.Time - RampMidpoint;
    }

    [Fact]
    public void TheOutputLeavesTheRailAndSettlesOnTheMiddleLevel()
    {
        // Long enough for the tail to arrive, whatever the step.
        ElectricalCircuit circuit = Load();
        TransientResult result = circuit.ToTransientNetlist().Run(new TransientOptions
        {
            StartTime = 0,
            StopTime = 3e-6,
            Steps = (int)Math.Round(3e-6 / StepSeconds),
        });

        Assert.Null(result.Failure);
        Assert.Equal(5, result.Points[0].NodeVoltages["out"], 3);

        // The level the run settles on is the static operating point at the driven value, so it is
        // compared with a DC solve rather than a literal: device charge delays the arrival, it does
        // not move the destination. The shipped circuit's own DC solve describes the start, where
        // the source is still at 0 V, so it is biased here instead.
        ElectricalCircuit biased = Load();
        biased.FindComponent("VIN")!.Value = 2.5;
        double settled = biased.ToDcNetlist().Solve().NodeVoltages["out"];
        Assert.Equal(2.5, settled, 3);
        Assert.Equal(settled, result.Points[^1].NodeVoltages["out"], 3);

        // Approached from the rail, and it never dips below the level it is heading for.
        Assert.All(
            result.Points.Where(point => point.Time > 40e-9),
            point => Assert.InRange(point.NodeVoltages["out"], settled - 1e-9, 5));
    }

    [Fact]
    public void TheSummingNodeIsTheMeanOfTheTwoBranchOutputs()
    {
        // Two equal summing resistors make the output the mean of the branch nodes, which is a
        // property of the network rather than of any particular run length.
        TransientResult result = Run();
        TransientPoint last = result.Points[^1];

        Assert.Equal(
            (last.NodeVoltages["out_low"] + last.NodeVoltages["out_high"]) / 2,
            last.NodeVoltages["out"],
            6);

        // The low branch conducts and the high branch does not, so the levels are not symmetric.
        Assert.InRange(last.NodeVoltages["out_low"], 0, 0.05);
        Assert.InRange(last.NodeVoltages["out_high"], 4.95, 5);

        // That transition is charged by the devices, and it is over well inside this run.
        Assert.InRange(SettlingTime(result, "out"), 40e-9, 60e-9);
    }

    [Fact]
    public void TheDriveChargesTheGateBusBeforeTheOutputMoves()
    {
        // 10 kOhm into four 0.25 pF gates is 1 pF, so the bus reaches the 1 V threshold at about
        // 26 ns and half of the drive a few nanoseconds later. Without device capacitance the gates
        // would follow within the 1 ns ramp, so this is what makes the delay the devices'.
        TransientResult result = Run();

        TransientPoint gateHalf = result.Points.First(point => point.NodeVoltages["g"] >= 1.25);
        Assert.InRange(gateHalf.Time, 25e-9, 31e-9);
        Assert.Equal(2.5, result.Points[^1].NodeVoltages["g"], 3);

        // A device cannot conduct before its gate reaches the threshold, so at that sample the
        // output has not left the rail (the gate-drain feedthrough lifts it a few millivolts).
        TransientPoint gateThreshold = result.Points.First(point => point.NodeVoltages["g"] >= 1);
        Assert.True(
            gateThreshold.NodeVoltages["out"] > 5 - 0.01,
            $"output {gateThreshold.NodeVoltages["out"]} fell while the gate was below threshold");
    }

    [Fact]
    public void DoublingTheGateCapacitanceDoublesTheChargeTime()
    {
        // The bus is a first-order RC: 10 kOhm into the devices' gate-source capacitance, so
        // doubling that capacitance doubles the half-rise time. Unlike the drain node, whose tail
        // is set by the device's on-resistance once it leaves saturation, this is a clean closed
        // form and it cannot be produced by the integrator.
        double single = GateChargeTime(Run());
        double doubled = GateChargeTime(Run(gateSourceScale: 2));

        Assert.InRange(single, 5e-9, 12e-9);
        Assert.InRange(doubled / single, 1.8, 2.2);
    }

    [Fact]
    public void GateDrainCapacitanceFeedsTheGateTransitionIntoTheDrain()
    {
        // A rising gate pulls its own drain up through Cgd before the channel conducts, so the drain
        // node visits a voltage above the rail. Without the feedback it cannot: the difference is a
        // measurement of the Miller path rather than a claim that the parameter is stored.
        TransientResult coupled = Run();
        double peak = coupled.Points.Max(point => point.NodeVoltages["out_low"]);
        Assert.True(peak > 5.01, $"out_low peaked at {peak} V");

        ElectricalCircuit without = Load();
        foreach (string id in Devices)
        {
            without.FindComponent(id)!.GateDrainCapacitance = 0;
        }

        TransientResult plain = without.ToTransientNetlist().Run(new TransientOptions
        {
            StartTime = 0,
            StopTime = 900e-9,
            Steps = (int)Math.Round(900e-9 / StepSeconds),
        });

        Assert.Null(plain.Failure);
        Assert.All(
            plain.Points,
            point => Assert.True(
                point.NodeVoltages["out_low"] <= 5 + 1e-9,
                $"out_low reached {point.NodeVoltages["out_low"]} V at {point.Time} s without feedback"));
    }
}
