# DC electrical solver

RADIX has a separate core DC linear solver for the first, deliberately narrow
electrical-analysis layer:

- ideal resistors with finite positive resistance;
- ideal independent voltage sources;
- ideal independent current sources;
- one explicit ground reference.

It is **not** connected to the digital event engine or the ERC checker.
Authored electrical circuits are stored as separate `.radix` project assets;
they never reuse digital nets or interpret signal-domain indices as volts. The
editor's **Electrical** tab browses and edits these assets, including their
explicit terminals, and can solve the selected circuit on demand through the
same `DcNetlist` bridge. The displayed operating point can be copied as JSON or
CSV for use in a notebook, spreadsheet, or test fixture. It does not run
time-domain simulation. The solver is
an independent netlist API under `Radix.Core.Electrical`, exposed through the
headless `radix dc` command, with analytical tests rather than a claim that
existing digital designs now have analog behavior.

## Netlist API

```csharp
using Radix.Core.Electrical;

DcSolution solution = new DcNetlist("gnd")
    .AddVoltageSource("V1", "in", "gnd", 10)
    .AddResistor("R1", "in", "out", 1_000)
    .AddResistor("R2", "out", "gnd", 1_000)
    .Solve();

double outputVolts = solution.NodeVoltages["out"]; // 5 V
double r1Current = solution.ElementCurrents["R1"]; // 5 mA, in -> out
double sourcePower = solution.ElementPowers["V1"]; // -50 mW (delivered)
```

`DcSolution` returns node voltages, element currents, element voltages, and
absorbed element power. All dictionaries are keyed by the stable IDs supplied to
the netlist. Ground is present in `NodeVoltages` with value zero.

## Authored electrical circuits

Projects may contain one or more `electricalCircuits` (project format v8). Each
circuit owns its own nodes and components:

```csharp
var circuit = new ElectricalCircuit
{
    Id = "divider",
    Name = "Voltage divider",
    GroundNodeId = "gnd",
};
circuit.Nodes.Add(new ElectricalNode { Id = "gnd", Name = "Ground" });
circuit.Nodes.Add(new ElectricalNode { Id = "in", Name = "Input" });
circuit.Nodes.Add(new ElectricalNode { Id = "out", Name = "Output" });
circuit.Components.Add(new ElectricalComponent
{
    Id = "R1",
    Kind = ElectricalComponentKind.Resistor,
    Value = 1_000,
    Terminals =
    {
        new ElectricalTerminal { Id = "a", Polarity = ElectricalTerminalPolarity.Passive, NodeId = "in" },
        new ElectricalTerminal { Id = "b", Polarity = ElectricalTerminalPolarity.Passive, NodeId = "out" },
    },
});

DcSolution solution = circuit.ToDcNetlist().Solve();
```

`ElectricalComponentMetadata` defines the reusable terminal shape, parameter
name, and unit for each kind:

| Kind | Terminals | Parameter | Unit |
| --- | --- | --- | --- |
| `resistor` | `a`/`b`, passive | resistance | ohm |
| `voltage-source` | `a` positive, `b` negative | voltage | V |
| `current-source` | `a` positive, `b` negative | current | A |

Terminals are explicit bindings to circuit node IDs. The validator checks
missing, duplicate, unknown, misplaced, and self-looped terminals; invalid
values; missing grounds; and ungrounded circuits with stable `RAD-ELEC-*` codes.
This validation is separate from digital project validation, so an electrical
error does not silently change digital compile behavior. `ToDcNetlist` refuses to
run when validation has errors.

Use the authored example:

```bash
radix dc examples/dc-divider.radix
radix dc examples/dc-divider.radix --json
radix dc examples/dc-divider.radix --circuit divider
```

If a project has exactly one electrical circuit, `--circuit` may be omitted. A
project with several circuits requires it. Standalone JSON netlists remain
supported for tools that do not author a project.

## Editor result export

