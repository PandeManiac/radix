using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The buffered ternary inverter is a second, deliberately analog design: the summing
/// node is no longer the output. A complementary pair of CMOS inverters restores the
/// inverter polarity before the 100 kOhm summing network, and a two-stage CMOS feedback
/// amplifier buffers the summed node. These tests keep the resulting levels, plateau,
/// load regulation, and power honest; the 100 kOhm operating point also exercises the
/// MOSFET-current continuation fallback in the DC solver.
/// </summary>
public sealed class BufferedTernaryInverterTests
{
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    private static string ExamplePath(string fileName) =>
        Path.Combine(RepositoryRoot(), "examples", fileName);

    private static DcNetlist Netlist() =>
        DcNetlistJson.Load(ExamplePath("dc-ternary-inverter-buffered.json"));

    private static DcSolution Solve(DcNetlist netlist, double inputVolts, double? loadOhms = null)
    {
        DcNetlist biased = netlist.WithElementValue("VIN", inputVolts);
        if (loadOhms is double ohms)
        {
            biased.AddResistor("RLOAD", "out", "gnd", ohms);
        }

        return biased.Solve();
    }

    private static double Output(DcNetlist netlist, double inputVolts, double? loadOhms = null) =>
        Solve(netlist, inputVolts, loadOhms).NodeVoltages["out"];

    [Fact]
    public void NetlistAndAuthoredProjectDescribeTheSameCircuit()
    {
        CircuitProject project = ProjectSerializer.Load(
            ExamplePath("dc-ternary-inverter-buffered.radix"));
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        Assert.Equal(14, circuit.Nodes.Count);
        Assert.Equal(24, circuit.Components.Count);
        Assert.False(ElectricalCircuitValidator.Validate(circuit).HasErrors);

        DcNetlist json = Netlist();
        DcNetlist authored = circuit.ToDcNetlist();
        foreach (double volts in new[] { 0d, 1.25, 2.5, 3.75, 5 })
        {
            Assert.Equal(
                Output(json, volts),
                Output(authored, volts),
                12);
        }
    }

    [Fact]
    public void BufferedInverterProducesThreeNominalLevels()
    {
        DcNetlist netlist = Netlist();

        // The output is a solved electrical node, not a selected table entry. The small
        // low-state error is the finite loop gain and the 1 pS MOSFET leakage floor.
        Assert.True(Output(netlist, 0) > 4.99);
        Assert.InRange(Output(netlist, 2.5), 2.49, 2.52);
        Assert.True(Output(netlist, 5) < 0.01);

        // Relative to the original resistor-summed gate, the buffered output is a true
        // inverter: 0 V in gives 5 V out and 5 V in gives 0 V out.
        DcNetlist original = DcNetlistJson.Load(ExamplePath("dc-ternary-inverter.json"));
        Assert.True(Output(original, 0) > 4.99);
        Assert.True(Output(original, 5) < 0.01);
    }

    [Fact]
    public void BufferedInverterKeepsTheMiddlePlateau()
    {
        DcNetlist netlist = Netlist();

        // The pre-summing complements matter: a high-gain stage after the summing node
        // would amplify the 100 mV summing plateau into a full transition. With the
        // complements before the resistors, the buffered output stays within 1 mV of
        // 2.508 V across the whole 2 V input band.
        IReadOnlyList<DcSweepRow> middle = DcSweep.Run(
            netlist, "VIN", DcSweep.Linspace(1.5, 3.5, 21));
        Assert.All(middle, row => Assert.True(row.Solved, row.Message));
        Assert.All(middle, row =>
            Assert.InRange(row.Solution!.NodeVoltages["out"], 2.50, 2.52));

        // The full curve is monotone decreasing and reaches both outer rails.
        IReadOnlyList<DcSweepRow> full = DcSweep.Run(
            netlist, "VIN", DcSweep.Linspace(0, 5, 41));
        Assert.All(full, row => Assert.True(row.Solved, row.Message));
        for (int index = 1; index < full.Count; index++)
        {
            Assert.True(
                full[index].Solution!.NodeVoltages["out"] <=
                    full[index - 1].Solution!.NodeVoltages["out"] + 1e-6);
        }

        Assert.True(full[0].Solution!.NodeVoltages["out"] > 4.99);
        Assert.True(full[^1].Solution!.NodeVoltages["out"] < 0.01);
    }

