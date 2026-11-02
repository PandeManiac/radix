# DC electrical solver

RADIX has a separate core DC linear solver for the first, deliberately narrow
electrical-analysis layer:

- ideal resistors with finite positive resistance;
- ideal independent voltage sources;
- ideal independent current sources;
- a two-terminal Shockley diode with optional series resistance;
- a three-terminal square-law MOSFET (n- or p-channel) with channel-length modulation;
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
or below 1 means Newton converged. Each node's KCL residual is scaled by the
currents incident on that node, and each ideal-source equation by that source's own
voltage. An unrelated high-current or high-voltage branch therefore cannot mask a
poorly converged operating point elsewhere in the circuit.
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

The netlist supports a two-terminal Shockley diode:

```
I(V) = Is * (exp(V / (N * Vt)) - 1)
```

`NodeA` is the anode and `NodeB` the cathode, so the reported current flows from
anode to cathode and the reported voltage is `V(anode) - V(cathode)`. `Is` is the
saturation current in amperes, `N` the dimensionless emission coefficient, and
`Vt` the thermal voltage in volts. The default thermal voltage is 25.852 mV,
corresponding to 300.15 K; it is a fixed parameter, not a temperature sweep.
The derived junction voltage `N * Vt` must also be representable as a finite
positive double; the netlist rejects parameter pairs whose product overflows or
underflows to zero. It also rejects parameters whose zero-bias forward or
reverse-breakdown conductance would overflow the numeric range.

Optional reverse breakdown parameters `BV` (positive breakdown voltage in volts)
and `IBV` (positive reverse current at `-BV` in amperes) add the exponential
breakdown branch:

```
I(V) = Is * (exp(V / (N * Vt)) - 1) - IBV * exp((-V - BV) / (N * Vt))
```

Both parameters must be supplied together; omitting them preserves the original
Shockley-only model. At `V = -BV`, reverse current is approximately `-IBV`. This
smooth knee is a compact model, not a manufacturer-specific avalanche curve.

The forward Shockley current evaluates `exp(x) - 1` with a cancellation-safe
small-argument expansion and a stable identity near zero, preserving low-current
behavior. For numerical safety, exponential arguments above 80 use the linear tangent
continuation of `exp(x)` at 80 for both forward conduction and reverse breakdown.
The continuation keeps the reported conductance equal to the derivative of the
current law and avoids exponential overflow. Results far beyond this numerical
knee are solver continuation values, not a physical high-injection or avalanche
prediction.

An optional series resistance `Rs` adds the terminal relation
`Vterminal = Vjunction + Rs * I`. The junction voltage is recovered from the
terminal voltage by a monotonic scalar bisection that continues until the floating-
point bracket can no longer be narrowed. The circuit-level Newton iteration
linearizes the combined terminal conductance
`Gj / (1 + Rs * Gj)`. `Rs = 0` (the default) is the ideal junction. The model is
isothermal and has no junction capacitance, self-heating, or high-injection
effects. Reverse breakdown is included only when `BV` and `IBV` are supplied.

Diodes are solved with Newton iteration and pn-junction step limiting. Each iteration
linearizes every junction around its current voltage, solves the same scaled-pivot
MNA system used for linear circuits, and applies standard pn-junction limiting when
a forward step exceeds two junction voltages. A result is accepted only when the
junction voltages stop moving and the nonlinear KCL/source residual is within a relative
`1e-9` tolerance plus a 1 fA current and 1 pV branch-equation floor. Voltage
updates are scaled only by node and junction voltages; source branch currents do not
set a voltage tolerance. The absolute floors matter at picoamp reverse currents,
where subtracting nearly equal node voltages limits how small a KCL residual is
representable. Failure to converge
within 100 iterations raises `NonConvergent` rather than returning a partial
operating point. The linear fast path is unchanged when
a netlist has no diodes.

Exponential arguments above 80 (about 2 V per junction at `N = 1`) use the linear
tangent continuation described above. This keeps the arithmetic finite, but it is
a numerical continuation rather than a physical high-current model or a convergence
tolerance.

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
      "saturationCurrent": 1e-12, "emissionCoefficient": 1, "thermalVoltage": 0.025852,
      "breakdownVoltage": 5.1, "breakdownCurrent": 0.005 }
  ]
}
```

`emissionCoefficient`, `thermalVoltage`, and `seriesResistance` are optional and
default to `1`, `0.025852` V, and `0` Ω. `breakdownVoltage` and `breakdownCurrent`
are an optional pair; the report includes all supplied diode model parameters.
Authored `electricalCircuits` in project files do not expose diodes yet; that needs
a second component parameter in the project format.

```bash
radix dc examples/dc-diode.json
radix dc examples/dc-zener.json
```

## MOSFET model

A three-terminal device whose channel current is set by another node's voltage is the
first genuinely electrical control element. It is what lets a circuit switch itself, so
the model is deliberately small and written down in full rather than hidden behind a
library call.

```bash
radix dc examples/dc-mosfet-common-source.json
radix dc examples/dc-mosfet-common-source.json --sweep VG=0:5:11
```

### Terminals and parameters

| Netlist field | Meaning | Unit |
| --- | --- | --- |
| `drain`, `source` | the channel terminals; also reported as `nodeA` and `nodeB` | node id |
| `gate` | the insulated control terminal; draws exactly zero current | node id |
| `channel` | `n` or `p` | — |
| `thresholdVoltage` | threshold voltage magnitude; negative for a depletion device | V |
| `transconductance` | SPICE `KP`, i.e. `mu Cox W/L` | A/V² |
| `channelLengthModulation` | SPICE `lambda`; optional, defaults to 0 | 1/V |

`transconductance` is required and must be greater than zero. The **body/bulk terminal is
not exposed**: it is tied to the source, so there is no body effect and the device is
exactly symmetric in drain and source.

### Governing equations

With `Vgs` and `Vds` the terminal voltages, `Vt` the threshold voltage and `Vov = Vgs - Vt`:

```text
cutoff      Vov <= 0:          Ids = 0
triode      0 < Vds <  Vov:    Ids = K (Vov Vds - Vds^2/2) (1 + lambda Vds)
saturation  Vds >= Vov:        Ids = K/2 Vov^2 (1 + lambda Vds)
```

That is the Shichman-Hodges square law, SPICE's level 1. A p-channel device negates both
driving voltages and negates the resulting current, so one set of equations covers both
polarities. Because the device is symmetric, a negative `Vds` is evaluated with the drain
and source roles exchanged, which keeps reverse conduction signed correctly instead of
reporting a saturation current with the wrong direction. The derivatives used by Newton
follow from the same three branches:

```text
triode:      gds = K((Vov - Vds)(1 + lambda Vds) + lambda (Vov Vds - Vds^2/2))
             gm  = K Vds (1 + lambda Vds)
saturation:  gds = lambda K Vov^2 / 2
             gm  = K Vov (1 + lambda Vds)
