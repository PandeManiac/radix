# DC electrical solver

RADIX has a separate core DC linear solver for the first, deliberately narrow
electrical-analysis layer:

- ideal resistors with finite positive resistance;
- ideal independent voltage sources;
- ideal independent current sources;
- one explicit ground reference.

It is **not** connected to the digital event engine, the editor, the `.radix`
project format, or the ERC checker. It is an independent netlist API under
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
- automatic mapping of `.radix` digital nets or domain indices to volts;
- editor instruments or a graphical analog canvas.

A digital domain index is not a voltage. Any future integration must introduce
explicit electrical terminals, units, models, and a documented digital/analog
boundary rather than inferring electrical meaning from the existing signal
engine.

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
