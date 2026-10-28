# RADIX test scripts

A `.radixtest` file is a deterministic experiment over a project. It drives top-level input pins at chosen simulated ticks and asserts top-level pin values or decoded numeric lanes. Scripts are JSON data, never executable code, and run through the same compiler and `SimulationEngine` as the editor.

Scripts stay outside `.radix` projects on purpose: a project describes a circuit, while a script describes one experiment over that circuit. The same project can have many scripts, and a script can target a non-root definition for isolated component tests.

```json
{
  "format": "radix-test",
  "version": 1,
  "name": "D flip-flop captures data on the rising edge",
  "definition": "demo.dff",
  "steps": [
    { "at": 0, "set": { "clk": 0, "d": 1 }, "expect": { "q": 0, "qn": 1 } },
    { "at": 20, "set": { "clk": 1 } },
    { "at": 30, "expect": { "q": 1, "qn": 0 } }
  ]
}
```

## Top-level fields

| Field | Required | Meaning |
| --- | --- | --- |
| `format` | yes | Must be `radix-test`. |
| `version` | yes | `1` in this build. Newer versions are rejected, not guessed. |
| `name` | no | Reported by the CLI and used as the test name. |
| `description` | no | Optional note. |
| `definition` | no | Definition to simulate. Defaults to the project root, and `--definition` overrides it. |
| `steps` | yes | At least one ordered step. |

Unknown fields are rejected so a misspelled assertion cannot silently pass.

## Steps

| Field | Meaning |
| --- | --- |
| `at` | Absolute simulated tick. Omit it to reuse the previous step's tick; the first step defaults to 0. |
| `set` | Object of input pin ID to value. Drives are external inputs and persist until changed or cleared with `null`/`"undriven"`. |
| `expect` | Object of interface pin ID to expected value. Both inputs and outputs can be read. |
| `expectLane` | Object of numeric lane ID to expected decoded integer. The lane's pins, order, and format come from the definition. |
| `within` | Optional eventual assertion: `{ "ticks": N, "expect": {...}, "expectLane": {...} }`. Values must hold together at `at` or at some tick up to `at + N`, inclusive. |
| `stable` | Optional held-value assertion with the same shape as `within`. The values must match at `at` and at every tick through `at + N`, inclusive. Cannot be combined with `within` or `expectHalted: true`. |
| `expectHalted` | Optional assertion about the engine's halt state after the step settles. |
| `description` | Optional note for readers; not interpreted. |

Each step's execution order is fixed:

1. advance to `at`;
2. apply this step's `set` drives;
3. settle zero-delay consequences at `at`;
4. check `expect` and `expectLane`;
5. run the `within` window (searching for a match) or the `stable` window (checking every tick through its deadline);
6. check `expectHalted`.

Transport-delayed outputs therefore appear in a later step, not at the edge that caused them. A step with no actions is a pure advance marker and is useful for setup time before a clock edge.

## Timing windows

A plain `expect` names one exact tick. Real outputs often settle after a transport delay, and test authors do not always want to pin down the exact tick. A `within` window states a timing bound instead: all of its pin and lane expectations must hold at the same tick, at `at` or at most `ticks` later.

```json
{ "at": 20, "set": { "clk": 1 }, "within": { "ticks": 10, "expect": { "q": 1, "qn": 0 } } }
```

The runner checks the window once per integer tick, which is sufficient because signals change only at integer ticks. A match is reported as a pass even if the values change again later in the window; this is a "settles by" assertion, not a stability interval. Windows are checked after immediate `expect`/`expectLane` values, so a step can assert the state at the edge and the converged state in one place.

A window advances simulated time. Later steps must be at the tick the window ended on or later; if a window never matches, the failure reports the deadline and the last observed values. A simulation halt during the window fails the window unless the expectations happened to match at the halt tick. Windows require at least one `expect` or `expectLane` entry, and `ticks` is required and non-negative.

## Stability windows

A `within` window asks "does this value appear before the deadline?" and stops at the first match. A `stable` window asks the opposite: "does this value stay correct for the whole interval?" It checks `at`, then advances one tick at a time and checks again through `at + ticks`, inclusive. Any mismatch fails immediately with the tick and observed values, and the run does not advance beyond that tick.

```json
{ "at": 40, "set": { "clk": 0, "d": 0 }, "stable": { "ticks": 10, "expect": { "q": 1, "qn": 0 } } }
```

