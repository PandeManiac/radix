using System.Text.Json;
using System.Text.Json.Serialization;
using Radix.Core.Model;

namespace Radix.Core.Testing;

/// <summary>
/// Stable JSON projection of scripted test results. The human reports remain the
/// default; this exists for CI and other tools that need the same outcomes without
/// parsing console prose. It contains no project source and never executes anything.
/// </summary>
public static class SimulationScriptReport
{
    public const string FormatName = "radix-test-report";
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(
        CircuitProject project,
        SimulationScriptResult result,
        string? scriptPath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(result);

        var script = new ScriptReportDto(
            scriptPath,
            result.ScriptName,
            result.DefinitionId,
            result.Passed,
            result.StepsExecuted,
            result.FinalTime,
            result.EventsProcessed,
            result.Halted,
            result.HaltReason,
            null,
            result.Failures.Select(ToDto).ToArray(),
            result.Diagnostics.Select(ToDto).ToArray());
        var report = new ReportDto(
            FormatName,
            FormatVersion,
            project.Name,
            result.Passed,
            result.Passed ? 1 : 0,
            result.Passed ? 0 : 1,
            0,
            result.StepsExecuted,
            result.EventsProcessed,
            false,
            [script]);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string ToJson(
        CircuitProject project,
        SimulationScriptSuiteResult suite,
        bool emptyDirectory = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(suite);

        ScriptReportDto[] scripts = suite.Entries
            .Select(entry => new ScriptReportDto(
                entry.Path,
                entry.ScriptName ?? entry.Result?.ScriptName,
                entry.Result?.DefinitionId,
                entry.Passed,
                entry.Result?.StepsExecuted ?? 0,
                entry.Result?.FinalTime ?? 0,
                entry.Result?.EventsProcessed ?? 0,
                entry.Result?.Halted ?? false,
                entry.Result?.HaltReason,
                entry.Error,
                entry.Result?.Failures.Select(ToDto).ToArray() ?? [],
                entry.Result?.Diagnostics.Select(ToDto).ToArray() ?? []))
            .ToArray();
        var report = new ReportDto(
            FormatName,
            FormatVersion,
            project.Name,
            suite.Passed && !emptyDirectory,
            suite.PassedCount,
            suite.FailedCount,
            suite.ErrorCount,
            suite.StepsExecuted,
            suite.EventsProcessed,
            emptyDirectory,
            scripts);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    private static FailureDto ToDto(ScriptFailure failure) =>
        new(failure.Step, failure.At, failure.Message);

    private static DiagnosticDto ToDto(Diagnostic diagnostic) =>
        new(
            diagnostic.Severity.ToString(),
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Location.Length == 0 ? null : diagnostic.Location);

    private sealed record ReportDto(
        string Format,
        int Version,
        string Project,
        bool Passed,
        int PassedCount,
        int FailedCount,
        int ErrorCount,
        long StepsExecuted,
        long EventsProcessed,
        bool EmptyDirectory,
        IReadOnlyList<ScriptReportDto> Scripts);

    private sealed record ScriptReportDto(
        string? Path,
        string? Name,
        string? DefinitionId,
        bool Passed,
        int StepsExecuted,
        long FinalTime,
        long EventsProcessed,
        bool Halted,
        string? HaltReason,
        string? Error,
        IReadOnlyList<FailureDto> Failures,
        IReadOnlyList<DiagnosticDto> Diagnostics);

    private sealed record FailureDto(int Step, long At, string Message);

    private sealed record DiagnosticDto(
        string Severity,
        string Code,
        string Message,
        string? Location);
}
