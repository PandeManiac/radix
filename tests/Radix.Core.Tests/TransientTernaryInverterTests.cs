using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The ternary inverter in the time domain. Its middle level is not an assignment the solver makes
/// but a state the network reaches, so this checks that the level is reached, that the decay belongs
/// to the summing node rather than to the integrator, and that the drive has to charge the gates
/// before the devices can respond.
/// </summary>
public sealed class TransientTernaryInverterTests
{
    private const double StepEnd = 21e-9;

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

    private static ElectricalCircuit Load(double loadFarads = 2e-12)
    {
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(RepositoryRoot(), "examples", "transient-ternary-inverter.radix"));
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        circuit.FindComponent("CLOAD")!.Value = loadFarads;
        return circuit;
    }

    private static TransientResult Run(double loadFarads = 2e-12, double stopSeconds = 600e-9) =>
        Run(Load(loadFarads), stopSeconds);

    private static TransientResult Run(ElectricalCircuit circuit, double stopSeconds)
    {
        TransientResult result = circuit.ToTransientNetlist().Run(new TransientOptions
        {
            StartTime = 0,
            StopTime = stopSeconds,
            Steps = (int)Math.Round(stopSeconds / 1e-9),
        });

        Assert.Null(result.Failure);
        Assert.True(result.Completed);
        return result;
    }

    /// <summary>
    /// The slow tail's constant, from where the curve is halfway and nine tenths of the way to its
    /// final value: <c>tau = (t90 - t50) / ln 5</c>. Two crossings rather than one remove the delay
    /// in front of the decay — which here includes charging the gate bus — so the number describes
    /// the summing node and not everything that happened before it.
    /// </summary>
    private static double TailTimeConstant(TransientResult result)
    {
        double final = result.Points[^1].NodeVoltages["out"];
        double swing = 5 - final;
        TransientPoint half = result.Points.First(point =>
            point.NodeVoltages["out"] <= final + (0.5 * swing));
        TransientPoint ninth = result.Points.First(point =>
            point.NodeVoltages["out"] <= final + (0.1 * swing));
        return (ninth.Time - half.Time) / Math.Log(5);
    }

    [Fact]
    public void TheOutputLeavesTheRailAndSettlesOnTheMiddleLevel()
    {
        // Long enough for the tail to arrive: at 600 ns the curve is still 8 mV above its
        // destination, which is inside the 2% band but nowhere near converged.
        ElectricalCircuit circuit = Load();
        TransientResult result = Run(circuit, 3e-6);

        Assert.Equal(5, result.Points[0].NodeVoltages["out"], 3);

        // The level the run settles on is the static operating point at the driven value, so it is
        // compared with a DC solve rather than a literal: charging the gates delays the arrival, it
        // does not move the destination. The DC solve of the shipped circuit describes the start,
        // where the source is still at 0 V, so it is biased here instead.
        ElectricalCircuit biased = Load();
        biased.FindComponent("VIN")!.Value = 2.5;
        double settled = biased.ToDcNetlist().Solve().NodeVoltages["out"];
        Assert.Equal(settled, result.Points[^1].NodeVoltages["out"], 3);
        Assert.True(settled > 2.4 && settled < 2.6, $"settled {settled}");

        // It is a middle level, not a rail, and it is approached from above rather than jumped to.
        double final = result.Points[^1].NodeVoltages["out"];
        Assert.True(final > 2.4 && final < 2.6, $"final {final}");
        Assert.All(
            result.Points.Where(point => point.Time > StepEnd),
            point => Assert.InRange(point.NodeVoltages["out"], final, 5));
    }

    [Fact]
    public void TheDriveChargesTheGateBusBeforeTheOutputMoves()
    {
        // 10 kOhm into four 0.25 pF gates is 1 pF, so the bus half-rises ln 2 of 10 ns after the
        // drive's midpoint. Without the device capacitance the gates would follow within the 1 ns
        // ramp, so this is the assertion that makes the delay partly the devices'.
        TransientResult result = Run();

        TransientPoint gateHalf = result.Points.First(point => point.NodeVoltages["g"] >= 1.25);
        Assert.InRange(gateHalf.Time, 25e-9, 30e-9);
        Assert.Equal(2.5, result.Points[^1].NodeVoltages["g"], 3);

        // A device cannot conduct before its gate reaches the threshold, so the output is still on
        // the rail at the sample where the bus gets there.
        TransientPoint gateThreshold = result.Points.First(point => point.NodeVoltages["g"] >= 1);
        Assert.True(
            gateThreshold.NodeVoltages["out"] > 4.99,
            $"output {gateThreshold.NodeVoltages["out"]} fell while the gate was still below threshold");
    }

    [Fact]
    public void TheSwitchingTimeMatchesTheSummingNodeRc()
    {
        // Two 100 kOhm summing resistors in parallel give 50 kOhm; with 2 pF that is 100 ns.
        TransientResult result = Run();
        Assert.InRange(TailTimeConstant(result), 90e-9, 110e-9);

        // The 2% settling band of the 5 V -> 2.5 V swing is reached in about four constants, after
        // the gate bus has finished charging.
        double final = result.Points[^1].NodeVoltages["out"];
        double band = 0.02 * (5 - final);
        TransientPoint settled = result.Points.First(point =>
            Math.Abs(point.NodeVoltages["out"] - final) <= band);
        Assert.InRange(settled.Time, 350e-9, 460e-9);
    }

    [Fact]
    public void DoublingTheLoadCapacitanceDoublesTheDelay()
    {
        // The decay must come from the network, not from the integrator: a physical model doubles
        // when the load does. The gate bus is unchanged, and the tail measurement ignores the delay
        // it contributes.
        double single = TailTimeConstant(Run(2e-12));
        // The doubled load needs twice the time to settle, so its run is twice as long.
        double doubled = TailTimeConstant(Run(4e-12, 1.2e-6));
        Assert.InRange(doubled / single, 1.9, 2.1);
    }
}
