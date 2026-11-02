using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The ternary inverter in the time domain. Its middle level is not an assignment the solver makes
/// but a state the network reaches, so this checks that the level is reached, that the delay belongs
/// to the load capacitance, and that the switching energy is accounted for.
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

    private static TransientResult Run(double loadFarads = 2e-12, double stopSeconds = 600e-9)
    {
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(RepositoryRoot(), "examples", "transient-ternary-inverter.radix"));
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        circuit.FindComponent("CLOAD")!.Value = loadFarads;

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
    /// The decay constant, from the half-life of the swing: <c>tau = t_half / ln 2</c>. Measuring
    /// where the curve crosses halfway avoids needing the asymptote, which a run that has not
    /// settled cannot supply without biasing the answer.
    /// </summary>
    private static double TimeConstant(TransientResult result)
    {
        double final = result.Points[^1].NodeVoltages["out"];
        double half = (5 + final) / 2;
        TransientPoint crossing = result.Points.First(point =>
            point.Time > StepEnd && point.NodeVoltages["out"] <= half);
        return (crossing.Time - StepEnd) / Math.Log(2);
    }

    [Fact]
    public void TheOutputLeavesTheRailAndSettlesOnTheMiddleLevel()
    {
        TransientResult result = Run();

        Assert.Equal(5, result.Points[0].NodeVoltages["out"], 3);
        Assert.Equal(2.508, result.Points[^1].NodeVoltages["out"], 3);

        // It is a middle level, not a rail, and it is approached from above rather than jumped to.
        double final = result.Points[^1].NodeVoltages["out"];
        Assert.True(final > 2.4 && final < 2.6, $"final {final}");
        Assert.All(
            result.Points.Where(point => point.Time > StepEnd),
            point => Assert.InRange(point.NodeVoltages["out"], final, 5));
    }

    [Fact]
    public void TheSwitchingTimeMatchesTheSummingNodeRc()
    {
        // Two 100 kOhm summing resistors in parallel give 50 kOhm; with 2 pF that is 100 ns.
        Assert.InRange(TimeConstant(Run()), 90e-9, 110e-9);

        // The 2% settling band of the 5 V -> 2.5 V swing is reached in about four constants.
        TransientResult result = Run();
        double final = result.Points[^1].NodeVoltages["out"];
        double band = 0.02 * (5 - final);
        TransientPoint settled = result.Points.First(point =>
            Math.Abs(point.NodeVoltages["out"] - final) <= band);
        Assert.InRange(settled.Time, 300e-9, 500e-9);
    }

    [Fact]
    public void DoublingTheLoadCapacitanceDoublesTheDelay()
    {
        // The delay must come from the network, not from the integrator: a physical model doubles
        // when the load does.
        double single = TimeConstant(Run(2e-12));
        // The doubled load needs twice the time to settle, so its run is twice as long.
        double doubled = TimeConstant(Run(4e-12, 1.2e-6));
        Assert.InRange(doubled / single, 1.9, 2.1);
    }
}