cutoff:      gds = gm = 0
```

A leakage conductance `GMIN = 1e-12 S` is added across drain and source at every operating
point — five picoamperes at five volts. It stands in for SPICE's `GMIN` and keeps a cutoff
device from making the Jacobian exactly singular, which is what would otherwise happen when
a CMOS output node is driven only by an off transistor. It is part of the reported device
current, so KCL and the convergence residual stay consistent.

### MNA stamping

Linearizing `Ids ~ I0 + gds (Vds - Vds0) + gm (Vgs - Vgs0)` around the previous Newton
iterate gives one equivalent current
`Ieq = I0 - gds Vds0 - gm Vgs0` plus conductance and transconductance terms, with
`d` the drain, `s` the source and `g` the gate node:

```text
G[d,d] += gds   G[s,s] += gds   G[d,s] -= gds   G[s,d] -= gds
G[d,g] += gm    G[s,g] -= gm    G[d,s] -= gm    G[s,s] += gm
rhs[d] -= Ieq   rhs[s] += Ieq
```

The gate contributes no row of its own; it appears only as the control node in the drain
and source KCL rows. Newton carries no extra state for a MOSFET — it is a pure
voltage-controlled current source — so each iteration linearizes it at the previous
iterate's node voltages, starting from zero volts. Convergence is unchanged: node voltages
must stop moving within `1e-9` relative and the nonlinear KCL/source residual must fall
within its acceptance bound. Failing to converge in 100 iterations raises
`NonConvergent`; the solver never returns a partly converged point.

A MOSFET's gate node is added to the solved node set so its voltage is reported, but it is
**not** added to the conductive adjacency used by the floating-network check. Gate oxide
conducts nothing, so a node attached only to gates has no DC operating point and is
reported as `FloatingNetwork` instead of being given an invented voltage.

### Conservation and conventions

The reported element current is the channel current from drain to source, and the reported
element voltage is `V(drain) - V(source)`, so absorbed power is `Vds * Ids` and the gate
draws no power. The algebraic sum of all absorbed element powers is zero at any solved
operating point, which the tests assert. `radix dc` and the JSON report list a MOSFET's
`channel`, `gate`, `thresholdVoltage`, `transconductance` and
`channelLengthModulation` beside its solved values.

### What this model is not

It is a **compact teaching model, not a production SPICE transistor**. In particular:

- no junction, overlap or body capacitance, so without the optional device capacitances
  below the device is charge-free and adds no dynamics;
- no subthreshold conduction, so current is exactly zero below the threshold apart from
  `GMIN`;
- no mobility degradation, velocity saturation, high-injection, drain-induced barrier
  lowering or gate leakage;
- no body effect, no temperature dependence, no self-heating, no noise, no mismatch, no
  process corners, and no breakdown;
- no channel-length modulation unless `lambda` is given, and `lambda` here is a constant
  rather than a geometry lookup.

### Milestone A: controlled-device validation

`examples/dc-mosfet-common-source.json` is an n-channel device with a 1 kΩ resistor to a
5 V rail, driven by the gate source `VG`:

| `VG` | `out` | region |
| --- | --- | --- |
| 0, 0.5, 1 | 5 | cutoff |
| 2 | 4.45545 | saturation (`Vov = 1 V`) |
| 3 | 2.88462 | saturation |
| 5 | 1.10533 | triode |

Every value matches the closed form. At `VG = 2` the saturation current is
`0.5 mA (1 + lambda Vds)`, and `Vds = 5 - 1000 Ids` gives `Vds = 4.5 / 1.01 = 4.45545 V`. At
`VG = 5` the triode law and the load together give `Vds^2 - 10 Vds + 10 = 0`, so
`Vds = 5 - sqrt(15) = 1.127 V`. The tests check those two roots directly, the p-channel
mirror, reverse conduction (`Vds < 0`, where the device sources 6 mA into a grounded drain
and the 2 V source delivers exactly the channel's absorbed 12 mW), and that the summed
absorbed power is zero.

### Milestone B: electrical binary inverter

`examples/dc-cmos-inverter.json` is a static CMOS inverter: a p-channel pull-up to the
5 V rail, an n-channel pull-down, both gates driven by `VIN`, `K = 1 mA/V²`,
`Vt = 1 V`, `lambda = 0.02 1/V`.

```bash
radix dc examples/dc-cmos-inverter.json --sweep VIN=0:5:21
```

| `VIN` | `out` | | `VIN` | `out` |
| --- | --- | --- | --- | --- |
| 0 | 5 | | 2.5 | 2.5 |
| 1 | 5 | | 2.75 | 0.574822 |
| 1.5 | 4.9445 | | 3 | 0.293467 |
| 2 | 4.70653 | | 3.5 | 0.0554996 |
| 2.25 | 4.42518 | | 4 | 1.67e-9 |
| 2.45 | 3.88167 | | 5 | 1.25e-9 |

- **Static current**: at either rail exactly one device has overdrive, so the rail supplies
  only the `GMIN` leakage (`< 1e-9 A`). The inverter pays current only while switching.
- **Transition**: matched devices put the switching point at `VDD/2` by symmetry, and the
  measured maximum small-signal gain over a 50 mV grid is about 28 V/V. The curve has real
  analog shoulders (a 21-point sweep shows 4.43 V at 2.25 V and 0.575 V at 2.75 V), which a
  behavioural inverter could not produce.
- **Output loading**: the output resistance in the transition is `1/(gm (ro_n || ro_p))`;
  with `lambda = 0` it would be a pure current balance with no finite conductance, which is
  why the example sets `lambda` explicitly.

### Milestone C: electrical ternary inverter

`examples/dc-ternary-inverter.json` is a resistor-summed dual-threshold standard ternary
inverter. Two CMOS inverters share `VIN` but have opposite device-strength ratios, which
moves their switching points to 1.25 V and 3.75 V:

```text
sqrt(K_n/K_p) = 11   =>  Vin_sw = (beta Vtn + VDD - |Vtp|) / (beta + 1) = 15/12 = 1.25 V
sqrt(K_n/K_p) = 1/11 =>  Vin_sw = 4.0909/1.0909 = 3.75 V
```

The two outputs are summed by two 100 kΩ resistors (`out_low`, `out_high` → `out`), so the
output is a solved divider between two low-impedance nodes:

```bash
radix dc examples/dc-ternary-inverter.json --sweep VIN=0:5:21
radix dc examples/dc-ternary-inverter.json --sweep VIN=0,2.5,5
```

| `VIN` | `out` | `out_low` | `out_high` |
| --- | --- | --- | --- |
| 0 | 5 | 5 | 5 |
| 1.25 | 4.1933 | 3.38912 | 4.99748 |
| 1.5 | 2.55006 | 0.108655 | 4.99146 |
| 2 | 2.51341 | 0.0393598 | 4.98747 |
| **2.5** | **2.5** | 0.0206093 | 4.97939 |
| 3 | 2.48659 | 0.0125317 | 4.96064 |
| 3.5 | 2.44994 | 0.00853836 | 4.89134 |
| 3.75 | 0.806701 | 0.0025211 | 1.61088 |
| 5 | 2.9e-8 | 1.6e-9 | 5.6e-8 |

The truth table is a result, not an assignment: `0 V → 5 V`, `2.5 V → 2.5 V`, `5 V → 0 V`,
and the middle rail appears because one inverter output is low while the other is high.

The full curve, rather than the three sampled points, is what the design is judged on:

- **Middle plateau.** Every input from 1.5 V to 3.5 V resolves within 100 mV of 2.5 V, a
  2 V wide input band, so the intermediate state is not a knife edge. Over that band the
  output stays within 60 mV of 2.5 V, the worst samples being the endpoints themselves
  (2.55006 V at 1.5 V and 2.44994 V at 3.5 V).
- **Shoulders.** The transitions are the two inverters' own crossings, so the curve is
  monotone and crosses 2.5 V only once from either side. Between 1.25 V and 1.5 V the output
  moves from 4.19 V to 2.55 V, and between 3.5 V and 3.75 V from 2.45 V to 0.81 V.
- **Output impedance.** In the middle state the output is `R/2 = 50 kΩ` from a Thevenin pair
  of about 0 V and 5 V. A 100 kΩ load to ground therefore divides 2.5 V by
  `(2 + R/R_load) = 3`, and the solve returns **1.667 V**; a 1 MΩ load returns 2.487 V. The
  tests assert that the loaded result matches the two-source prediction, so the loading is
  characterised rather than discovered later.
- **Static power.** At either rail one inverter per pair is fully off and the summing path
  carries only leakage, so the total is below a microwatt. The middle state is not free:
  the divider carries 25 µA and both inverters sit in their own crossover region, so the
  5 V rail supplies about 49.5 µA — **0.248 mW**. This is inherent to resistor summing and
  would be removed by a buffered output stage.
- **Noise margin (ideal, unloaded).** `radix dc --sweep … --margins out` finds the unity-gain
  crossings and reports the windows between them: `0 → 1.042`, `1.422 → 3.578`, `3.958 → 5` V.
  The middle window is 2.155 V wide, so a level placed at 2.5 V has ±1.078 V of margin, and each
  rail window is 1.042 V wide. That figure shrinks with load because the middle level moves; it
  is a property of this topology, not a datasheet number.
- **Region of validity.** This is an *idealised educational* topology: the levels come from
  two ideal CMOS inverters and ideal resistors, with no mismatch, no temperature, no
  capacitance, and a fixed `lambda`. It demonstrates that an intermediate logic level can be
  produced by a solved electrical network; it is not a manufacturable ternary gate.

**Gain.** `radix dc --sweep … --gain <node>` differentiates the measured curve and reports
`d(node)/d(swept)`, so the shoulder sharpness a gate is judged on is a number rather than an
impression:

```bash
radix dc examples/dc-cmos-inverter.json --sweep VIN=2.3:2.7:5 --gain out
```

```text
d(out)/dVIN: 5 points, 5 with a defined slope, max |gain| 15.9101 V/V at VIN=2.5
         VIN        out'
         2.3     -2.4696
         2.4    -9.18984
         2.5    -15.9101
         2.6    -9.18984
         2.7     -2.4696
