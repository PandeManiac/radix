using System.Diagnostics;
using System.Globalization;
using Radix.Core.Electrical;
using Xunit;
using Xunit.Abstractions;

namespace Radix.Core.Tests;

/// <summary>
/// Opt-in transient cost benchmark. It is skipped unless RADIX_TRANSIENT_BENCH=1, so the
/// normal suite stays fast and deterministic, and it asserts only correctness (the run
/// completed and reported every step): timings are printed for comparison on the machine
/// that runs them. The ladder is linear, so each step is one dense assembly plus one
/// factorisation, which is exactly the cost the solver's own limit describes.
/// </summary>
public sealed class TransientBenchmarkTests(ITestOutputHelper output)
{
    private static readonly int[] Sizes = [5, 25, 50];
    private static readonly int[] StepCounts = [1_000, 5_000];

    [Fact]
    public void TransientRunCostByNodeCountAndStepCount()
    {
        if (Environment.GetEnvironmentVariable("RADIX_TRANSIENT_BENCH") != "1")
        {
            return;
        }

        // Warm the JIT and the shared static state before timing anything.
        _ = RcLadder(5).Run(new TransientOptions { StopTime = 1e-3, Steps = 200 });

        foreach (int nodes in Sizes)
        {
            TransientNetlist netlist = RcLadder(nodes);
            foreach (int steps in StepCounts)
            {
                double best = double.MaxValue;
                TransientResult? last = null;
                for (int run = 0; run < 3; run++)
                {
                    var watch = Stopwatch.StartNew();
                    last = netlist.Run(new TransientOptions
                    {
                        StopTime = 0.01,
                        Steps = steps,
                        Method = TransientMethod.Trapezoidal,
                    });
                    watch.Stop();
                    best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                }

                Assert.NotNull(last);
                Assert.True(last.Completed, last.Message);
                Assert.Equal(steps + 1, last.Points.Count);
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{nodes,3} nodes x {steps,5} steps: {best,9:F1} ms total, {best * 1000 / steps,7:F1} us/step"));
            }
        }
    }

    /// <summary>
    /// A series RC ladder: one source, <paramref name="nodes"/> resistors and one capacitor
    /// per node to ground, all starting discharged. Every capacitor adds a state variable and
    /// every node adds one unknown, so the dense system grows with the node count.
    /// </summary>
    private static TransientNetlist RcLadder(int nodes)
    {
        var netlist = new TransientNetlist("gnd")
            .AddVoltageSource("V1", "n0", "gnd", 5);
        for (int index = 1; index <= nodes; index++)
        {
            netlist = netlist.AddResistor($"R{index}", $"n{index - 1}", $"n{index}", 1_000);
            netlist = netlist.AddCapacitor($"C{index}", $"n{index}", "gnd", 1e-6);
        }

        for (int index = 1; index <= nodes; index++)
        {
            netlist.SetCapacitorInitialVoltage($"C{index}", 0);
        }

        return netlist;
    }
}
