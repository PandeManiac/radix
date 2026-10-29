using Radix.Core;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class DefinitionCloneTests
{
    [Fact]
    public void CloneDeepCopiesPresentationAndKeepsDefinitionLocalIds()
    {
        CircuitProject project = TestFixtures.WithStandardLibrary("clone");
        CircuitDefinition source = RichDefinition();
        project.Definitions.Add(source);

        CircuitDefinition clone = source.Clone("part-copy", "Part copy");

        Assert.Equal("part-copy", clone.Id);
        Assert.Equal("Part copy", clone.Name);
        Assert.Equal(source.Description, clone.Description);
        Assert.Equal(source.Pins.Select(pin => pin.Id), clone.Pins.Select(pin => pin.Id));
        Assert.Equal(source.Pins.Select(pin => pin.Name), clone.Pins.Select(pin => pin.Name));
        Assert.Equal(source.Instances.Select(instance => instance.Id), clone.Instances.Select(instance => instance.Id));
        Assert.Equal(source.Nets.Select(net => net.Id), clone.Nets.Select(net => net.Id));
        Assert.Equal(source.Ports, clone.Ports);
        Assert.Equal(source.NumericLanes.Single().PinIds, clone.NumericLanes.Single().PinIds);

        Assert.NotSame(source, clone);
        Assert.NotSame(source.Pins[0], clone.Pins[0]);
        Assert.NotSame(source.Nets[0], clone.Nets[0]);
        Assert.NotSame(source.NumericLanes[0], clone.NumericLanes[0]);

        ComponentInstance sourceInstance = source.Instances[0];
        ComponentInstance cloneInstance = clone.Instances[0];
        Assert.NotSame(sourceInstance, cloneInstance);
        Assert.Equal(sourceInstance.X, cloneInstance.X);
        Assert.Equal(sourceInstance.Y, cloneInstance.Y);
        Assert.Equal(sourceInstance.Label, cloneInstance.Label);
        Assert.Same(sourceInstance.Primitive, cloneInstance.Primitive);

        Net sourceNet = source.Nets[0];
        Net cloneNet = clone.Nets[0];
        Assert.Equal(sourceNet.Label, cloneNet.Label);
        Assert.Equal(sourceNet.InitialValue, cloneNet.InitialValue);
        Assert.Equal(sourceNet.WireColor, cloneNet.WireColor);
        Assert.Equal(sourceNet.RoutePoints, cloneNet.RoutePoints);
        Assert.Equal(sourceNet.Pins, cloneNet.Pins);

        project.Definitions.Add(clone);
        DiagnosticBag report = CircuitValidator.Validate(project);
        Assert.False(report.HasErrors, string.Join('\n', report));
    }

    [Fact]
    public void EditingACloneLeavesTheOriginalUntouched()
    {
        CircuitDefinition source = RichDefinition();
        CircuitDefinition clone = source.Clone("part-copy", "Part copy");

        clone.Nets[0].Label = "changed";
        clone.Nets[0].WireColor = "#112233";
        clone.Nets[0].RoutePoints.Add(new NetWaypoint(1, 2));
        clone.Nets[0].Pins.Clear();
        clone.Pins[0].Name = "CHANGED";
        clone.NumericLanes[0].PinIds.Clear();
        clone.Instances[0].X = 999;
        clone.Instances.Clear();
        clone.Ports.Clear();

        Assert.Equal("n1", source.Nets[0].Label);
        Assert.Equal("#AABBCC", source.Nets[0].WireColor);
        Assert.Single(source.Nets[0].RoutePoints);
        Assert.Single(source.Nets[0].Pins);
        Assert.Equal("Y", source.Nets[1].Label);
        Assert.Equal("A", source.Pins[0].Name);
        Assert.Equal(2, source.NumericLanes[0].PinIds.Count);
        Assert.Equal(40, source.Instances[0].X);
        Assert.Single(source.Instances);
        Assert.Equal(2, source.Ports.Count);
    }

    [Fact]
    public void CloneRejectsAnInvalidDefinitionId()
    {
        CircuitDefinition source = RichDefinition();

        Assert.Throws<ArgumentException>(() => source.Clone("bad id", "Copy"));
        Assert.Throws<ArgumentException>(() => source.Clone(string.Empty, "Copy"));
    }

    private static CircuitDefinition RichDefinition()
    {
        var definition = new CircuitDefinition
        {
            Id = "part",
            Name = "Part",
            Description = "A rich test definition.",
        };
        definition.Pins.Add(new PinDeclaration
        {
            Id = "p1",
            Name = "A",
            Direction = PinDirection.Input,
            DomainId = StandardDomains.BinaryId,
        });
        definition.Pins.Add(new PinDeclaration
        {
            Id = "p2",
            Name = "Y",
            Direction = PinDirection.Output,
            DomainId = StandardDomains.BinaryId,
        });

        var net = new Net
        {
            Id = "n1",
            DomainId = StandardDomains.BinaryId,
            Label = "n1",
            InitialValue = 1,
            WireColor = "#AABBCC",
        };
        net.RoutePoints.Add(new NetWaypoint(120, 60));
        net.Pins.Add(new PinRef("g", "in0"));
        definition.Nets.Add(net);

        var output = new Net
        {
            Id = "n2",
            DomainId = StandardDomains.BinaryId,
            Label = "Y",
        };
        output.Pins.Add(new PinRef("g", "out"));
        definition.Nets.Add(output);

        definition.Ports.Add(new PortBinding("p1", "n1"));
        definition.Ports.Add(new PortBinding("p2", "n2"));

        var lane = new NumericLane
        {
            Id = "lane1",
            Name = "Value",
            FormatId = NumericFormats.UnsignedBinary.Id,
        };
        lane.PinIds.AddRange(["p1", "p2"]);
        definition.NumericLanes.Add(lane);

        definition.Instances.Add(new ComponentInstance
        {
            Id = "g",
            Primitive = new LookupSpec(1, [0, 1]),
            Label = "Buffer",
            X = 40,
            Y = 20,
        });

        return definition;
    }
}