```

A finer grid measures a larger peak because the transition is steeper than a coarse grid can
resolve: this is the slope of the reported points, not a second solve, so it cannot disagree
with the table above it. Being a difference quotient, it needs two solved points — a
single-point sweep, a value that failed to solve, or repeated swept values report no slope
instead of a fabricated zero.

**Switching.** `examples/transient-ternary-inverter.radix` drives the same inverter from 0 V to
2.5 V through a 1 ns ramp, and every capacitance in it now belongs to a device: 0.25 pF of
gate-source and 1 pF of drain-source per transistor, plus 0.02 pF from each gate to its own drain.
There is no separate load capacitor, so the output's speed is the transistors':

```bash
radix transient examples/transient-ternary-inverter.radix --stop 900e-9 --steps 3600
```

| `t` | 0 | 26 ns | 900 ns |
| --- | --- | --- | --- |
| `g` | 0 | 1.0 | 2.5 |
| `out` | 5 | 5.010 | 2.5 |

The gate bus must be charged first: 10 kΩ into the four gates is 1 pF, so the bus reaches the 1 V
threshold at 26 ns and half of the drive at 29 ns, and no device can respond before it. Their drains
then charge the 2 pF that the two devices on each branch present, seen through the 50 kΩ the two
summing resistors give the output, and the 2 % band closes at 50 ns with the gates settled at 68 ns.

The output lands on **exactly 2.5000 V**, which is what a DC solve reports for `VIN = 2.5 V`, and it
is the mean of the two branch outputs — `(0.0206 + 4.9794)/2` — because two equal resistors sum
them. The tests assert the DC agreement and the summing identity rather than a literal, so the
figures cannot drift with the step size. Doubling the gate capacitance doubles the time the bus
takes to reach half of its drive, which is the closed form `RG·C·ln 2` and cannot come from the
integrator. The branch nodes are not that simple: once a device leaves saturation its on-resistance
sets the tail, so doubling the drain capacitance moves the settling time by much less than double,
and the tests do not claim otherwise.

Each device also has 0.02 pF from gate to drain, and that feedback is visible rather than decorative:
while the bus climbs it drags the drain nodes up through that path before any channel conducts, so
`out_low` peaks at **5.019 V**, 19 mV above its own rail, and the output takes a 10 mV bump before
falling. Removing the gate-drain capacitance removes the overshoot entirely, which is what the test
compares, so the effect measures the feedback path and not the step size.

**Step size matters here.** At 1 ns steps the energy ledger closes to 1.8 × 10⁻² of the energy
delivered, because 1 ns is a fiftieth of the transition; at 0.25 ns it closes to 1.5 × 10⁻³. The
model is fixed-step, so the figure is quoted with the grid that produced it.

### Frequency-domain analysis

`AcAnalysis` linearizes a circuit at its DC operating point and solves the complex network
frequency by frequency. A capacitor becomes `jωC`, an inductor `1/(jωL)`, a diode its junction
conductance and a MOSFET its drain conductance with its gate transconductance; independent sources
are set to zero except the one named as the drive, whose branch is imposed at unit amplitude. Each
node's phasor is therefore the transfer function from that source to that node, phase included.

```csharp
AcResponse response = AcAnalysis.Run(
    circuit.ToTransientNetlist(),
    driveSourceId: "V1",
    AcFrequencySweep.Logarithmic(1, 1e6, 61));
Console.Write(response.ToText());
```

The device capacitances take part, so a transistor's own charge appears in its bandwidth: a
common-source stage with 1 mA/V of transconductance, a 1 kΩ drain resistor and 1 pF of drain-source
capacitance has unity gain at low frequency, inverts (180°), and halves at
`1/(2π · 1 kΩ · 1 pF)`. That case, the RC corner where the magnitude is `1/√2` and the phase −45°,
an RL corner at `R/(2πL)`, a tank resonance at `1/(2π√(LC))` and a resistive divider that must be
flat at its DC ratio are the tests, so an error in the assembly or in the complex arithmetic cannot
hide behind a plausible curve.

This is a small-signal linearization, not a large-signal answer: it describes how a small
disturbance travels around the operating point, which is what a gain, a corner frequency or a phase
margin is about.

`radix ac` runs the same analysis from the command line. The drive must be named, because the
transfer function is from that source to every node, and `--bias` exists because an operating point
takes each source at its waveform's value at t = 0 — the ternary example's drive starts at 0 V,
where the gate is off and its gain is zero. Held on the lower transition instead:

```bash
radix ac examples/transient-ternary-inverter.radix --drive VIN --bias VIN=1.3 --from 1e3 --to 1e7 --points 7
```

```text
  Hz  g dB  phase g  in dB  phase in  out dB  phase out  ...
  1000          -2.6e-12  -1.2e-05         0         0  29.4397        180  ...
  215443        -0.104154 -2.38925         0         0  28.6745   155.532  ...
  4.64159E+06   -1.02871  -16.7123         0         0  9.52886   79.6753  ...
