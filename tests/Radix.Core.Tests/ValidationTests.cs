using Radix.Core.Model;
using Radix.Core.Signals;
using Xunit;

namespace Radix.Core.Tests;

public sealed class ValidationTests
{
    private static DiagnosticBag Validate(CircuitProject project, string? root = null) =>
        CircuitValidator.Validate(project, root);

    private static bool Has(DiagnosticBag bag, string code) => bag.Any(d => d.Code == code);

    [Fact]
    public void ValidProjectHasNoErrors()
    {
        DiagnosticBag report = Validate(TestCircuits.ValidProject());
        Assert.False(report.HasErrors, string.Join('\n', report));
    }

    [Fact]
    public void MissingRootIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.RootDefinitionId = "does-not-exist";
        Assert.True(Has(Validate(project), "RAD031"));
    }

    [Fact]
    public void DuplicateDefinitionIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions.Add(new CircuitDefinition { Id = "main", Name = "Duplicate" });
        Assert.True(Has(Validate(project), "RAD021"));
    }

    [Fact]
    public void InvalidIdentifierIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Id = "bad id";
        Assert.True(Has(Validate(project), "RAD020"));
    }

    [Fact]
    public void UnboundInterfacePinIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Ports.Clear();
        Assert.True(Has(Validate(project), "RAD094"));
    }

    [Fact]
    public void PortDomainMismatchIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Pins[0].DomainId = StandardDomains.TernaryId;
        Assert.True(Has(Validate(project), "RAD093"));
    }

    [Fact]
    public void MissingDefinitionForInstanceIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Instances[0].Primitive = null;
        project.Definitions[0].Instances[0].DefinitionId = "missing";
        Assert.True(Has(Validate(project), "RAD053"));
    }

    [Fact]
    public void InstanceNeedsExactlyOneSource()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Instances[0].DefinitionId = "main";
        Assert.True(Has(Validate(project), "RAD052"));
    }

    [Fact]
    public void DuplicateInstanceIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Instances.Add(new ComponentInstance
        {
            Id = "buf",
            Primitive = new LookupSpec(1, [0, 1]),
        });
        Assert.True(Has(Validate(project), "RAD051"));
    }

    [Fact]
    public void NetWithUnknownDomainIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Nets[0].DomainId = "missing";
        Assert.True(Has(Validate(project), "RAD072"));
    }

    [Fact]
    public void NetInitialValueMustBeInDomain()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Nets[0].InitialValue = 2;
        Assert.True(Has(Validate(project), "RAD073"));
    }

    [Fact]
    public void PinMayOnlyBelongToOneNet()
    {
        CircuitProject project = TestCircuits.ValidProject();
        Net extra = new() { Id = "extra", DomainId = StandardDomains.BinaryId };
        extra.Pins.Add(new PinRef("buf", "in0"));
        project.Definitions[0].Nets.Add(extra);
        Assert.True(Has(Validate(project), "RAD076"));
    }

    [Fact]
    public void PinListedTwiceOnOneNetIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Nets[0].Pins.Add(new PinRef("buf", "in0"));
        Assert.True(Has(Validate(project), "RAD075"));
    }

    [Fact]
    public void UnknownPinReferenceIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Nets[0].Pins.Add(new PinRef("buf", "banana"));
        Assert.True(Has(Validate(project), "RAD078"));
    }

    [Fact]
    public void UnknownInstanceReferenceIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Nets[0].Pins.Add(new PinRef("ghost", "out"));
        Assert.True(Has(Validate(project), "RAD077"));
    }

    [Fact]
    public void RecursiveDefinitionHierarchyIsAnError()
    {
        var builder = new ProjectBuilder("recursive");
        var a = builder.Define("a", "A");
        var b = builder.Define("b", "B");
        a.AddInstance("to_b", "b");
        b.AddInstance("to_a", "a");
        builder.Build("a");

        DiagnosticBag report = Validate(builder.Project);
        Assert.True(Has(report, "RAD032"));
    }

    [Fact]
    public void LookupTableCannotContainNegativeEntries()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Instances[0].Primitive = new LookupSpec(1, [0, -1]);
        Assert.True(Has(Validate(project), "RAD057"));
    }

    [Fact]
    public void LookupInputCountIsBounded()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Instances[0].Primitive = new LookupSpec(17, [0]);
        Assert.True(Has(Validate(project), "RAD055"));
    }

    [Fact]
    public void UnconnectedInputIsOnlyAWarning()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Definitions[0].Nets[0].Pins.Clear();
        DiagnosticBag report = Validate(project);
        Assert.False(report.HasErrors);
        Assert.True(Has(report, "RAD080"));
    }

    [Fact]
    public void DuplicatePortBindingIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        CircuitDefinition root = project.Definitions[0];
        root.Ports.Add(new PortBinding("a", "y_net"));
        Assert.True(Has(Validate(project), "RAD091"));
    }

    [Fact]
    public void UnsupportedFormatVersionIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.FormatVersion = 99;
        Assert.True(Has(Validate(project), "RAD001"));
    }

    [Fact]
    public void DuplicateDomainIdIsAnError()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Domains.Add(StandardDomains.Binary());
        Assert.True(Has(Validate(project), "RAD012"));
    }
}
