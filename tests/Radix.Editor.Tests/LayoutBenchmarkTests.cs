using System.Diagnostics;
using Avalonia;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Editor.Rendering;
using Radix.Editor.Services;
using Radix.Editor.ViewModels;
using Radix.StdLib;
using Xunit;
using Xunit.Abstractions;

namespace Radix.Editor.Tests;

/// <summary>
/// Opt-in layout benchmark. It is skipped unless RADIX_LAYOUT_BENCH=1 so the normal
/// suite stays fast, and it asserts nothing about timing: results are printed for
/// comparison on the machine that runs it.
/// </summary>
public sealed class LayoutBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public void DragFramesWithAndWithoutRouteReuse()
    {
        if (Environment.GetEnvironmentVariable("RADIX_LAYOUT_BENCH") != "1")
        {
            return;
        }

        Warmup();

        foreach (int count in new[] { 200, 500, 1000, 2000 })
        {
            CircuitProject project = SyntheticChain(count);
            long construct = long.MaxValue;
            for (int run = 0; run < 2; run++)
            {
                var watch = Stopwatch.StartNew();
                _ = new EditorViewModel(project);
                watch.Stop();
                construct = Math.Min(construct, watch.ElapsedMilliseconds);
            }

            var viewModel = new EditorViewModel(project);
            ComponentInstance instance = viewModel.Definition.Instances[count / 2];
            double x = instance.X;
            double y = instance.Y;

            // SetInstanceState takes the canonical path, so this is the cost of a
            // drag where every frame reroutes the whole definition.
            var canonical = Stopwatch.StartNew();
            for (int i = 1; i <= 10; i++)
            {
                viewModel.SetInstanceState(instance.Id, x + (i * 9), y + (i * 5));
            }

            canonical.Stop();

            var nudgeModel = new EditorViewModel(project);
            ComponentInstance nudgeInstance = nudgeModel.Definition.Instances[count / 2];
            var nudgeWatch = Stopwatch.StartNew();
            for (int i = 1; i <= 10; i++)
            {
                nudgeModel.NudgeInstance(nudgeInstance.Id, 1, i % 2);
            }

            nudgeWatch.Stop();

            long drag = long.MaxValue;
            long end = long.MaxValue;
            for (int run = 0; run < 3; run++)
            {
                x = instance.X;
                y = instance.Y;
                viewModel.BeginDrag();
                var dragWatch = Stopwatch.StartNew();
                for (int i = 1; i <= 10; i++)
                {
                    viewModel.MoveInstance(instance.Id, x + (i * 9), y + (i * 5));
                }

                dragWatch.Stop();
                drag = Math.Min(drag, dragWatch.ElapsedMilliseconds);
                var endWatch = Stopwatch.StartNew();
                viewModel.EndDrag();
                endWatch.Stop();
                end = Math.Min(end, endWatch.ElapsedMilliseconds);
            }

            // A placement at empty space should not reroute the existing design. Measure
            // the same ten additions through a full canonical rebuild for comparison.
            CircuitProject canonicalPlacementProject = SyntheticChain(count);
            CircuitDefinition canonicalPlacementDefinition = canonicalPlacementProject.FindDefinition("main")!;
            var canonicalPlacementWatch = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                canonicalPlacementDefinition.Instances.Add(new ComponentInstance
                {
                    Id = $"placed{i}",
                    Primitive = ComponentCatalog.Components[0].Spec,
                    Label = "AND gate",
                    X = 40 + (i * 230),
                    Y = 2000,
                });
                _ = CircuitLayout.Build(canonicalPlacementDefinition, canonicalPlacementProject);
            }

            canonicalPlacementWatch.Stop();

            var structuralModel = new EditorViewModel(project);
            structuralModel.SetPlacementPoint(new Point(40, 2000));
            var structuralWatch = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                structuralModel.PlaceLibraryComponent(ComponentCatalog.Components[0]);
            }

            structuralWatch.Stop();

            output.WriteLine(
                $"{count} gates: construct {construct} ms, canonical frames {canonical.ElapsedMilliseconds} ms, " +
                $"nudge frames {nudgeWatch.ElapsedMilliseconds} ms, reused frames {drag} ms, final rebuild {end} ms, " +
                $"10 canonical placements {canonicalPlacementWatch.ElapsedMilliseconds} ms, " +
                $"10 incremental placements {structuralWatch.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public void HierarchicalAccumulatorLayoutBenchmark()
    {
        if (Environment.GetEnvironmentVariable("RADIX_LAYOUT_BENCH") != "1")
        {
            return;
        }

        CircuitProject project = ExampleProjects.CreateAccumulatorCpuDemo();
        var constructWatch = Stopwatch.StartNew();
        var viewModel = new EditorViewModel(project);
        constructWatch.Stop();

        CircuitLayout initial = Assert.IsType<CircuitLayout>(viewModel.Layout);
        ComponentInstance target = viewModel.Definition.Instances[viewModel.Definition.Instances.Count / 2];

        var canonicalWatch = Stopwatch.StartNew();
        for (int i = 1; i <= 10; i++)
        {
            viewModel.SetInstanceState(target.Id, target.X + (i * 9), target.Y + (i * 5));
        }

        canonicalWatch.Stop();

        // Use a separate model at the original authored position for the route-reuse
        // measurement, so the baseline and timed edits do not compound.
        var reusedModel = new EditorViewModel(ExampleProjects.CreateAccumulatorCpuDemo());
        ComponentInstance reusedTarget = reusedModel.Definition.Instances[reusedModel.Definition.Instances.Count / 2];
        double startX = reusedTarget.X;
        double startY = reusedTarget.Y;
        reusedModel.BeginDrag();
        var reuseWatch = Stopwatch.StartNew();
        for (int i = 1; i <= 10; i++)
        {
            reusedModel.MoveInstance(reusedTarget.Id, startX + (i * 9), startY + (i * 5));
        }

        reuseWatch.Stop();

        output.WriteLine(
            $"4-bit accumulator CPU ({initial.Components.Count} components, {initial.Wires.Count} nets): " +
            $"construct {constructWatch.ElapsedMilliseconds} ms, ten canonical frames " +
            $"{canonicalWatch.ElapsedMilliseconds} ms, ten route-reused drag frames {reuseWatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void GeneratedHierarchyLayoutBenchmark()
    {
        if (Environment.GetEnvironmentVariable("RADIX_LAYOUT_BENCH") != "1")
        {
            return;
        }

        foreach (int count in new[] { 25, 100, 250 })
        {
            CircuitProject project = RepeatedChildHierarchy(count);
            CircuitDefinition root = project.FindDefinition("main")!;
            CircuitDefinition child = project.FindDefinition("child")!;

            var constructWatch = Stopwatch.StartNew();
            var viewModel = new EditorViewModel(project);
            constructWatch.Stop();

            var layoutWatch = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                _ = CircuitLayout.Build(viewModel.Definition, viewModel.Project);
            }

            layoutWatch.Stop();
            output.WriteLine(
                $"{count} repeated children: root instances {root.Instances.Count}, root nets {root.Nets.Count}, " +
                $"child primitives {child.Instances.Count}, child nets {child.Nets.Count}, " +
                $"construct {constructWatch.ElapsedMilliseconds} ms, ten canonical builds {layoutWatch.ElapsedMilliseconds} ms");
        }
    }

    private static CircuitProject RepeatedChildHierarchy(int childInstanceCount)
    {
        var builder = new ProjectBuilder("generated-hierarchy-layout-benchmark");
        DefinitionBuilder child = builder.Define("child", "Two-stage lookup child");
        Net inputA = child.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net inputB = child.AddPin("b", "B", PinDirection.Input, StandardDomains.BinaryId);
        Net output = child.AddPin("out", "OUT", PinDirection.Output, StandardDomains.BinaryId);
        child.AddPrimitive("first", new LookupSpec(2, [0, 1, 1, 0]), 0, 0);
        child.AddPrimitive("second", new LookupSpec(2, [0, 1, 1, 0]), 240, 0);
        child.Connect(inputA, child.Pin("first", "in0"));
        child.Connect(inputB, child.Pin("first", "in1"));
        Net middle = child.AddNet("middle", StandardDomains.BinaryId);
        child.Connect(middle, child.Pin("first", "out"));
        child.Connect(middle, child.Pin("second", "in0"));
        child.Connect(inputB, child.Pin("second", "in1"));
        child.Connect(output, child.Pin("second", "out"));

        DefinitionBuilder root = builder.Define("main", "Generated hierarchy root");
        Net rootInput = root.AddPin("in", "IN", PinDirection.Input, StandardDomains.BinaryId);
        Net rootOutput = root.AddPin("out", "OUT", PinDirection.Output, StandardDomains.BinaryId);
        for (int i = 0; i < childInstanceCount; i++)
        {
            root.AddInstance($"child{i}", "child", (i % 20) * 300, (i / 20) * 140);
            root.Connect(rootInput, root.Pin($"child{i}", "b"));
            Net input = i == 0 ? rootInput : root.Definition.FindNet($"chain{i}")!;
            root.Connect(input, root.Pin($"child{i}", "a"));

            Net destination = i == childInstanceCount - 1
                ? rootOutput
                : root.AddNet($"chain{i + 1}", StandardDomains.BinaryId);
            root.Connect(destination, root.Pin($"child{i}", "out"));
        }

        return builder.Build("main");
    }

    private static void Warmup()
    {
        CircuitProject project = SyntheticChain(50);
        var viewModel = new EditorViewModel(project);
        ComponentInstance instance = viewModel.Definition.Instances[25];
        double x = instance.X;
        double y = instance.Y;
        viewModel.BeginDrag();
        for (int i = 1; i <= 10; i++)
        {
            viewModel.MoveInstance(instance.Id, x + (i * 9), y + (i * 5));
        }

        viewModel.EndDrag();
    }

    /// <summary>A grid of single-input lookup gates wired nose-to-tail through one net per gate.</summary>
    private static CircuitProject SyntheticChain(int count)
    {
        var builder = new ProjectBuilder("layout-benchmark");
        DefinitionBuilder definition = builder.Define("main", "Main");
        definition.AddPin("out", "OUT", PinDirection.Output, StandardDomains.BinaryId);
        Net? previous = null;
        for (int i = 0; i < count; i++)
        {
            string id = $"gate{i}";
            definition.AddPrimitive(id, new LookupSpec(1, [0, 1]), (i % 20) * 190, (i / 20) * 120);
            if (previous is null)
            {
                definition.AddPrimitive("src", new ConstantSpec(1), -220, 60);
                previous = definition.AddNet("n0", StandardDomains.BinaryId);
                definition.Connect(previous, definition.Pin("src", "out"));
            }

            definition.Connect(previous, definition.Pin(id, "in0"));
            Net next = i == count - 1
                ? definition.Definition.FindPortNet("out")!
                : definition.AddNet($"n{i + 1}", StandardDomains.BinaryId);
            definition.Connect(next, definition.Pin(id, "out"));
            previous = next;
        }

        return builder.Build("main");
    }
}
