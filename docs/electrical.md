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

A fixed-step run uses one uniform step, `(stop - start) / steps`, with `steps`
between 1 and 1,000,000, and the last point lands exactly on `stop`. The run is
deterministic: identical netlists produce identical samples. Choosing `h`
against the smallest time constant or period that matters is then the user's
responsibility; roughly twenty steps per time constant is a reasonable starting
point.

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
partially solved run is never presented as a completed one.

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
| `diode` | `saturationCurrent` | `emissionCoefficient`, `thermalVoltage`, `seriesResistance` |
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
noise, temperature dependence, transformer, dependent source, switch, relay, or
transmission-line model. There is no AC or small-signal analysis, no
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
looks like.

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
times, with the coarse trace interpolated between its own points.

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
| `capacitor` | `a`/`b`, passive | capacitance | F |
| `inductor` | `a`/`b`, passive | inductance | H |

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

- transformers, dependent sources, switches, relays, fuses, or transmission
  lines;
- frequency-domain/AC analysis or small-signal extraction;
- transistors, MOSFETs, op-amps, regulators, LEDs, or temperature-dependent
  models;
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
