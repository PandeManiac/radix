using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The documented comparison has to be reproducible from the repository's own files, not only from a
/// test that flips a flag. These two examples differ in exactly that flag, so the pair is the claim.
/// </summary>
public sealed class BiasDependentExampleTests
{
    private const string Constant = "transient-ternary-inverter.radix";
    private const string BiasDependent = "transient-ternary-inverter-bias-dependent.radix";

    private static ElectricalCircuit Load(string file)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", file),
            validate: false);
        return Assert.Single(project.ElectricalCircuits);
    }

    private static double GateArrival(ElectricalCircuit circuit)
    {
        TransientResult result = circuit.ToTransientNetlist().Run(
            new TransientOptions { StartTime = 0, StopTime = 120e-9, Steps = 1280 });
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
    public void TheShippedExampleIsFormatFifteenAndAsksForTheModelOnEveryDevice()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", BiasDependent));
        Assert.Equal(15, project.FormatVersion);

        ElectricalComponent[] devices = [.. Assert.Single(project.ElectricalCircuits).Components
            .Where(component => component.Kind == ElectricalComponentKind.Mosfet)];
        Assert.Equal(4, devices.Length);
        Assert.All(devices, device => Assert.True(device.BiasDependentCapacitance));
    }

    [Fact]
    public void TheTwoShippedFilesDifferInTheWayTheDocsSay()
    {
        double constant = GateArrival(Load(Constant));
        double biasDependent = GateArrival(Load(BiasDependent));

        // The docs quote 67.4 ns against 43.6 ns, a factor of 1.55. The band carries the measured
        // figure so the table cannot drift away from the files without this failing.
        Assert.InRange(constant / biasDependent, 1.45, 1.65);
    }
}
