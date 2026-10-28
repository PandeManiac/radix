# RADIX

**RADIX is a deterministic circuit workbench for constructing computation from primitives.** It is not a CPU simulator with configurable options: the kernel knows nothing about binary logic, arithmetic, clocks, memory, or processors. Those concepts are built from reusable lookup, delay, waveform, constant, and probe primitives.

The repository currently contains a working simulation kernel, a hierarchical standard library, a headless CLI, a versioned project format, and an Avalonia graphical editor. Everything in the standard library—including the D flip-flop, adders, and counter—is wired from lower-level parts and stored in the project, not hardcoded in the engine.

## Status at a glance

| Area | State |
| --- | --- |
| Event-driven simulation kernel | Working, deterministic, single-threaded |
| Arbitrary discrete signal domains | Working, including custom project domains |
| Hierarchy and reusable definitions | Working, cycle-checked, flattened at compile time |
| Propagation delays and zero-delay convergence guard | Working |
| Asynchronous feedback and oscillation reporting | Working |
| Standard library from primitives | Gates, latch, D flip-flop, register, RAM, adders, mux, ternary parts; complete CPU example |
| Project save/load | Versioned JSON (v5), strict validation, atomic writes |
| Numeric interpretation | Built-in and project-authored formats; saved numeric lanes; display-only |
| Headless CLI | Validate, simulate, scripted tests, inspect, examples, benchmark |
| Graphical editor | Orthogonal obstacle-aware routing, bendpoints, multi-select, clipboard, search, hierarchy extraction, numeric lanes and canvas lane legend, light/dark themes, simulation, waveform, diagnostics, undo/redo |
| Reusable parts and libraries | Extract selection to a pinned definition, promote nets to pins, carry numeric formats, import/export `.radixlib` |
| Editor view persistence | Active definition and canvas zoom/pan save with the project; panning does not mark it dirty |
| Headless GUI tests and screenshots | Working with Avalonia Headless + Skia |
| Analog/electrical solver | Not implemented; boundary documented |
| JIT/compiled native evaluation | Not implemented; managed engine benchmarked |

## Build and test

Requires the **.NET 10 SDK** (the repository pins `10.0.113` through `global.json`; later 10.0 patches are fine).

```bash
dotnet build Radix.slnx -c Release
dotnet test tests/Radix.Core.Tests/Radix.Core.Tests.csproj
dotnet test tests/Radix.Editor.Tests/Radix.Editor.Tests.csproj
```

The editor tests use **Avalonia Headless** with offscreen Skia rendering. They never open a visible window, move the mouse, or take focus. See [MANUAL_TESTING.md](MANUAL_TESTING.md) for the interactive checks that intentionally remain manual.

## Run the workbench

```bash
dotnet run --project src/Radix.Editor
```

The editor opens the 4-bit counter example. It is a normal desktop application when you launch it yourself.

### Editor basics

