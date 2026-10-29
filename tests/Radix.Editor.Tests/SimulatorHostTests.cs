using Radix.Core;
using Radix.Core.Model;
using Radix.Core.Simulation;
using Radix.Core.Signals;
using Radix.Editor.Services;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class SimulatorHostTests
{
    [Fact]
    public void ReportsSuppressedRuntimeWarningsInDiagnostics()
    {
        var builder = new ProjectBuilder("warning cap");
        var root = builder.Define("root", "Root");
        for (int i = 0; i < 1_002; i++)
        {
            Net net = root.AddNet($"n{i}", StandardDomains.BinaryId);
            root.AddPrimitive($"a{i}", new LookupSpec(0, [0]));
            root.AddPrimitive($"b{i}", new LookupSpec(0, [1]));
            root.Connect(net, root.Pin($"a{i}", "out"));
            root.Connect(net, root.Pin($"b{i}", "out"));
        }

        CircuitProject project = builder.Build("root");
        var host = new SimulatorHost(project, project.Definitions[0]);

        Assert.True(host.EnsureCompiled());
        Diagnostic? summary = host.AllDiagnostics()
            .FirstOrDefault(diagnostic => diagnostic.Code == SimulationEngine.SuppressedDiagnosticsCode);
        Assert.NotNull(summary);
        Assert.Contains("2 further runtime warning(s)", summary!.Message, StringComparison.Ordinal);
    }
}
