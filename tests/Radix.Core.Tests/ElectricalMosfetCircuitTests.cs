using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The authored-model path for a MOSFET: it must validate, map onto the same netlist the
/// JSON fixtures use, survive a project save/load round trip, and produce the same
/// operating points. These tests build circuits through the model API, not through the
/// netlist API, so a gap between the two cannot hide.
/// </summary>
public sealed class ElectricalMosfetCircuitTests
{
    private static ElectricalComponent Source(string id, string positive, string negative, double volts) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = ElectricalComponentKind.VoltageSource,
            Value = volts,
            Terminals =
            {
                new ElectricalTerminal
                {
                    Id = "a",
                    Name = "Positive",
                    Polarity = ElectricalTerminalPolarity.Positive,
                    NodeId = positive,
                },
                new ElectricalTerminal
                {
                    Id = "b",
                    Name = "Negative",
                    Polarity = ElectricalTerminalPolarity.Negative,
                    NodeId = negative,
                },
            },
        };

    private static ElectricalComponent Resistor(string id, string nodeA, string nodeB, double ohms) =>
        new()
        {
            Id = id,
            Kind = ElectricalComponentKind.Resistor,
            Value = ohms,
            Terminals =
            {
                new ElectricalTerminal
                {
                    Id = "a",
                    Polarity = ElectricalTerminalPolarity.Passive,
                    NodeId = nodeA,
                },
                new ElectricalTerminal
                {
                    Id = "b",
                    Polarity = ElectricalTerminalPolarity.Passive,
                    NodeId = nodeB,
                },
            },
        };

    private static ElectricalComponent Mosfet(
        string id,
        string drain,
        string gate,
        string source,
        DcMosfetChannel channel,
        double threshold,
        double transconductance) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = ElectricalComponentKind.Mosfet,
            Value = transconductance,
            Channel = channel,
            ThresholdVoltage = threshold,
            Terminals =
            {
                new ElectricalTerminal
                {
                    Id = "drain",
                    Name = "Drain",
                    Polarity = ElectricalTerminalPolarity.Passive,
                    NodeId = drain,
                },
                new ElectricalTerminal
                {
                    Id = "gate",
                    Name = "Gate",
                    Polarity = ElectricalTerminalPolarity.Passive,
                    NodeId = gate,
                },
                new ElectricalTerminal
                {
                    Id = "source",
                    Name = "Source",
                    Polarity = ElectricalTerminalPolarity.Passive,
                    NodeId = source,
                },
            },
        };

    private static ElectricalCircuit Circuit(params string[] nodes)
    {
        var circuit = new ElectricalCircuit
        {
            Id = "authored",
            Name = "Authored circuit",
            GroundNodeId = "gnd",
        };
        foreach (string node in nodes)
        {
            circuit.Nodes.Add(new ElectricalNode { Id = node, Name = node });
        }

        return circuit;
    }

    private static ElectricalCircuit CmosInverter()
    {
        ElectricalCircuit circuit = Circuit("gnd", "vdd", "in", "out");
        circuit.Components.Add(Source("VDD", "vdd", "gnd", 5));
        circuit.Components.Add(Source("VIN", "in", "gnd", 0));
        circuit.Components.Add(Mosfet("MP", "out", "in", "vdd", DcMosfetChannel.PChannel, 1, 1e-3));
        circuit.Components.Add(Mosfet("MN", "out", "in", "gnd", DcMosfetChannel.NChannel, 1, 1e-3));
        return circuit;
    }

    /// <summary>
    /// The resistor-summed dual-threshold ternary inverter, authored in the model and
    /// solved through the same bridge as the JSON fixture. This is the proof that the
    /// ternary behaviour is reachable from the editor's data model.
    /// </summary>
    private static ElectricalCircuit TernaryInverter()
    {
        ElectricalCircuit circuit = Circuit("gnd", "vdd", "in", "out", "out_low", "out_high");
        circuit.Components.Add(Source("VDD", "vdd", "gnd", 5));
        circuit.Components.Add(Source("VIN", "in", "gnd", 0));
        circuit.Components.Add(Mosfet("MPL", "out_low", "in", "vdd", DcMosfetChannel.PChannel, 1, 1e-5));
        circuit.Components.Add(Mosfet("MNL", "out_low", "in", "gnd", DcMosfetChannel.NChannel, 1, 1.21e-3));
        circuit.Components.Add(Mosfet("MPH", "out_high", "in", "vdd", DcMosfetChannel.PChannel, 1, 1.21e-3));
        circuit.Components.Add(Mosfet("MNH", "out_high", "in", "gnd", DcMosfetChannel.NChannel, 1, 1e-5));
        circuit.Components.Add(Resistor("RSUM1", "out_low", "out", 100_000));
        circuit.Components.Add(Resistor("RSUM2", "out_high", "out", 100_000));
        return circuit;
    }

    private static double Output(ElectricalCircuit circuit, double inputVolts)
    {
        circuit.FindComponent("VIN")!.Value = inputVolts;
        return circuit.ToDcNetlist().Solve().NodeVoltages["out"];
    }

    [Fact]
    public void MetadataDescribesTheThreeTerminalMosfet()
    {
        Assert.Equal(
            ["drain", "gate", "source"],
            ElectricalComponentMetadata.Terminals(ElectricalComponentKind.Mosfet).Select(t => t.Id));
        Assert.Equal("Transconductance", ElectricalComponentMetadata.ParameterName(ElectricalComponentKind.Mosfet));
        Assert.Equal("A/V^2", ElectricalComponentMetadata.Unit(ElectricalComponentKind.Mosfet));
        Assert.Equal("mosfet", ElectricalComponentMetadata.KindName(ElectricalComponentKind.Mosfet));
        Assert.True(ElectricalComponentMetadata.RequiresPositiveValue(ElectricalComponentKind.Mosfet));
        Assert.False(ElectricalComponentMetadata.HasState(ElectricalComponentKind.Mosfet));
        Assert.False(ElectricalComponentMetadata.SupportsWaveform(ElectricalComponentKind.Mosfet));
        Assert.Equal("p", ElectricalComponentMetadata.ChannelName(DcMosfetChannel.PChannel));
        Assert.Equal(DcMosfetChannel.NChannel, ElectricalComponentMetadata.ParseChannel("N"));
    }

    [Fact]
    public void AuthoredCmosInverterValidatesAndSolves()
    {
        ElectricalCircuit circuit = CmosInverter();
        DiagnosticBag diagnostics = ElectricalCircuitValidator.Validate(circuit);
        Assert.False(diagnostics.HasErrors);

        Assert.True(Output(circuit, 0) > 4.99);
        Assert.Equal(2.5, Output(circuit, 2.5), 3);
        Assert.True(Output(circuit, 5) < 1e-6);
    }

    [Fact]
    public void AuthoredTernaryInverterReproducesTheNetlistResult()
    {
        ElectricalCircuit circuit = TernaryInverter();
        Assert.False(ElectricalCircuitValidator.Validate(circuit).HasErrors);

        Assert.Equal(5, Output(circuit, 0), 3);
        Assert.Equal(2.5, Output(circuit, 2.5), 6);
        Assert.True(Output(circuit, 5) < 1e-5);

        // The same model also reaches the transient solver, where the summing network
        // settles the middle level from a capacitor.
        circuit.FindComponent("VIN")!.Value = 2.5;
        TransientNetlist transient = circuit.ToTransientNetlist();
        Assert.Contains(transient.Elements, element => element is DcMosfet);
    }

    [Fact]
    public void MosfetMustBindThreeDistinctTerminals()
    {
        ElectricalCircuit missingGate = CmosInverter();
        missingGate.FindComponent("MN")!.Terminals.RemoveAll(
            terminal => string.Equals(terminal.Id, "gate", StringComparison.Ordinal));
        DiagnosticBag diagnostics = ElectricalCircuitValidator.Validate(missingGate);
        Assert.Contains(diagnostics, d => d.Code == "RAD-ELEC-008");

        ElectricalCircuit shortedChannel = CmosInverter();
        shortedChannel.FindComponent("MN")!.FindTerminal("source")!.NodeId = "out";
        Assert.Contains(
            ElectricalCircuitValidator.Validate(shortedChannel),
            d => d.Code == "RAD-ELEC-013");
    }

    [Fact]
    public void MosfetGateMayTieToItsDrainOrSource()
    {
        // A diode-connected device and a Vgs = 0 device are normal circuits, so the
        // self-loop rule applies only to the channel pair.
        ElectricalCircuit circuit = CmosInverter();
        ElectricalComponent mosfet = circuit.FindComponent("MN")!;
        mosfet.FindTerminal("gate")!.NodeId = "out";
        Assert.DoesNotContain(
            ElectricalCircuitValidator.Validate(circuit),
            d => d.Code == "RAD-ELEC-013");

        mosfet.FindTerminal("gate")!.NodeId = "gnd";
        Assert.DoesNotContain(
            ElectricalCircuitValidator.Validate(circuit),
            d => d.Code == "RAD-ELEC-013");
    }

    [Fact]
    public void MosfetParametersAreValidated()
    {
        ElectricalCircuit zeroGain = CmosInverter();
        zeroGain.FindComponent("MN")!.Value = 0;
        Assert.Contains(
            ElectricalCircuitValidator.Validate(zeroGain),
            d => d.Code == "RAD-ELEC-007");

        ElectricalCircuit badThreshold = CmosInverter();
        badThreshold.FindComponent("MN")!.ThresholdVoltage = double.NaN;
        Assert.Contains(
            ElectricalCircuitValidator.Validate(badThreshold),
            d => d.Code == "RAD-ELEC-021");

        ElectricalCircuit badChannel = CmosInverter();
        badChannel.FindComponent("MN")!.Channel = (DcMosfetChannel)9;
        Assert.Contains(
            ElectricalCircuitValidator.Validate(badChannel),
            d => d.Code == "RAD-ELEC-022");

        // Lambda is a physical slope: zero means no channel-length modulation. A negative or
        // non-finite value would describe a device that is not a transistor, so both are
        // refused before a solve instead of being handed to Newton.
        ElectricalCircuit negativeLambda = CmosInverter();
        negativeLambda.FindComponent("MN")!.ChannelLengthModulation = -0.1;
        Assert.Contains(
            ElectricalCircuitValidator.Validate(negativeLambda),
            d => d.Code == "RAD-ELEC-023");

        ElectricalCircuit nonFiniteLambda = CmosInverter();
        nonFiniteLambda.FindComponent("MP")!.ChannelLengthModulation = double.PositiveInfinity;
        Assert.Contains(
            ElectricalCircuitValidator.Validate(nonFiniteLambda),
            d => d.Code == "RAD-ELEC-023");

        // Zero itself is the documented default and must stay valid.
        Assert.DoesNotContain(
            ElectricalCircuitValidator.Validate(CmosInverter()),
            d => d.Code == "RAD-ELEC-023");
    }

    [Fact]
    public void ThreeTerminalNetlistBridgeRejectsAnInvalidDevice()
    {
        // The bridge refuses to hand the solver a device it would have to guess about;
        // validation runs before any netlist is built.
        ElectricalCircuit circuit = CmosInverter();
        circuit.FindComponent("MN")!.Value = -1;
        Assert.Throws<ElectricalCircuitValidationException>(() => circuit.ToDcNetlist());
        Assert.Throws<ElectricalCircuitValidationException>(() => circuit.ToTransientNetlist());
    }

    [Fact]
    public void MosfetProjectRoundTripsThroughJson()
    {
        var project = CircuitProject.CreateEmpty("MOSFET round trip");
        var root = new CircuitDefinition { Id = "main", Name = "Main" };
        project.Definitions.Add(root);
        project.RootDefinitionId = root.Id;

        ElectricalCircuit circuit = CmosInverter();
        circuit.FindComponent("MP")!.ThresholdVoltage = 1.4;
        project.ElectricalCircuits.Add(circuit);

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radix");
        try
        {
            ProjectSerializer.Save(path, project);
            string json = File.ReadAllText(path);
            Assert.Contains("\"channel\": \"p\"", json, StringComparison.Ordinal);
            Assert.Contains("\"thresholdVoltage\": 1.4", json, StringComparison.Ordinal);
            Assert.Contains($"\"version\": {CircuitProject.CurrentFormatVersion}", json, StringComparison.Ordinal);

            CircuitProject reloaded = ProjectSerializer.Load(path);
            ElectricalComponent mosfet = reloaded.ElectricalCircuits[0].FindComponent("MP")!;
            Assert.Equal(ElectricalComponentKind.Mosfet, mosfet.Kind);
            Assert.Equal(DcMosfetChannel.PChannel, mosfet.Channel);
            Assert.Equal(1.4, mosfet.ThresholdVoltage);
            Assert.Equal(1e-3, mosfet.Value);
            Assert.Equal(3, mosfet.Terminals.Count);
            Assert.False(ElectricalCircuitValidator.Validate(reloaded.ElectricalCircuits[0]).HasErrors);

            // The reloaded project solves to the same operating point as the original,
            // including the edited threshold, which moves the switching point to 2.3 V.
            Assert.Equal(
                Output(circuit, 2.5),
                Output(reloaded.ElectricalCircuits[0], 2.5),
                12);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProjectJsonRejectsMosfetFieldsOnOtherKindsAndMissingChannel()
    {
        static string Project(string component) => $$"""
            {
              "format": "radix-project",
              "version": {{CircuitProject.CurrentFormatVersion}},
              "name": "mosfet json",
              "root": "main",
              "domains": [ { "id": "binary", "name": "Binary", "values": ["0", "1"], "drivers": { "kind": "single" } } ],
              "definitions": [ { "id": "main", "name": "Main", "pins": [], "instances": [], "nets": [], "ports": [] } ],
              "electricalCircuits": [
                {
                  "id": "c1",
                  "ground": "gnd",
                  "nodes": [ { "id": "gnd" }, { "id": "n1" } ],
                  "components": [ {{component}} ]
                }
              ]
            }
            """;

        // A resistor cannot carry a MOSFET parameter.
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString(Project(
            """{ "id": "R1", "kind": "resistor", "value": 1000, "thresholdVoltage": 1, "terminals": [ { "id": "a", "polarity": "passive", "node": "gnd" }, { "id": "b", "polarity": "passive", "node": "n1" } ] }""")));

        // A MOSFET must say which channel it is.
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString(Project(
            """{ "id": "M1", "kind": "mosfet", "value": 0.001, "thresholdVoltage": 1, "terminals": [ { "id": "drain", "polarity": "passive", "node": "n1" }, { "id": "gate", "polarity": "passive", "node": "gnd" }, { "id": "source", "polarity": "passive", "node": "gnd" } ] }""")));

        // And it must give its threshold.
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString(Project(
            """{ "id": "M1", "kind": "mosfet", "value": 0.001, "channel": "n", "terminals": [ { "id": "drain", "polarity": "passive", "node": "n1" }, { "id": "gate", "polarity": "passive", "node": "gnd" }, { "id": "source", "polarity": "passive", "node": "gnd" } ] }""")));

        // A complete one loads.
        CircuitProject loaded = ProjectSerializer.LoadFromString(Project(
            """{ "id": "M1", "kind": "mosfet", "value": 0.001, "channel": "n", "thresholdVoltage": 1, "terminals": [ { "id": "drain", "polarity": "passive", "node": "n1" }, { "id": "gate", "polarity": "passive", "node": "gnd" }, { "id": "source", "polarity": "passive", "node": "gnd" } ] }"""));
        Assert.Equal(DcMosfetChannel.NChannel, loaded.ElectricalCircuits[0].FindComponent("M1")!.Channel);
    }

    [Fact]
    public void DeviceSweptTransconductanceChangesTheAuthoredCircuit()
    {
        // A parameter sweep reaches an authored device through the same bridge, so the
        // editor's value and `radix dc --sweep` describe one device.
        ElectricalCircuit circuit = CmosInverter();
        // At mid-input both devices conduct, so the n-channel strength is what moves the
        // output; at 0 V or 5 V the swept device is off and the curve would not move.
        circuit.FindComponent("VIN")!.Value = 2.5;
        DcNetlist netlist = circuit.ToDcNetlist();
        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(netlist, "MN", [1e-3, 2e-3, 4e-3]);

        Assert.All(rows, row => Assert.True(row.Solved));
        Assert.True(
            rows[0].Solution!.NodeVoltages["out"] > rows[^1].Solution!.NodeVoltages["out"]);
    }
}