After **Solve**, **Copy JSON** and **Copy CSV** place the displayed result on the
system clipboard, while **Save JSON…** and **Save CSV…** write it to a chosen
file. All four actions use the displayed result; they never re-run the solver,
mutate the project, or change the digital simulation. Editing the circuit clears
the preview and disables the actions. If a solve fails, there is no result to
copy or save.

The JSON document has `format: "radix-dc-solve"`, `version: 1`, the circuit ID,
name and ground, the current/voltage/power conventions, one object per authored
node, and one object per solved element. Unreferenced nodes have
`"solved": false` and a null `volts` value.

The CSV document has one header row and then node and element rows. The columns
are `circuit,type,id,name,kind,node_a,node_b,value,unit,volts,amperes,absorbed_power_watts,solved`.
Node rows leave the element fields empty; element rows use the same
`nodeA -> nodeB` sign convention as the solve preview and the headless JSON
report. Numeric fields use invariant, round-trip formatting, and text fields are
quoted with CSV escaping.

## Sign and unit conventions

- Voltages and node voltages are in volts.
- Resistance is in ohms; currents are in amperes; power is in watts.
- Every element has `NodeA` and `NodeB`. The reported element current flows
  through the element **from `NodeA` to `NodeB`**.
- A voltage source enforces `V(NodeA) - V(NodeB) = Volts`.
- A current source forces `Amperes` through the source from `NodeA` to `NodeB`.
  It therefore draws current from `NodeA` and injects it into `NodeB`.
- Element power is **absorbed** power, `V(NodeA) - V(NodeB)` times the current.
  A resistor therefore has non-negative power; a source with negative power is
  delivering energy to the rest of the network.
- The ground node is the zero-volt reference. It is excluded from the solved
  node-voltage unknowns, and at least one element must reference it. Otherwise
  the solver raises `NoGround` rather than inventing an absolute reference.

These conventions are exercised directly by the tests, including the source
direction cases.

## Numerical method

The solver assembles the modified nodal analysis (MNA) system:

```text
[ G  B ] [ V ]   [ I ]
[ C  D ] [ J ] = [ E ]
```

- Node voltages are unknowns for every non-ground node.
- Each independent voltage source adds one branch-current unknown and one
  constraint row.
- Resistors stamp conductance into `G`.
- Current sources stamp into the right-hand side.
- Voltage sources stamp their incidence pattern and voltage into `B`, `C`, and
  `E`.

The dense system is solved by Gaussian elimination with partial pivoting. A
scale-relative pivot tolerance detects singular systems. Complexity is
`O((n + m)^3)` time and `O((n + m)^2)` memory for `n` non-ground nodes and `m`
voltage sources. This is intentionally a foundation for small and medium
networks, not a sparse or large-scale SPICE engine.

## Failure modes

- `DcSolveFailure.NoGround`: no element references the configured ground node.
- `DcSolveFailure.SingularMatrix`: no unique operating point, such as a floating
  node, an ideal current source into an open circuit, or a loop of ideal voltage
  sources.
- `DcSolveFailure.NonFiniteResult`: the numerical result is not finite.

Construction also rejects empty element IDs, duplicate element IDs, empty node
IDs, self-loop two-terminal elements, non-finite source values, and zero,
negative, or non-finite resistance. A zero-ohm resistor is not silently treated
as a short; ideal voltage-source loops are reported as singular systems.

## Supported and unsupported

Supported now:

- authoring and editing circuits, nodes, components, values, polarity, and
  explicit terminal bindings in the editor's Electrical inspector;
- on-demand DC solve preview of node voltages and each element's terminal
  voltage, current, and absorbed power, with explicit structural/singularity
  failures;
- copying the displayed solve result as a versioned JSON document or as one CSV
  table containing both node rows and element rows;
- arbitrary two-terminal wiring among named nodes;
- multiple independent voltage and current sources;
- series, parallel, bridge, divider, and multi-source linear networks;
- analytical node voltages, element currents, element voltages, and power.

Not supported:

