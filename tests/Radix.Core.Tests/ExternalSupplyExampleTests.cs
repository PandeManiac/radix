using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The paired example with its output pin declared externally supplied: the solved node writes Y
/// instead of being checked against it. A supplied pin is masked while it is driven, so the
/// definition's own answer can only be compared in a second run where nothing supplies it - that is
/// what the audit does, and the tests hold both its agreement and its teeth.
/// </summary>
public sealed class ExternalSupplyExampleTests
{
    private static (CircuitProject Project, ElectricalCircuit Circuit, TransientResult Run, ElectricalLevelSampler Sampler)
        Load(string file)
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
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        ElectricalLevelSampler sampler = Assert.IsType<ElectricalLevelSampler>(
            ElectricalLevelSampler.ForCircuit(circuit));
        TransientResult run = circuit.ToTransientNetlist().Run(new TransientOptions
        {
            StartTime = 0,
            StopTime = 1.2e-7,
            Steps = 1280,
            MaximumStepCount = 100_000,
        });
        return (project, circuit, run, sampler);
    }

    [Fact]
    public void TheSolvedNodeSuppliesTheDeclaredOutput()
    {
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load("electrical-ternary-supply.radix");
        CircuitDefinition definition = Assert.IsType<CircuitDefinition>(
            project.FindDefinition("demo.supply"));
        Assert.True(Assert.IsType<PinDeclaration>(definition.FindPin("y")).ExternalSupply);
        Assert.False(Assert.IsType<PinDeclaration>(definition.FindPin("a")).ExternalSupply);

        ElectricalDrivePlan plan = ElectricalDriveSimulation.Plan(circuit, run, sampler);
        var simulation = ElectricalDriveSimulation.Run(project, plan);

        Assert.True(
            simulation.Passed,
            string.Join("; ", simulation.Failures.Select(failure => failure.Message)));

        // Nothing is claimed: the definition is no longer the authority on Y, the network is.
        Assert.Empty(plan.Claims);
        Assert.Contains(circuit.DriveBindings, binding => binding.PinId == "y" && !binding.Checks);
    }

    [Fact]
    public void TheAuditFindsThatTheDefinitionWouldHaveAgreed()
    {
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load("electrical-ternary-supply.radix");

        ElectricalDriveAudit audit = Assert.IsType<ElectricalDriveAudit>(
            ElectricalDriveSimulation.Audit(project, circuit, run, sampler));

        Assert.True(
            audit.Passed,
            string.Join("; ", audit.Result.Failures.Select(failure => failure.Message)));

        // The supplied pin is asked about across the whole window, exactly as a claim would be.
        Assert.Equal(45, audit.CheckedTicks);
        Assert.Equal(1, audit.AuditedPins);
    }

    [Fact]
    public void TheAuditCatchesADefinitionThatWouldHaveDisagreed()
    {
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load("electrical-ternary-supply.radix");

        // A definition that answers 0 for every input: the network says level 2 then level 1.
        ComponentInstance gate = Assert.Single(
            project.FindDefinition("demo.supply")!.Instances,
            instance => instance.Id == "gate");
        gate.Primitive = new LookupSpec(1, [0, 0, 0]);

        // The supply run itself still passes, because the network wrote Y and the definition was
        // never asked anything. Silence is not agreement, which is the whole reason for the audit.
        ElectricalDrivePlan plan = ElectricalDriveSimulation.Plan(circuit, run, sampler);
        Assert.True(ElectricalDriveSimulation.Run(project, plan).Passed);

        ElectricalDriveAudit audit = Assert.IsType<ElectricalDriveAudit>(
            ElectricalDriveSimulation.Audit(project, circuit, run, sampler));

        Assert.False(audit.Passed);
        Assert.Contains(
            audit.Result.Failures,
            failure => failure.Message.Contains("y", StringComparison.Ordinal));
    }

    [Fact]
    public void ACircuitThatOnlyClaimsItsOutputHasNoAudit()
    {
        // The drive example asks Y to agree instead of taking it over, so there is nothing to audit:
        // the audit is about pins the network wrote, not about every binding in the file.
        (CircuitProject project, ElectricalCircuit circuit, TransientResult run, ElectricalLevelSampler sampler) =
            Load("electrical-ternary-drive.radix");

        Assert.Null(ElectricalDriveSimulation.Audit(project, circuit, run, sampler));
    }
}
