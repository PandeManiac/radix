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
                WireLabelMode = WireLabelModes.Named,
            }, path);

            WorkspaceSettings loaded = WorkspacePreferences.Load(path);

            Assert.Equal(1280, loaded.WindowWidth);
            Assert.Equal(800, loaded.WindowHeight);
            Assert.True(loaded.Maximized);
            Assert.Equal(2, loaded.InspectorTabIndex);
            Assert.Equal(WireLabelModes.Named, loaded.WireLabelMode);
            Assert.Contains("\"windowWidth\": 1280", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Contains("\"wireLabelMode\": \"Named\"", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void RecentProjectsRoundTripAndNormalize()
    {
        string path = TemporaryPath();
        try
        {
            WorkspacePreferences.Save(new WorkspaceSettings
            {
                RecentProjects = [" /a.radix ", "/a.radix", "", "/b.radix"],
            }, path);

            WorkspaceSettings loaded = WorkspacePreferences.Load(path);
            Assert.Equal(["/a.radix", "/b.radix"], loaded.RecentProjects);

            var many = Enumerable.Range(0, WorkspacePreferences.MaxRecentProjects + 4)
                .Select(index => $"/p{index}.radix")
                .ToList();
            WorkspacePreferences.Save(new WorkspaceSettings { RecentProjects = many }, path);
            loaded = WorkspacePreferences.Load(path);
            Assert.Equal(WorkspacePreferences.MaxRecentProjects, loaded.RecentProjects.Count);
            Assert.Equal(many[0], loaded.RecentProjects[0]);

            File.WriteAllText(path, """{"recentProjects":null}""");
            Assert.Empty(WorkspacePreferences.Load(path).RecentProjects);
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
            Assert.Equal(WireLabelModes.All, clamped.WireLabelMode);

            File.WriteAllText(path, """{"wireLabelMode":"Loud"}""");
            Assert.Equal(WireLabelModes.All, WorkspacePreferences.Load(path).WireLabelMode);

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

    [Theory]
    [InlineData(WireLabelModes.All, null, true)]
    [InlineData(WireLabelModes.All, "clk", true)]
    [InlineData(WireLabelModes.Named, null, false)]
    [InlineData(WireLabelModes.Named, "clk", true)]
    [InlineData(WireLabelModes.None, "clk", false)]
    [InlineData("invalid", "clk", true)]
    public void WireLabelModesDecideWhichLabelsAreDrawn(string mode, string? label, bool expected)
    {
        Assert.Equal(expected, WireLabelModes.Shows(mode, label));
        Assert.Contains(WireLabelModes.Normalize(mode), WireLabelModes.Options);
    }

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), "radix-workspace-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
    }
}
