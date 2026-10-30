using System.Text.Json;
using System.Text.Json.Serialization;
using Radix.Core.Compilation;
using Radix.Core.Model;

namespace Radix.Core.Serialization;

/// <summary>
/// Stable JSON projection of a validation/compile attempt. The default CLI output
/// remains human-readable prose; this report exists for CI and other tools that need
/// the same diagnostics without parsing it. It never executes or mutates a project.
/// </summary>
public static class ValidationReport
{
    public const string FormatName = "radix-validation-report";
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Serializes one validation result. <paramref name="succeeded"/> is the
    /// compiler's own outcome; <paramref name="diagnostics"/> supplies the ordered
    /// messages. A report passes only when the compiler succeeded and no error was
    /// recorded. <paramref name="circuit"/> is omitted for all-definition validation,
    /// which compiles each definition separately and has no single flat circuit.
    /// </summary>
    public static string ToJson(
        CircuitProject project,
        string definitionId,
        bool validateAll,
        bool succeeded,
        IReadOnlyList<Diagnostic> diagnostics,
        CompiledCircuit? circuit = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(definitionId);
        ArgumentNullException.ThrowIfNull(diagnostics);

        int errorCount = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        int warningCount = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        int infoCount = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Info);
        var report = new ReportDto(
            FormatName,
            FormatVersion,
            project.Name,
            definitionId,
            validateAll ? "all" : "root",
            succeeded && errorCount == 0,
            errorCount,
            warningCount,
            infoCount,
            project.Definitions.Count,
            project.Domains.Count,
            circuit is null
                ? null
                : new CompiledDto(circuit.Components.Count, circuit.Nets.Count, circuit.Pins.Count),
            diagnostics.Select(ToDto).ToArray());
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    private static DiagnosticDto ToDto(Diagnostic diagnostic) =>
        new(
            diagnostic.Severity.ToString(),
            diagnostic.Code,
            diagnostic.Message,
            EmptyToNull(diagnostic.DefinitionId),
            EmptyToNull(diagnostic.InstanceId),
            EmptyToNull(diagnostic.NetId),
            EmptyToNull(diagnostic.PinId),
            EmptyToNull(diagnostic.Path),
            EmptyToNull(diagnostic.Location),
            EmptyToNull(diagnostic.Assumption),
            EmptyToNull(diagnostic.Suggestion),
            diagnostic.SimulationRemainsValid,
            diagnostic.Nature?.ToString());

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private sealed record ReportDto(
        string Format,
        int Version,
        string Project,
        string RootDefinitionId,
        string Scope,
        bool Passed,
        int ErrorCount,
        int WarningCount,
        int InfoCount,
        int Definitions,
        int Domains,
        CompiledDto? Compiled,
        IReadOnlyList<DiagnosticDto> Diagnostics);

    private sealed record CompiledDto(int Components, int Nets, int Pins);

    private sealed record DiagnosticDto(
        string Severity,
        string Code,
        string Message,
        string? DefinitionId,
        string? InstanceId,
        string? NetId,
        string? PinId,
        string? Path,
        string? Location,
        string? Assumption,
        string? Suggestion,
        bool? SimulationRemainsValid,
        string? Nature);
}
