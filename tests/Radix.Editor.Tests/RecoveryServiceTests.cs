using Radix.Core.Model;
using Radix.Editor.Services;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class RecoveryServiceTests
{
    [Fact]
    public void SnapshotRoundTripsProjectAndOrigin()
    {
        string directory = TemporaryDirectory();
        try
        {
            CircuitProject project = ExampleProjects.CreateTernaryCounterDemo();
            RecoveryService.Save(project, "/tmp/counter.radix", directory);

            RecoverySnapshot? snapshot = RecoveryService.TryLoad(directory);

            Assert.NotNull(snapshot);
            Assert.Equal("/tmp/counter.radix", snapshot!.ProjectPath);
            Assert.Equal(project.Name, snapshot.Project.Name);
            Assert.Equal(project.RootDefinitionId, snapshot.Project.RootDefinitionId);
            Assert.Equal(project.Definitions.Count, snapshot.Project.Definitions.Count);
            Assert.True(snapshot.CapturedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void MissingOrCorruptSnapshotsAreTreatedAsAbsent()
    {
        string directory = TemporaryDirectory();
        try
        {
            Assert.Null(RecoveryService.TryLoad(directory));

            Directory.CreateDirectory(directory);
            File.WriteAllText(RecoveryService.SnapshotPath(directory), "{not a project");
            Assert.Null(RecoveryService.TryLoad(directory));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void ClearRemovesTheSnapshotAndMetadata()
    {
        string directory = TemporaryDirectory();
        try
        {
            RecoveryService.Save(ExampleProjects.CreateDFlipFlopDemo(), null, directory);
            Assert.NotNull(RecoveryService.TryLoad(directory));

            RecoveryService.Clear(directory);

            Assert.Null(RecoveryService.TryLoad(directory));
            Assert.False(File.Exists(RecoveryService.SnapshotPath(directory)));
            Assert.False(File.Exists(RecoveryService.MetadataPath(directory)));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SnapshotWithoutMetadataStillLoadsWithoutAnOrigin()
    {
        string directory = TemporaryDirectory();
        try
        {
            RecoveryService.Save(ExampleProjects.CreateFullAdderDemo(), "/tmp/adder.radix", directory);
            File.Delete(RecoveryService.MetadataPath(directory));

            RecoverySnapshot? snapshot = RecoveryService.TryLoad(directory);

            Assert.NotNull(snapshot);
            Assert.Null(snapshot!.ProjectPath);
            Assert.Equal("Full adder demo", snapshot.Project.Name);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static string TemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), "radix-recovery-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