- **Place a part** from the Library, search the library and definitions, or use `＋` beside a definition to instantiate it.
- **Wire** by dragging from one pin to another pin or to an existing wire. Wires route orthogonally around component bodies, gap at crossings, and show junction dots only at real branches.
- **Adjust routes** by double-clicking a wire to add a bendpoint, dragging its handle, or Alt+clicking a handle to remove it. Reset a route from the net inspector.
- **Select multiple parts** with Shift+click or a rubber-band drag. Drag any selected part to move the group; copy, paste, and duplicate carry internal nets as one undo step.
- **Inspect** by selecting a component or net. Primitive parameters, labels, positions, net initial values, wire colors, and route controls are editable. Commit a field by pressing Enter or clicking elsewhere.
- **Extract a reusable part** by selecting components, entering a name in **Reusable part**, and clicking **Extract selection**. Boundary nets become input/output pins, internal nets move into the new definition, and the parent keeps one replacement instance.
- **Expose a net** as an input or output pin from the net inspector. Give it a name first if desired; leave the name blank to use the net label.
- **Edit an interface in place** in the Interface list: rename a pin, flip its direction with `⇄`, or change its domain. The bound net follows the pin, and each edit is one undo step. Removing a pin also prunes parent connections that pointed at it, so existing instances do not leave dangling references.
- **Read a group of pins as a number** in **NUMERIC LANES**: choose a format, tick the interface pins (first pin is least significant), and add the lane. Its decoded value updates during simulation and appears both in the inspector and in a fixed legend at the lower-left of the canvas. If every pin is an input, type a number and press Enter or click **Drive** to encode it and drive all pins together. **Edit** loads a lane back into the form; saving keeps the lane's stable ID while replacing its name, format, and pins, and cancelling leaves it untouched. Lanes are saved with the definition and copied by library export; they never change nets or signal values.
- **Author or edit a numeric format** under **CUSTOM NUMERIC FORMATS**: name it, choose a domain, enter digit values and radix, select standard/complement/sign-magnitude weighting, and add it. **Edit** loads a saved format back into the form with a live decode preview; updating keeps its stable ID so lanes keep resolving, and the whole edit is one undo step. Removing a format is refused while a lane still uses it. The bundled accumulator includes a signed two's-complement result lane as an example.
- **Navigate hierarchy** by double-clicking a composite part, or selecting it in Definitions. Edited definitions propagate to every instance.
- **Share parts** with **Export part…** (current definition and dependencies) and **Import library…** (`.radixlib`). Equivalent definitions are reused; conflicting IDs are renamed instead of overwritten.
- **Simulate** with Begin, Advance, Event, Clock, and Settle. The current definition is compiled as the simulation root, so a reusable component can be tested in isolation.
- **Drive inputs** from the I/O PINS section. Each input shows its domain values as buttons.
- **Inspect signals** on the canvas badges, the I/O section, the Waveform tab, and the Trace tab.
- **Undo/redo** uses whole-project snapshots. Structural changes invalidate the running simulation, as do parameter changes.
- **Create domains** (for example a five-value logic or a radix-5 digit set) in the Inspect tab. Values are arbitrary labels; no arithmetic semantics are attached. Choose a **Single driver** policy, or a **Priority table** with one priority per value and a conflict index for equal-priority disagreements; the list shows each domain's values and resolution policy.
- **Choose a theme** with the Auto/Light/Dark selector in the toolbar. Auto follows the operating system; the choice is persisted per user. Canvas, wires, waveform, inspectors, menus, and diagnostics share one semantic token set.

Shortcuts: `Ctrl+Z` / `Ctrl+Shift+Z` / `Ctrl+Y` undo and redo; `Ctrl+C` / `Ctrl+V` / `Ctrl+D` copy, paste, and duplicate; `Ctrl+A` selects all parts; `Delete` removes the selection; `F` fits the circuit; arrows nudge selected parts; `Ctrl+wheel` zooms; middle-drag or Alt+drag pans; right-click opens canvas actions; `Ctrl+S`, `Ctrl+Shift+S`, `Ctrl+O`, and `Ctrl+N` save, save as, open, and create projects. New, Open, and window close prompt to Save, Discard, or Cancel when there are unsaved changes.

## Headless command line

```bash
# Validate and compile
dotnet run --project src/Radix.Cli -- validate examples/binary-counter.radix

# Inspect the project structure
dotnet run --project src/Radix.Cli -- inspect examples/binary-counter.radix

# Run a definition for 100 simulated ticks
dotnet run --project src/Radix.Cli -- simulate examples/binary-counter.radix --ticks 100

# Drive initial input values and show a bus as a number
dotnet run --project src/Radix.Cli -- simulate examples/full-adder.radix \
  --set a=1,b=1,cin=0 --bus s,cout --format unsigned-binary

# Sequence a clock edge: power up with CLK=0, raise it at tick 10
dotnet run --project src/Radix.Cli -- simulate examples/dff-demo.radix \
  --ticks 80 --set d=1,clk=0 --schedule 10:clk=1

# Run a deterministic scripted drive/assert suite
dotnet run --project src/Radix.Cli -- test examples/accumulator-cpu.radix \
  examples/tests/accumulator.radixtest

# Write every bundled example project
dotnet run --project src/Radix.Cli -- examples examples

# Measure the managed event engine
dotnet run -c Release --project src/Radix.Cli -- bench --gates 10000 --ticks 50
```

`--set` applies an input value at time zero; `--schedule <tick>:<pin>=<value>` adds a timed drive (repeatable, comma-separated). The counter example has its own waveform clock, so it runs forever. For an edge-triggered circuit, power the clock up at a defined low level before scheduling the rising edge, as in the D flip-flop command above.

`.radixtest` scripts go beyond one-shot scheduling: each step advances to a tick, drives inputs, settles zero-delay logic, and asserts interface pins or decoded numeric lanes. They use the same public engine API as the editor, reject unknown fields, and exit `1` on a failed assertion or unexpected halt. The format and semantics are documented in [docs/test-scripts.md](docs/test-scripts.md); [examples/tests/](examples/tests/) contains runnable regression scripts for the shipped examples.

