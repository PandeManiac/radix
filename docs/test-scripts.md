# RADIX test scripts

A `.radixtest` file is a deterministic experiment over a project. It drives top-level input pins at chosen simulated ticks and asserts top-level pin values, decoded numeric lanes, values on authored internal nets at any hierarchy level, or a typed relation between two signals. Scripts are JSON data, never executable code, and run through the same compiler and `SimulationEngine` as the editor.

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
| `expectNet` | Object of authored-net reference to expected value. A bare ID names a net in the simulated definition; `instance/netId` names a net under a hierarchy path. See [Internal nets](#internal-nets). |
| `expectRelation` | Array of typed comparisons between two signals: `{ "left": "q", "right": "net:ff/q_net", "op": "eq" }`. See [Relations](#relations). |
| `within` | Optional eventual assertion: `{ "ticks": N, "expect": {...}, "expectLane": {...}, "expectNet": {...}, "expectRelation": [...] }`. Values and relations must hold together at `at` or at some tick up to `at + N`, inclusive. |
| `setup` | Optional pre-drive held-value assertion with the same shape as `within`. The values must match at every tick from `at - N` through `at - 1`, inclusive, before this step's `set` drives. |
| `stable` | Optional held-value assertion with the same shape as `within`. The values must match at `at` and at every tick through `at + N`, inclusive. Cannot be combined with `within` or `expectHalted: true`. |
| `transitions` | Optional change-count assertion: `{ "ticks": N, "expect": { "pin": count }, "expectNet": { "net": count } }`. Each signal must change value exactly the given number of times from the step's tick through `at + N`, inclusive. Cannot be combined with `within`, `stable`, `edges`, or `expectHalted: true`. |
| `edges` | Optional direction-filtered edge assertion: `{ "ticks": N, "expect": { "pin": [ { "from": value, "to": value, "count": N }, ... ] }, "expectNet": { "net": [ ... ] } }`. Each filter counts only transitions that match its `from` and/or `to` value. Cannot be combined with `within`, `stable`, `transitions`, `sequence`, or `expectHalted: true`. |
| `sequence` | Optional ordered-edge assertion: `{ "ticks": N, "expect": [ { "pin": "clk", "to": 1 }, { "net": "adder/c1", "to": 1 } ] }`. Each item names a `pin` or a `net` and must occur in the given order, with unrelated transitions allowed between matches. Cannot be combined with `within`, `stable`, `transitions`, `edges`, or `expectHalted: true`. |
| `expectHalted` | Optional assertion about the engine's halt state after the step settles. |
| `description` | Optional note for readers; not interpreted. |

Each step's execution order is fixed:

1. advance through the `setup` window, from `at - N` through `at - 1`, checking its values;
2. advance to `at`;
3. apply this step's `set` drives;
4. settle zero-delay consequences at `at`;
5. check `expect`, `expectLane`, `expectNet`, and `expectRelation`;
6. run the `within` window (searching for a match), the `stable` window (checking every tick through its deadline), the `transitions` window (advancing to its deadline and counting changes), the `edges` window (advancing to its deadline and counting matching transitions), or the `sequence` window (advancing to its deadline and matching ordered transitions); a `within`, `stable`, or `setup` block may also contain `expectRelation` entries, which are checked at every sampled tick;
7. check `expectHalted`.

Transport-delayed outputs therefore appear in a later step, not at the edge that caused them. A step with no actions is a pure advance marker and is useful for setup time before a clock edge.

## Internal nets

Interface pins are the right boundary for a whole-design experiment, but some behaviour is only visible inside a part: a carry between adder stages, a decoder's internal enable, or a register's master/slave node. `expectNet` names an authored net instead of a pin:

```json
{ "at": 100, "expectNet": { "adder/c1": 1, "adder/c2": 0, "w": 1 } }
```

- A bare ID such as `"w"` names a net in the simulated definition (the project root by default, or the target of `definition`/`--definition`).
- `instance/netId` names a net inside an instance. Path segments are instance IDs from the simulated definition, joined with `/`; the last segment is the authored net ID. `"adder/c1"` means instance `adder` of the simulated definition, net `c1` inside it. Deeper paths extend the same way, for example `"cpu/regfile/word0/q_net"`.
- Each authored net is addressable even when the compiler merges it with other nets or binds it to an interface port. The assertion reads the resolved value of the merged net.
- Net IDs and path segments follow the normal identifier rule, so a typo or a blank segment is rejected when the script loads rather than silently matching another net.
- `expectNet` can be used on a step and inside `setup`, `within`, and `stable`. It uses the same value forms and domain checks as `expect`, and a window may contain only `expectNet` entries. `transitions` and `edges` also accept an `expectNet` object, and a `sequence` item may name a `net` instead of a `pin`. Relation assertions use the same hierarchy-relative references with a `net:` prefix, for example `{"left":"q","right":"net:adder/c1","op":"eq"}`.

The runner resolves the path against the compiled circuit, not against a display label. A failure names the reference, the expected and observed values, and the net's domain; an unknown reference also lists a bounded sample of the authored nets that do exist. Because the reference is hierarchy-relative, the same script can target a reusable definition directly and keep working when that definition is instantiated elsewhere.

## Timing windows

A plain `expect` names one exact tick. Real outputs often settle after a transport delay, and test authors do not always want to pin down the exact tick. A `within` window states a timing bound instead: all of its pin, lane, and internal-net expectations must hold at the same tick, at `at` or at most `ticks` later.

```json
{ "at": 20, "set": { "clk": 1 }, "within": { "ticks": 10, "expect": { "q": 1, "qn": 0 } } }
```

The runner checks the window once per integer tick, which is sufficient because signals change only at integer ticks. A match is reported as a pass even if the values change again later in the window; this is a "settles by" assertion, not a stability interval. Windows are checked after immediate `expect`/`expectLane`/`expectNet`/`expectRelation` values, so a step can assert the state at the edge and the converged state in one place.

A window advances simulated time. Later steps must be at the tick the window ended on or later; if a window never matches, the failure reports the deadline and the last observed values. A simulation halt during the window fails the window unless the expectations happened to match at the halt tick. Windows require at least one `expect`, `expectLane`, `expectNet`, or `expectRelation` entry, and `ticks` is required and non-negative.

## Setup windows

A setup window describes the ticks immediately *before* a step's drives. It is useful for clocked parts: the data and clock levels must already be correct before the edge, not merely at the tick where the edge is applied. It accepts the same `expect`, `expectLane`, `expectNet`, and `expectRelation` entries as a settle window.

```json
{ "at": 20, "set": { "clk": 1 }, "setup": { "ticks": 5, "expect": { "clk": 0, "d": 1 } } }
```

This requires `clk=0` and `d=1` at t=15, 16, 17, 18, and 19, before `clk` is driven high at t=20. All pin, lane, and internal-net expectations must hold at every sampled tick, so an event inside the window that changes the value fails the assertion. The failure names the first diverging tick and the observed values. `ticks: 0` samples the values at `at` after any events at that tick have been processed but before the step's drives are applied, which checks the immediate pre-drive state.

Because values are sampled while time advances, the window cannot reach back into already-simulated time. If the previous step ended at t=40 and the next step at t=45 asks for `ticks: 10`, the runner reports that the window starts at t=35, before the current time, instead of pretending it observed t=35..39. Space the earlier step earlier or reduce `ticks`. A setup window may be combined with a `stable`, `within`, `transitions`, `edges`, or `sequence` block on the same step, so one step can assert both pre-edge setup and post-edge behavior.

## Stability windows

A `within` window asks "does this value appear before the deadline?" and stops at the first match. A `stable` window asks the opposite: "does this value stay correct for the whole interval?" It checks `at`, then advances one tick at a time and checks again through `at + ticks`, inclusive. Any mismatch fails immediately with the tick and observed values, and the run does not advance beyond that tick.

```json
{ "at": 40, "set": { "clk": 0, "d": 0 }, "stable": { "ticks": 10, "expect": { "q": 1, "qn": 0 } } }
```

This requires `q=1` and `qn=0` at t=40, 41, …, 50. Use it for hold times, latch behavior, memory persistence, and handshake levels that must not drop. Like `within` and `setup`, it accepts internal nets through `expectNet` and typed comparisons through `expectRelation`. `ticks: 0` checks only the starting tick, which makes a `stable` window with zero duration equivalent to an immediate `expect`.

Because the interval must be observed, a simulation halt before the deadline fails the window even if the values matched at the halt tick. Combining `stable` with `expectHalted: true` is rejected at load time for the same reason. A step may use either `within` or `stable`, not both: one waits for convergence, the other requires an already-correct value to remain correct.

## Transition counts

Levels alone do not say whether a clock actually toggled or whether a state changed exactly once. A `transitions` window counts resolved value changes on interface pins or internal nets over an interval:

```json
{
  "at": 670,
  "set": { "clk": 2 },
  "transitions": { "ticks": 30, "expect": { "clk": 1, "t0": 1, "t1": 1 }, "expectNet": { "adder/c1": 1 } }
}
```

This asserts one CLK change, one low-trit change, one high-trit change, and one internal carry change between t=670 and t=700. Counting starts after the step's drives are applied at its tick and includes changes at that tick, so a drive-triggered edge is visible. It then advances to `at + ticks` and compares every signal's count; a mismatch reports the expected and observed counts plus the interval.

`count: 0` proves a value did not change, complementing `stable` (which also checks that it equals a particular value). A simulation halt anywhere through the deadline fails the window, because the full interval was not observed. Transition expectations are non-negative integers, and a window needs at least one `expect` or `expectNet` entry. Use separate consecutive steps when a test needs both held values and transition counts over adjacent intervals.

## Filtered edges

A total transition count cannot tell a rising clock edge from a falling one, or a clock edge from a simultaneous data change. An `edges` window counts only transitions that match a direction filter:

```json
{
  "at": 190,
  "set": { "clk": 2 },
  "edges": {
    "ticks": 30,
    "expect": {
      "clk": [ { "from": 0, "to": 2, "count": 1 } ],
      "t0": [ { "from": 2, "to": 0, "count": 1 }, { "from": 0, "to": 2, "count": 0 } ]
    }
  }
}
```

Each pin or net maps to an array of filters, and every filter is checked independently; use `expect` for pins and `expectNet` for authored nets. `from` and `to` use the same indices, domain labels, `null`, and `"undriven"` forms as other script values. Omit one side to count every change that enters or leaves a value: `{ "to": 1, "count": 2 }` counts all rising edges, and `{ "from": 0, "count": 2 }` counts every departure from 0. At least one side is required; a filter with neither has no direction and is rejected. `count` is required and non-negative.

The interval starts after the step's drives are applied and runs through `at + ticks`, inclusive, exactly like a `transitions` window. A count of zero is useful for proving the absence of a glitch in one direction, for example a falling edge on a line that must only rise. Values are resolved against the pin's or net's domain, so a typo in a label fails the step instead of silently matching nothing.

## Edge sequences

Some properties are about order, not counts: a clock edge must precede a capture, or a request must precede an acknowledge. A `sequence` window asserts that the listed transitions occur in order anywhere inside its interval:

```json
{
  "at": 270,
  "set": { "clk": 2 },
  "sequence": {
    "ticks": 30,
    "expect": [
      { "pin": "clk", "from": 0, "to": 2 },
      { "net": "t0_net", "from": 0, "to": 1 }
    ]
  }
}
```

Each item names exactly one of `pin` or `net` and is a single `from`/`to` filter with the same value forms as `edges`; at least one side is required. The runner scans observed transitions in commit order and starts each search after the previous match, so unrelated edges may happen in between. This also means two transitions at the same tick are ordered by the engine's deterministic event order, not by the order of keys in the script. A filter whose `from` and `to` resolve to the same value is rejected because no transition can ever match it.

The interval starts after the step's drives are applied, so a drive at the step's own tick is visible to the sequence. A missing item reports its one-based position, signal kind and name, direction, and interval. A sequence advances to its deadline; later steps must be at that tick or later.

## Relations

A value assertion names one expected value. A relation asserts how two signals compare without naming either value, which keeps an invariant readable and lets it survive a domain relabeling. `expectRelation` is an array of typed comparisons:

```json
{
  "at": 10,
  "expectRelation": [
    { "left": "q", "right": "qn", "op": "ne" },
    { "left": "q", "right": "net:ff/q_net", "op": "eq" }
  ]
}
```

Each item has exactly three fields:

| Field | Meaning |
| --- | --- |
| `left`, `right` | Signal references. A bare name or a `pin:` prefix is an interface pin; `net:` names an authored net, including a hierarchy path such as `net:adder/c1`. |
| `op` | `eq`, `ne`, `lt`, `le`, `gt`, or `ge`, compared as domain value indices. |

The signals must share one domain. Equality across two different domains is not inferred from labels, because a label such as `1` or `high` does not imply that the two domains have the same meaning. Both signals must also be driven; an undriven interface pin or net fails the relation instead of comparing the kernel's `undriven` marker as if it were a value. Comparisons are ordinal within the shared domain, so `lt` and `gt` follow the domain's declared value order.

Relations read the resolved value of each signal's compiled net, so an interface pin and an authored net merged into the same net compare equal. They can be combined with any other immediate expectation on a step, and they can appear inside `setup`, `within`, or `stable`; a window may contain only relation entries. Inside a window, the relation is evaluated at each tick the window samples, with that window's semantics: `within` searches for a tick where it holds, `stable` requires it at every tick through the deadline, and `setup` requires it at every pre-drive tick. A failed relation reports its one-based position, operator, both signal kinds and names, both domain values, and why it failed.

A relation is one binary comparison. It is deliberately not an expression language: write another array item for another comparison, or use a value assertion when the exact value is what matters.

## Values

- An integer is a domain value index (`0`, `1`, `2`, …).
- A string is a domain value label (`"high"`, `"X"`, `"+"`), matched case-insensitively. Numeric strings such as `"2"` are treated as indices.
- `null` or `"undriven"` clears an external drive. This is the kernel's undriven marker, not a domain value.
- `expectLane` values must be integers; use `expect` or `expectNet` when a raw signal index is what matters.

Values are checked against the pin's or internal net's domain. A value outside the domain fails the step with a message naming the domain and its valid range.

## Halt handling

The runner stops when the engine halts. Unexpected halts fail the current step and report the engine's reason. To test oscillation or another deliberate halt, put `"expectHalted": true` on the step where it happens; the test passes and later steps are not executed. `"expectHalted": false` asserts that the design still runs.

## CLI

```bash
dotnet run --project src/Radix.Cli -- test <project.radix> <script-or-directory> [<more>...] [--definition <id>]
```

For one script, the command prints the script name, project, definition, diagnostics, and either `PASS` with steps/ticks/events or `FAIL` with one line per failure:

```text
FAIL: 1 failure(s) after 3 step(s).
  step 3 (t=30): pin 'q': expected 1 (1), got 0 (0).
```

Several script paths, or a directory, run as a suite. Directories are searched recursively for `*.radixtest` files and their results are ordered by full path, so the report is stable. Duplicate paths are removed. Each script gets a numbered block with its path, name, definition, diagnostics, and PASS/FAIL lines; a malformed script or one that cannot compile is reported as an `ERROR` block and the remaining scripts still run:

```text
project:    D flip-flop demo
scripts:    2

[1/2] tests/a.radixtest
test:       D flip-flop captures data on the rising edge
definition: demo.dff
PASS: 4 step(s) through t=50; 82 events.

[2/2] tests/b.radixtest
ERROR: Step 1 'set.d' must be a number, a string, or null.

SUITE ERROR: 1 script(s) could not run, 0 failed, 1 passed.
```

The suite finish line is `PASS: N script(s), S step(s), E events`, `FAIL: X of N script(s) failed`, or `SUITE ERROR: …`. `--definition` overrides the definition of every script in the run; without it each script uses its own `definition` field or the project root.

Exit codes are `0` when every script passes, `1` for one or more failed assertions or unexpected halts, and `2` for a malformed script, unknown definition, missing file, empty directory, or I/O error. A suite with both errors and failures exits `2`, because not every script ran.

### From the editor

The Diagnostics tab's **Run script…** button runs one file against the current in-memory project, including unsaved edits. **Run folder…** recursively discovers every `.radixtest` below a folder and runs them through the same suite runner. The folder suite serializes the project to a private snapshot and runs on a background thread, so the canvas stays responsive and edits cannot race it; a progress row reports each completed script and **Cancel** stops between scripts. A result panel lists the PASS/FAIL summary, final tick, event count, and every failed step with its tick; suite failures are prefixed with their script file name, and a malformed script is listed as that script's error while the rest still run. **Clear** removes the panel, and loading or creating another project clears it automatically. Both runs compile and simulate on a separate engine, so they do not reset or advance the live workbench simulation. Each run reads scripts from disk, so rerun after editing one.

## Shipped scripts

`examples/tests/` contains regression scripts for the D flip-flop (which asserts a 5-tick pre-edge setup window, its capture with a 10-tick settle window, its low-phase hold with a 10-tick stability window, and the complementary `q`/`qn` invariant plus `q` against its authored internal net through relations), binary counter (which checks the ripple adder's internal carry levels and an exact one-transition carry count as well as the output bits), balanced and unsigned ternary 2-trit adders, ternary counter, ternary accumulator, ternary program machine, ternary branch machine, ternary call machine, balanced ternary accumulator, binary accumulator, and tiny program-driven CPU examples. The core test suite locates and runs them, so changing an example or its timing cannot silently invalidate the shipped demonstrations. The balanced-ternary adder script asserts both raw trit pins and the example's numeric lanes at the extremes of the −8…8 range; the unsigned-ternary adder script checks carries and the top of its 0..16 range; the ternary counter script steps all nine states through the `Count` lane, checks the wrap, counts exactly one CLK/T0/T1 change on the wrap edge, and uses a filtered edge window to distinguish the low-trit wrap and carry from the opposite transitions and an ordered sequence to require the clock edge before the trit update; the ternary accumulator script checks load, add, hold, and modulo-9 wrap; the ternary machine script steps through its whole LOAD/ADD/NOP program and PC wrap; the branch machine script follows STORE into a memory lane, a BZ that falls through, a BZ that jumps over an instruction, LOADM reading the stored word through the RAM read port, and the PC wrap back to the first instruction; the call machine script follows two nested CALLs, both RETs in LIFO order, the stack-pointer walk 0→1→2→1→0, and the read port showing each popped word; the 9×2 memory script writes words 5 and 0, reads both back through the asynchronous port, and checks that WE=1 holds word 5 while the data pins change; the stack machine script follows PUSH 2, PUSH 3, SWAP, ADD, DUP, POP, OUT, POP, and JMP 0 through TOS/SP/output/read lanes; the four-state bus script checks Z when both drivers are disabled, a driven value, an X conflict with its CONFLICT flag, and agreement; the balanced-ternary accumulator script checks its signed lanes through LOAD +4, HOLD, ADD +4 wrapping to −1, ADD +2 reaching 1, and HOLD again.

## Intentional limits

- Scripts are linear. There are no loops, variables, expressions, random drives, or reusable subroutines; generate or copy steps instead.
- Value, relation, transition, edge, and sequence assertions read top-level interface pins and authored internal nets. Value assertions also decode numeric lanes. Internal nets cannot be driven directly; use an interface input or a component instance to affect them.
- Relations compare values from one shared domain. There is no cross-domain conversion, arithmetic expression, or comparison against a decoded lane value; assert the lane separately and the signals separately.
- Assertions sample values at integer ticks. `within`, `stable`, `setup`, `transitions`, `edges`, and `sequence` express convergence, held values, pre-drive setup, total change counts, filtered edge counts, and relative edge order, not continuous time; hold time is expressed as a `stable` window after the edge. Use exact expectations, filtered edge counts, sequences, and engine traces for those properties.
- A `setup` window cannot observe time that has already been simulated; script steps must leave enough room before the edge for the requested interval.
- A suite runs its scripts sequentially and starts each from time zero. It has no parallelism, ordering dependencies, or shared state between scripts.
- The workbench's folder suite reports progress after each script and checks cancellation between scripts, so one long-running script cannot be interrupted mid-run. Its result panel lists only failing or errored scripts individually; the summary carries the pass count. Use the CLI when per-script PASS output is useful.
- A script does not save simulation state. Every run starts from time zero.

These limits keep the format inspectable and deterministic. Extending it should add explicit, typed actions rather than an embedded expression language.