```

`--csv` writes the sweep as one row per node per frequency and `--json` as a versioned report;
`--compare <archive.json> --compare-tolerance <db>` reads such a report back and prints how far each
node's gain and phase moved, exiting non-zero when the gain moves further than the tolerance. That is
how a capacitance change is shown rather than described: doubling the RC example's capacitor is
6.02 dB down well above the corner, which is exactly what the comparison reports.

The gate has 29.4 dB of gain at low frequency, inverting at 180°, and is 3 dB down near 500 kHz,
with the phase past 136° by 447 kHz. `--devices` adds the transistors themselves: each one's
region at the bias, its `gm` and `gds`, and their ratio, which is the largest gain the device could
manage into an ideal load. The node table is assembled from those slopes, so printing them makes a
gain traceable to a device instead of to a matrix.

The editor's inspector shows the same numbers for the selected transistor, beside the bias it was
measured at. Without a bias the circuit sits at its rest state — each source at its value at t = 0 —
so a driven gate reads `Cutoff`; entering `VIN=1.3` puts it on the transition and the region, `gm`,
`gds` and `|gm/gds|` appear next to the capacitances they are assembled with, together with the
gate-charge fractions that region presents (`Cgs ×`, `Cgd ×`), so the bias dependence sits beside
the fields that author it. The bias-sweep CSV carries the same two columns for every device at
every bias.

One operating point says where a device sits; a sweep says where it switches. `radix ac
--bias-sweep 0:5:11` steps the drive and prints every transistor's region, `Vgs`, `Vds`, `gm` and
`gds` at each value, so the ternary gate's two transitions appear as a sequence of regions rather
than as one number taken at a bias somebody guessed. The sweep holds the source at each value in
turn — a waveform would otherwise pin every point to the rest state — and it measures devices with
the same helper the AC analysis uses, so the two cannot report different slopes for one bias.

The editor's **BIAS SWEEP** section runs the same study on the selected circuit, using the AC
drive id and a `from:to:count` range. It draws transconductance against bias with one series per
transistor — a device that is cut off at 0 V and conducting at 2.5 V is a slope you can see rather
than a region column you have to read — names the device that conducts hardest and the bias where
it does, and copies the whole table as CSV with one row per device per bias. Those are the same numbers the DC sweep measures as a slope at
the same bias: because the analysis is linearized at the operating point, the test asserts that the
AC magnitude at 1 Hz agrees with the difference quotient within 15 %, so the frequency-domain
assembly and the DC slope cross-check each other instead of each being trusted alone.

The editor's **AC SWEEP** panel runs the same analysis on the selected circuit: name the drive, a
`from:to:points` range, and optionally `source=volts` as a bias. It lists every node's gain and phase
per frequency, plots gain in dB and phase in degrees against log frequency on separate axes, and
copies the table as the same CSV `radix ac --csv` writes, and exports both curves as one standalone
SVG document — a response shared as a file keeps its axes, its decade labels and its units, which a
screenshot does not. The frequency axis is labelled in decades
(1k, 10k, 100k, …) even though it plots exponents, because "3" is not a frequency a reader can use. Editing the circuit drops the response, because a curve describing an older
circuit is worse than no curve.

**Gate charge.** `TransientNetlist.AddMosfet` also accepts `gateSourceFarads` and `gateDrainFarads`,
or `cgs`/`cgd` in a transient JSON netlist, both defaulting to zero so an existing netlist keeps its
behaviour. Given, they are stamped between gate and source and between gate and drain on every step,
so the gate must be charged through whatever drives it before the channel can follow. They are
internal capacitors rather than authored elements: they carry state, energy and companion stamps
like any other capacitor, but never appear in `Capacitors`, element tables or reports.

An authored MOSFET carries the same values as `gateSourceCapacitance`, `gateDrainCapacitance` and
`drainSourceCapacitance`, so a `.radix` circuit can be built with device charge from the Electrical
panel's inspector; all three default to zero, and a DC run ignores them because an operating point
holds no charge. Drain-source capacitance is what lets a transistor load its own output, so a
circuit can derive its speed from the devices instead of from a capacitor beside them.

The closed form is checkable. A 10 kΩ resistor driving a gate with 1 pF of gate-source capacitance
gives `RC = 10 ns`; the measured half-rise converts back to that constant, and adding 1 pF of
gate-drain capacitance against a fixed drain doubles it, because both plates then see an AC ground.
Both are tests, so a capacitance that never reached the solver would fail as an instant rise instead
of hiding inside a plausible-looking curve.

**Input windows.** `--margins <node>` turns the measured gain into the windows a level can
occupy, by finding where `|dV(node)/d(swept)|` crosses one and reporting the ranges between those
crossings:

```bash
radix dc examples/dc-ternary-inverter.radix --sweep VIN=0:5:64 --margins out
```

```text
input windows for out over VIN: 3 windows where |dV(out)/dVIN| < 1
  0 -> 1.042  (1.042 wide)
  1.422 -> 3.578  (2.155 wide)
  3.958 -> 5  (1.042 wide)
unity-gain crossings: 1.042, 1.422, 3.578, 3.958
```

A circuit holds a level where it does not amplify, so the gaps are the legal levels and the bands
between crossings are forbidden. The same command on the binary inverter reports two windows of
2.085 V each meeting at a single transition, and on a resistive divider a single window covering
the whole range, because a divider never amplifies. The crossings are interpolated from the
measured gain, so they inherit its grid: a coarse sweep reports a correspondingly coarse edge,
which is why the number is quoted with the sweep that produced it.

The same topology ships as an authored project asset,
`examples/dc-ternary-inverter.radix` (format v12), so the design opens in the Electrical
inspector instead of living only in a netlist file. Authored MOSFETs carry a
transconductance, a channel, a threshold and `channelLengthModulation`, so with the
example's `lambda = 0.02 1/V` the authored circuit reproduces the netlist table above
exactly: 4.1933 V at 1.25 V, 2.55006 V at 1.5 V, 2.5 V at 2.5 V, 0.806701 V at 3.75 V.
The example test solves both files and compares them to twelve decimal places, so the two
shipped forms of the same design cannot drift apart.

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

A fixed-step run uses one uniform step, `(stop - start) / steps`, with `steps`
between 1 and 1,000,000, and the last point lands exactly on `stop`. The run is
deterministic: identical netlists produce identical samples. Choosing `h`
against the smallest time constant or period that matters is then the user's
responsibility; roughly twenty steps per time constant is a reasonable starting
point. A step must also advance the absolute time at double precision; a start
time so large that the requested step rounds away is rejected, and any later
step that cannot advance time ends with a numerical failure instead of spinning
through the step budget.

Setting `AdaptiveTolerance` (CLI `--tol`) switches to step-doubling control. At
each point the solver takes one step of size `h` and two of size `h/2` from the
same state, compares the state variables, and divides the difference by
`2^p - 1` for the method order `p`. The step is accepted when that estimate is
within `atol + rtol * max(|value|, |value of the other solution|)` for every
state variable, and an accepted step keeps the two half steps. The next step
size grows or shrinks by `clamp(0.9 * error^(-1/(p+1)), 0.2, 4)`.

Only the true ordinary-differential-equation states are controlled: capacitor
voltages and inductor currents. The derived capacitor current and inductor
voltage are deliberately excluded, because at a drive kink their magnitude does
not fall with the step and a purely relative tolerance on them would collapse
the step size (an earlier version of this code did exactly that at the start of
a ramp). `AdaptiveAbsoluteTolerance` (CLI `--atol`) sets `atol`; when zero it is
derived as `rtol` times the circuit's own scale, the largest source magnitude
over the interval or initial reactive state, with a floor of 1. That scale is a
numerical reference shared by voltages and currents, not a physical unit.

A step that cannot be solved at all (singular, non-convergent, or non-finite) is
retried at half the size, so a Newton failure caused by an over-large step
becomes a smaller step instead of a failed run. `InitialTimeStep`,
`MinimumTimeStep`, `MaximumTimeStep`, and `MaximumStepCount` bound the search;
failing to meet the tolerance above the minimum step is reported as
`NonConvergent` with the minimum-step message, never as a completed run.
Adaptive runs are deterministic too, and the trapezoidal rule's first accepted
step still uses backward Euler.

Adaptive control does not make the solver sparse or fast: each trial step still
factors a dense MNA system, and step doubling costs three solves per trial.

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
partially solved run is never presented as a completed one. A completed run is
also rejected with `NonFiniteResult` if its derived energy ledger overflows, even
when each solved voltage, current, and element power is finite. For any run with
an unrepresentable energy ledger, solved points and any earlier step failure are
preserved, while the energy object is omitted from JSON so the failure report
remains valid JSON.

### Command line and JSON

```bash
radix transient examples/transient-rc.json
radix transient examples/transient-rlc.json --method trapezoidal
radix transient examples/transient-pulse.json --start 0 --stop 0.003 --steps 300 --json
radix transient examples/transient-rc.json --tol 1e-6 --method trapezoidal --json
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

The `analysis` block accepts `start`, `stop`, `steps`, `method`, and, for
adaptive control, `tolerance`, `absoluteTolerance`, `initialStep`, `minStep`,
`maxStep`, and `maxSteps`.

| Type | Required | Optional |
| --- | --- | --- |
| `resistor` | `ohms` | |
| `voltage-source` | `volts` | `waveform` |
| `current-source` | `amperes` | `waveform` |
| `diode` | `saturationCurrent` | `emissionCoefficient`, `thermalVoltage`, `seriesResistance`, `breakdownVoltage`, `breakdownCurrent` |
| `capacitor` | `farads` | `initialVolts` |
| `inductor` | `henries` | `initialAmperes` |

The loader rejects unknown fields, wrong formats or versions, missing units,
properties that belong to another element type, empty waveforms, non-finite
values, duplicate or non-increasing breakpoint times, and an out-of-range
analysis block. Input is bounded to 16 MiB.

