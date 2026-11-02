using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A single ratio from one grid is not evidence, because the arrival time is quantised by the step
/// and the devices change region during the run. The same comparison is taken at three step counts;
/// the ratio must stay positive and must not drift with the grid, which is what separates a real
/// effect from an artefact of where the samples happened to land.
/// </summary>
public sealed class BiasDependentSettlingTests
{
    private static ElectricalCircuit Example()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix"),
            validate: false);
        return Assert.Single(project.ElectricalCircuits);
    }

    private static TransientResult Run(ElectricalCircuit circuit, int steps, bool biasDependent)
    {
        TransientNetlist netlist = circuit.ToTransientNetlist();
        foreach (ElectricalComponent device in circuit.Components
                     .Where(component => component.Kind == ElectricalComponentKind.Mosfet))
        {
            device.BiasDependentCapacitance = biasDependent;
        }

        netlist = circuit.ToTransientNetlist();
        return netlist.Run(new TransientOptions { StartTime = 0, StopTime = 120e-9, Steps = steps });
    }

    private static double GateArrival(TransientResult result)
    {
        double final = result.Points[^1].NodeVoltages["g"];
        foreach (TransientPoint point in result.Points)
        {
            if (point.NodeVoltages["g"] >= final * 0.9)
            {
                return point.Time;
            }
        }

        return result.Points[^1].Time;
    }

    [Fact]
    public void TheGateArrivesSoonerOnEveryGrid()
    {
        ElectricalCircuit circuit = Example();
        double[] ratios = [.. new[] { 240, 480, 960 }
            .Select(steps => GateArrival(Run(circuit, steps, biasDependent: false)) /
                             GateArrival(Run(circuit, steps, biasDependent: true)))];

        // Less gate charge, so the bus arrives sooner: the ratio is above one at every resolution
        // and below the Miller-load bound of 4.5 measured on the single-device stage.
        Assert.All(ratios, ratio => Assert.InRange(ratio, 1.0, 4.5));

        // The ratio must not drift with the grid: the spread stays within 10 % of the mean, so the
        // effect is the model rather than where the samples landed.
        double mean = ratios.Average();
        Assert.InRange(ratios.Max() - ratios.Min(), 0, mean * 0.1);
    }
}
