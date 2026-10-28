using System.Text.Json;
using Radix.Core;
using Radix.Core.Testing;
using Xunit;

namespace Radix.Core.Tests;

public sealed class SimulationScriptReportTests
{
    [Fact]
    public void SingleResultReportContainsOutcomeFailuresAndDiagnostics()
    {
        var result = new SimulationScriptResult(
            "counter",
            "root",
            [new ScriptFailure(4, 30, "Q expected 1")],
            [
                new Diagnostic(
                    "RAD101",
                    DiagnosticSeverity.Error,
                    "conflicting drivers",
                    DefinitionId: "root",
                    NetId: "q_net"),
            ],
            9,
            40,
            77,
            halted: false,
            haltReason: null);

        string json = SimulationScriptReport.ToJson(
            TestCircuits.ValidProject(),
            result,
            "tests/counter.radixtest");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(SimulationScriptReport.FormatName, root.GetProperty("format").GetString());
        Assert.Equal(SimulationScriptReport.FormatVersion, root.GetProperty("version").GetInt32());
        Assert.Equal("valid", root.GetProperty("project").GetString());
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal(0, root.GetProperty("passedCount").GetInt32());
        Assert.Equal(1, root.GetProperty("failedCount").GetInt32());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());
        Assert.Equal(9, root.GetProperty("stepsExecuted").GetInt32());
        Assert.Equal(77, root.GetProperty("eventsProcessed").GetInt64());
        Assert.False(root.GetProperty("emptyDirectory").GetBoolean());

        JsonElement script = Assert.Single(root.GetProperty("scripts").EnumerateArray());
        Assert.Equal("tests/counter.radixtest", script.GetProperty("path").GetString());
        Assert.Equal("counter", script.GetProperty("name").GetString());
        Assert.Equal("root", script.GetProperty("definitionId").GetString());
        Assert.False(script.GetProperty("passed").GetBoolean());

        JsonElement failure = Assert.Single(script.GetProperty("failures").EnumerateArray());
        Assert.Equal(4, failure.GetProperty("step").GetInt32());
        Assert.Equal(30, failure.GetProperty("at").GetInt64());
        Assert.Equal("Q expected 1", failure.GetProperty("message").GetString());

        JsonElement diagnostic = Assert.Single(script.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("Error", diagnostic.GetProperty("severity").GetString());
        Assert.Equal("RAD101", diagnostic.GetProperty("code").GetString());
        Assert.Contains(
            "definition 'root'",
            diagnostic.GetProperty("location").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SuiteReportIncludesPassedFailedAndErroredScripts()
    {
        var passing = new SimulationScriptResult(
            "passing",
            "root",
            [],
            [],
            3,
            20,
            15,
            halted: false,
            haltReason: null);
        var failing = new SimulationScriptResult(
            "failing",
            "root",
            [new ScriptFailure(2, 10, "bad value")],
            [],
            5,
            30,
            40,
            halted: false,
            haltReason: null);
        var suite = new SimulationScriptSuiteResult(
        [
            new SimulationScriptSuiteEntry("a.radixtest", "passing", passing, null),
            new SimulationScriptSuiteEntry("b.radixtest", "failing", failing, null),
            new SimulationScriptSuiteEntry("c.radixtest", null, null, "Invalid script JSON"),
        ]);

        string json = SimulationScriptReport.ToJson(
            TestCircuits.ValidProject(),
            suite,
            emptyDirectory: true);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal(1, root.GetProperty("passedCount").GetInt32());
        Assert.Equal(1, root.GetProperty("failedCount").GetInt32());
        Assert.Equal(1, root.GetProperty("errorCount").GetInt32());
        Assert.Equal(8, root.GetProperty("stepsExecuted").GetInt64());
        Assert.Equal(55, root.GetProperty("eventsProcessed").GetInt64());
        Assert.True(root.GetProperty("emptyDirectory").GetBoolean());

        JsonElement[] scripts = root.GetProperty("scripts").EnumerateArray().ToArray();
        Assert.Equal(3, scripts.Length);
        Assert.True(scripts[0].GetProperty("passed").GetBoolean());
        Assert.False(scripts[1].GetProperty("passed").GetBoolean());
        Assert.False(scripts[2].GetProperty("passed").GetBoolean());
        Assert.Equal("Invalid script JSON", scripts[2].GetProperty("error").GetString());
        Assert.Empty(scripts[2].GetProperty("failures").EnumerateArray());
    }
}