`--json` emits one `radix-transient-report` (version 1) with the ground node,
method, start/stop/step count and step size, the `adaptive` flag and `tolerance`
when step control is on, `completed`, an optional stable `failure` name with a
message and failing step index, and one point per accepted step. Each point carries its time, the solve's `relativeResidual`, node
voltages, and each element's terminal voltage, current, and absorbed power. Exit
codes are unchanged: `0` completed, `1` a step failed, `2` malformed input or
invalid options. The command never writes project or netlist state.

### Not modelled

There is no junction capacitance, carrier storage, high-injection, self-heating,
noise, temperature dependence, transformer, other dependent-source model
(VCVS/VCCS/CCCS), switch, relay, or transmission-line model, and the MOSFET has no
charge storage of its own. There is no AC or small-signal analysis, no
stiffness detection or implicit method switching, no gmin or source
continuation, and no SPICE-style built-in source functions beyond
piecewise-linear tables. Adaptive control uses step doubling with one method at
a time; it does not estimate a spectral radius or choose between methods. The
editor's **Electrical** tab runs the same solver on an authored circuit, and
`radix transient` remains the headless entry point.

## Peak and settling metrics

Every run also summarises each node: its minimum, maximum, final value, the time of
the maximum, and the time after which it stayed inside a settling band of its own
final value or swing. The band defaults to 2% and is configurable through
`--settling-band <fraction>` on the CLI and the **Settling band** field in the
panel; both reject a value outside the open interval `(0, 1)`, so a typo cannot
silently turn a settling report into a trivial one. The band is a fraction of the
node's own numbers, so it means the same thing for a 5 V step and a millivolt
ripple, and a null settling time is the honest report of a node that was still
moving at the last solved point, which is what an oscillation or a diverging run
looks like. The band comparison is normalized by the largest voltage magnitude,
so a finite swing from a large negative to a large positive value does not overflow
its range or appear settled immediately.

The metrics describe the reported points rather than a second solve, so they cannot
disagree with the values the caller already has, and the panel lists them directly
above the solved-point table. They are checked against closed
forms: an RC charge settles where `5*(e^(-t/tau) - e^(-T/tau))` enters the band
(accounting for the finite final value), an underdamped series RLC peaks at
`1 + e^(-pi*zeta/sqrt(1-zeta^2))` a quarter period in, and a driven pulse peaks
where the drive falls below the capacitor voltage rather than where the drive itself
peaks. `radix transient` prints the table, and the JSON report carries it under
`metrics` with a `settlingBand` and one object per node.

`--csv` writes the solved points as a wide table instead: a `time_s` header followed by one
column per node in ordinal order, one row per point. Numbers use the invariant round-trip
format, so a reader parses back exactly the doubles the solver produced, and node names are
quoted with their own quotes doubled, so an id containing a comma cannot break the table. The
panel's **Copy CSV** / **Save CSV…** call the same writer, so the file a designer saves and the
one a script produces cannot drift apart.

Both files begin with a deterministic provenance header, written as `# key: value` comment
lines (`source`, `circuit`, `method`, `start_s`, `stop_s`, `steps`, `step_s`, `adaptive`,
`settling_band`, `points`, `completed`, plus `tolerance` for an adaptive run). There is no
wall-clock timestamp on purpose: two exports of the same run are byte-identical, which keeps
diffs and tests honest. A comment value cannot contain a line break, so a label cannot forge a
header row, and a reader that wants only the table skips lines starting with `#`.

**Copy SVG** / **Save SVG…** stamp the picture too: a `<metadata>` block carries the same
key/value pairs as a `provenance` element, and a caption line under the axis labels names the
circuit, method, step count and point count. The caption reserves its own 14 px, so it never
covers a tick, and the document is deterministic — the same run exported twice is byte-identical.
Both exports therefore describe a run the same way, whether the reader is looking at a picture or
parsing a table.

**Which circuit was solved.** Every export also carries a `netlist` fingerprint: a shortened
SHA-256 over the solved netlist written in a canonical form — the ground node, each element with
its type, nodes and values (including diode parameters), the reactive elements, any preset initial
condition and any source waveform, all ordered by id. Two circuits that solve the same equations
share it even if they were built in a different order, and changing one value changes it. It
answers the question an id cannot: `rc-charge` in two revisions of a project is the same name but
not the same circuit, and a result copied into a report now proves which one produced it. Inspect
it with `--json` (`"netlist"`), in the CSV comment block, or as a `provenance` attribute in SVG.

**Sweeping a component.** `radix transient rc.json --sweep R1=800:1200:5` solves the circuit once for
each value and prints a table of every node's peak, final value and settling time; a comma list
(`R1=500,1000,2000`) gives exact values. Each row is a full run of its own copy of the netlist, with
only that element changed, so a difference between rows belongs to the value rather than to how the
circuit was rebuilt — and its fingerprint differs, which the exports show. A value that cannot solve
appears as a failed row instead of vanishing, and the command exits non-zero if any row failed. A
diode has four parameters rather than one value, so it is rejected with an explanation instead of
being half-changed.

**Archiving a study.** `--csv` writes the sweep as a long table (one row per value and node, with
its value, netlist fingerprint and failure if any), and `--json` writes a versioned
`radix-transient-sweep` report with the same information. Long form is deliberate: metrics are per
node, and a value that fails to solve still belongs in the record instead of vanishing. Both
exports are deterministic — the same study written twice is byte-identical — so a diff shows what
changed in the circuit, and labels are sanitised and quoted so a circuit named `rc,charge` cannot
break a row.

**Comparing two studies.** `--compare archived.json` reloads a saved `--json` report and prints each
metric that differs from the study just computed, matched by swept value and node. A metric that only
one side has shows `-`, so a node or a value that appeared or disappeared is visible rather than
dropped; a value held by only one study is listed before any metric is compared. The comparison is
exact — identical circuits produce identical doubles, so any difference is a real change in the
circuit — and it refuses a report that swept a different element, or one written by another format or
version, instead of comparing unrelated studies.

Each row of a study also records the run's **energy-ledger residual** — the fraction of energy the
bookkeeping failed to account for — so a value whose step was too coarse is visible in the archive
rather than only on screen. The comparison reports that residual when both studies have it, and
`--compare-tolerance <fraction>` sets a relative threshold: the default 0 is exact, which is what one
build produces, while a small positive fraction tolerates the last bits that can differ between
machines or runtimes without hiding a real change. Tolerance is relative to the larger magnitude, so
a metric sitting at exactly zero has no scale to be tolerant of: a change there is still reported.
It never excuses a value that appeared or disappeared, a node present on one side only, or a run that
stopped completing.

**Sweeping in the app.** The Electrical tab has **Sweep**: name one element and give either a comma
list (`500,1000,2000`) or `from:to:count` (`800:1200:5`). It reuses the displayed run's circuit and
settings, so a study never mixes options, and it lists each node's peak, final value and settling time
per value with the value printed once per block and ground omitted. A value that cannot solve keeps a
row, and any edit that invalidates the run also clears the table, so on-screen numbers always describe
the selected circuit. **Copy sweep CSV/JSON** and **Save sweep CSV/JSON…** write the study through the
same writers the CLI uses, so a file produced in the app is byte-identical to one produced by
`radix transient --sweep … --csv`/`--json` for the same circuit and values.

**Comparing in the app.** **Compare…** loads an archived report and lists every metric that differs
from the displayed study, using the same comparison as `--compare`. The `rtol` box next to it sets the
relative tolerance (0 by default, exact). A report that swept a different element, or one from another
format or version, is refused with a sentence instead of being compared. A value or node that exists on
only one side is shown with `-` on the missing side, and editing anything that clears the run clears the
comparison with it. **Copy diff CSV** and **Save diff JSON…** write the comparison itself, naming the
study it was taken against, so a review of "what changed" can be attached to a change instead of
living only on screen. `--compare … --csv`/`--json` produce the same files from the command line.

## Initial-condition notes

