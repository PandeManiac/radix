using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Testing;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A binding is the circuit's own claim about what it means to the rest of the project, so it has to
/// survive a save, refuse a node it does not have, and schedule drives from the claim rather than
/// from arguments a caller passes separately.
/// </summary>
public sealed class ElectricalDriveBindingPersistenceTests
{
    private static string ProjectPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix");
    }

    private static TransientResult Run(ElectricalCircuit circuit) => circuit.ToTransientNetlist()
        .Run(new TransientOptions { StartTime = 0, StopTime = 120e-9, Steps = 1280 });

    [Fact]
    public void ABindingSurvivesASaveAndSchedulesWhatItClaims()
    {
        CircuitProject project = ProjectSerializer.Load(ProjectPath(), validate: false);
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        circuit.DriveBindings.Add(new ElectricalDriveBinding
        {
            NodeId = "out",
            PinId = "a",
            TicksPerSecond = 1e9,
        });

        string path = Path.Combine(Path.GetTempPath(), $"radix-binding-{Guid.NewGuid():N}.radix");
        try
        {
            File.WriteAllText(path, ProjectSerializer.SaveToString(project, includeView: false));
            Assert.Contains("\"driveBindings\": [", File.ReadAllText(path), StringComparison.Ordinal);

            CircuitProject reloaded = ProjectSerializer.Load(path);
            Assert.Equal(CircuitProject.CurrentFormatVersion, reloaded.FormatVersion);

            ElectricalCircuit reloadedCircuit = Assert.Single(reloaded.ElectricalCircuits);
            ElectricalDriveBinding binding = Assert.Single(reloadedCircuit.DriveBindings);
            Assert.Equal("out", binding.NodeId);
            Assert.Equal("a", binding.PinId);
            Assert.Equal(1e9, binding.TicksPerSecond);

            // The binding schedules from its own claim: no node or rate is supplied here.
            IReadOnlyList<(long At, ScriptDrive Drive)> scheduled = binding.Schedule(
                Run(reloadedCircuit), ElectricalLevelSampler.TernaryFromRail(5));
            Assert.Equal([2, 1], scheduled.Select(entry => entry.Drive.Value.Index!.Value));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileWithoutBindingsLoadsWithNone()
    {
        CircuitProject project = ProjectSerializer.Load(ProjectPath(), validate: false);

        Assert.Empty(Assert.Single(project.ElectricalCircuits).DriveBindings);
    }

    [Fact]
    public void ABindingToANodeTheCircuitDoesNotHaveIsRefusedOnLoad()
    {
        CircuitProject project = ProjectSerializer.Load(ProjectPath(), validate: false);
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        circuit.DriveBindings.Add(new ElectricalDriveBinding
        {
            NodeId = "out",
            PinId = "a",
            TicksPerSecond = 1e9,
        });

        string path = Path.Combine(Path.GetTempPath(), $"radix-binding-{Guid.NewGuid():N}.radix");
        try
        {
            string saved = ProjectSerializer.SaveToString(project, includeView: false);
            File.WriteAllText(path, saved.Replace("\"node\": \"out\"", "\"node\": \"nope\"", StringComparison.Ordinal));

            ProjectFormatException exception = Assert.Throws<ProjectFormatException>(
                () => ProjectSerializer.Load(path));
            Assert.Contains("unknown node 'nope'", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
