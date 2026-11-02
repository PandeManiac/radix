using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The paired example with its output pin declared externally supplied: the solved node writes Y
/// instead of being checked against it. This is the direction that was refused outright before the
/// declaration existed, and the run has to pass for it to mean anything - a plan can name a pin and
/// still be rejected when it is applied.
/// </summary>
public sealed class ExternalSupplyExampleTests
{
    private static string ExamplePath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(
            directory!.FullName,
            "examples",
            "electrical-ternary-supply.radix");
    }

    [Fact]
    public void TheSolvedNodeSuppliesTheDeclaredOutput()
    {
        CircuitProject project = ProjectSerializer.Load(ExamplePath(), validate: false);
        CircuitDefinition definition = Assert.IsType<CircuitDefinition>(
            project.FindDefinition("demo.supply"));
        Assert.True(Assert.IsType<PinDeclaration>(definition.FindPin("y")).ExternalSupply);
        Assert.False(Assert.IsType<PinDeclaration>(definition.FindPin("a")).ExternalSupply);

        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        ElectricalLevelSampler sampler = Assert.IsType<ElectricalLevelSampler>(
            ElectricalLevelSampler.ForCircuit(circuit));
        TransientResult result = circuit.ToTransientNetlist().Run(new TransientOptions
        {
            StartTime = 0,
            StopTime = 1.2e-7,
            Steps = 1280,
            MaximumStepCount = 100_000,
        });

        ElectricalDrivePlan plan = ElectricalDriveSimulation.Plan(circuit, result, sampler);
        var simulation = ElectricalDriveSimulation.Run(project, plan);

        Assert.True(
            simulation.Passed,
            string.Join("; ", simulation.Failures.Select(failure => failure.Message)));

        // Nothing is claimed: the definition is no longer the authority on Y, the network is.
        Assert.Empty(plan.Claims);
        Assert.Contains(circuit.DriveBindings, binding => binding.PinId == "y" && !binding.Checks);
    }
}
