using System.Text.Json;
using Radix.Core.Analysis;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Xunit;

namespace Radix.Core.Tests;

public sealed class ElectricalRuleCheckingTests
{
    private static DiagnosticBag Validate(CircuitProject project) =>
        CircuitValidator.Validate(project);

    private static Diagnostic Single(DiagnosticBag report, string code) =>
        Assert.Single(report, diagnostic => diagnostic.Code == code);

    [Fact]
    public void FloatingInputNetReportsActionableStructuralWarning()
    {
        DiagnosticBag report = Validate(FloatingInputProject());

        Diagnostic diagnostic = Single(report, ElectricalRuleChecker.FloatingInputNetCode);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("root", diagnostic.DefinitionId);
        Assert.Equal("floating", diagnostic.NetId);
        Assert.True(diagnostic.Nature is DiagnosticNature.Structural);
        Assert.True(diagnostic.SimulationRemainsValid == true);
        Assert.Contains("buf.in0", diagnostic.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Assumption));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Suggestion));
    }

    [Fact]
    public void RootInputNetIsNotReportedAsFloating()
    {
        DiagnosticBag report = Validate(TestCircuits.ValidProject());

        Assert.DoesNotContain(
            report,
            diagnostic => diagnostic.Code == ElectricalRuleChecker.FloatingInputNetCode);
    }

    [Fact]
    public void ExplicitInitialValueSuppressesFloatingNetWarning()
    {
        DiagnosticBag report = Validate(FloatingInputProject(initialValue: 0));

        Assert.DoesNotContain(
            report,
            diagnostic => diagnostic.Code == ElectricalRuleChecker.FloatingInputNetCode);
    }

    [Fact]
    public void InputPortNetDrivenInternallyReportsStructuralConflict()
    {
        DiagnosticBag report = Validate(InputPortConflictProject());

        Diagnostic diagnostic = Single(report, ElectricalRuleChecker.InputPortDrivenCode);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("a_net", diagnostic.NetId);
        Assert.True(diagnostic.Nature is DiagnosticNature.Structural);
        Assert.True(diagnostic.SimulationRemainsValid == true);
        Assert.Contains("src.out", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("not a calculated short-circuit current", diagnostic.Assumption, StringComparison.Ordinal);
        Assert.DoesNotContain("overcurrent was calculated", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleDriverConflictKeepsStableCodeAndAddsErcExplanation()
    {
        DiagnosticBag report = Validate(SingleDriverConflictProject());

        Diagnostic diagnostic = Single(report, "RAD081");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("y_net", diagnostic.NetId);
        Assert.True(diagnostic.Nature is DiagnosticNature.Structural);
        Assert.True(diagnostic.SimulationRemainsValid == true);
        Assert.Contains("not an overcurrent", diagnostic.Assumption, StringComparison.Ordinal);
        Assert.Contains("multi-driver policy", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void PriorityDomainAllowsMultipleDriversWithoutErcWarning()
    {
        DiagnosticBag report = Validate(PriorityDomainProject());

        Assert.DoesNotContain(report, diagnostic => diagnostic.Code == "RAD081");
        Assert.DoesNotContain(
            report,
            diagnostic => diagnostic.Code.StartsWith("RAD-ERC-", StringComparison.Ordinal));
        Assert.DoesNotContain(
            report,
            diagnostic => diagnostic.Code == ElectricalRuleChecker.InputPortDrivenCode);
    }

    [Fact]
    public void HierarchicalFloatingNetIsReportedAtItsOwningDefinition()
    {
        DiagnosticBag report = Validate(HierarchicalFloatingProject());

        Diagnostic diagnostic = Single(report, ElectricalRuleChecker.FloatingInputNetCode);
        Assert.Equal("child", diagnostic.DefinitionId);
        Assert.Equal("floating", diagnostic.NetId);
    }

    [Fact]
    public void SerializedProjectPreservesFloatingNetDiagnostic()
    {
        CircuitProject project = FloatingInputProject();
        string json = ProjectSerializer.SaveToString(project);
        CircuitProject reloaded = ProjectSerializer.LoadFromString(json, validate: false);

        DiagnosticBag report = Validate(reloaded);

        Diagnostic diagnostic = Single(report, ElectricalRuleChecker.FloatingInputNetCode);
        Assert.Equal("floating", diagnostic.NetId);
        Assert.True(diagnostic.Nature is DiagnosticNature.Structural);
    }

    [Fact]
    public void LargeFloatingNetSetProducesOneDiagnosticPerNet()
    {
        const int count = 256;
        var builder = new ProjectBuilder("large ERC");
        var root = builder.Define("root", "Root");
        for (int index = 0; index < count; index++)
        {
            Net output = root.AddPin($"y{index}", $"Y{index}", PinDirection.Output, StandardDomains.BinaryId);
            root.AddPrimitive($"b{index}", new LookupSpec(1, [0, 1]));
            Net floating = root.AddNet($"f{index}", StandardDomains.BinaryId);
            root.Connect(floating, root.Pin($"b{index}", "in0"));
            root.Connect(output, root.Pin($"b{index}", "out"));
        }

        DiagnosticBag report = Validate(builder.Build("root"));
        Diagnostic[] diagnostics = report
            .Where(diagnostic => diagnostic.Code == ElectricalRuleChecker.FloatingInputNetCode)
            .ToArray();

        Assert.Equal(count, diagnostics.Length);
        Assert.Equal(count, diagnostics.Select(diagnostic => diagnostic.NetId).Distinct().Count());
    }

    [Fact]
    public void ValidationReportExposesErcMetadata()
    {
        CircuitProject project = FloatingInputProject();
        DiagnosticBag report = Validate(project);
        Diagnostic diagnostic = Single(report, ElectricalRuleChecker.FloatingInputNetCode);

        string json = ValidationReport.ToJson(
            project,
            project.RootDefinitionId,
            validateAll: false,
            succeeded: true,
            report);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement item = Assert.Single(
            document.RootElement.GetProperty("diagnostics").EnumerateArray(),
            entry => entry.GetProperty("code").GetString() == ElectricalRuleChecker.FloatingInputNetCode);

        Assert.Equal("Structural", item.GetProperty("nature").GetString());
        Assert.True(item.GetProperty("simulationRemainsValid").GetBoolean());
        Assert.Contains("floating", item.GetProperty("netId").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("assumption").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("suggestion").GetString()));
        Assert.True(diagnostic.Nature is DiagnosticNature.Structural);
    }

    private static CircuitProject FloatingInputProject(int? initialValue = null)
    {
        var builder = new ProjectBuilder("floating input");
        var root = builder.Define("root", "Root");
        Net output = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("buf", new LookupSpec(1, [0, 1]));
        Net floating = root.AddNet("floating", StandardDomains.BinaryId);
        if (initialValue is int value)
        {
            root.SetInitial(floating, value);
        }

        root.Connect(floating, root.Pin("buf", "in0"));
        root.Connect(output, root.Pin("buf", "out"));
        return builder.Build("root");
    }

    private static CircuitProject InputPortConflictProject()
    {
        var builder = new ProjectBuilder("input conflict");
        var root = builder.Define("root", "Root");
        Net input = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net output = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("src", new ConstantSpec(1));
        root.AddPrimitive("buf", new LookupSpec(1, [0, 1]));
        root.Connect(input, root.Pin("src", "out"));
        root.Connect(input, root.Pin("buf", "in0"));
        root.Connect(output, root.Pin("buf", "out"));
        return builder.Build("root");
    }

    private static CircuitProject SingleDriverConflictProject()
    {
        var builder = new ProjectBuilder("single-driver conflict");
        var root = builder.Define("root", "Root");
        Net input = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net output = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("b1", new LookupSpec(1, [0, 1]));
        root.AddPrimitive("b2", new LookupSpec(1, [0, 1]));
        root.Connect(input, root.Pin("b1", "in0"));
        root.Connect(input, root.Pin("b2", "in0"));
        root.Connect(output, root.Pin("b1", "out"));
        root.Connect(output, root.Pin("b2", "out"));
        return builder.Build("root");
    }

    private static CircuitProject PriorityDomainProject()
    {
        var builder = new ProjectBuilder("priority drivers");
        var root = builder.Define("root", "Root");
        Net input = root.AddPin("a", "A", PinDirection.Input, StandardDomains.FourStateId);
        Net output = root.AddPin("bus", "BUS", PinDirection.Output, StandardDomains.FourStateId);
        root.AddPrimitive("d1", new LookupSpec(1, [0, 1, 2, 3]));
        root.AddPrimitive("d2", new LookupSpec(1, [0, 1, 2, 3]));
        root.Connect(input, root.Pin("d1", "in0"));
        root.Connect(input, root.Pin("d2", "in0"));
        root.Connect(output, root.Pin("d1", "out"));
        root.Connect(output, root.Pin("d2", "out"));
        return builder.Build("root");
    }

    private static CircuitProject HierarchicalFloatingProject()
    {
        var builder = new ProjectBuilder("hierarchical ERC");
        var child = builder.Define("child", "Child");
        Net childOutput = child.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        child.AddPrimitive("buf", new LookupSpec(1, [0, 1]));
        Net floating = child.AddNet("floating", StandardDomains.BinaryId);
        child.Connect(floating, child.Pin("buf", "in0"));
        child.Connect(childOutput, child.Pin("buf", "out"));

        var root = builder.Define("root", "Root");
        root.AddInstance("c", "child");
        Net output = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.Connect(output, root.Pin("c", "y"));
        return builder.Build("root");
    }
}
