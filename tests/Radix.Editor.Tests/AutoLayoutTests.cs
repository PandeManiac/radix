using Avalonia;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Editor.Rendering;
using Radix.Editor.Services;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class AutoLayoutTests
{
    [Fact]
    public void ChainIsLaidOutLeftToRightByDependency()
    {
        var builder = new ProjectBuilder("auto-chain");
        DefinitionBuilder definition = builder.Define("main", "Main");
        definition.AddPin("out", "OUT", PinDirection.Output, StandardDomains.BinaryId);
        definition.AddPrimitive("src", new ConstantSpec(1), 900, 500);
        definition.AddPrimitive("a", new LookupSpec(1, [0, 1]), 900, 500);
        definition.AddPrimitive("b", new LookupSpec(1, [0, 1]), 900, 500);
        definition.AddPrimitive("c", new LookupSpec(1, [0, 1]), 900, 500);
        Net first = definition.AddNet("n1", StandardDomains.BinaryId);
        Net second = definition.AddNet("n2", StandardDomains.BinaryId);
        Net third = definition.AddNet("n3", StandardDomains.BinaryId);
        definition.Connect(first, definition.Pin("src", "out"), definition.Pin("a", "in0"));
        definition.Connect(second, definition.Pin("a", "out"), definition.Pin("b", "in0"));
        definition.Connect(third, definition.Pin("b", "out"), definition.Pin("c", "in0"));
        definition.Connect(
            definition.Definition.FindPortNet("out")!,
            definition.Pin("c", "out"));
        CircuitProject project = builder.Build("main");

        AutoLayoutResult result = CircuitAutoLayout.Arrange(project, definition.Definition);

        Assert.Equal(4, result.Positions.Count);
        Assert.True(result.Positions["src"].X < result.Positions["a"].X);
        Assert.True(result.Positions["a"].X < result.Positions["b"].X);
        Assert.True(result.Positions["b"].X < result.Positions["c"].X);
        Assert.Equal(4, result.Layers);
        AssertNoOverlap(project, definition.Definition, result.Positions);

        AutoLayoutResult repeated = CircuitAutoLayout.Arrange(project, definition.Definition);
        Assert.Equal(result.Positions, repeated.Positions);
    }

    [Fact]
    public void BarycenterOrderingReducesCrossingsBetweenLayers()
    {
        var builder = new ProjectBuilder("crossing");
        DefinitionBuilder definition = builder.Define("main", "Main");
        definition.AddPin("c_out", "C_OUT", PinDirection.Output, StandardDomains.BinaryId);
        definition.AddPin("d_out", "D_OUT", PinDirection.Output, StandardDomains.BinaryId);
        definition.AddPrimitive("a", new ConstantSpec(0), 0, 100);
        definition.AddPrimitive("b", new ConstantSpec(1), 0, 300);
        definition.AddPrimitive("c", new LookupSpec(1, [0, 1]), 500, 100);
        definition.AddPrimitive("d", new LookupSpec(1, [0, 1]), 500, 300);
        definition.Connect(
            definition.AddNet("wire_c", StandardDomains.BinaryId),
            definition.Pin("a", "out"),
            definition.Pin("d", "in0"));
        definition.Connect(
            definition.AddNet("wire_d", StandardDomains.BinaryId),
            definition.Pin("b", "out"),
            definition.Pin("c", "in0"));
        definition.Connect(
            definition.Definition.FindPortNet("c_out")!,
            definition.Pin("c", "out"));
        definition.Connect(
            definition.Definition.FindPortNet("d_out")!,
            definition.Pin("d", "out"));
        CircuitProject project = builder.Build("main");

        AutoLayoutResult result = CircuitAutoLayout.Arrange(project, definition.Definition);

        // Authored positions put c above d, but a->d and b->c cross. The barycenter
        // pass must invert the sinks while keeping each layer's own ordering stable.
        Assert.True(result.Positions["d"].Y < result.Positions["c"].Y);
        Assert.True(result.Positions["a"].Y < result.Positions["b"].Y);
        Assert.Equal(2, result.Layers);
        AssertNoOverlap(project, definition.Definition, result.Positions);
        Assert.Equal(result.Positions, CircuitAutoLayout.Arrange(project, definition.Definition).Positions);
    }

    [Fact]
    public void SequentialFeedbackIsGroupedWithoutOverlappingBodies()
    {
        CircuitProject project = ExampleProjects.CreateCounterDemo();
        CircuitDefinition definition = project.RequireDefinition(project.RootDefinitionId);

        AutoLayoutResult result = CircuitAutoLayout.Arrange(project, definition);

        Assert.Equal(definition.Instances.Count, result.Positions.Count);
        AssertNoOverlap(project, definition, result.Positions);
        AutoLayoutResult repeated = CircuitAutoLayout.Arrange(project, definition);
        Assert.Equal(result.Positions, repeated.Positions);
    }

    private static void AssertNoOverlap(
        CircuitProject project,
        CircuitDefinition definition,
        IReadOnlyDictionary<string, Point> positions)
    {
        var boxes = definition.Instances.Select(instance =>
        {
            Point position = positions[instance.Id];
            double height = CircuitLayout.MeasureHeight(project.GetInstancePins(instance));
            return new Rect(position.X, position.Y, CircuitLayout.BoxWidth, height);
        }).ToArray();

        for (int i = 0; i < boxes.Length; i++)
        {
            for (int j = i + 1; j < boxes.Length; j++)
            {
                Assert.False(boxes[i].Intersects(boxes[j]), $"auto layout overlaps boxes {i} and {j}");
            }
        }
    }
}
