using Radix.Core.Model;

namespace Radix.Core.Testing;

/// <summary>One script in a suite run, including load or compile errors.</summary>
public sealed record SimulationScriptSuiteEntry(
    string Path,
    string? ScriptName,
    SimulationScriptResult? Result,
    string? Error)
{
    public bool Passed => Error is null && Result is { Passed: true };
}

/// <summary>
/// Aggregate outcome of running several test scripts. A script that cannot be
/// loaded or compiled is reported as an error entry while the remaining scripts
/// still run, so one broken file does not hide the rest of a project's checks.
/// </summary>
public sealed class SimulationScriptSuiteResult
{
    public SimulationScriptSuiteResult(IReadOnlyList<SimulationScriptSuiteEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = entries;
    }

    public IReadOnlyList<SimulationScriptSuiteEntry> Entries { get; }

    public bool Passed => Entries.Count > 0 && Entries.All(entry => entry.Passed);

    public int PassedCount => Entries.Count(entry => entry.Passed);

    public int FailedCount => Entries.Count(entry => entry.Error is null && entry.Result is { Passed: false });

    public int ErrorCount => Entries.Count(entry => entry.Error is not null);

    public long StepsExecuted => Entries.Sum(entry => entry.Result?.StepsExecuted ?? 0);

    public long EventsProcessed => Entries.Sum(entry => entry.Result?.EventsProcessed ?? 0);

    public bool HasErrors => ErrorCount > 0;

    public bool HasFailures => FailedCount > 0;
}

/// <summary>
/// Discovers and runs groups of <see cref="SimulationScript"/> files. This is a
/// thin loop over the single-script runner, so a suite cannot diverge from the
/// behavior of an individual run; it only adds discovery and aggregate counts.
/// </summary>
public static class SimulationScriptSuiteRunner
{
    /// <summary>
    /// Returns every script below <paramref name="directory"/> in ordinal path
    /// order, including scripts in nested directories. The order is stable so a
    /// suite report is deterministic across platforms and file systems.
    /// </summary>
    public static IReadOnlyList<string> Discover(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Script directory '{directory}' does not exist.");
        }

        return Directory
            .EnumerateFiles(directory, "*" + SimulationScript.DefaultExtension, SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static SimulationScriptSuiteResult RunFiles(
        CircuitProject project,
        IEnumerable<string> paths,
        string? definitionOverride = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(paths);

        var entries = new List<SimulationScriptSuiteEntry>();
        foreach (string path in paths)
        {
            string displayPath = path;
            try
            {
                SimulationScript script = SimulationScript.Load(path);
                SimulationScriptResult result = SimulationScriptRunner.Run(project, script, definitionOverride);
                entries.Add(new SimulationScriptSuiteEntry(displayPath, script.Name, result, null));
            }
            catch (SimulationScriptException exception)
            {
                entries.Add(new SimulationScriptSuiteEntry(displayPath, null, null, exception.Message));
            }
            catch (CircuitValidationException exception)
            {
                entries.Add(new SimulationScriptSuiteEntry(displayPath, null, null, exception.Message));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                entries.Add(new SimulationScriptSuiteEntry(displayPath, null, null, exception.Message));
            }
        }

        return new SimulationScriptSuiteResult(entries);
    }
}
