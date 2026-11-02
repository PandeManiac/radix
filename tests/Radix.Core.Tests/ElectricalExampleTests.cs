using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// End-to-end checks over the shipped electrical example netlists: they must parse from
/// disk, solve, and produce the electrical behaviour their names claim. These are the
/// circuits a reader runs first, so they are a regression surface of their own.
/// </summary>
public sealed class ElectricalExampleTests
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

    private static double Output(DcNetlist netlist, string source, double volts) =>
        netlist.WithElementValue(source, volts).Solve().NodeVoltages["out"];

    [Theory]
    [InlineData("dc-mosfet-common-source.json")]
    [InlineData("dc-cmos-inverter.json")]
    [InlineData("dc-ternary-inverter.json")]
    public void EveryElectricalExampleReopensAndSolves(string fileName)
    {
        DcNetlist netlist = DcNetlistJson.Load(ExamplePath(fileName));
        DcSolution solution = netlist.Solve();

        Assert.NotEmpty(netlist.Elements);
        // For a netlist with nonlinear devices the residual field is the largest KCL or
        // source residual relative to its acceptance bound, so <= 1 means converged.
        Assert.InRange(solution.RelativeResidual, 0, 1);
        Assert.All(
            netlist.Nodes,
            node => Assert.True(solution.NodeVoltages.ContainsKey(node)));
    }

    [Fact]
    public void AuthoredTernaryInverterExampleSwitchesThroughTheMiddleRail()
    {
        // The same inverter as the netlist example, but shipped as an authored project
        // asset so it can be opened in the inspector. Its devices carry no lambda, so the
        // shoulders sit slightly higher than the netlist version; the levels do not move.
        CircuitProject project = ProjectSerializer.Load(ExamplePath("dc-ternary-inverter.radix"));
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        Assert.Equal(6, circuit.Nodes.Count);
        Assert.Equal(4, circuit.Components.Count(component =>
            component.Kind == ElectricalComponentKind.Mosfet));

        DcNetlist netlist = circuit.ToDcNetlist();
        Assert.Equal(5, Output(netlist, "VIN", 0), 3);
        Assert.Equal(2.5, Output(netlist, "VIN", 2.5), 3);
        Assert.True(Output(netlist, "VIN", 5) < 1e-6, Output(netlist, "VIN", 5).ToString());

        // The authored asset and the netlist example describe the same design, so now that an
        // authored MOSFET carries its channel-length modulation too, they must agree exactly.
        DcNetlist netlistTwin = DcNetlistJson.Load(ExamplePath("dc-ternary-inverter.json"));
        foreach (double volts in new[] { 0d, 1.25, 2.5, 3.75, 5 })
        {
            Assert.Equal(
                Output(netlistTwin, "VIN", volts),
                Output(netlist, "VIN", volts),
                12);
        }

        // Every input from 1.5 V to 3.5 V stays within 60 mV of the middle rail, the worst
        // samples being the endpoints (2.55006 V and 2.44994 V), so the intermediate level
        // is a plateau rather than a knife edge.
        foreach (double volts in new[] { 1.5, 2.0, 2.5, 3.0, 3.5 })
        {
            Assert.InRange(Output(netlist, "VIN", volts), 2.44, 2.56);
        }
    }

    [Fact]
    public void CommonSourceExampleIsControlledByItsGateVoltage()
    {
        // Below the 1 V threshold the stage is off and the drain sits on the rail. Above
        // it the drain follows the square law: a 2 V gate gives 4.455 V (Vov = 1 V at
        // K = 1 mA/V^2 into 1 kΩ with lambda = 0.02), and a 5 V gate gives 1.105 V.
        DcNetlist netlist = DcNetlistJson.Load(ExamplePath("dc-mosfet-common-source.json"));

        Assert.True(Output(netlist, "VG", 0.5) > 4.999);
        Assert.Equal(4.455, Output(netlist, "VG", 2), 3);
        Assert.Equal(1.105, Output(netlist, "VG", 5), 3);

        // The response is monotone in the control voltage, which is what makes this a
        // controlled device rather than a set of fixed levels.
        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(netlist, "VG", DcSweep.Linspace(0, 5, 51));
        Assert.All(rows, row => Assert.True(row.Solved));
        for (int index = 1; index < rows.Count; index++)
        {
            Assert.True(
                rows[index].Solution!.NodeVoltages["out"] <=
                    rows[index - 1].Solution!.NodeVoltages["out"] + 1e-9);
        }
    }

    [Fact]
    public void CmosInverterExampleSwingsRailToRailWithoutStaticCurrent()
    {
        DcNetlist netlist = DcNetlistJson.Load(ExamplePath("dc-cmos-inverter.json"));

        DcSolution high = netlist.WithElementValue("VIN", 0).Solve();
        DcSolution middle = netlist.WithElementValue("VIN", 2.5).Solve();
        DcSolution low = netlist.WithElementValue("VIN", 5).Solve();

        Assert.True(high.NodeVoltages["out"] > 4.999);
        Assert.True(low.NodeVoltages["out"] < 1e-6);
        Assert.Equal(2.5, middle.NodeVoltages["out"], 3);

        // Exactly one device conducts at a rail, because the other has no overdrive.
        Assert.InRange(Math.Abs(high.ElementPowers["VDD"]), 0, 1e-9);
        Assert.InRange(Math.Abs(low.ElementPowers["VDD"]), 0, 1e-9);
        // Both conduct in the middle, so the inverter draws current exactly there.
        Assert.InRange(-middle.ElementPowers["VDD"], 1e-6, 1e-2);
    }

    [Fact]
    public void TernaryInverterExampleMatchesTheStandardTruthTable()
    {
        // The table is a result of the network, not an assignment: two CMOS inverters
        // with 121:1 device-strength ratios switch at 1.25 V and 3.75 V, and their
        // resistor-summed outputs land on the three rails in the middle.
        DcNetlist netlist = DcNetlistJson.Load(ExamplePath("dc-ternary-inverter.json"));

        Assert.Equal(5, Output(netlist, "VIN", 0), 3);
        Assert.Equal(2.5, Output(netlist, "VIN", 2.5), 6);
        Assert.True(Output(netlist, "VIN", 5) < 1e-5);
    }

    [Fact]
    public void TernaryInverterHasAUsableMiddlePlateau()
    {
        DcNetlist netlist = DcNetlistJson.Load(ExamplePath("dc-ternary-inverter.json"));
        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(netlist, "VIN", DcSweep.Linspace(1.5, 3.5, 21));

        // Every input from 1.5 V to 3.5 V resolves within 100 mV of the middle rail:
        // a 2 V wide input band, so the intermediate state is not a knife edge.
        Assert.All(rows, row => Assert.InRange(row.Solution!.NodeVoltages["out"], 2.4, 2.6));

        // The full curve is monotone and reaches both outer rails.
        IReadOnlyList<DcSweepRow> full = DcSweep.Run(netlist, "VIN", DcSweep.Linspace(0, 5, 51));
        for (int index = 1; index < full.Count; index++)
        {
            Assert.True(
                full[index].Solution!.NodeVoltages["out"] <=
                    full[index - 1].Solution!.NodeVoltages["out"] + 1e-6);
        }

        Assert.True(full[0].Solution!.NodeVoltages["out"] > 4.9);
        Assert.True(full[^1].Solution!.NodeVoltages["out"] < 0.1);
    }

    [Fact]
    public void TernaryInverterMiddleStateFollowsItsTheveninModelWhenLoaded()
    {
        // The summed output is two 100 kΩ resistors driven by the two inverter outputs,
        // so it behaves like a Thevenin source of that divider with R/2 = 50 kΩ of
        // output resistance. A 100 kΩ load must therefore divide by 2 + R/R_load = 3.
        DcNetlist netlist = DcNetlistJson.Load(ExamplePath("dc-ternary-inverter.json"))
            .WithElementValue("VIN", 2.5);
        DcSolution unloaded = netlist.Solve();
        double predicted = (unloaded.NodeVoltages["out_low"] + unloaded.NodeVoltages["out_high"]) / 3;

        DcSolution loaded = netlist.Clone()
            .AddResistor("RLOAD", "out", "gnd", 100_000)
            .Solve();

        Assert.InRange(loaded.NodeVoltages["out"], predicted * 0.95, predicted * 1.05);
        Assert.True(loaded.NodeVoltages["out"] < unloaded.NodeVoltages["out"]);
    }

    [Fact]
    public void TernaryInverterPaysStaticPowerOnlyInTheMiddleState()
    {
        DcNetlist netlist = DcNetlistJson.Load(ExamplePath("dc-ternary-inverter.json"));

        // At a rail one inverter of each pair is fully off, so the only current is the
        // summing divider's own leakage path.
        Assert.InRange(Math.Abs(netlist.WithElementValue("VIN", 0).Solve().ElementPowers["VDD"]), 0, 1e-6);
        Assert.InRange(Math.Abs(netlist.WithElementValue("VIN", 5).Solve().ElementPowers["VDD"]), 0, 1e-6);

        // In the middle state the summing divider carries 25 µA and both inverters are
        // in their own crossover region, so the rail supplies about 49.5 µA: 0.25 mW.
        double middlePower = -netlist.WithElementValue("VIN", 2.5).Solve().ElementPowers["VDD"];
        Assert.InRange(middlePower, 2e-4, 3e-4);
    }
}
