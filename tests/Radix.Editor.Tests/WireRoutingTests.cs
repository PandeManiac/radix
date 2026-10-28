using Avalonia;
using Radix.Core.Model;
using Radix.Core.Serialization;
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

    private static bool SegmentIntersectsRect(Point a, Point b, Rect rect)
    {
        double minX = Math.Min(a.X, b.X);
        double maxX = Math.Max(a.X, b.X);
        double minY = Math.Min(a.Y, b.Y);
        double maxY = Math.Max(a.Y, b.Y);
        return maxX > rect.Left && minX < rect.Right && maxY > rect.Top && minY < rect.Bottom;
    }
}
