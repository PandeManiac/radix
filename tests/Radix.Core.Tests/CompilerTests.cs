using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class CompilerTests
{
    private static DiagnosticBag CompileReport(CircuitProject project, string? root = null)
    {
        CircuitCompiler.TryCompile(project, root ?? project.RootDefinitionId, out _, out DiagnosticBag report);
        return report;
    }

    private static bool Has(DiagnosticBag bag, string code) => bag.Any(d => d.Code == code);

    [Fact]
    public void CompilesValidProject()
    {
        CompiledCircuit circuit = CircuitCompiler.Compile(TestCircuits.ValidProject());
        Assert.Single(circuit.Components);
        Assert.Equal(2, circuit.Pins.Count);
        Assert.Equal(2, circuit.Nets.Count);
        Assert.Equal(2, circuit.Ports.Count);
    }

    [Fact]
    public void LookupTableLengthMustMatchInputDomains()
    {
        CircuitProject project = TestCircuits.LookupRoot(2, [0, 1, 1]);
        DiagnosticBag report = CompileReport(project);
        Assert.True(Has(report, "RAD111"), string.Join('\n', report));
    }

    [Fact]
    public void LookupTableValuesMustFitOutputDomain()
    {
        CircuitProject project = TestCircuits.LookupRoot(2, [0, 1, 1, 2]);
        DiagnosticBag report = CompileReport(project);
        Assert.True(Has(report, "RAD112"), string.Join('\n', report));
    }

    [Fact]
    public void ConstantMustFitOutputDomain()
    {
        CircuitProject project = TestCircuits.ConstantRoot(2);
        DiagnosticBag report = CompileReport(project);
        Assert.True(Has(report, "RAD113"), string.Join('\n', report));
    }

    [Fact]
    public void DelayCannotCrossDomainsImplicitly()
    {
        var builder = new ProjectBuilder("delay domains");
        var root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.TernaryId);
        root.AddPrimitive("delay", new DelaySpec(1));
        root.Connect(a, root.Pin("delay", "in0"));
        root.Connect(y, root.Pin("delay", "out"));

        DiagnosticBag report = CompileReport(builder.Build("root"));
        Assert.True(Has(report, "RAD116"), string.Join('\n', report));
    }

    [Fact]
    public void DelayInitialValueMustFitDomain()
    {
        var builder = new ProjectBuilder("delay initial");
        var root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("delay", new DelaySpec(1, initialValue: 5));
        root.Connect(a, root.Pin("delay", "in0"));
        root.Connect(y, root.Pin("delay", "out"));

        DiagnosticBag report = CompileReport(builder.Build("root"));
        Assert.True(Has(report, "RAD114"), string.Join('\n', report));
    }

    [Fact]
    public void WaveformValuesMustFitDomain()
    {
        CircuitProject project = TestCircuits.ClockRoot([1, 1], [0, 2]);
        DiagnosticBag report = CompileReport(project);
        Assert.True(Has(report, "RAD115"), string.Join('\n', report));
    }

    [Fact]
    public void UnconnectedLookupInputIsReported()
    {
        var builder = new ProjectBuilder("unconnected");
        var root = builder.Define("root", "Root");
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("g", new LookupSpec(2, [0, 0, 0, 1]));
        root.Connect(y, root.Pin("g", "out"));

        DiagnosticBag report = CompileReport(builder.Build("root"));
        Assert.False(report.HasErrors);
        Assert.True(Has(report, "RAD121"));
    }

    [Fact]
    public void MultipleDriversUnderSinglePolicyProduceWarning()
    {
        var builder = new ProjectBuilder("drivers");
        var root = builder.Define("root", "Root");
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("c0", new ConstantSpec(0));
        root.AddPrimitive("c1", new ConstantSpec(1));
        root.Connect(y, root.Pin("c0", "out"), root.Pin("c1", "out"));

        DiagnosticBag report = CompileReport(builder.Build("root"));
        Assert.False(report.HasErrors);
        Assert.True(Has(report, "RAD119") || Has(report, "RAD081"));
    }

    [Fact]
    public void HierarchyFlatteningKeepsAllLeafComponents()
    {
        var builder = new ProjectBuilder("flat");
        var child = builder.Define("child", "Child");
        Net ca = child.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net cy = child.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        child.AddPrimitive("g", new LookupSpec(1, [1, 0]));
        child.Connect(ca, child.Pin("g", "in0"));
        child.Connect(cy, child.Pin("g", "out"));

        var root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddInstance("u1", "child");
        root.AddInstance("u2", "child");
        root.AddNet("n1", StandardDomains.BinaryId, new PinRef("u1", "y"), new PinRef("u2", "a"));
        root.Connect(a, root.Pin("u1", "a"));
        root.Connect(y, root.Pin("u2", "y"));

        CompiledCircuit circuit = CircuitCompiler.Compile(builder.Build("root"));
        Assert.Equal(2, circuit.Components.Count);
        Assert.Equal(["u1", "u2"], circuit.Components.Select(c => c.Path));
        Assert.True(circuit.TryGetNet(new NetLocation("u1", "y_net"), out int nestedNet));
        Assert.True(circuit.TryGetNet(new NetLocation(string.Empty, "n1"), out int rootNet));
        Assert.Equal(nestedNet, rootNet);
        Assert.Single(circuit.Nets[rootNet].DriverPins);
    }

    [Fact]
    public void PinAndNetLocationsIncludeNestedPaths()
    {
        CircuitProject project = ExampleProjects.CreateFullAdderDemo();
        CompiledCircuit circuit = CircuitCompiler.Compile(project, "demo.full-adder");

        Assert.True(circuit.TryGetPin(new PinLocation("adder/h1/xor", "g", "out"), out _));
        Assert.True(circuit.TryGetNet(new NetLocation("adder/h1", "s_net"), out _));
    }

    [Fact]
    public void TryCompileReturnsFalseOnErrors()
    {
        Assert.False(CircuitCompiler.TryCompile(
            TestCircuits.LookupRoot(2, [0, 1, 1]),
            "root",
            out CompiledCircuit? circuit,
            out DiagnosticBag report));
        Assert.Null(circuit);
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void ValidateAllReportsBrokenUnusedDefinitions()
    {
        var builder = new ProjectBuilder("unused broken part");
        var root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("ok", new LookupSpec(1, [1, 0]));
        root.Connect(a, root.Pin("ok", "in0"));
        root.Connect(y, root.Pin("ok", "out"));

        var broken = builder.Define("broken", "Broken part");
        Net b = broken.AddPin("b", "B", PinDirection.Input, StandardDomains.BinaryId);
        Net by = broken.AddPin("by", "BY", PinDirection.Output, StandardDomains.BinaryId);
        broken.AddPrimitive("bad", new LookupSpec(1, [0, 5]));
        broken.Connect(b, broken.Pin("bad", "in0"));
        broken.Connect(by, broken.Pin("bad", "out"));
        CircuitProject project = builder.Build("root");

        Assert.True(CircuitCompiler.TryCompile(project, "root", out _, out DiagnosticBag rootReport));
        Assert.False(rootReport.HasErrors);

        Assert.False(CircuitCompiler.TryValidateAllDefinitions(project, out DiagnosticBag allReport));
        Assert.Contains(allReport, diagnostic =>
            diagnostic.Code == "RAD112" && diagnostic.DefinitionId == "broken");
        Assert.Single(allReport, diagnostic => diagnostic.Code == "RAD112");

        Assert.True(CircuitCompiler.TryValidateAllDefinitions(
            TestCircuits.ValidProject(),
            out DiagnosticBag validReport));
        Assert.False(validReport.HasErrors);
    }

    [Fact]
    public void EmptyDefinitionCompiles()
    {
        var builder = new ProjectBuilder("empty");
        builder.Define("empty", "Empty");
        CompiledCircuit circuit = CircuitCompiler.Compile(builder.Build("empty"));
        Assert.Empty(circuit.Components);
        Assert.Empty(circuit.Nets);
        Assert.Empty(circuit.Ports);
    }
}
