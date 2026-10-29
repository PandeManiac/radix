using System.Text.Json;
using Radix.Core.Model;
using Radix.Core.Serialization;

namespace Radix.Editor.Services;

/// <summary>A project captured from a dirty editor session, with its origin if known.</summary>
public sealed record RecoverySnapshot(string? ProjectPath, DateTimeOffset CapturedAt, CircuitProject Project);

/// <summary>
/// One-slot crash recovery next to the workspace preferences. The snapshot is
/// written atomically so a crash during autosave leaves either the previous
/// complete snapshot or the new one, never a truncated project.
/// </summary>
public static class RecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Radix");

    public static string SnapshotPath(string? directory = null) =>
        Path.Combine(directory ?? DefaultDirectory, "recovery.radix");

    public static string MetadataPath(string? directory = null) =>
        Path.Combine(directory ?? DefaultDirectory, "recovery.json");

    /// <summary>Best-effort snapshot write; recovery must never break editing.</summary>
    public static void Save(CircuitProject project, string? projectPath, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            string folder = directory ?? DefaultDirectory;
            Directory.CreateDirectory(folder);

            string snapshot = SnapshotPath(folder);
            string projectJson = ProjectSerializer.SaveToString(project, includeView: true);
            string temporary = snapshot + ".tmp";
            File.WriteAllText(temporary, projectJson);
            File.Move(temporary, snapshot, overwrite: true);

            var metadata = new RecoveryMetadata(projectPath, DateTimeOffset.UtcNow, project.Name);
            string metadataPath = MetadataPath(folder);
            string metadataTemporary = metadataPath + ".tmp";
            File.WriteAllText(metadataTemporary, JsonSerializer.Serialize(metadata, JsonOptions));
            File.Move(metadataTemporary, metadataPath, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ProjectFormatException or InvalidOperationException)
        {
            // Recovery is a convenience; never surface a failed snapshot as an edit error.
        }
    }

    /// <summary>
    /// Loads the snapshot without full validation so an intermediate invalid project
    /// (the editor allows those) is still recoverable. Malformed or unreadable
    /// snapshots are treated as absent.
    /// </summary>
    public static RecoverySnapshot? TryLoad(string? directory = null)
    {
        try
        {
            string folder = directory ?? DefaultDirectory;
            string snapshot = SnapshotPath(folder);
            if (!File.Exists(snapshot))
            {
                return null;
            }

            CircuitProject project = ProjectSerializer.Load(snapshot, validate: false);
            RecoveryMetadata? metadata = TryReadMetadata(MetadataPath(folder));
            return new RecoverySnapshot(
                metadata?.ProjectPath,
                metadata?.CapturedAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(snapshot), TimeSpan.Zero),
                project);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ProjectFormatException or JsonException)
        {
            return null;
        }
    }

    public static void Clear(string? directory = null)
    {
        try
        {
            string folder = directory ?? DefaultDirectory;
            File.Delete(SnapshotPath(folder));
            File.Delete(SnapshotPath(folder) + ".tmp");
            File.Delete(MetadataPath(folder));
            File.Delete(MetadataPath(folder) + ".tmp");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static RecoveryMetadata? TryReadMetadata(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<RecoveryMetadata>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record RecoveryMetadata(string? ProjectPath, DateTimeOffset CapturedAt, string ProjectName);
}
