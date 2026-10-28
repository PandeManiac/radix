using System.Text.Json;

namespace Radix.Editor.Services;

/// <summary>
/// Per-user window and inspector state. This is deliberately separate from projects:
/// presentation preferences must not make a saved circuit look modified.
/// </summary>
public sealed class WorkspaceSettings
{
    public const double DefaultWidth = 1480;

    public const double DefaultHeight = 920;

    public double WindowWidth { get; set; } = DefaultWidth;

    public double WindowHeight { get; set; } = DefaultHeight;

    public bool Maximized { get; set; }

    public int InspectorTabIndex { get; set; }

    /// <summary>Most recently opened or saved project paths, newest first.</summary>
    public List<string> RecentProjects { get; set; } = [];
}

/// <summary>
/// Reads and writes workspace settings under the platform application-data directory.
/// Missing, unreadable, or malformed files fall back to defaults; saving is best-effort
/// so a read-only home directory never prevents the editor from starting.
/// </summary>
public static class WorkspacePreferences
{
    public const double MinWindowWidth = 1040;

    public const double MinWindowHeight = 660;

    public const double MaxWindowWidth = 10000;

    public const double MaxWindowHeight = 10000;

    public const int MaxInspectorTabIndex = 5;

    public const int MaxRecentProjects = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Per-user settings path, shared by all projects for this account.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Radix",
        "workspace.json");

    public static WorkspaceSettings Load(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            if (!File.Exists(file))
            {
                return new WorkspaceSettings();
            }

            WorkspaceSettings? settings = JsonSerializer.Deserialize<WorkspaceSettings>(
                File.ReadAllText(file),
                JsonOptions);
            return Normalize(settings ?? new WorkspaceSettings());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WorkspaceSettings();
        }
    }

    public static void Save(WorkspaceSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            string file = path ?? DefaultPath;
            string? directory = Path.GetDirectoryName(file);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(settings), JsonOptions));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preferences are convenience state; never block shutdown on them.
        }
    }

    private static WorkspaceSettings Normalize(WorkspaceSettings settings) => new()
    {
        WindowWidth = double.IsFinite(settings.WindowWidth)
            ? Math.Clamp(settings.WindowWidth, MinWindowWidth, MaxWindowWidth)
            : WorkspaceSettings.DefaultWidth,
        WindowHeight = double.IsFinite(settings.WindowHeight)
            ? Math.Clamp(settings.WindowHeight, MinWindowHeight, MaxWindowHeight)
            : WorkspaceSettings.DefaultHeight,
        Maximized = settings.Maximized,
        InspectorTabIndex = Math.Clamp(settings.InspectorTabIndex, 0, MaxInspectorTabIndex),
        RecentProjects = NormalizeRecentProjects(settings.RecentProjects),
    };

    private static List<string> NormalizeRecentProjects(IEnumerable<string>? paths)
    {
        var result = new List<string>();
        if (paths is null)
        {
            return result;
        }

        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string trimmed = path.Trim();
            if (result.Contains(trimmed, StringComparer.Ordinal))
            {
                continue;
            }

            result.Add(trimmed);
            if (result.Count == MaxRecentProjects)
            {
                break;
            }
        }

        return result;
    }
}
