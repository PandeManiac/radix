# DC electrical solver

RADIX has a separate core DC linear solver for the first, deliberately narrow
electrical-analysis layer:

- ideal resistors with finite positive resistance;
- ideal independent voltage sources;
- ideal independent current sources;
- a two-terminal Shockley diode with optional series resistance;
- one explicit ground reference.

The solvers are **not** connected to the digital event engine: electrical
circuits are separate `.radix` project assets (or standalone JSON netlists) that
never reuse digital nets or interpret signal-domain indices as volts, and
nothing in them is compiled into the event engine. Electrical rule checking
shares the solver's topology analysis (`RAD-ERC-101`–`RAD-ERC-103`, see
[erc.md](erc.md)). The editor's **Electrical** tab browses and edits the authored
assets, including their explicit terminals, and can solve the selected circuit
on demand through the same `DcNetlist` bridge. The displayed operating point can
be copied as JSON or CSV for use in a notebook, spreadsheet, or test fixture;
that preview is DC only.

A second path adds fixed-step time-domain analysis for capacitors and inductors
([Transient analysis](#transient-analysis)). It reuses the same MNA
factorization and the same failure classification, and is exposed through the
headless `radix transient` command with its own `radix-transient-netlist`
format. Both solvers are an independent netlist API under
`Radix.Core.Electrical`, with analytical tests rather than a claim that existing
digital designs now have analog behavior.

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

## Numerical method and failure classification

The unknown vector contains every non-ground node voltage in ordinal node-ID
order, followed by one branch current per ideal voltage source. Resistors are
stamped as conductances, current sources into the right-hand side, and voltage
sources as their modified-nodal branch rows.

The dense linear system is factorized with **scaled partial pivoting**: each
pivot candidate is compared with the largest coefficient in its own row, not
with one global matrix magnitude. This matters for real circuits with wide
parameter spreads: a 1 GΩ branch next to a 1 mΩ branch is weak, not singular. A
pivot is rejected only when it is below `1e-12` of its row scale.

After back-substitution the solver computes the relative backward error
`max|Ax - b| / max(||b||∞, ||A||∞ ||x||∞)` and exposes it as
`DcSolution.RelativeResidual`. If the residual exceeds `1e-8`, the solve is
refused with `IllConditioned` instead of returning a plausible-looking wrong
answer. The residual is a backward-error measure; it does not bound forward
error, so a severely ill-conditioned but solvable circuit can still lose digits.
For a netlist containing diodes, `RelativeResidual` instead reports the largest
nonlinear KCL/source residual as a fraction of its acceptance bound, so a value at
or below 1 means Newton converged.
The randomized ladder tests state their accuracy expectations explicitly for
that reason.

Failures are classified before or after factorization:

| Failure | Meaning |
| --- | --- |
| `NoGround` | no element references the declared ground node |
| `FloatingNetwork` | one or more nodes form an island with no path to ground; the message names them |
| `InconsistentSourceConstraints` | ideal voltage sources impose contradictory voltage differences; the message names the conflicting source |
| `SingularMatrix` | the system is underdetermined, e.g. a current source into an open node or a *consistent but redundant* ideal-source loop whose branch currents are not unique |
| `NonFiniteResult` | a coefficient, node voltage, or branch current became infinite or NaN |
| `IllConditioned` | the system solved but its residual is too large to trust |

A consistent redundant source loop is deliberately not reported as
inconsistent: its node voltages are well defined even though the individual
source currents are not. The solver refuses the whole solve rather than choosing
an arbitrary current distribution.

`radix validate`, the editor **Diagnostics** tab, and electrical rule checking
share this topology analysis. An island with no ground path, a contradictory
ideal-source loop, and a consistent but redundant source loop are reported as
`RAD-ERC-101`–`RAD-ERC-103` warnings before a solve (see [erc.md](erc.md)). They
are warnings so an electrical asset never blocks digital compilation.

The implementation is a dense `double` factorization: time and memory grow as
O(n³) and O(n²). It is intended for interactive, small and medium netlists; it is
not a sparse or large-scale production simulator.

## Nonlinear diode model

The netlist also supports a two-terminal Shockley diode:

```
I(V) = Is * (exp(V / (N * Vt)) - 1)
```

`NodeA` is the anode and `NodeB` the cathode, so the reported current flows from
anode to cathode and the reported voltage is `V(anode) - V(cathode)`. `Is` is the
saturation current in amperes, `N` the dimensionless emission coefficient, and
`Vt` the thermal voltage in volts. The default thermal voltage is 25.852 mV,
corresponding to 300.15 K; it is a fixed parameter, not a temperature sweep.

An optional series resistance `Rs` adds the terminal relation
`Vterminal = Vjunction + Rs * I`. The junction voltage is recovered from the
terminal voltage by a monotonic scalar bisection, and the circuit-level Newton
iteration linearizes the combined terminal conductance
`Gj / (1 + Rs * Gj)`. `Rs = 0` (the default) is the ideal junction. The model is
isothermal and has no reverse breakdown, junction capacitance, or high-injection
effects.

Diodes are solved with a damped Newton iteration. Each iteration linearizes every
junction around its current voltage, solves the same scaled-pivot MNA system used
for linear circuits, and applies standard pn-junction limiting when a forward step
exceeds two junction voltages. A result is accepted only when the junction
voltages stop moving and the nonlinear KCL/source residual is within a relative
`1e-9` tolerance plus a 1 fA current and 1 pV branch-equation floor. The absolute
floors matter at picoamp reverse currents, where subtracting nearly equal node
voltages limits how small a KCL residual is representable. Failure to converge within 100 iterations raises `NonConvergent` rather
than returning a partial operating point. The linear fast path is unchanged when
a netlist has no diodes.

The exponential argument is capped at 80 (about 2 V per junction at `N = 1`).
That keeps the arithmetic finite but means the ideal model must not be used at
large forward bias without series resistance; it is a modelling limit, not a
convergence tolerance.

Standalone netlists can express a diode:

```json
{
  "format": "radix-dc-netlist",
  "version": 1,
  "ground": "gnd",
  "elements": [
    { "id": "V1", "type": "voltage-source", "nodeA": "in", "nodeB": "gnd", "volts": 5 },
    { "id": "R1", "type": "resistor", "nodeA": "in", "nodeB": "out", "ohms": 1000 },
    { "id": "D1", "type": "diode", "nodeA": "out", "nodeB": "gnd",
      "saturationCurrent": 1e-12, "emissionCoefficient": 1, "thermalVoltage": 0.025852 }
  ]
}
```

`emissionCoefficient`, `thermalVoltage`, and `seriesResistance` are optional and
default to `1`, `0.025852` V, and `0` Ω. The report adds all three model
parameters to diode element objects. Authored `electricalCircuits` in project files do not expose
diodes yet; that needs a second component parameter in the project format.

```bash
radix dc examples/dc-diode.json
```

## Transient analysis

`TransientNetlist` adds ideal capacitors and inductors to the element set and
integrates them with fixed-step companion models. Every step is solved as a
purely resistive modified-nodal system by the same factorization, scaled
pivoting, and residual check the DC solver uses, so a step inherits its
floating-network, contradictory-source, singular, non-finite, and
ill-conditioned classification. Diodes stay in the step netlist, so a step with
a diode runs the existing Newton iteration instead of a linearized shortcut.

### Discrete equations

The state variables are each capacitor's voltage and each inductor's current.
For step `h`, backward Euler replaces a capacitor by conductance `C/h` in
parallel with a current source carrying `(C/h) v(t-h)`, and an inductor by
conductance `h/L` in parallel with a current source carrying `i(t-h)`:

```text
i_n = (C/h)(v_n - v_{n-1})
i_n = i_{n-1} + (h/L) v_n
```

The trapezoidal rule doubles those conductances and adds the previous branch
current (capacitor) or previous branch voltage (inductor) to the history term:

```text
i_n = (2C/h)(v_n - v_{n-1}) - i_{n-1}
i_n = i_{n-1} + (h / 2L)(v_n + v_{n-1})
```

Each companion model is one conductance plus one independent current source, so
it stamps like a resistor and a source and needs no new linear-algebra path.

| Method | Order | Stability | Energy on an ideal LC tank |
| --- | --- | --- | --- |
| `BackwardEuler` (default) | 1 | L-stable | numerically damped |
| `Trapezoidal` | 2 | A-stable, not L-stable | conserved to rounding |

The first step always uses backward Euler because the trapezoidal history
current is not defined yet. A trapezoidal run therefore loses a small amount of
tank energy in its first step and conserves it afterwards; a backward-Euler run
cannot ring on a stiff step but keeps damping oscillation energy. Tests assert
both behaviours instead of assuming them.

### Time step and determinism

`TransientOptions` fixes one uniform step, `(stop - start) / steps`, with `steps`
between 1 and 1,000,000, and the last point lands exactly on `stop`. The run is
deterministic: identical netlists produce identical samples. There is no
adaptive step control, no local truncation error estimate, and no automatic
stiffness detection, so choosing `h` against the smallest time constant or
period that matters is the user's responsibility; roughly twenty steps per time
constant is a reasonable starting point.

### Initial conditions

The initial state is solved in two phases. First a DC operating point supplies
defaults: capacitors are open, inductors are ideal zero-volt shorts, and every
source uses its waveform value at `start`. Then a state-imposing network (each
capacitor as an ideal voltage source at its initial voltage, each inductor as an
ideal current source at its initial current) produces the reported initial node
voltages. Without the second phase the reported `t = start` point would repeat
the DC values while the integration started somewhere else.

`SetCapacitorInitialVoltage` and `SetInductorInitialCurrent` override that state
per element, which is how a precharged capacitor or a live inductor current is
expressed. A capacitor whose nodes have no other DC path has no operating-point
voltage; it is reported as a floating network unless an initial voltage is
preset, rather than being started from an invented value.

### Piecewise-linear sources

`SetWaveform` attaches a piecewise-linear drive to an independent voltage or
current source; without one the source keeps its constant value. Breakpoints
must be finite with distinct times, are sorted so input order does not matter,
and hold their endpoint values outside the listed range. A pulse, ramp,
triangle, or sampled drive is a longer breakpoint list. A true discontinuity is
integrated as one step's worth of charge through a capacitor (`C * ΔV`), so a
finite ramp is preferred when the charging current itself matters.

### Failures

A run keeps every operating point that solved before a later step failed, and
reports the failing step index with the same `DcSolveFailure` classification as
the DC solver. A failure at the initial operating point returns no points. A
partially solved run is never presented as a completed one.

### Command line and JSON

```bash
radix transient examples/transient-rc.json
radix transient examples/transient-rlc.json --method trapezoidal
radix transient examples/transient-pulse.json --start 0 --stop 0.003 --steps 300 --json
```

`radix transient` reads a versioned `radix-transient-netlist` document. An
optional `analysis` block supplies defaults that command-line options override:

```json
{
  "format": "radix-transient-netlist",
  "version": 1,
  "ground": "gnd",
  "elements": [
    { "id": "V1", "type": "voltage-source", "nodeA": "in", "nodeB": "gnd", "volts": 0,
      "waveform": [ { "time": 0, "value": 0 }, { "time": 0.0001, "value": 5 } ] },
    { "id": "R1", "type": "resistor", "nodeA": "in", "nodeB": "out", "ohms": 1000 },
    { "id": "C1", "type": "capacitor", "nodeA": "out", "nodeB": "gnd", "farads": 1e-6 }
  ],
  "analysis": { "start": 0, "stop": 0.003, "steps": 60, "method": "backward-euler" }
}
```

| Type | Required | Optional |
| --- | --- | --- |
| `resistor` | `ohms` | |
| `voltage-source` | `volts` | `waveform` |
| `current-source` | `amperes` | `waveform` |
| `diode` | `saturationCurrent` | `emissionCoefficient`, `thermalVoltage`, `seriesResistance` |
| `capacitor` | `farads` | `initialVolts` |
| `inductor` | `henries` | `initialAmperes` |

The loader rejects unknown fields, wrong formats or versions, missing units,
properties that belong to another element type, empty waveforms, non-finite
values, duplicate or non-increasing breakpoint times, and an out-of-range
analysis block. Input is bounded to 16 MiB.

`--json` emits one `radix-transient-report` (version 1) with the ground node,
method, start/stop/step count and step size, `completed`, an optional stable
`failure` name with a message and failing step index, and one point per simulated
step. Each point carries its time, the solve's `relativeResidual`, node
voltages, and each element's terminal voltage, current, and absorbed power. Exit
codes are unchanged: `0` completed, `1` a step failed, `2` malformed input or
invalid options. The command never writes project or netlist state.

### Not modelled

There is no junction capacitance, carrier storage, high-injection, self-heating,
noise, temperature dependence, transformer, dependent source, switch, relay, or
transmission-line model. There is no AC or small-signal analysis, no adaptive
stepping, no gmin or source continuation, and no SPICE-style built-in source
functions beyond piecewise-linear tables. Authored `.radix` electrical circuits
still expose only resistors and independent sources, and the editor has no
transient UI: `radix transient` is the supported entry point.

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

A transient run reports the same values plus the index of the step that failed,
and keeps the operating points it already solved.

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
- analytical node voltages, element currents, element voltages, and power;
- fixed-step transient analysis of resistors, capacitors, inductors, ideal
  sources with optional piecewise-linear waveforms, and Shockley diodes;
- backward Euler and trapezoidal integration with an explicit time step,
  per-element initial conditions, and per-step failure reporting.

Not supported:

- transformers, dependent sources, switches, relays, fuses, or transmission
  lines;
- frequency-domain/AC analysis or small-signal extraction;
- transistors, MOSFETs, op-amps, regulators, LEDs, or temperature-dependent
  models;
- junction capacitance, carrier storage, high-injection, self-heating, or noise;
- adaptive time stepping, local truncation error control, gmin continuation, or
  SPICE-style built-in source functions beyond piecewise-linear tables;
- transient analysis of authored `.radix` circuits, or any transient editor UI
  (the Electrical tab stays a DC inspector and preview);
- temperature, thermal limits, component tolerances, or datasheet ratings;
- wire resistance, parasitic elements, or physical layout;
- short-circuit current, overcurrent, or fuse-trip prediction;
- automatic mapping of digital `.radix` nets or domain indices to volts;
- graphical analog schematic canvas or editor instruments.

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
