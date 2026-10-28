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
| `expectHalted` | Optional assertion about the engine's halt state after the step settles. |
| `description` | Optional note for readers; not interpreted. |

Each step's execution order is fixed:

1. advance to `at`;
2. apply this step's `set` drives;
3. settle zero-delay consequences at `at`;
4. check `expect` and `expectLane`;
5. check `expectHalted`.

Transport-delayed outputs therefore appear in a later step, not at the edge that caused them. A step with no actions is a pure advance marker and is useful for setup time before a clock edge.

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

`examples/tests/` contains regression scripts for the D flip-flop, binary counter, balanced and unsigned ternary 2-trit adders, ternary counter, ternary accumulator, ternary program machine, ternary branch machine, balanced ternary accumulator, binary accumulator, and tiny program-driven CPU examples. The core test suite locates and runs them, so changing an example or its timing cannot silently invalidate the shipped demonstrations. The balanced-ternary adder script asserts both raw trit pins and the example's numeric lanes at the extremes of the −8…8 range; the unsigned-ternary adder script checks carries and the top of its 0..16 range; the ternary counter script steps all nine states through the `Count` lane and checks the wrap; the ternary accumulator script checks load, add, hold, and modulo-9 wrap; the ternary machine script steps through its whole LOAD/ADD/NOP program and PC wrap; the branch machine script follows STORE into a memory lane, a BZ that falls through, a BZ that jumps over an instruction, LOADM reading the stored word through the RAM read port, and the PC wrap back to the first instruction; the balanced-ternary accumulator script checks its signed lanes through LOAD +4, HOLD, ADD +4 wrapping to −1, ADD +2 reaching 1, and HOLD again.

## Intentional limits

- Scripts are linear. There are no loops, variables, expressions, random drives, or reusable subroutines; generate or copy steps instead.
- Assertions read top-level interface pins and definition numeric lanes only. Internal nets are available through `--trace` and the waveform UI, not script assertions.
- There is no waveform or timing-window assertion yet. Check values at specific ticks, or use the engine trace programmatically.
- A script does not save simulation state. Every run starts from time zero.

These limits keep the format inspectable and deterministic. Extending it should add explicit, typed actions rather than an embedded expression language.
