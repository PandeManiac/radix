using System.Text.Json;
using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

public sealed class ElectricalCircuitTests
{
    [Fact]
    public void AuthoredDividerBuildsAndSolvesWithExplicitTerminals()
    {
        ElectricalCircuit circuit = CreateDivider();

        DcNetlist netlist = circuit.ToDcNetlist();
        DcSolution solution = netlist.Solve();

        Assert.Equal(3, netlist.Elements.Count);
        Assert.Equal(10, solution.NodeVoltages["in"]);
        Assert.Equal(5, solution.NodeVoltages["out"]);
        Assert.Equal(5e-3, solution.ElementCurrents["R1"]);
        Assert.Equal(-5e-3, solution.ElementCurrents["V1"]);
        Assert.Equal(ElectricalComponentKind.Resistor, circuit.FindComponent("R1")!.Kind);
        Assert.Equal("ohm", circuit.FindComponent("R1")!.Unit);
        Assert.Equal("a", circuit.FindComponent("R1")!.Terminals[0].Id);
        Assert.Equal("in", circuit.FindComponent("R1")!.Terminals[0].NodeId);
    }

    [Fact]
    public void ComponentMetadataDefinesTerminalPolarityAndUnits()
    {
        Assert.Equal(
            ["a", "b"],
            ElectricalComponentMetadata.Terminals(ElectricalComponentKind.Resistor)
                .Select(terminal => terminal.Id));
        Assert.Equal(
            ElectricalTerminalPolarity.Positive,
            ElectricalComponentMetadata.Terminals(ElectricalComponentKind.VoltageSource)[0].Polarity);
        Assert.Equal("Resistance", ElectricalComponentMetadata.ParameterName(ElectricalComponentKind.Resistor));
        Assert.Equal("V", ElectricalComponentMetadata.Unit(ElectricalComponentKind.VoltageSource));
    }

    [Fact]
    public void ProjectRoundTripPreservesAuthoredElectricalCircuit()
    {
        CircuitProject project = CreateProject();
        project.ElectricalCircuits.Add(CreateDivider());

        string json = ProjectSerializer.SaveToString(project);
        Assert.Contains("\"electricalCircuits\"", json, StringComparison.Ordinal);

        CircuitProject loaded = ProjectSerializer.LoadFromString(json, validate: false);
        ElectricalCircuit circuit = Assert.Single(loaded.ElectricalCircuits);

        Assert.Equal(10, loaded.FormatVersion);
        Assert.Equal("divider", circuit.Id);
        Assert.Equal(5, circuit.ToDcNetlist().Solve().NodeVoltages["out"]);
        Assert.Equal(ElectricalComponentKind.VoltageSource, loaded.ElectricalCircuits[0].Components[0].Kind);
    }

    [Fact]
    public void MissingTerminalAndUnknownNodeAreValidationErrors()
    {
        var missing = new ElectricalCircuit
        {
            Id = "missing",
            Name = "Missing terminal",
            GroundNodeId = "gnd",
        };
        missing.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        var source = new ElectricalComponent
        {
            Id = "I1",
            Kind = ElectricalComponentKind.CurrentSource,
            Value = 1e-3,
        };
        source.Terminals.Add(new ElectricalTerminal
        {
            Id = "a",
            Name = "Positive",
            Polarity = ElectricalTerminalPolarity.Positive,
            NodeId = "gnd",
        });
        missing.Components.Add(source);

        DiagnosticBag diagnostics = ElectricalCircuitValidator.Validate(missing);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "RAD-ELEC-008");
        Assert.Throws<ElectricalCircuitValidationException>(() => missing.ToDcNetlist());

