using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A circuit's own boundaries have to outrank the rail heuristic, survive a save, and be refused
/// when they cannot describe a signal. Each of those is a way the feature could look right and
/// behave wrongly.
/// </summary>
public sealed class LevelThresholdTests
{
    private static string ProjectPath(string file)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "examples", file);
    }

    private static CircuitProject Load() =>
        ProjectSerializer.Load(ProjectPath("transient-ternary-inverter.radix"), validate: false);

    [Fact]
    public void AuthoredBoundariesOutrankTheRail()
    {
        CircuitProject project = Load();
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);

        // Two thirds of the rail and the rail itself: 2.5 V then reads as 0, not as the middle.
        circuit.LevelThresholds.Add(3.0);
        circuit.LevelThresholds.Add(5.0);

        ElectricalLevelSampler? sampler = ElectricalLevelSampler.ForCircuit(circuit, 5);
        Assert.NotNull(sampler);
        Assert.Equal(0, sampler!.LevelFor(2.5));
        Assert.Equal(1, sampler.LevelFor(4.0));
        Assert.Equal(2, sampler.LevelFor(5.0));
        // Two boundaries describe three levels, the top one only reachable on the rail itself.
        Assert.Equal(3, sampler.LevelCount);
    }

    [Fact]
    public void WithoutAuthoredBoundariesTheRailSuppliesThem()
    {
        ElectricalCircuit circuit = Assert.Single(Load().ElectricalCircuits);

        ElectricalLevelSampler? sampler = ElectricalLevelSampler.ForCircuit(circuit, 5);
        Assert.NotNull(sampler);
        Assert.Equal(1, sampler!.LevelFor(2.5));

        // No rail and no boundaries is no reading, rather than a boundary invented from nothing.
        Assert.Null(ElectricalLevelSampler.ForCircuit(circuit, 0));
    }

    [Fact]
    public void AuthoredBoundariesSurviveASaveAndLoad()
    {
        CircuitProject project = Load();
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        circuit.LevelThresholds.Add(2.6);
        circuit.LevelThresholds.Add(3.0);

        string path = Path.Combine(Path.GetTempPath(), $"radix-levels-{Guid.NewGuid():N}.radix");
        try
        {
            File.WriteAllText(path, ProjectSerializer.SaveToString(project, includeView: false));
            string saved = File.ReadAllText(path);
            Assert.Contains("\"levelThresholds\": [", saved, StringComparison.Ordinal);

            CircuitProject reloaded = ProjectSerializer.Load(path);
            Assert.Equal(16, reloaded.FormatVersion);
            Assert.Equal([2.6, 3.0], Assert.Single(reloaded.ElectricalCircuits).LevelThresholds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileWithoutThemLoadsAsNoBoundaries()
    {
        ElectricalCircuit circuit = Assert.Single(Load().ElectricalCircuits);

        // The shipped example predates the field; loading it must not invent boundaries.
        Assert.Empty(circuit.LevelThresholds);
    }
}