## Examples

| File | What it demonstrates |
| --- | --- |
| [dff-demo.radix](examples/dff-demo.radix) | A master/slave D flip-flop built from NAND gates and a NOT gate |
| [full-adder.radix](examples/full-adder.radix) | Hierarchical half adders combined into a full adder |
| [binary-counter.radix](examples/binary-counter.radix) | Four DFFs plus a ripple-carry adder and a waveform clock |
| [balanced-ternary-half-adder.radix](examples/balanced-ternary-half-adder.radix) | Three-valued logic with sum and carry trits |
| [accumulator-cpu.radix](examples/accumulator-cpu.radix) | Instruction-controlled 4-bit accumulator with registered carry |
| [tiny-cpu.radix](examples/tiny-cpu.radix) | A complete program-driven 4-bit CPU: ROM, program counter, decoder, ALU, 16×4 RAM, stack pointer, and a subroutine program |

The examples are generated by `Radix.StdLib.ExampleProjects` and can be regenerated at any time. Matching `.radixtest` scripts in [examples/tests/](examples/tests/) exercise edge timing, counting, the signed accumulator lane, and the tiny CPU's fetch/decode/execute loop including RAM stores, stack push/pop, and a call/return subroutine; the core test suite runs them.

## Architecture in one page

```text
                 project JSON (.radix)
                          |
                  ProjectSerializer
                          |
        +-----------------+------------------+
        |                                    |
  CircuitValidator                     CircuitCompiler
                                             |
                                   immutable CompiledCircuit
                                             |
                                   deterministic SimulationEngine
                                             |
                 +---------------------------+------------------+
                 |                                              |
          Radix.Cli (headless)                         Radix.Editor
                 |                                              |
          text reports                                Avalonia canvas + panels
```

- **Signals** are indices into a `SignalDomain`; the engine never interprets them as numbers. `-1` is the kernel-level *undriven* marker, distinct from any domain value such as `Z`.
- **Models** describe domains, definitions, pins, instances, nets, port bindings, and primitive parameters. Models are mutable for editing; simulation compiles an immutable snapshot.
- **The compiler** validates, expands hierarchy, merges connected authored nets with union-find, and creates flat arrays of pins, nets, and leaf components. Recursion and excessive depth are rejected.
- **The engine** schedules woken components and delayed output commits in a stable priority queue. Events at the same timestamp run FIFO. Non-converging zero-delay feedback halts with a diagnostic rather than spinning forever.
- **The editor** owns project state and a `SimulatorHost`; the canvas renders layout snapshots and never contains a second simulator.

See [docs/architecture.md](docs/architecture.md) for correctness rules, tradeoffs, and extension points. The project format is documented in [docs/project-format.md](docs/project-format.md). Measured engine throughput is in [docs/benchmarks.md](docs/benchmarks.md).

## Standard library semantics

`Radix.StdLib` installs definitions into a project. The engine has no special cases for them.

- Boolean gates are one- or two-input lookup primitives with a one-tick delay.
- The D latch is four NAND gates; the D flip-flop is two gated latches plus an inverter.
- Adders are structural: half adder from XOR/AND, full adder from two half adders and an OR, ripple adder from four full adders.
- The 4-bit register combines four D flip-flops with synchronous load and clear.
- The 16×4 RAM is 64 D flip-flops plus address decoders and a mux read tree. Writes are synchronous; reads are asynchronous. The engine has no memory primitive.
- The tiny CPU composes registers, a ripple adder, muxes, gates, lookup tables, and the RAM into a 16-word instruction machine with immediate, memory, stack, and subroutine operations. Its stack pointer is an ordinary 4-bit register; `PUSH`/`POP` and `CALL`/`RET` use RAM addresses derived from it. The opcode map and program live in [ExampleProjects.cs](src/Radix.StdLib/ExampleProjects.cs); the kernel contains no processor, memory, or call-stack concepts.
- Ternary parts use project-defined three-value domains. Balanced ternary half addition produces a sum trit and carry trit documented as `A + B = 3*C + S`.
- The waveform primitive is the only clock source in the examples; clocks are not a kernel concept.

Library definitions can be inspected, copied, modified, or deleted like any other definition.

## Explicit limitations

These are deliberate current boundaries, not hidden behavior:

- No analog/electrical solver. Discrete event simulation is not a substitute for SPICE-like analysis. The integration boundary is discussed in the architecture document.
- No JIT or native code generation. The managed engine measured roughly 9–10 million events per second on the development machine.
- Single-threaded deterministic engine. Parallel evaluation is not implemented.
- No user-supplied executable primitives. The project format never loads code; adding a primitive kind requires a code change and a format-version review.
- Buses are sequences of individual pins, not a first-class vector type. Numeric lanes group interface pins for display, but no net carries multiple values and there is no bus driver or splitter primitive. Lookup tables and hierarchical definitions remain the construction mechanism.
- Auto-routing resolves obstacles but cannot guarantee a short route in every dense design; authored bendpoints are the escape hatch. Wires can still be visually busy at low zoom.
- Extraction refuses boundary nets driven from both sides, because the interface model has only input/output pins and no inout direction. Split the net or adjust the selection.
- Library import compares electrical structure, not layout. A local part with the same ID but different labels or positions is reused; genuinely different structure is imported under a new ID.
- Active definition and canvas pan/zoom persist in the project. Selection, external drive history, and waveform history do not; instance positions, wire routes, and colors do.
- Numeric formats and lanes can be authored and edited in place; the preview decodes the first lane in the current definition that uses the format, not an arbitrary draft pin set. Lanes appear in the inspector and in a fixed canvas legend capped at seven rows; the legend does not move with the circuit. A lane edit cannot reorder pins independently of declaration order.
- `--set` drives static initial values and `--schedule <tick>:<pin>=<value>` adds timed drives for a one-shot run. Multi-step assertions belong in a `.radixtest` script, which can drive, settle, and check pins and numeric lanes at chosen ticks.
- Domain creation in the editor supports single-driver and priority-table policies. A saved domain's policy cannot be changed in place yet; recreate the domain or edit the project JSON.
- Undo snapshots the whole project and retains up to 80 states. This is simple and reliable, not asymptotically optimal for very large designs.
- Very large traces are bounded (default 500,000 net changes); old changes are dropped and the UI shows only a subset of nets.

## Repository layout

```text
src/Radix.Core/          signals, model, validation, compiler, engine, serialization, interpretation, test scripts
src/Radix.StdLib/        standard definitions and example projects
src/Radix.Cli/           headless command line
src/Radix.Editor/        Avalonia workbench, canvas, view models, services
tests/Radix.Core.Tests/  kernel, compiler, serialization, determinism, differential, examples
tests/Radix.Editor.Tests/ view-model editing and isolated headless rendering
examples/                generated example projects
examples/tests/          scripted regression tests for the examples
docs/                    architecture, project format, test scripts, benchmarks
```

## Testing summary

- Kernel and library tests: **171 passing**.
- Editor tests: **60 passing**, including offscreen Skia screenshots of the light workbench, dark workbench, waveform tab, dark waveform tab, numeric-lane inspector, canvas lane legend, lane editor, custom-format editor, and domain-policy editor.
- Tests cover simultaneous events, transport delays, zero-delay divergence, legitimate sequential feedback, hierarchy, ports, custom domains, serialization round trips, malformed projects, built-in and custom numeric formats, exhaustive encode/decode ranges, scripted drive/assert runs, determinism, randomized differential comparison against a reference evaluator, RAM write/read/hold behavior, and the tiny CPU's fetch/decode/execute loop including memory operations, nested calls, a taken branch, and reset.
- Editor tests cover wire geometry (no segments through component bodies), bendpoint undo/persistence, theming, copy/paste internal nets, search, extraction with simulation, net promotion, numeric-lane decoding and driving, in-place lane editing with stable IDs and cancellation, custom-format authoring and in-place editing, single-driver and priority-domain authoring with validation, saved viewport restoration, and library import/export collision rules. GUI tests still do not replace human interaction; use [MANUAL_TESTING.md](MANUAL_TESTING.md) for the interactive pass.

## Near-term roadmap

1. Add a faster incremental compile path for large projects; measure before optimizing.
2. Add a direct indirect-addressing mode and stack-limit diagnostics to the tiny CPU, with a richer scripted program suite.
3. Persist user preferences such as theme and default panel layout separately from projects.
4. Explore an analog boundary as a separate solver sharing the project and UI, not as a new discrete primitive.

RADIX is intentionally a foundation: the interesting architectures should live in projects, not in this engine.
