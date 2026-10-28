using Avalonia;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Editor.Rendering;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class WireRoutingTests
{
    [Theory]
    [InlineData("counter")]
    [InlineData("accumulator")]
    [InlineData("full-adder")]
    public void AutomaticWiresStayOrthogonalAndClearOfComponentBodies(string example)
    {
        EditorViewModel viewModel = example switch
        {
            "counter" => new EditorViewModel(ExampleProjects.CreateCounterDemo()),
            "accumulator" => new EditorViewModel(ExampleProjects.CreateAccumulatorCpuDemo()),
            _ => new EditorViewModel(ExampleProjects.CreateFullAdderDemo()),
        };

        CircuitLayout layout = Assert.IsType<CircuitLayout>(viewModel.Layout);
        var violations = new List<string>();
        foreach (NetWire wire in layout.Wires)
        {
            foreach ((Point a, Point b) in wire.Segments)
            {
                if (Math.Abs(a.X - b.X) > 0.01 && Math.Abs(a.Y - b.Y) > 0.01)
                {
                    violations.Add($"{wire.NetId} has a diagonal segment {a}->{b}");
                }

                foreach (ComponentBox component in layout.Components)
                {
                    Rect body = component.Bounds.Deflate(3);
                    if (SegmentIntersectsRect(a, b, body))
                    {
                        violations.Add($"{wire.NetId} crosses {component.Instance.Id} ({a}->{b})");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DragFramesReuseUnchangedRoutesAndStayClearOfBodies()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        CircuitLayout initial = Assert.IsType<CircuitLayout>(viewModel.Layout);
        NetWire[] before = initial.Wires.ToArray();
        ComponentInstance instance = viewModel.Definition.Instances.First(i => i.Id.StartsWith("ff", StringComparison.Ordinal));
        double x = instance.X;
        double y = instance.Y;

        viewModel.BeginDrag();
        for (int i = 1; i <= 6; i++)
        {
            viewModel.MoveInstance(instance.Id, x + (i * 9), y + (i * 5));
            AssertWiresClear(Assert.IsType<CircuitLayout>(viewModel.Layout));
        }

        Assert.Contains(viewModel.Layout!.Wires, before.Contains);
        viewModel.EndDrag();
        AssertWiresClear(Assert.IsType<CircuitLayout>(viewModel.Layout));
        Assert.DoesNotContain(viewModel.Layout!.Wires, before.Contains);
    }

    [Fact]
    public void MovingABodyInvalidatesRoutesThroughItButReusesOthers()
    {
        var builder = new ProjectBuilder("route-cache");
        DefinitionBuilder definition = builder.Define("main", "Main");
        Net longNet = definition.AddNet("long", StandardDomains.BinaryId);
        definition.AddPrimitive("src", new ConstantSpec(1), 100, 200);
        definition.Connect(longNet, definition.Pin("src", "out"));
        definition.AddPrimitive("dst", new LookupSpec(1, [0, 1]), 700, 500);
        definition.Connect(longNet, definition.Pin("dst", "in0"));
        definition.AddPrimitive("mover", new LookupSpec(1, [0, 1]), 1200, 100);
        Net farNet = definition.AddNet("far", StandardDomains.BinaryId);
        definition.AddPrimitive("farSrc", new ConstantSpec(0), 1400, 800);
        definition.Connect(farNet, definition.Pin("farSrc", "out"));
        CircuitProject project = builder.Build("main");

        CircuitLayout baseline = CircuitLayout.Build(definition.Definition, project);
        NetWire routed = baseline.Wires.Single(wire => wire.NetId == "long");
        NetWire far = baseline.Wires.Single(wire => wire.NetId == "far");
        (Point a, Point b) = routed.Segments
            .OrderByDescending(segment =>
                Math.Abs(segment.A.X - segment.B.X) + Math.Abs(segment.A.Y - segment.B.Y))
            .First();
        Point midpoint = new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        ComponentInstance mover = definition.Definition.FindInstance("mover")!;
        mover.X = midpoint.X - (CircuitLayout.BoxWidth / 2);
        mover.Y = midpoint.Y - 30;

        CircuitLayout updated = CircuitLayout.Build(definition.Definition, project, baseline);

        Assert.NotSame(routed, updated.Wires.Single(wire => wire.NetId == "long"));
        Assert.Same(far, updated.Wires.Single(wire => wire.NetId == "far"));
        AssertWiresClear(updated);
    }

    [Fact]
    public void BendpointsAreEditableUndoableAndPersisted()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateFullAdderDemo());
        NetWire wire = viewModel.Layout!.Wires.First();
        string netId = wire.NetId;
        Point first = wire.LabelPoint;

        viewModel.SelectNet(netId);
        viewModel.AddNetWaypoint(netId, first);
        Assert.Single(viewModel.Definition.FindNet(netId)!.RoutePoints);
        Assert.True(viewModel.SelectedNetHasRoute);

        viewModel.BeginDrag();
        viewModel.MoveNetWaypoint(netId, 0, new Point(first.X + 75, first.Y + 30));
        viewModel.EndDrag();
        Assert.Equal(first.X + 75, viewModel.Definition.FindNet(netId)!.RoutePoints[0].X, 1);

        string json = ProjectSerializer.SaveToString(viewModel.Project);
        CircuitProject reloaded = ProjectSerializer.LoadFromString(json);
        Assert.Single(reloaded.FindDefinition(viewModel.Project.RootDefinitionId)!.FindNet(netId)!.RoutePoints);

        viewModel.Undo();
        viewModel.Undo();
        Assert.Empty(viewModel.Definition.FindNet(netId)!.RoutePoints);
    }

    [Fact]
    public void CustomWireColorIsNormalizedValidatedAndUndoable()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateFullAdderDemo());
        string netId = viewModel.Layout!.Wires.First().NetId;
        viewModel.SelectNet(netId);

        viewModel.SetNetWireColor(netId, "#38bdf8");
        Assert.Equal("#38BDF8", viewModel.Definition.FindNet(netId)!.WireColor);
        Assert.DoesNotContain(viewModel.Diagnostics, d => d.Severity == Core.DiagnosticSeverity.Error);

        viewModel.SetNetWireColor(netId, "not-a-color");
        Assert.Equal("#38BDF8", viewModel.Definition.FindNet(netId)!.WireColor);

        viewModel.Undo();
        Assert.Null(viewModel.Definition.FindNet(netId)!.WireColor);
    }

    [Fact]
    public void MultiSelectionDeletesAndRestoresAsOneUndoStep()
    {
        var viewModel = new EditorViewModel(ExampleProjects.CreateCounterDemo());
        string[] ids = viewModel.Definition.Instances.Take(2).Select(i => i.Id).ToArray();

        viewModel.SelectInstances(ids);

        Assert.Equal(2, viewModel.SelectionCount);
        Assert.True(viewModel.HasMultipleSelection);
        Assert.Equal("2 parts", viewModel.SelectionTitle);
        Assert.All(ids, id => Assert.Contains(id, viewModel.SelectedInstanceIds));

        viewModel.DeleteSelection();
        Assert.DoesNotContain(viewModel.Definition.Instances, i => ids.Contains(i.Id));
        viewModel.Undo();
        Assert.All(ids, id => Assert.NotNull(viewModel.Definition.FindInstance(id)));
    }

    private static void AssertWiresClear(CircuitLayout layout)
    {
        var violations = new List<string>();
        foreach (NetWire wire in layout.Wires)
        {
            foreach ((Point a, Point b) in wire.Segments)
            {
                foreach (ComponentBox component in layout.Components)
                {
                    if (SegmentIntersectsRect(a, b, component.Bounds.Deflate(3)))
                    {
                        violations.Add($"{wire.NetId} crosses {component.Instance.Id} ({a}->{b})");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static bool SegmentIntersectsRect(Point a, Point b, Rect rect)
    {
        double minX = Math.Min(a.X, b.X);
        double maxX = Math.Max(a.X, b.X);
        double minY = Math.Min(a.Y, b.Y);
        double maxY = Math.Max(a.Y, b.Y);
        return maxX > rect.Left && minX < rect.Right && maxY > rect.Top && minY < rect.Bottom;
    }
}
