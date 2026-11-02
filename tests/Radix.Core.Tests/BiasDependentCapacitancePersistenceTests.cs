using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The bias-dependent gate model is only usable if a file can ask for it. The round trip checks the
/// format version and the field; the settling test checks that the model a loaded file asks for is
/// the one that runs.
/// </summary>
public sealed class BiasDependentCapacitancePersistenceTests
{
    private static string ExamplePath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix");
    }

    private static CircuitProject LoadExample() => ProjectSerializer.Load(ExamplePath(), validate: false);

    [Fact]
    public void TheFieldSurvivesASaveAndLoad()
    {
        CircuitProject project = LoadExample();
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        circuit.FindComponent("MNL")!.BiasDependentCapacitance = true;

        string path = Path.Combine(Path.GetTempPath(), $"radix-bias-{Guid.NewGuid():N}.radix");
        try
        {
            File.WriteAllText(path, ProjectSerializer.SaveToString(project, includeView: false));
            string saved = File.ReadAllText(path);
            Assert.Contains("\"biasDependentCapacitance\": true", saved, StringComparison.Ordinal);

            CircuitProject reloaded = ProjectSerializer.Load(path);
            Assert.Equal(15, reloaded.FormatVersion);

            ElectricalCircuit reloadedCircuit = Assert.Single(reloaded.ElectricalCircuits);
            Assert.True(reloadedCircuit.FindComponent("MNL")!.BiasDependentCapacitance);
            // The other devices did not ask for it, and the field is stored per device.
            Assert.False(reloadedCircuit.FindComponent("MPH")!.BiasDependentCapacitance);
            Assert.False(reloadedCircuit.FindComponent("RG")!.BiasDependentCapacitance);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ALegacyFileWithoutTheFieldLoadsAsConstantCapacitance()
    {
        CircuitProject project = LoadExample();

        // Format 14 files have no such field; loading one must not invent the model.
        foreach (ElectricalComponent component in Assert.Single(project.ElectricalCircuits).Components)
        {
            Assert.False(component.BiasDependentCapacitance);
        }
    }

    [Fact]
    public void TheModelAFileAsksForIsTheOneThatRuns()
    {
        CircuitProject project = LoadExample();
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);

        TransientResult constant = Run(circuit);

        foreach (ElectricalComponent component in circuit.Components
                     .Where(component => component.Kind == ElectricalComponentKind.Mosfet))
        {
            component.BiasDependentCapacitance = true;
        }

        TransientResult biased = Run(circuit);

        // Less gate charge means the gate bus arrives sooner. Both runs step over the same times,
        // so the comparison is a like-for-like arrival time rather than a curve shape.
        double constantArrival = GateArrival(constant);
        double biasedArrival = GateArrival(biased);
        Assert.True(
            biasedArrival < constantArrival,
            $"biased {biasedArrival:G6} s against constant {constantArrival:G6} s");
        Assert.InRange(constantArrival / biasedArrival, 1.02, 3.0);
    }

    private static TransientResult Run(ElectricalCircuit circuit) => circuit.ToTransientNetlist()
        .Run(new TransientOptions { StartTime = 0, StopTime = 120e-9, Steps = 240 });

    /// <summary>Time at which the gate bus first reaches 90 % of its final value.</summary>
    private static double GateArrival(TransientResult result)
    {
        double final = result.Points[^1].NodeVoltages["g"];
        double target = final * 0.9;
        foreach (TransientPoint point in result.Points)
        {
            if (point.NodeVoltages["g"] >= target)
            {
                return point.Time;
            }
        }

        return result.Points[^1].Time;
    }
}