A preset `initialValue` is legitimate — starting a capacitor pre-charged is a normal design
question — but it can start the circuit away from its own DC operating point, and then the first
samples move faster than the sources alone explain. The solver compares every preset with the
operating point across that element and reports a note when the two differ beyond rounding
(`max(|v|) * 1e-9`), for example:

```text
notes:
  'C1' starts at 2 V but its DC operating point is 5 V; the run begins with a 3 V discontinuity.
```

A note never changes a result, a failure classification, or the time grid, and an exact match
never produces one. `radix transient` prints the notes before the point table, the JSON report
carries them as `notes` (omitted when there are none), and the panel shows them above the energy
ledger so a fast first sample is explained rather than mysterious.

## Plotting a transient run

The **Electrical** tab draws the last run instead of leaving the node voltages as a
table. The geometry lives in `Radix.Editor.Rendering.TransientPlot`, which is plain
data and has no Avalonia dependency, so the scaling can be checked without a window;
`TransientPlotView` only places the points it is given.

All series share one time axis and one value axis, so nodes are directly comparable
and a 5 V step next to a 50 mV ripple is honestly shown as a ripple. The value axis
is padded by 10% of the data span, which keeps a trace off the frame edges; a flat
trace (a node that never moves) would otherwise have a zero span and be undrawable,
so it falls back to a pad of `max(|v| * 0.1, 0.5)` and is drawn down the middle.
A run longer than 600 points per node is decimated by even stride, and the *last*
sample is always kept, because that sample is the settled value a reader is looking
for; the plotted time range and tick labels still describe the true first and last
solved times, so decimation never changes the reported interval. A single sample,
an empty run, or a non-finite value produces no plot rather than a misleading one,
and the panel says so instead of drawing an empty frame.

Every node also gets a checkbox under the plot. Hiding a trace is how a designer reads a 50 mV
ripple next to a 5 V step, and the palette slot of each node is assigned from the full node list
rather than the drawn order, so hiding one trace never repaints the others — the colours in a
half-read picture stay meaningful. Hiding every trace produces the same explicit empty state as
a run with nothing to draw, and a new run starts with every trace shown.

A plotted curve answers "what shape" but not "what value at 1.5 ms". **Probe at (s)**
plus **Probe** reads every node at a chosen time and marks that time with a dashed
vertical line on the plot. A time that is not a solved sample is interpolated linearly
between the two points that bracket it, and the readout names those points, so an
interpolated value is never presented as one the integrator produced. A time outside
the run is rejected with the valid interval instead of being clamped silently, a
non-numeric entry says so, and the probe is cleared with the run by any edit or
settings change, because a readout of a stale run would be worse than none.

**Copy SVG** / **Save SVG…** render the same plot as a standalone document, so a
result can go into a note or a report without a screenshot. The exporter is a pure
function over the plot geometry with the control's own margins, so the file shows the
same frame, ticks, zero line, series colours, and probe marker; node names are escaped,
so an unusual id cannot produce a malformed file, and a size with no room for the axes
is rejected rather than silently drawn. Colours are the light palette's, because a
document has no theme to follow.

## Estimating error without a closed form

The energy residual and the metrics describe one run; neither says whether halving the
step would still change the answer. `radix transient --refine` answers that by solving
the same circuit at the requested step count and at each doubled count (three levels by
default), comparing every finer run against the coarser one sampled at the finer run's
times, with the coarse trace interpolated between its own points. When subtracting
opposite-sign extreme endpoints would overflow, interpolation uses a weighted sum
of the endpoints instead.

The comparison is reported per level, with the order implied by the ratio of successive
deviations: `p = log2(d_prev / d_now)`. A first-order scheme at `h/τ = 0.125` measures
`0.99`, and trapezoidal on the same circuit measures `1.82`, which is the honest way to
show a second-order method before it has fully reached its asymptotic range. Given that
order, the finest run's error is estimated as `d / (2^p - 1)` relative to the largest
node magnitude — the Richardson estimate — so the number describes the run the caller
is holding, not a hypothetical one. An order near zero with a large estimate means the
step is too coarse for the claim to hold, and that is reported rather than rounded away.

A failed refined solve stops the study and reports the same `DcSolveFailure`
classification as the base run. Adaptive runs are rejected: they already choose their
own step, so there is no nominal step to halve.

`--refine` also works with `--json`: the report gains a `refinement` object with
`completed`, `levels` (each with `steps`, `timeStep`, `maximumDeviationVolts`, and
`observedOrder` once two deviations exist), the finest level's `maximumDeviationVolts`,
the study's `observedOrder`, `estimatedRelativeError`, and the `worstNode`. A failed
study still reports the levels that solved plus its `failure`, so a consumer can tell
"not checked" from "checked and failed"; the field is omitted entirely when no study was
requested, and the panel includes it in **Copy JSON** / **Save JSON…** exactly when a
check has been run on the displayed result.

```bash
radix transient examples/transient-rc.radix --circuit rc-charge --stop 0.005 --steps 40 --refine
radix transient examples/transient-rc.radix --circuit rc-charge --steps 40 --method trapezoidal --refine
```

The same study is one action in the app: **Check accuracy** next to the transient result
refines the displayed run and reports the estimate, the measured order, and the node that
moved most, e.g. *Accuracy at 800 steps: estimated error at the finest step: 5.8e-3 relative
(measured order 0.989) worst node out moved 0.0285 V at t=0.000969 s.* It stays a button
because it costs three solves, it reuses the displayed run as the coarsest level so the two
cannot disagree, and it is cleared along with the run by any edit or settings change. The panel
also lists one row per level — step count, step size, the deviation from the coarser level, and
the order that deviation implies — because a single summary hides the point where the order stops
improving, which is exactly where a coarser claim would be unsupported. The coarsest row says
`reference` instead of a zero deviation, since it is what the others are measured against.

The study is three levels by default and accepts two to five through `--refine-levels <n>` or
the **Refine levels** field. That matters for a circuit whose fastest time constant sits far
below the interval: on the RC example at 40 steps a fourth level moves the measured order from
`0.989` to `0.995` and halves the estimate from `5.8e-3` to `2.9e-3`. A count outside that range
is rejected, and a study whose finest step count would exceed the solver's limit is refused with
the limit named rather than silently truncated.

## Waveform breakpoints as step boundaries

A piecewise-linear drive changes slope at its breakpoints, so the integrator never
steps across one. A fixed-step run shortens the step that would straddle a
breakpoint, lands on it, and then continues at the requested size, so `steps` is
the nominal step size and a run with breakpoints reports more points than it has
steps. An adaptive run clamps its trial step the same way. Without this, a pulse
narrower than the step would be sampled only at times where it is zero and would
vanish from the result, and a ramp kink inside a step would be integrated with a
source value that never held.

The result's first and last times are still exactly the requested start and stop
times: the final step absorbs any floating-point remainder instead of emitting a
nearly zero-length step after the interval ends. The trade-off is that a hard-edged
stroke is still limited by the integration scheme: backward Euler applies the
endpoint value across the whole step, and the adaptive absolute floor is
`tolerance * 1e-6` of the circuit scale, so a very small pulse tail is controlled
in absolute terms rather than relative to its own tiny magnitude.

## Energy ledger

Every transient run carries an energy ledger in joules: the energy the independent
sources deliver, the energy resistors and diodes dissipate, and the change in
stored reactive energy `v²C/2 + i²L/2`, each integrated over the reported points
with the trapezoidal rule. The residual `delivered - dissipated - stored`
therefore measures how well the sampled element powers agree with the reactive
state, rather than restating the solve.

That residual is also an independent accuracy signal. For a 5 V, 1 kΩ, 1 µF charge
at `h/τ = 0.01` it closes to `1.2e-5` relative; for a lossless `1 mH`/`1 µF` tank
at `hω = 0.032` it closes to `1.0e-3`; refining the step 4× cuts either residual
about 16×, which is the trapezoidal rule's second order. A backward-Euler run at
`h/τ = 0.5` shows `8e-2`, first order in the step. A large residual is therefore
an honest sign of integration error or of a too-large step, not of a wrong answer
being reported silently.

