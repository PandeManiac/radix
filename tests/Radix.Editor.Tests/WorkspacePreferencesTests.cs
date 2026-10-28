using Radix.Editor.Services;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class WorkspacePreferencesTests
{
    [Fact]
    public void WorkspaceSettingsRoundTripThroughJson()
    {
        string path = TemporaryPath();
        try
        {
            WorkspacePreferences.Save(new WorkspaceSettings
            {
                WindowWidth = 1280,
                WindowHeight = 800,
                Maximized = true,
                InspectorTabIndex = 2,
            }, path);

            WorkspaceSettings loaded = WorkspacePreferences.Load(path);

            Assert.Equal(1280, loaded.WindowWidth);
            Assert.Equal(800, loaded.WindowHeight);
            Assert.True(loaded.Maximized);
            Assert.Equal(2, loaded.InspectorTabIndex);
            Assert.Contains("\"windowWidth\": 1280", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void InvalidOrOutOfRangeWorkspaceSettingsFallBackSafely()
    {
        string path = TemporaryPath();
        try
        {
            File.WriteAllText(path, "{not json");
            WorkspaceSettings fallback = WorkspacePreferences.Load(path);
            Assert.Equal(WorkspaceSettings.DefaultWidth, fallback.WindowWidth);
            Assert.Equal(WorkspaceSettings.DefaultHeight, fallback.WindowHeight);
            Assert.False(fallback.Maximized);
            Assert.Equal(0, fallback.InspectorTabIndex);

            File.WriteAllText(path, """{"windowWidth":100,"windowHeight":20000,"inspectorTabIndex":99}""");
            WorkspaceSettings clamped = WorkspacePreferences.Load(path);
            Assert.Equal(WorkspacePreferences.MinWindowWidth, clamped.WindowWidth);
            Assert.Equal(WorkspacePreferences.MaxWindowHeight, clamped.WindowHeight);
            Assert.Equal(WorkspacePreferences.MaxInspectorTabIndex, clamped.InspectorTabIndex);

            File.Delete(path);
            WorkspaceSettings missing = WorkspacePreferences.Load(path);
            Assert.Equal(WorkspaceSettings.DefaultWidth, missing.WindowWidth);
            Assert.Equal(WorkspaceSettings.DefaultHeight, missing.WindowHeight);
        }
        finally
        {
            Delete(path);
        }
    }

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), "radix-workspace-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
    }
}
