# Electrical rule checking

RADIX now has a structural electrical rule checker over the authored project.
It is deliberately narrower than a SPICE-style electrical simulator: every rule
uses data the project actually contains, and every diagnostic states the
assumption behind it.

The checker runs as part of `CircuitValidator.Validate`, so the same results are
available from:

- `radix validate <project.radix>`
- `radix validate <project.radix> --all`
- `radix validate <project.radix> --json`
- the editor **Diagnostics** tab
- `CircuitCompiler` and `SimulatorHost` compile diagnostics

## Supported rules

| Code | Severity | Meaning | Nature |
| --- | --- | --- | --- |
| `RAD-ERC-001` | Warning | A net has input pins but no instance output and no input port binding. | Structural |
| `RAD-ERC-002` | Warning | A net is bound to an input port and also driven by one or more instance outputs under a single-driver domain. | Structural |
| `RAD-ERC-101` | Warning | Authored electrical nodes form an island with no conductive path to the declared ground node. | Structural |
| `RAD-ERC-102` | Warning | Authored ideal voltage sources impose contradictory voltage differences. | Structural |
| `RAD-ERC-103` | Warning | Authored ideal voltage sources form a consistent but redundant loop, so individual source currents are not unique. | Structural |
| `RAD080` | Warning | A component input pin is not connected to any net. | Structural |
| `RAD081` | Warning | A single-driver net has more than one output pin; the domain policy resolves it as a conflict. | Structural |

`RAD-ERC-001` treats an input port as an external source supplied by the parent
or testbench. A net with an explicit initial value is intentionally excluded:
the initial value is a startup/hold state, not an inferred external driver.

`RAD-ERC-002` is a structural conflict between an external input and an internal
driver. It is not a claim that a physical short circuit or overcurrent exists.

The electrical rules inspect authored `electricalCircuits` topology through the
same `DcNetlist.AnalyzeTopology` projection the DC solver uses, so the rule
checker and the solver cannot disagree about connectivity. They are deliberately
**warnings**, not errors: an electrical asset is independent of the digital
project, and an unsolvable DC circuit must not block digital compilation. Each
warning carries the `electrical/<circuitId>` path, the affected node or source
names in its message, and the usual assumption and suggestion fields.

`RAD-ERC-102` and `RAD-ERC-103` are deliberately distinct. Contradictory ideal
sources have no solution at all. A consistent but redundant source loop has
well-defined node voltages but non-unique branch currents; the rule tells the
author which condition applies instead of collapsing both into "singular".

`RAD081` preserves its existing stable code and location. It now carries an
assumption and a suggested correction. It means the selected domain's driver
policy will resolve multiple active outputs; it does not calculate current,
resistance, power, or temperature.

Multiple drivers are **not** automatically faults. A domain with an explicit
priority or multi-driver policy is allowed to share a net; a valid wired-logic or
open-drain arrangement must use that policy. The checker does not infer
open-drain behavior from a pin name or from an ideal digital output.

## Diagnostic payload

Each ERC diagnostic carries the normal stable fields:

- code
- severity
- definition, instance, net, and pin location where available
- message
- location string

It may also carry:

- `assumption`: the electrical or modelling limit behind the rule
- `suggestion`: a concrete corrective action
- `simulationRemainsValid`: whether the current simulation path can still run
- `nature`: `Structural`, `Runtime`, or `Heuristic`

The JSON validation report exposes these fields directly. The text CLI prints
the assumption and suggestion below the diagnostic line, and the editor shows
them below the diagnostic in the Diagnostics tab.

## Deliberately unsupported

RADIX does **not** yet model or claim to detect:

- node voltages, branch currents, resistance, power, or temperature
- analog or mixed-signal behavior
- transistor, diode, regulator, relay, fuse, or transformer behavior
- power rails, ground references, supply pins, polarity, or voltage domains
- component maximum ratings or datasheet limits
- overcurrent, thermal overload, or fuse trip behavior
- physical short-circuit resistance or low-resistance path analysis
- decoupling capacitors, fan-out limits, clock-domain crossings, reset
  distribution, timing violations, or signal-integrity risks
- missing power or ground connections on digital definitions

Those checks require real electrical component metadata and a solver. RADIX
must not infer them from ideal digital values.

## Electrical solver boundary

A separate core DC linear solver now covers ideal resistors, independent voltage
sources, independent current sources, and one ground reference. It has explicit
units, node/ground conventions, singular-system failures, analytical tests, and
headless JSON netlists and authored project v8 electrical circuits through
`radix dc`; see [electrical.md](electrical.md). It is not connected to the digital engine. The ERC layer now reports its
topology findings as `RAD-ERC-101`–`RAD-ERC-103` warnings before a solve, using
the solver's own analysis rather than a second connectivity model.

The next step is explicit electrical terminals and component metadata with a
documented mapping to electrical nodes. Transient, nonlinear, and component-rating
analysis come after that boundary, not by interpreting digital values as volts.

## Verification

`ElectricalRuleCheckingTests` covers positive, negative, borderline,
hierarchical, serialized, and 256-net cases. The existing example projects are
also validated in CI; a rule that floods existing designs with false positives
is treated as a regression.