`radix transient` prints the ledger after the final operating point, and `--json`
reports it as `energy` with `deliveredJoules`, `dissipatedJoules`,
`storedChangeJoules`, `finalStoredJoules`, `residualJoules`, and
`relativeResidual`.

## Authored electrical circuits

Projects may contain one or more `electricalCircuits` (project format v12; older
versions load unchanged). Each
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
| `capacitor` | `a`/`b`, passive | capacitance | F |
| `inductor` | `a`/`b`, passive | inductance | H |

In the editor, component values, capacitor/inductor initial conditions, source
waveform times and values, transient stop/probe times, and transient sweep values
accept invariant-culture numbers with an optional SI magnitude suffix: `p`, `n`,
`u`/`µ`, `m`, `k`, `M`, or `G` (and `meg` for mega). SI symbols are case-sensitive:
use lowercase `p`, `n`, `u`, `m`, and `k`, and uppercase `M` and `G`; `meg` is a
case-insensitive spelling alias. For example, `4.7k` on a resistor stores 4,700
ohms, `2.2u` on a capacitor stores 2.2 microfarads, and `1m` in a waveform time
means 1 millisecond. Project files continue to store finite numeric values in the
component's declared unit and seconds; suffix text is only an editor input format.

Terminals are explicit bindings to circuit node IDs. The validator checks
missing, duplicate, unknown, misplaced, and self-looped terminals; invalid
values; missing grounds; and ungrounded circuits with stable `RAD-ELEC-*` codes.
This validation is separate from digital project validation, so an electrical
error does not silently change digital compile behavior. `ToDcNetlist` refuses to
run when validation has errors.

Reactive components are authored on the same terms: a `capacitor` (farads) or an
`inductor` (henries) between two passive terminals, with an optional
`initialValue` holding the initial capacitor voltage in volts or the initial
inductor current in amperes, positive from terminal `a` to terminal `b`.
A blank initial value starts the component at the DC operating point. The
initial value is rejected on any other kind (`RAD-ELEC-015`) and must be finite
(`RAD-ELEC-016`).

`radix dc` still solves a circuit that contains reactive components, using the
standard DC assumptions: a capacitor is an open circuit and is left out of the
report, while an inductor is stamped as a zero-volt source and appears as one in
the element list. Use transient analysis to see them. `ToTransientNetlist()`
exposes the same authored circuit to the transient solver, which is what
`radix transient <project.radix>` runs:

Use the authored example:

```bash
radix dc examples/dc-divider.radix
radix dc examples/dc-divider.radix --json
radix dc examples/dc-divider.radix --circuit divider
radix transient examples/transient-rc.radix --circuit rc-charge --stop 0.005 --steps 60
radix transient examples/transient-pulse.radix --circuit rc-step --stop 0.003 --steps 600
```