        ElectricalCircuit unknownNode = CreateDivider();
        unknownNode.FindComponent("R1")!.FindTerminal("b")!.NodeId = "missing";
        DiagnosticBag unknownDiagnostics = ElectricalCircuitValidator.Validate(unknownNode);
        Assert.Contains(unknownDiagnostics, diagnostic => diagnostic.Code == "RAD-ELEC-011");
    }

    [Fact]
    public void InvalidValueSelfLoopAndUngroundedCircuitAreRejected()
    {
        ElectricalCircuit invalidValue = CreateDivider();
        invalidValue.FindComponent("R1")!.Value = 0;
        Assert.Contains(
            ElectricalCircuitValidator.Validate(invalidValue),
            diagnostic => diagnostic.Code == "RAD-ELEC-007");

        ElectricalCircuit selfLoop = CreateDivider();
        selfLoop.FindComponent("R1")!.FindTerminal("b")!.NodeId = "in";
        Assert.Contains(
            ElectricalCircuitValidator.Validate(selfLoop),
            diagnostic => diagnostic.Code == "RAD-ELEC-013");

        var ungrounded = new ElectricalCircuit
        {
            Id = "ungrounded",
            Name = "Ungrounded",
            GroundNodeId = "gnd",
        };
        ungrounded.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        ungrounded.Nodes.Add(new ElectricalNode { Id = "a", Name = "A" });
        ungrounded.Nodes.Add(new ElectricalNode { Id = "b", Name = "B" });
        ungrounded.Components.Add(Component(
            "R1",
            ElectricalComponentKind.Resistor,
            1_000,
            "a",
            "b"));
        Assert.Contains(
            ElectricalCircuitValidator.Validate(ungrounded),
            diagnostic => diagnostic.Code == "RAD-ELEC-014");
    }

    [Fact]
    public void SerializedReportIncludesCircuitIdentity()
    {
        DcNetlist netlist = CreateDivider().ToDcNetlist();
        DcSolution solution = netlist.Solve();

        string json = DcNetlistJson.ToReportJson("examples/dc-divider.radix", netlist, solution, "divider");
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("divider", document.RootElement.GetProperty("circuit").GetString());
    }

    [Fact]
    public void ReactiveKindsExposePassiveTerminalsUnitsAndState()
    {
        Assert.True(ElectricalComponentMetadata.HasPassiveTerminals(ElectricalComponentKind.Capacitor));
        Assert.True(ElectricalComponentMetadata.HasPassiveTerminals(ElectricalComponentKind.Inductor));
        Assert.False(ElectricalComponentMetadata.HasPassiveTerminals(ElectricalComponentKind.VoltageSource));
        Assert.True(ElectricalComponentMetadata.HasState(ElectricalComponentKind.Capacitor));
        Assert.False(ElectricalComponentMetadata.HasState(ElectricalComponentKind.Resistor));
        Assert.Equal("F", ElectricalComponentMetadata.Unit(ElectricalComponentKind.Capacitor));
        Assert.Equal("H", ElectricalComponentMetadata.Unit(ElectricalComponentKind.Inductor));
        Assert.Equal(
            "Capacitance",
            ElectricalComponentMetadata.ParameterName(ElectricalComponentKind.Capacitor));
        Assert.Equal("inductor", ElectricalComponentMetadata.KindName(ElectricalComponentKind.Inductor));
        Assert.Equal(
            ["a", "b"],
            ElectricalComponentMetadata.Terminals(ElectricalComponentKind.Capacitor)
                .Select(terminal => terminal.Id));
    }

    [Fact]
    public void DcBridgeOpensCapacitorsAndShortsInductors()
    {
        ElectricalCircuit capacitive = CreateDivider();
        ElectricalComponent capacitor = capacitive.FindComponent("R2")!;
        capacitor.Kind = ElectricalComponentKind.Capacitor;
        capacitor.Value = 1e-6;

        DcNetlist netlist = capacitive.ToDcNetlist();
        Assert.Equal(2, netlist.Elements.Count);
        Assert.Equal(10, netlist.Solve().NodeVoltages["out"]);

        ElectricalCircuit inductive = CreateDivider();
        ElectricalComponent inductor = inductive.FindComponent("R2")!;
        inductor.Kind = ElectricalComponentKind.Inductor;
        inductor.Value = 0.01;

        DcSolution inductiveSolution = inductive.ToDcNetlist().Solve();
        Assert.Equal(0, inductiveSolution.NodeVoltages["out"]);
        Assert.Equal(0.01, inductiveSolution.ElementCurrents["R2"]);
    }

    [Fact]
    public void TransientBridgeRunsAnAuthoredRcDischargeAgainstTheClosedFormRecurrence()
    {
        var circuit = new ElectricalCircuit { Id = "rc", Name = "RC discharge", GroundNodeId = "gnd" };
        circuit.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Add(Component("R1", ElectricalComponentKind.Resistor, 1_000, "out", "gnd"));
        ElectricalComponent capacitor = Component(
            "C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = 5;
        circuit.Components.Add(capacitor);

        var options = new TransientOptions { StopTime = 1e-3, Steps = 50 };
        TransientResult result = circuit.ToTransientNetlist().Run(options);

        Assert.True(result.Completed, result.Message);
        Assert.Equal(5, result.InitialPoint!.NodeVoltages["out"]);
        double expected = 5;
        double h = options.TimeStep;
        for (int step = 1; step <= options.Steps; step++)
        {
            expected /= 1 + (h / 1e-3);
            Assert.Equal(expected, result.Points[step].NodeVoltages["out"], 9);
        }
    }

    [Fact]
    public void TransientBridgeSettlesToTheDcOperatingPoint()
    {
        ElectricalCircuit circuit = CreateDivider();
        ElectricalComponent capacitor = Component(
            "C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = 0;
        circuit.Components.Add(capacitor);

        TransientResult result = circuit.ToTransientNetlist().Run(new TransientOptions
        {
            StopTime = 0.05,
            Method = TransientMethod.Trapezoidal,
            AdaptiveTolerance = 1e-7,
        });

        Assert.True(result.Completed, result.Message);
        double dc = circuit.ToDcNetlist().Solve().NodeVoltages["out"];
        Assert.Equal(dc, result.FinalPoint!.NodeVoltages["out"], 6);
    }

    [Fact]
    public void ValidationRejectsInitialValuesOutsideReactiveKinds()
    {
        ElectricalCircuit resistor = CreateDivider();
        resistor.FindComponent("R1")!.InitialValue = 1;
        Assert.Contains(
            ElectricalCircuitValidator.Validate(resistor),
            diagnostic => diagnostic.Code == "RAD-ELEC-015");

        ElectricalCircuit nonFinite = CreateDivider();
        ElectricalComponent capacitor = Component(
            "C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = double.NaN;
        nonFinite.Components.Add(capacitor);
        Assert.Contains(
            ElectricalCircuitValidator.Validate(nonFinite),
            diagnostic => diagnostic.Code == "RAD-ELEC-016");

        ElectricalCircuit zero = CreateDivider();
        zero.Components.Add(Component("C1", ElectricalComponentKind.Capacitor, 0, "out", "gnd"));
        Assert.Contains(
            ElectricalCircuitValidator.Validate(zero),
            diagnostic => diagnostic.Code == "RAD-ELEC-007");
    }

    [Fact]
    public void ProjectRoundTripPreservesReactiveComponentsAndInitialValues()
    {
        CircuitProject project = CreateProject();
        ElectricalCircuit circuit = CreateDivider();
        circuit.Components.Add(Component("L1", ElectricalComponentKind.Inductor, 0.01, "out", "gnd"));
        circuit.Components.Add(Component("C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd"));
        circuit.FindComponent("C1")!.InitialValue = 2.5;
        circuit.FindComponent("L1")!.InitialValue = 0.004;
        project.ElectricalCircuits.Add(circuit);

        string json = ProjectSerializer.SaveToString(project);
        CircuitProject loaded = ProjectSerializer.LoadFromString(json, validate: false);
        ElectricalCircuit reloaded = Assert.Single(loaded.ElectricalCircuits);

        Assert.Equal(10, loaded.FormatVersion);
        Assert.Equal(ElectricalComponentKind.Capacitor, reloaded.FindComponent("C1")!.Kind);
        Assert.Equal(1e-6, reloaded.FindComponent("C1")!.Value);
        Assert.Equal(2.5, reloaded.FindComponent("C1")!.InitialValue);
        Assert.Equal(ElectricalComponentKind.Inductor, reloaded.FindComponent("L1")!.Kind);
        Assert.Equal(0.004, reloaded.FindComponent("L1")!.InitialValue);
        Assert.True(reloaded.HasReactiveComponents);

        TransientResult result = reloaded.ToTransientNetlist().Run(new TransientOptions
        {
            StopTime = 1e-4,
            Steps = 10,
        });
        Assert.True(result.Completed, result.Message);
    }

    [Fact]
    public void AuthoredWaveformsDriveSourcesThroughTheTransientBridge()
    {
        var circuit = new ElectricalCircuit { Id = "step", Name = "RC step", GroundNodeId = "gnd" };
        circuit.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        ElectricalComponent source = Component(
            "V1", ElectricalComponentKind.VoltageSource, 5, "in", "gnd");
        // Breakpoint times must strictly increase, so a near-ideal step uses two
        // breakpoints one microsecond apart instead of a repeated time.
        source.Waveform.Add(new ElectricalWaveformPoint { Time = 0, Value = 0 });
        source.Waveform.Add(new ElectricalWaveformPoint { Time = 0.999e-3, Value = 0 });
        source.Waveform.Add(new ElectricalWaveformPoint { Time = 1e-3, Value = 5 });
        circuit.Components.Add(source);
        circuit.Components.Add(Component("R1", ElectricalComponentKind.Resistor, 1_000, "in", "out"));
        ElectricalComponent capacitor = Component(
            "C1", ElectricalComponentKind.Capacitor, 1e-6, "out", "gnd");
        capacitor.InitialValue = 0;
        circuit.Components.Add(capacitor);

        TransientResult result = circuit.ToTransientNetlist().Run(new TransientOptions
        {
            StopTime = 3e-3,
            Steps = 3_000,
            Method = TransientMethod.Trapezoidal,
        });

        Assert.True(result.Completed, result.Message);
        Assert.Equal(0, result.Points[500].NodeVoltages["out"], 12);
        Assert.True(
            Math.Abs(result.Points[1_000].NodeVoltages["out"]) < 0.01,
            $"the capacitor has barely charged when the step ends: {result.Points[1_000].NodeVoltages["out"]} V");
        // The 1 us ramp injects slightly more charge than an ideal step, so the bound
        // allows 2 mV (4e-4 of the 5 V drive) on top of the trapezoidal error.
        double expected = 5 * (1 - Math.Exp(-1));
        double measured = result.Points[2_000].NodeVoltages["out"];
        Assert.True(
            Math.Abs(measured - expected) < 2e-3,
            $"one time constant after the step: {measured} V, expected {expected} V");
        Assert.True(result.Points[2_500].NodeVoltages["out"] > measured);
    }

    [Fact]
    public void ValidationRejectsMalformedAuthoredWaveforms()
    {
        ElectricalCircuit passive = CreateDivider();
        ElectricalComponent resistor = passive.FindComponent("R1")!;
        resistor.Waveform.Add(new ElectricalWaveformPoint { Time = 0, Value = 1 });
        resistor.Waveform.Add(new ElectricalWaveformPoint { Time = 1, Value = 0 });
        Assert.Contains("RAD-ELEC-017", ValidateCodes(passive));

        ElectricalCircuit single = CreateDivider();
        single.FindComponent("V1")!.Waveform.Add(new ElectricalWaveformPoint { Time = 0, Value = 0 });
        Assert.Contains("RAD-ELEC-018", ValidateCodes(single));

        ElectricalCircuit repeated = CreateDivider();
        ElectricalComponent repeatedSource = repeated.FindComponent("V1")!;
        repeatedSource.Waveform.Add(new ElectricalWaveformPoint { Time = 0, Value = 0 });
        repeatedSource.Waveform.Add(new ElectricalWaveformPoint { Time = 0, Value = 5 });
        Assert.Contains("RAD-ELEC-020", ValidateCodes(repeated));

        ElectricalCircuit nonFinite = CreateDivider();
        ElectricalComponent infinite = nonFinite.FindComponent("V1")!;
        infinite.Waveform.Add(new ElectricalWaveformPoint { Time = 0, Value = 0 });
        infinite.Waveform.Add(new ElectricalWaveformPoint
        {
            Time = 1e-3,
            Value = double.PositiveInfinity,
        });
        Assert.Contains("RAD-ELEC-019", ValidateCodes(nonFinite));
    }

    [Fact]
    public void ProjectRoundTripPreservesAuthoredWaveforms()
    {
        CircuitProject project = CreateProject();
        ElectricalCircuit circuit = CreateDivider();
        ElectricalComponent source = circuit.FindComponent("V1")!;
        source.Waveform.Add(new ElectricalWaveformPoint { Time = 0, Value = 0 });
        source.Waveform.Add(new ElectricalWaveformPoint { Time = 2e-3, Value = 10 });
        project.ElectricalCircuits.Add(circuit);

        CircuitProject loaded = ProjectSerializer.LoadFromString(
            ProjectSerializer.SaveToString(project),
            validate: false);

        ElectricalComponent reloaded = Assert.Single(loaded.ElectricalCircuits).FindComponent("V1")!;
        Assert.Equal(10, loaded.FormatVersion);
        Assert.Equal([0.0, 2e-3], reloaded.Waveform.Select(point => point.Time));
        Assert.Equal([0.0, 10.0], reloaded.Waveform.Select(point => point.Value));
        Assert.True(reloaded.Waveform.Count == 2);
    }

    private static string[] ValidateCodes(ElectricalCircuit circuit) =>
        ElectricalCircuitValidator.Validate(circuit)
            .Select(diagnostic => diagnostic.Code)
            .ToArray();

    private static CircuitProject CreateProject()
    {
        CircuitProject project = CircuitProject.CreateEmpty("DC project");
        project.Definitions.Add(new CircuitDefinition { Id = "main", Name = "Main" });
        project.RootDefinitionId = "main";
        return project;
    }

    private static ElectricalCircuit CreateDivider()
    {
        var circuit = new ElectricalCircuit
        {
            Id = "divider",
            Name = "Voltage divider",
            GroundNodeId = "gnd",
        };
        circuit.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
        circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
        circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
        circuit.Components.Add(Component(
            "V1",
            ElectricalComponentKind.VoltageSource,
            10,
            "in",
            "gnd"));
        circuit.Components.Add(Component(
            "R1",
            ElectricalComponentKind.Resistor,
            1_000,
            "in",
            "out"));
        circuit.Components.Add(Component(
            "R2",
            ElectricalComponentKind.Resistor,
            1_000,
            "out",
            "gnd"));
        return circuit;
    }

    private static ElectricalComponent Component(
        string id,
        ElectricalComponentKind kind,
        double value,
        string nodeA,
        string nodeB)
    {
        var component = new ElectricalComponent
        {
            Id = id,
            Name = id,
            Kind = kind,
            Value = value,
        };
        bool source = !ElectricalComponentMetadata.HasPassiveTerminals(kind);
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "a",
            Name = source ? "Positive" : "A",
            Polarity = source ? ElectricalTerminalPolarity.Positive : ElectricalTerminalPolarity.Passive,
            NodeId = nodeA,
        });
        component.Terminals.Add(new ElectricalTerminal
        {
            Id = "b",
            Name = source ? "Negative" : "B",
            Polarity = source ? ElectricalTerminalPolarity.Negative : ElectricalTerminalPolarity.Passive,
            NodeId = nodeB,
        });
        return component;
    }
}
