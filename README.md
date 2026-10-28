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
| Standard library from primitives | Gates, latch, D flip-flop, adders, mux, ternary parts |
| Project save/load | Versioned JSON (v2), strict validation, atomic writes |
| Numeric interpretation | Positional and sign-magnitude formats; display-only |
| Headless CLI | Validate, simulate, inspect, examples, benchmark |
| Graphical editor | Orthogonal obstacle-aware routing, bendpoints, multi-select, clipboard, search, hierarchy extraction, light/dark themes, simulation, waveform, diagnostics, undo/redo |
| Reusable parts and libraries | Extract selection to a pinned definition, promote nets to pins, import/export `.radixlib` |
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
- **Navigate hierarchy** by double-clicking a composite part, or selecting it in Definitions. Edited definitions propagate to every instance.
- **Share parts** with **Export part…** (current definition and dependencies) and **Import library…** (`.radixlib`). Equivalent definitions are reused; conflicting IDs are renamed instead of overwritten.
- **Simulate** with Begin, Advance, Event, Clock, and Settle. The current definition is compiled as the simulation root, so a reusable component can be tested in isolation.
- **Drive inputs** from the I/O PINS section. Each input shows its domain values as buttons.
- **Inspect signals** on the canvas badges, the I/O section, the Waveform tab, and the Trace tab.
- **Undo/redo** uses whole-project snapshots. Structural changes invalidate the running simulation, as do parameter changes.
- **Create domains** (for example a five-value logic or a radix-5 digit set) in the Inspect tab. Values are arbitrary labels; no arithmetic semantics are attached.
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

# Write every bundled example project
dotnet run --project src/Radix.Cli -- examples examples

# Measure the managed event engine
dotnet run -c Release --project src/Radix.Cli -- bench --gates 10000 --ticks 50
```

`--set` applies an input value at time zero; `--schedule <tick>:<pin>=<value>` adds a timed drive (repeatable, comma-separated). The counter example has its own waveform clock, so it runs forever. For an edge-triggered circuit, power the clock up at a defined low level before scheduling the rising edge, as in the D flip-flop command above.

## Examples

| File | What it demonstrates |
| --- | --- |
| [dff-demo.radix](examples/dff-demo.radix) | A master/slave D flip-flop built from NAND gates and a NOT gate |
| [full-adder.radix](examples/full-adder.radix) | Hierarchical half adders combined into a full adder |
| [binary-counter.radix](examples/binary-counter.radix) | Four DFFs plus a ripple-carry adder and a waveform clock |
| [balanced-ternary-half-adder.radix](examples/balanced-ternary-half-adder.radix) | Three-valued logic with sum and carry trits |
| [accumulator-cpu.radix](examples/accumulator-cpu.radix) | Instruction-controlled 4-bit accumulator with registered carry |

The examples are generated by `Radix.StdLib.ExampleProjects` and can be regenerated at any time.

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
- Ternary parts use project-defined three-value domains. Balanced ternary half addition produces a sum trit and carry trit documented as `A + B = 3*C + S`.
- The waveform primitive is the only clock source in the examples; clocks are not a kernel concept.

Library definitions can be inspected, copied, modified, or deleted like any other definition.

## Explicit limitations

These are deliberate current boundaries, not hidden behavior:

- No analog/electrical solver. Discrete event simulation is not a substitute for SPICE-like analysis. The integration boundary is discussed in the architecture document.
- No JIT or native code generation. The managed engine measured roughly 9–10 million events per second on the development machine.
- Single-threaded deterministic engine. Parallel evaluation is not implemented.
- No user-supplied executable primitives. The project format never loads code; adding a primitive kind requires a code change and a format-version review.
- Buses are sequences of individual pins, not a first-class vector type. Lookup tables and hierarchical definitions are the intended construction mechanism.
- Auto-routing resolves obstacles but cannot guarantee a short route in every dense design; authored bendpoints are the escape hatch. Wires can still be visually busy at low zoom.
- Extraction refuses boundary nets driven from both sides, because the interface model has only input/output pins and no inout direction. Split the net or adjust the selection.
- Library import compares electrical structure, not layout. A local part with the same ID but different labels or positions is reused; genuinely different structure is imported under a new ID.
- Editor pan/zoom, active tab, and selection are not persisted in the project yet; instance positions, wire routes, and colors are.
- Numeric interpretation is a CLI/reporting feature in this build, not a saved display preference in the editor.
- `--set` drives only static initial values. Edge sequences require the editor's clock command, a waveform source, or a future timed CLI event option.
- Domain creation in the editor uses a single-driver policy. Projects loaded from JSON can still declare priority/conflict policies, but the GUI does not edit those policies yet.
- Undo snapshots the whole project and retains up to 80 states. This is simple and reliable, not asymptotically optimal for very large designs.
- Very large traces are bounded (default 500,000 net changes); old changes are dropped and the UI shows only a subset of nets.

## Repository layout

```text
src/Radix.Core/          signals, model, validation, compiler, engine, serialization, interpretation
src/Radix.StdLib/        standard definitions and example projects
src/Radix.Cli/           headless command line
src/Radix.Editor/        Avalonia workbench, canvas, view models, services
tests/Radix.Core.Tests/  kernel, compiler, serialization, determinism, differential, examples
tests/Radix.Editor.Tests/ view-model editing and isolated headless rendering
examples/                generated example projects
docs/                    architecture, format, benchmarks
```

## Testing summary

- Kernel and library tests: **141 passing**.
- Editor tests: **35 passing**, including offscreen Skia screenshots of the light workbench, dark workbench, waveform tab, and dark waveform tab.
- Tests cover simultaneous events, transport delays, zero-delay divergence, legitimate sequential feedback, hierarchy, ports, custom domains, serialization round trips, malformed projects, numeric formats, determinism, and randomized differential comparison against a reference evaluator.
- Editor tests cover wire geometry (no segments through component bodies), bendpoint undo/persistence, theming, copy/paste internal nets, search, extraction with simulation, net promotion, and library import/export collision rules. GUI tests still do not replace human interaction; use [MANUAL_TESTING.md](MANUAL_TESTING.md) for the interactive pass.

## Near-term roadmap

1. Add a scripted/assertion test harness for timed CLI simulations and edge-triggered circuits.
2. Add first-class bus/vector construction helpers without baking numeric meaning into signals.
3. Persist viewport, active tab, and editor preferences with the project or user settings.
4. Add a faster incremental compile path for large projects; measure before optimizing.
5. Extend the accumulator into an instruction-driven processor example with a small custom ISA.
6. Explore an analog boundary as a separate solver sharing the project and UI, not as a new discrete primitive.

RADIX is intentionally a foundation: the interesting architectures should live in projects, not in this engine.