An independent source may carry a `waveform` array of absolute-time breakpoints
in its own unit. The transient solver interpolates linearly between breakpoints
and clamps outside the range, which makes steps, ramps, and pulses a data
edit rather than a new component. The inspector edits the same drive as a
comma-separated `time=value` list in the source's unit; an edit with fewer than
two breakpoints, a non-finite number, or non-increasing times is rejected with a
reason and leaves the circuit unchanged. A DC solve ignores the waveform and uses the
source's `value`, so the DC operating point stays the steady-state one. See
[project-format.md](project-format.md#electrical-circuits) for the validation
rules (`RAD-ELEC-017` to `RAD-ELEC-020`).

If a project has exactly one electrical circuit, `--circuit` may be omitted. A
project with several circuits requires it. Standalone JSON netlists remain
supported for tools that do not author a project.

## Editor DC sweep

One operating point does not show a transfer characteristic, so the panel has its own
**DC SWEEP**: name one component and give either a comma list (`0,2.5,5`) or
`from:to:count` (`0:5:21`), and the circuit is solved once per value by the same
`DcSweep` that `radix dc --sweep` runs. Each point is a real operating point of a fresh
copy with that one value changed, so a difference between rows belongs to the value
rather than to how the circuit was rebuilt.

The table prints the swept value once per value and one row per non-ground node, because
ground is the reference by definition and a 0 V row is not information. Each row also carries
the measured `dV(node)/d(swept)` — the same difference quotient as `radix dc --gain` — and the
summary names the steepest point, so the shoulder a gate is judged on is visible in the panel
rather than only on the command line. A value with no solved neighbour shows `-` instead of a
zero. CSV export stays the CLI's table, which carries values only, so a file saved in the panel
and one written by `radix dc --sweep --csv` remain identical.

Below the table the panel lists each node's measured **input windows** — the ranges where the
curve does not amplify, using the same analysis as `radix dc --margins` — so the levels a gate
can hold are visible next to the curve that produced them. A node whose curve never crosses
unity gain reports one window covering the sweep instead of an empty list. A value that
cannot solve keeps a row saying which failure it hit instead of vanishing, so a transfer
curve with a missing point is visible as such. **Copy sweep CSV** / **Save sweep CSV…**
write the table through `DcSweepReport.ToCsv`, the same writer the CLI uses, so a file
saved from the panel and one produced by `radix dc --sweep --csv` describe the same
study. Any edit invalidates the sweep along with the DC preview, because a curve of an
older circuit would be worse than no curve.

The sweep also **draws** its curve: the swept value on the horizontal axis, one series per
non-ground node, and one measured point per value. It reuses `TransientPlot` because the
padding, tick, decimation and palette rules are the same for any analog curve, so a long
sweep is decimated evenly and the value axis is padded rather than configurable per panel.
A value that could not solve leaves a gap in the trace rather than a point at zero, so the
picture and the table agree about where the study failed. **Copy sweep SVG** / **Save sweep
SVG…** write the curve through `TransientPlotSvg`, whose title names the circuit and the
swept component, so a transfer characteristic can go into a note or a report without a
screenshot.

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

## Editor transient preview

**Run transient** integrates the selected circuit with the same solver and report
builder as `radix transient`, using the panel's own settings: **Transient stop
(s)**, **Transient steps**, **Transient method** (`backward-euler` or
`trapezoidal`), and **Transient tolerance**. A blank tolerance runs the fixed
step count; a positive relative tolerance switches the run to adaptive stepping
with a bounded budget, so a stiff circuit does not silently report a coarse fixed
step as accurate. Those four values are view state, not project data: they are not
saved, they are not undo steps, and changing one clears the previous result
because the numbers on screen would describe an older circuit.

The panel lists solved points as time plus node voltages, sampled evenly when a
run has more than 300 points, shows the run's energy ledger, and offers **Copy
JSON** and **Save JSON…** for the same `radix-transient-report` document the CLI
writes, including the `circuit` id and `energy` object. **Copy CSV** and **Save
CSV…** write a wide table with a `time_s` column and one column per node, using
invariant round-trip numbers, and they always contain every solved point rather
than the sampled subset the panel displays. Editing the circuit,
switching circuits, or running a DC solve clears the preview instead of leaving
stale values, and a structural error or failed integration is reported in the
panel and the status bar instead of producing a result.

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
- authoring a three-terminal `mosfet` — channel, threshold voltage, and
  transconductance — in that same inspector, with its gate bound to a node like
  any other terminal, and solving it through the same `DcNetlist` bridge;
- on-demand DC solve preview of node voltages and each element's terminal
  voltage, current, and absorbed power, with explicit structural/singularity
  failures;
- copying the displayed solve result as a versioned JSON document or as one CSV
  table containing both node rows and element rows;
- arbitrary two-terminal wiring among named nodes;
- multiple independent voltage and current sources;
- series, parallel, bridge, divider, and multi-source linear networks;
- analytical node voltages, element currents, element voltages, and power;
- authoring capacitors and inductors, with optional initial capacitor voltage or
  inductor current, and independent-source piecewise-linear waveforms, in the
  editor's Electrical inspector;
- transient analysis of authored `.radix` circuits through `radix transient`,
  including reactive components, initial values, and authored piecewise-linear
  source waveforms;
- fixed-step transient analysis of resistors, capacitors, inductors, ideal
  sources with optional piecewise-linear waveforms, and Shockley diodes;
- backward Euler and trapezoidal integration with an explicit time step,
  per-element initial conditions, and per-step failure reporting;
- a per-run energy ledger (delivered, dissipated, stored, residual) that also
  measures the integration's own accuracy.

Not supported:

- transformers, op-amps, switches, relays, fuses, or transmission lines;
- dependent sources other than the MOSFET's gate-controlled channel;
- op-amps, regulators, LEDs, or temperature-dependent models;
- a production transistor model: the MOSFET is a square-law level-1 compact model with
  optional gate and drain capacitances, not a SPICE device with subthreshold
  conduction, a body effect, or geometry-derived parameters;
- junction capacitance, carrier storage, high-injection, self-heating, or noise;
- gmin continuation, or SPICE-style built-in source functions beyond
  piecewise-linear tables;
- an interactive transient plot or waveform cursor (the panel lists solved
  points as text, and `radix transient` remains the scriptable entry point);
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

## What the bias does to the gate charge

A gate capacitance is not a constant. `MosfetCapacitance` gives the fraction of the authored value
the channel presents at a bias point, by the Meyer charge split: nothing in cut-off (there is no
channel), one half towards each terminal in triode (the channel connects both), and two thirds
towards the source with nothing towards the drain in saturation, where the channel is pinched off.
The last of those is why a saturated stage stops feeding its output back into its own gate, and it
is why the authored `Cgd` is a zero-bias reference rather than a fixed coupling.

`radix ac --devices` prints the fractions as `cgs×` and `cgd×` beside `gm` and `gds`, so one table
shows the region, the gain the region allows, and the charge it presents. On the ternary inverter at
1.3 V the two saturated output devices read 0.667 and 0, while MPH in triode reads 0.5 and 0.5.

The constants are exact in the model — ⅔ and ½ — so the tests compare against the algebra rather
than a tolerance.

The transient solver can use them: `UseBiasDependentCapacitance()` re-evaluates each device
capacitor from the last accepted solution at the start of every step, so a saturated device is
charged through two thirds of its gate-source capacitance and none of its gate-drain. It is off by
default, because a constant capacitance is what every earlier measurement was taken with, and the
stamps stay explicit — the value is constant within a step, taken from the previous one, so the
charge moved is that value times the voltage change it saw.

`radix transient --bias-dependent-capacitance` runs a file with the model on, which is how the
table below was measured. On the ternary inverter over 120 ns at 1280 steps:

| quantity | constant model | bias-dependent |
|---|---|---|
| gate bus settle (2 % band) | 67.4 ns | **43.6 ns** |
| `out` settle | 49.5 ns | **38.4 ns** |
| `out_low` peak | 5.0194 V | **5.0000 V** |

`examples/transient-ternary-inverter-bias-dependent.radix` ships format 15 with the field set on
all four devices and nothing else changed, so the right-hand column reproduces without a switch:
`radix transient examples/transient-ternary-inverter-bias-dependent.radix --stop 1.2e-7 --steps 1280`.
A test compares the two files and holds their ratio to 1.45–1.65, so the table cannot drift away
from what the repository actually runs.

The gate arrives 1.55× sooner, and the overshoot above the rail disappears: with the authored
values held for the whole run, a saturated device keeps a drain-gate capacitance it should have
lost, and that Miller feedback is what pushes the output past 5 V. A single-device stage shows the
same effect more sharply — the gate load is `Cgs + (1+A)·Cgd`, so removing the drain coupling takes
the time constant down by about 4.5 when both capacitors are 1 pF and the gain is one. That closed
form, not a fitted number, is what the test asserts.

The ratio is refined across three grids (240, 480 and 960 steps) and required to stay inside 10 % of
its mean, so the figure is an effect of the model rather than of where samples landed. Arrival times
remain quantised by the step, which is why the comparison is stated as a ratio between two runs on
the same grid.

## Reading a solved node as a level

`ElectricalLevelSampler` turns a solved node voltage into a domain level, which is the index of a
value in that domain's list. Boundaries ascend; a voltage exactly on one reads as the level above
it, as a comparator with no offset would; outside the outermost boundaries the ends clamp, so a rail
that overshoots is still that rail. `TernaryFromRail(5)` places the boundaries at 1.667 V and
3.333 V, and the ternary inverter then reads level 2 at 0 s (4.98 V) and level 1 once settled
(2.50 V).

Sampling a time the run did not land on interpolates linearly between the two bracketing points, so a
probe does not have to coincide with a step. A time outside the run answers with the nearest end
rather than inventing an extrapolation.

The editor's probe uses the same conversion: each probed node reads as `g 2.5 V (L1)`, with the
level taken against the circuit's own rail — the highest source value, split into equal thirds — so
the digital reading is visible beside the voltage it came from. A circuit with no positive source
has no rail to read against and reports no levels rather than inventing one.

A circuit may author the boundaries itself with `levelThresholds` (project format 16), an ascending
list of volts, edited as `Level thresholds (V)` in the inspector. Authored boundaries outrank the
rail, so a 5 V circuit that signals between 2.5 V and 5 V can read 2.5 V as level 0; an empty list
returns the reader to the rail, and a descending, duplicated or non-numeric list is refused on load
and in the inspector rather than stored in a shape no reading could use. Editing the boundaries
invalidates the last run, as any circuit change does.

`ElectricalDriveSchedule` carries a level into the simulator. Only changes are emitted — a drive
repeating a held value adds events without adding information — and a level is written as its index,
which is how a domain identifies a value, so a ternary pin accepts it. Seconds and ticks are
different clocks, so the rate is supplied rather than guessed; when scheduling, two changes landing
on the same tick collapse to the later one, while the plain drive list keeps every change.

An end-to-end test builds a script from the ternary inverter's own run, drives a ternary gate with
it and lets the script runner's expectations check that the output followed: the circuit reads 2
then 1, and the gate turns those into 0 then 1. The runner applies the drives through the same
compiler and engine the editor and CLI use, so this is the handoff executed, not a type match.

A circuit can declare the claim itself with `driveBindings` (project format 17): a node, a digital
pin, and the ticks per second that convert solved time into simulator ticks. The rate travels with
the binding because it is part of the claim — without it the drives would land at times nobody
chose — and load refuses a binding to a node the circuit does not have, a missing pin, or a rate
that is not finite and positive. From a loaded file, `binding.Schedule(run, sampler)` needs no
further arguments.

`radix drive <project.radix>` runs a binding from the file: it solves the circuit over `--stop` and
`--steps`, reads the bound node as levels, and prints the tick, time, voltage and level of every
change. On the ternary inverter with `out` bound to `a` at one tick per nanosecond:

```
  node        pin   tick      time (s)     volts    level
  out         a     0         0.000E+000         5  2
  out         a     44        4.359E-008     3.317  1
```

The output crossing 3.333 V at 43.6 ns is the same settle the capacitance comparison measured, read
as a digital change 44 ticks in. `--json` emits the same report for a script, and a circuit that
declares no bindings is refused rather than silently doing nothing.

`--simulate` goes one step further and applies those drives: the changes become a script, the script
runs through the same runner the editor, the CLI and the tests use, and a definition of the project
takes the value. `--definition <id>` picks which definition (the root otherwise). The script carries
drives only — expectations belong to whoever knows what the digital side should answer — so a
definition without the bound pin fails with the runner's own message, `unknown interface pin 'a'
(available: d, q)`, rather than reporting a pass that proved nothing.

The editor has no equivalent control yet, so a binding is authored in the file and run from the
command line.

Project format 15 stores the choice per device as `biasDependentCapacitance`, so a file can ask
for it; the field is written only for a MOSFET, and a non-MOSFET that sets it is rejected on load
with the same rule that guards the other device fields. Format 14 files have no such field and load
with constant capacitance, which is what they were written with.

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