    [Fact]
    public void BufferedInverterDrivesA100kLoadWithoutLosingItsLevels()
    {
        DcNetlist buffered = Netlist();
        DcNetlist original = DcNetlistJson.Load(ExamplePath("dc-ternary-inverter.json"));

        // The original 50 kOhm Thevenin output collapses to 1.664 V under this load.
        // The buffered output holds its middle level to within 0.4 mV of the unloaded
        // value (2.508106 V -> 2.507769 V), and the outer levels stay near the rails.
        Assert.InRange(Output(original, 2.5, 100_000), 1.5, 1.8);
        Assert.True(Output(buffered, 2.5, 100_000) > 2.49);
        Assert.True(Output(buffered, 0, 100_000) > 4.95);
        Assert.True(Output(buffered, 5, 100_000) < 0.01);
        Assert.True(
            Output(buffered, 2.5, 100_000) >
            Output(original, 2.5, 100_000) + 0.5);

        double unloadedMiddle = Output(buffered, 2.5);
        double loadedMiddle = Output(buffered, 2.5, 100_000);
        Assert.InRange(Math.Abs(unloadedMiddle - loadedMiddle), 0, 0.005);

        // The loaded operating point is what needs the solver's continuation fallback;
        // a converged nonlinear solution reports a normalized residual at or below 1.
        DcSolution loaded = Solve(buffered, 2.5, 100_000);
        Assert.True(loaded.UsedContinuation);
        Assert.InRange(loaded.RelativeResidual, 0, 1);
    }

    [Fact]
    public void MixedDiodeClampUsesDeviceCurrentContinuation()
    {
        // The unclamped bias point solves directly, but a diode on the high-impedance
        // summing node makes the cold Newton start fail. Both device laws must be
        // ramped together for the fallback to reach this operating point.
        DcNetlist netlist = Netlist().WithElementValue("VIN", 2.5);
        netlist.AddDiode("DCLAMP", "sum", "gnd", 1e-12);

        DcSolution solution = netlist.Solve();

        Assert.True(solution.UsedContinuation);
        Assert.InRange(solution.NodeVoltages["out"], 4.5, 4.65);
        Assert.InRange(solution.NodeVoltages["sum"], 0.4, 0.5);
        Assert.InRange(solution.RelativeResidual, 0, 1);
        Assert.InRange(Math.Abs(solution.ElementPowers.Values.Sum()), 0, 1e-9);

        DcDiode diode = Assert.IsType<DcDiode>(netlist.Elements[^1]);
        Assert.Equal(
            diode.CurrentAt(solution.ElementVoltages["DCLAMP"]),
            solution.ElementCurrents["DCLAMP"],
            12);
    }

    [Fact]
    public void BufferedInverterBalancesPowerAndPaysForItsAmplifier()
    {
        DcSolution middle = Solve(Netlist(), 2.5);
        double sum = middle.ElementPowers.Values.Sum();
        Assert.InRange(Math.Abs(sum), 0, 1e-9);

        // The amplifier is class-A-ish: the first stage tail is always biased, so even
        // the rail states draw roughly 0.64-0.68 mW. The middle state additionally
        // conducts through the output stage and the reference divider.
        double middlePower = -middle.ElementPowers["VDD"];
        Assert.InRange(middlePower, 2.0e-3, 2.4e-3);
        Assert.InRange(-Solve(Netlist(), 0).ElementPowers["VDD"], 0.5e-3, 0.8e-3);
        Assert.InRange(-Solve(Netlist(), 5).ElementPowers["VDD"], 0.5e-3, 0.8e-3);

        // A 100 kOhm load adds about 0.1 mW in the middle state, not a collapse.
        double loadedMiddlePower = -Solve(Netlist(), 2.5, 100_000).ElementPowers["VDD"];
        Assert.InRange(loadedMiddlePower, middlePower, middlePower + 0.2e-3);
    }
}
