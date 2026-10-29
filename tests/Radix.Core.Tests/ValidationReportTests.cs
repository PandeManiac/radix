using System.Text.Json;
using Radix.Core.Compilation;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

public sealed class ValidationReportTests
{
    [Fact]
    public void SuccessfulRootReportIncludesCompiledCounts()
    {
        var project = TestCircuits.ValidProject();
        bool compiled = CircuitCompiler.TryCompile(
            project,
            project.RootDefinitionId,
            out CompiledCircuit? circuit,
            out DiagnosticBag diagnostics);
        Assert.True(compiled);
        Assert.NotNull(circuit);

        string json = ValidationReport.ToJson(
            project,
            project.RootDefinitionId,
            validateAll: false,
            succeeded: compiled,
            diagnostics,
            circuit);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(ValidationReport.FormatName, root.GetProperty("format").GetString());
        Assert.Equal(ValidationReport.FormatVersion, root.GetProperty("version").GetInt32());
        Assert.Equal("valid", root.GetProperty("project").GetString());
        Assert.Equal("main", root.GetProperty("rootDefinitionId").GetString());
        Assert.Equal("root", root.GetProperty("scope").GetString());
        Assert.True(root.GetProperty("passed").GetBoolean());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());
        Assert.Equal(0, root.GetProperty("warningCount").GetInt32());
        Assert.Equal(0, root.GetProperty("infoCount").GetInt32());
        Assert.Equal(project.Definitions.Count, root.GetProperty("definitions").GetInt32());
        Assert.Equal(project.Domains.Count, root.GetProperty("domains").GetInt32());
        Assert.Empty(root.GetProperty("diagnostics").EnumerateArray());

        JsonElement compiledDto = root.GetProperty("compiled");
        Assert.Equal(circuit!.Components.Count, compiledDto.GetProperty("components").GetInt32());
        Assert.Equal(circuit.Nets.Count, compiledDto.GetProperty("nets").GetInt32());
        Assert.Equal(circuit.Pins.Count, compiledDto.GetProperty("pins").GetInt32());
    }

    [Fact]
    public void FailedReportCarriesEveryDiagnosticLocation()
    {
        var project = TestCircuits.ConstantRoot(2);
        bool compiled = CircuitCompiler.TryCompile(
            project,
            project.RootDefinitionId,
            out _,
            out DiagnosticBag diagnostics);
        Assert.False(compiled);

        string json = ValidationReport.ToJson(
            project,
            project.RootDefinitionId,
            validateAll: false,
            succeeded: compiled,
            diagnostics);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.True(root.GetProperty("errorCount").GetInt32() > 0);
        Assert.False(root.TryGetProperty("compiled", out _));

        JsonElement diagnostic = Assert.Single(
            root.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "RAD113");
        Assert.Equal("Error", diagnostic.GetProperty("severity").GetString());
        Assert.Equal("root", diagnostic.GetProperty("definitionId").GetString());
        Assert.Equal("c", diagnostic.GetProperty("instanceId").GetString());
        Assert.Equal("y_net", diagnostic.GetProperty("netId").GetString());
        Assert.Equal("out", diagnostic.GetProperty("pinId").GetString());
        Assert.False(diagnostic.TryGetProperty("path", out _));
        string location = diagnostic.GetProperty("location").GetString()!;
        Assert.Contains("instance 'c'", location, StringComparison.Ordinal);
        Assert.DoesNotContain("path ''", location, StringComparison.Ordinal);
        Assert.Contains("outside domain", diagnostic.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AllDefinitionsScopeOmitsTheSingleFlatCircuit()
    {
        var project = TestCircuits.ValidProject();
        bool valid = CircuitCompiler.TryValidateAllDefinitions(project, out DiagnosticBag diagnostics);
        Assert.True(valid);

        string json = ValidationReport.ToJson(
            project,
            project.RootDefinitionId,
            validateAll: true,
            succeeded: valid,
            diagnostics);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal("all", root.GetProperty("scope").GetString());
        Assert.True(root.GetProperty("passed").GetBoolean());
        Assert.False(root.TryGetProperty("compiled", out _));
        Assert.Empty(root.GetProperty("diagnostics").EnumerateArray());
    }

    [Fact]
    public void WarningsAndInfoDoNotFailAReport()
    {
        var project = TestCircuits.ValidProject();
        Diagnostic[] diagnostics =
        [
            new("RAD200", DiagnosticSeverity.Warning, "unused part", DefinitionId: "main"),
            new("RAD300", DiagnosticSeverity.Info, "compiled from cache", DefinitionId: "main"),
        ];

        string json = ValidationReport.ToJson(
            project,
            project.RootDefinitionId,
            validateAll: true,
            succeeded: true,
            diagnostics);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.True(root.GetProperty("passed").GetBoolean());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());
        Assert.Equal(1, root.GetProperty("warningCount").GetInt32());
        Assert.Equal(1, root.GetProperty("infoCount").GetInt32());
        Assert.Equal(2, root.GetProperty("diagnostics").GetArrayLength());
    }
}