This requires `q=1` and `qn=0` at t=40, 41, …, 50. Use it for hold times, latch behavior, memory persistence, and handshake levels that must not drop. `ticks: 0` checks only the starting tick, which makes a `stable` window with zero duration equivalent to an immediate `expect`.

Because the interval must be observed, a simulation halt before the deadline fails the window even if the values matched at the halt tick. Combining `stable` with `expectHalted: true` is rejected at load time for the same reason. A step may use either `within` or `stable`, not both: one waits for convergence, the other requires an already-correct value to remain correct.

## Values

- An integer is a domain value index (`0`, `1`, `2`, …).
- A string is a domain value label (`"high"`, `"X"`, `"+"`), matched case-insensitively. Numeric strings such as `"2"` are treated as indices.
- `null` or `"undriven"` clears an external drive. This is the kernel's undriven marker, not a domain value.
- `expectLane` values must be integers; use pin expectations when a raw signal index is what matters.

Values are checked against the pin's domain. A value outside the domain fails the step with a message naming the domain and its valid range.

## Halt handling

The runner stops when the engine halts. Unexpected halts fail the current step and report the engine's reason. To test oscillation or another deliberate halt, put `"expectHalted": true` on the step where it happens; the test passes and later steps are not executed. `"expectHalted": false` asserts that the design still runs.

## CLI

```bash
dotnet run --project src/Radix.Cli -- test <project.radix> <script.radixtest> [--definition <id>]
```

The command prints the script name, project, definition, diagnostics, and either `PASS` with steps/ticks/events or `FAIL` with one line per failure:

```text
FAIL: 1 failure(s) after 3 step(s).
  step 3 (t=30): pin 'q': expected 1 (1), got 0 (0).
```

Exit codes are `0` for a passing script, `1` for a failed assertion or unexpected halt, and `2` for a malformed script, unknown definition, or I/O error.

## Shipped scripts

`examples/tests/` contains regression scripts for the D flip-flop (which asserts its capture with a 10-tick settle window and its low-phase hold with a 10-tick stability window), binary counter, balanced and unsigned ternary 2-trit adders, ternary counter, ternary accumulator, ternary program machine, ternary branch machine, ternary call machine, balanced ternary accumulator, binary accumulator, and tiny program-driven CPU examples. The core test suite locates and runs them, so changing an example or its timing cannot silently invalidate the shipped demonstrations. The balanced-ternary adder script asserts both raw trit pins and the example's numeric lanes at the extremes of the −8…8 range; the unsigned-ternary adder script checks carries and the top of its 0..16 range; the ternary counter script steps all nine states through the `Count` lane and checks the wrap; the ternary accumulator script checks load, add, hold, and modulo-9 wrap; the ternary machine script steps through its whole LOAD/ADD/NOP program and PC wrap; the branch machine script follows STORE into a memory lane, a BZ that falls through, a BZ that jumps over an instruction, LOADM reading the stored word through the RAM read port, and the PC wrap back to the first instruction; the call machine script follows two nested CALLs, both RETs in LIFO order, the stack-pointer walk 0→1→2→1→0, and the read port showing each popped word; the 9×2 memory script writes words 5 and 0, reads both back through the asynchronous port, and checks that WE=1 holds word 5 while the data pins change; the stack machine script follows PUSH 2, PUSH 3, SWAP, ADD, DUP, POP, OUT, POP, and JMP 0 through TOS/SP/output/read lanes; the four-state bus script checks Z when both drivers are disabled, a driven value, an X conflict with its CONFLICT flag, and agreement; the balanced-ternary accumulator script checks its signed lanes through LOAD +4, HOLD, ADD +4 wrapping to −1, ADD +2 reaching 1, and HOLD again.

## Intentional limits

- Scripts are linear. There are no loops, variables, expressions, random drives, or reusable subroutines; generate or copy steps instead.
- Assertions read top-level interface pins and definition numeric lanes only. Internal nets are available through `--trace` and the waveform UI, not script assertions.
- Assertions sample values at integer ticks. `within` and `stable` express convergence and held values, not continuous time, waveform-edge order, glitch counts, or minimum setup/hold constraints, and there is no assertion over internal nets. Use exact expectations and engine traces for those properties.
- A script does not save simulation state. Every run starts from time zero.

These limits keep the format inspectable and deterministic. Extending it should add explicit, typed actions rather than an embedded expression language.