- capacitors, inductors, transformers, or frequency-domain/AC analysis;
- transient or time-domain integration;
- diodes, LEDs, transistors, MOSFETs, regulators, relays, fuses, or switches;
- nonlinear devices, operating-point iteration, or convergence control;
- temperature, thermal limits, component tolerances, or datasheet ratings;
- wire resistance, parasitic elements, or physical layout;
- short-circuit current, overcurrent, or fuse-trip prediction;
- automatic mapping of digital `.radix` nets or domain indices to volts;
- graphical analog schematic canvas, time-domain electrical simulation, or editor instruments (the Electrical tab is a metadata/terminal inspector plus a read-only DC operating-point preview).

A digital domain index is not a voltage. Any future integration must introduce
explicit electrical terminals, units, models, and a documented digital/analog
boundary rather than inferring electrical meaning from the existing signal
engine.

## Headless JSON netlist

The CLI reads a versioned `radix-dc-netlist` document and writes either a human
report or a `radix-dc-report` JSON object:

```bash
radix dc examples/dc-divider.radix
radix dc examples/dc-divider.radix --json
radix dc examples/dc-divider.radix --circuit divider
radix dc examples/dc-divider.json
```

Input shape:

```json
{
  "format": "radix-dc-netlist",
  "version": 1,
  "ground": "gnd",
  "elements": [
    { "id": "V1", "type": "voltage-source", "nodeA": "in", "nodeB": "gnd", "volts": 10 },
    { "id": "R1", "type": "resistor", "nodeA": "in", "nodeB": "out", "ohms": 1000 },
    { "id": "R2", "type": "resistor", "nodeA": "out", "nodeB": "gnd", "ohms": 1000 }
  ]
}
```

- `format`, `version`, `ground`, and `elements` are required.
- Each element requires `id`, `type`, `nodeA`, and `nodeB`.
- `resistor` requires `ohms`; `voltage-source` requires `volts`;
  `current-source` requires `amperes`.
- Unknown fields, wrong versions, missing units, and mismatched unit properties
  are rejected. Comments and trailing commas are accepted.
- Input size is bounded to 16 MiB.

The text report prints ground, every node voltage, and every element's parameter,
terminal voltage, current, and absorbed power. The JSON report includes the same
values under stable camel-case fields:

```json
{
  "format": "radix-dc-report",
  "version": 1,
  "source": "examples/dc-divider.radix",
  "circuit": "divider",
  "solved": true,
  "ground": "gnd",
  "nodes": [ { "id": "out", "volts": 5 } ],
  "elements": [
    { "id": "R1", "kind": "resistor", "nodeA": "in", "nodeB": "out",
      "value": 1000, "unit": "ohm", "volts": 5,
      "amperes": 0.005, "absorbedPowerWatts": 0.025 }
  ]
}
```

A singular system still produces valid JSON with `solved: false`, a stable
`failure` value (`no-ground`, `singular-matrix`, or `non-finite-result`), and a
message. Malformed input exits 2 before a report is emitted; a solve failure
exits 1. Successful solves exit 0. The command never writes project or netlist
state.

## Analytical validation

`DcLinearSolverTests` checks the solver against closed-form results:

- a 10 V, 1 kΩ/1 kΩ voltage divider: 5 V, 5 mA, 25 mW per resistor, −50 mW from
  the source;
- two 1 kΩ parallel resistors: 12 mA each and 144 mW each;
- a 1 mA current source into 1 kΩ: 1 V, with the correct source-power sign;
- the reverse current-source direction: −1 V;
- two series 5 V sources: 10 V across a 1 kΩ load;
- a balanced Wheatstone bridge: equal bridge-node voltages and zero bridge
  current;
- singular ideal-current-source and parallel-ideal-voltage-source cases;
- missing ground, empty netlists, invalid values, duplicate IDs, and self-loops;
- 100 independent dividers sharing one source, checking every node, branch
  current, and the total source current.
- versioned JSON loading, strict field/unit rejection, solved reports, and
  machine-readable solve-failure reports.
