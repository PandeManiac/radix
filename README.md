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
| Standard library from primitives | Gates, latch, D flip-flop, register, binary RAM, adders, muxes, ternary parts including 3:1 selectors and 3×2/9×2 RAM, balanced-ternary latch/flip-flop/register; complete CPU example |
| Project save/load | Versioned JSON (v5), strict validation, atomic writes |
| Numeric interpretation | Built-in and project-authored formats; saved numeric lanes; display-only |
| Headless CLI | Validate, simulate, scripted tests, inspect, examples, benchmark |
| Graphical editor | Orthogonal obstacle-aware routing with drag-time route reuse, bendpoints, multi-select, clipboard, search, hierarchy extraction, numeric lanes and canvas lane legend, light/dark themes, simulation, waveform, diagnostics, undo/redo |
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

The editor opens the 4-bit counter example. Pass a project path to open that file instead:

```bash
dotnet run --project src/Radix.Editor -- examples/full-adder.radix
```

It is a normal desktop application when you launch it yourself.

### Editor basics

- **Place a part** from the Library, search the library and definitions, or use `＋` beside a definition to instantiate it.
- **Wire** by dragging from one pin to another pin or to an existing wire. Wires route orthogonally around component bodies, gap at crossings, and show junction dots only at real branches.
- **Adjust routes** by double-clicking a wire to add a bendpoint, dragging its handle, or Alt+clicking a handle to remove it. Reset a route from the net inspector.
- **Select multiple parts** with Shift+click or a rubber-band drag. Drag any selected part to move the group; copy, paste, and duplicate carry internal nets as one undo step. With two or more selected, **ARRANGE SELECTION** aligns them to the left or top edge, and with three or more it spaces them evenly between the outermost edges horizontally or vertically. Arranging is one undo step and keeps a running simulation alive.
- **Inspect** by selecting a component or net. Primitive parameters, labels, positions, net initial values, wire colors, and route controls are editable. Commit a field by pressing Enter or clicking elsewhere.
- **Extract a reusable part** by selecting components, entering a name in **Reusable part**, and clicking **Extract selection**. Boundary nets become input/output pins, internal nets move into the new definition, and the parent keeps one replacement instance.
- **Expose a net** as an input or output pin from the net inspector. Give it a name first if desired; leave the name blank to use the net label.
- **Edit an interface in place** in the Interface list: rename a pin, flip its direction with `⇄`, change its domain, or move it with `↑`/`↓`. Declaration order drives canvas pin layout and the default order of new numeric lanes; connections bind by pin ID, so reordering never rewires a net. The bound net follows the pin, and each edit is one undo step. Removing a pin also prunes parent connections that pointed at it, so existing instances do not leave dangling references.
- **Read a group of pins as a number** in **NUMERIC LANES**: choose a format, tick the interface pins (first pin is least significant), and add the lane. Its decoded value updates during simulation and appears both in the inspector and in a fixed legend at the lower-left of the canvas. If every pin is an input, type a number and press Enter or click **Drive** to encode it and drive all pins together. **Edit** loads a lane back into the form; saving keeps the lane's stable ID while replacing its name, format, and pins, and cancelling leaves it untouched. Lanes are saved with the definition and copied by library export; they never change nets or signal values.
- **Author or edit a numeric format** under **CUSTOM NUMERIC FORMATS**: name it, choose a domain, enter digit values and radix, select standard/complement/sign-magnitude weighting, and add it. **Edit** loads a saved format back into the form with a live decode preview; updating keeps its stable ID so lanes keep resolving, and the whole edit is one undo step. Removing a format is refused while a lane still uses it. The bundled accumulator includes a signed two's-complement result lane as an example.
- **Navigate hierarchy** by double-clicking a composite part, or selecting it in Definitions. Edited definitions propagate to every instance. The canvas `←`/`→` controls walk the definitions you have visited in this session; opening a different definition after backing up starts a new forward trail.
- **Fork a part** with `⧉` in the Definitions list. The copy gets a new ID (`part-copy`, then `part-copy2`) and an independent body, so layout, wiring, and edits stay separate; it keeps referencing the same child parts, so hierarchy is not flattened. Duplicating a standard part is the quickest way to start a variant.
- **Share parts** with **Export part…** (current definition and dependencies) and **Import library…** (`.radixlib`). Equivalent definitions are reused; conflicting IDs are renamed instead of overwritten.
- **Simulate** with Begin, Advance, Event, Clock, and Settle. The current definition is compiled as the simulation root, so a reusable component can be tested in isolation.
- **Drive inputs** from the I/O PINS section. Each input shows its domain values as buttons.
- **Inspect signals** on the canvas badges, the I/O section, the Waveform tab, and the Trace tab. The Waveform tab shows up to 16 nets in declaration order; select a net and click **Show in waveform** in its inspector to pin it, so it is listed first. Wheel over the plot to zoom around the pointer, drag to pan, and double-click to fit the whole trace. Pins and the visible time range are scoped to the editor session; they are not saved with the project.
- **Browse nets** in the Inspect tab's **NETS** list: filter by name, ID, or domain; see each net's domain, pin count, and live value; and click a row to select its wire without changing connectivity.
- **Locate a problem** in the Diagnostics tab: messages that name a definition, instance, net, or pin have a **Locate** action that opens the owning definition, selects the target, and centers it on the canvas. Project-wide messages have no action.
- **Undo/redo** uses whole-project snapshots. Structural changes, interface/domain edits, net initial values, and primitive parameters invalidate the running simulation. Presentation-only edits—moving, labeling, or arranging parts, wire colors and bendpoints, numeric lanes/formats, and project/definition metadata—keep the engine, simulated time, and drive history alive.
- **Create domains** (for example a five-value logic or a radix-5 digit set) in the Inspect tab. Values are arbitrary labels; no arithmetic semantics are attached. Choose a **Single driver** policy, or a **Priority table** with one priority per value and a conflict index for equal-priority disagreements; the list shows each domain's values and resolution policy.
- **Choose a theme** with the Auto/Light/Dark selector in the toolbar. Auto follows the operating system; the choice is persisted per user. Canvas, wires, waveform, inspectors, menus, and diagnostics share one semantic token set.
- **Keep your workspace** across launches: window size, maximized state, the active inspector tab, and the eight most recent projects are saved per user in `workspace.json`, separately from every project. The toolbar's **Recent ▾** menu reopens a project, missing files are dropped from the list, and resizing, maximizing, and switching tabs take effect on the next launch.
- **Recover unsaved work**: while the current project has unsaved changes, the editor writes a crash snapshot (`recovery.radix` and `recovery.json` beside `workspace.json`) every 20 seconds. If the process dies, the next launch reopens that snapshot as a dirty project with its original path, so a normal save writes back to the source file. Saving, explicitly discarding, or closing cleanly removes the snapshot.

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

`.radixtest` scripts go beyond one-shot scheduling: each step advances to a tick, drives inputs, settles zero-delay logic, and asserts interface pins or decoded numeric lanes. A step can also carry a `within` window that waits a bounded number of ticks for values to converge, which tests settle-time properties without pinning an exact tick. Scripts use the same public engine API as the editor, reject unknown fields, and exit `1` on a failed assertion or unexpected halt. The format and semantics are documented in [docs/test-scripts.md](docs/test-scripts.md); [examples/tests/](examples/tests/) contains runnable regression scripts for the shipped examples.

## Examples

| File | What it demonstrates |
| --- | --- |
| [dff-demo.radix](examples/dff-demo.radix) | A master/slave D flip-flop built from NAND gates and a NOT gate |
| [full-adder.radix](examples/full-adder.radix) | Hierarchical half adders combined into a full adder |
| [binary-counter.radix](examples/binary-counter.radix) | Four DFFs plus a ripple-carry adder and a waveform clock |
| [balanced-ternary-half-adder.radix](examples/balanced-ternary-half-adder.radix) | Three-valued logic with sum and carry trits |
| [balanced-ternary-adder-2.radix](examples/balanced-ternary-adder-2.radix) | Two 2-trit numbers added through a hierarchical full adder, with three-trit result lanes |
| [ternary-adder-2.radix](examples/ternary-adder-2.radix) | Unsigned 0/1/2 addition through a hierarchical full adder, with a three-trit result lane |
| [ternary-accumulator.radix](examples/ternary-accumulator.radix) | A trit register with LOAD/ADD/HOLD control built from muxes, a ternary adder, and trit flip-flops |
| [ternary-machine.radix](examples/ternary-machine.radix) | A stored-program ternary machine: 2-trit PC, two 9-entry lookup ROMs, a trit decoder, and the accumulator |
| [ternary-branch-machine.radix](examples/ternary-branch-machine.radix) | A branch-capable ternary machine: 2-trit opcode, a reusable 3×2 trit RAM, and JMP/BZ control |
| [ternary-call-machine.radix](examples/ternary-call-machine.radix) | Subroutine control flow in ternary: CALL/RET over a nine-word RAM with a two-trit stack pointer and latched stack faults |
| [ternary-memory.radix](examples/ternary-memory.radix) | A standalone 9-word × 2-trit RAM with a two-trit address and a two-level 3:1 selector read tree |
| [balanced-ternary-accumulator.radix](examples/balanced-ternary-accumulator.radix) | A signed −/0/+ accumulator with LOAD/ADD/HOLD control, built from balanced-ternary gates and a balanced register |
| [ternary-counter.radix](examples/ternary-counter.radix) | A sequential ternary machine: trit flip-flops, increment/carry lookups, and a 0..8 count lane |
| [accumulator-cpu.radix](examples/accumulator-cpu.radix) | Instruction-controlled 4-bit accumulator with registered carry |
| [tiny-cpu.radix](examples/tiny-cpu.radix) | A complete program-driven 4-bit CPU: ROM, program counter, decoder, ALU, 16×4 RAM, stack pointer with wrap detection, direct/indirect addressing, and a subroutine program |

The examples are generated by `Radix.StdLib.ExampleProjects` and can be regenerated at any time. Matching `.radixtest` scripts in [examples/tests/](examples/tests/) exercise edge timing, counting, multi-trit ternary addition in both signed and unsigned conventions, the ternary counter's wrap, the ternary accumulator's load/add/hold/wrap sequence, the ternary program machine's full instruction loop and PC wrap, the branch machine's store/branch/load-memory loop, the call machine's nested CALL/RET sequence and stack-pointer walk, the 9×2 RAM's write/read/hold sequence across three words, the balanced-ternary accumulator's signed load/add/hold/wrap sequence, the signed accumulator lane, and the tiny CPU's fetch/decode/execute loop including RAM stores, direct and indirect loads/adds, stack push/pop, and a call/return subroutine; the core test suite runs them. The D flip-flop script uses a settle window for its capture, so it asserts that Q converges within the clock period rather than exactly one tick after the edge.

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
- **The compiler** validates through one indexed pass, expands hierarchy, merges connected authored nets with union-find, and creates flat arrays of pins, nets, and leaf components. Recursion and excessive depth are rejected. Compile time is close to linear in flattened topology; [docs/benchmarks.md](docs/benchmarks.md) records the validation-scaling fix.
- **The engine** schedules woken components and delayed output commits in a stable priority queue. Events at the same timestamp run FIFO. Non-converging zero-delay feedback halts with a diagnostic rather than spinning forever.
- **The editor** owns project state and a `SimulatorHost`; the canvas renders layout snapshots and never contains a second simulator.

See [docs/architecture.md](docs/architecture.md) for correctness rules, tradeoffs, and extension points. The project format is documented in [docs/project-format.md](docs/project-format.md). Measured engine throughput and editor drag behavior are in [docs/benchmarks.md](docs/benchmarks.md).

## Standard library semantics

`Radix.StdLib` installs definitions into a project. The engine has no special cases for them.

- Boolean gates are one- or two-input lookup primitives with a one-tick delay.
- The D latch is four NAND gates; the D flip-flop is two gated latches plus an inverter.
- Adders are structural: half adder from XOR/AND, full adder from two half adders and an OR, ripple adder from four full adders.
- The 4-bit register combines four D flip-flops with synchronous load and clear.
- The 16×4 RAM is 64 D flip-flops plus address decoders and a mux read tree. Writes are synchronous; reads are asynchronous. The engine has no memory primitive.
- The tiny CPU composes registers, a ripple adder, muxes, gates, lookup tables, and the RAM into a 16-word instruction machine with immediate, direct/indirect memory, stack, and subroutine operations. Its stack pointer is an ordinary 4-bit register; `PUSH`/`POP` and `CALL`/`RET` use RAM addresses derived from it, and a latched `STACK_ERROR` output flags pointer wrap at 0 or 15 until reset. The opcode map and program live in [ExampleProjects.cs](src/Radix.StdLib/ExampleProjects.cs); the kernel contains no processor, memory, or call-stack concepts.
- Ternary parts use project-defined three-value domains. Balanced ternary half addition produces a sum trit and carry trit documented as `A + B = 3*C + S`; a full adder extends that to three input trits, and `std.bt-adder-2` chains two full adders into a 2-trit adder with a carry trit. All three are ordinary definitions of lookup primitives and instances, so a wider adder is the same pattern repeated.
- Ternary memories are hierarchical: `std.t-ram-3x2` is three 2-trit registers, per-word write decode, and one read lookup per lane, while `std.t-ram-9x2` uses a two-trit address, nine registers, and a two-level read tree of `std.t-mux3` selectors. `std.t-mux3` is a 3:1 selector (0/1/2 pick A/B/C, unlike `std.t-mux`'s unknown middle select), so widening a memory adds selector levels instead of one huge table. All of it is ordinary definitions; the engine has no memory primitive.
- Unsigned ternary arithmetic uses the 0/1/2 digit domain: `std.t-add` is digit-wise mod-3 addition, `std.t-full-adder` produces a sum trit and a full trit carry (`COUT` is 0..2), and `std.t-adder-2` chains two of them into a three-trit result. `std.t-inc`/`std.t-carry` are the one-digit special cases used by the counter, and `std.t-mux` selects on a trit (0 picks A, 2 picks B, 1 is unknown). `std.t-reg-2` packages two trit flip-flops with a shared level load, and the accumulator example composes it with the 2-trit adder and two muxes into LOAD/ADD/HOLD behaviour; the kernel has no register or accumulator concept.
- Sequential ternary parts live on the 0/1/2 domain, where 0 is false, 2 is true, and 1 is unknown. `std.t-latch` is two cross-coupled Kleene NANDs (`std.t-lognot` plus `std.t-min`), `std.t-dff` is a master/slave pair with an inverted clock, and `std.t-inc`/`std.t-carry` compute the next trit of the counter. `std.t-not` is digit-wise mod-3 negation and deliberately differs from logic negation; the counters use arithmetic lookups, not the logic encoding, for their digit values.
- The waveform primitive is the only clock source in the examples; clocks are not a kernel concept.

Library definitions can be inspected, copied, modified, or deleted like any other definition.

## Explicit limitations

These are deliberate current boundaries, not hidden behavior:

- No analog/electrical solver. Discrete event simulation is not a substitute for SPICE-like analysis. The integration boundary is discussed in the architecture document.
- No JIT or native code generation. The managed engine measured roughly 9–10 million events per second on the development machine.
- The tiny CPU's `STACK_ERROR` detects stack-pointer wrap only; it has no knowledge of a software stack boundary or of data placed in RAM.
- The ternary RAMs are 3×2 and 9×2. Addresses, words, and read paths are built from fixed-width definitions, so a wider or deeper memory needs another generated part; there is no runtime-sized or bus-typed memory.
- Single-threaded deterministic engine. Parallel evaluation is not implemented.
- No user-supplied executable primitives. The project format never loads code; adding a primitive kind requires a code change and a format-version review.
- Buses are sequences of individual pins, not a first-class vector type. Numeric lanes group interface pins for display, but no net carries multiple values and there is no bus driver or splitter primitive. Lookup tables and hierarchical definitions remain the construction mechanism.
- Auto-routing resolves obstacles but cannot guarantee a short route in every dense design; authored bendpoints are the escape hatch. Wires can still be visually busy at low zoom. Drag frames reuse unchanged routes for responsiveness; releasing or any other edit rebuilds the committed layout canonically, which can pause briefly on a very large definition.
- Extraction refuses boundary nets driven from both sides, because the interface model has only input/output pins and no inout direction. Split the net or adjust the selection.
- Library import compares electrical structure, not layout. A local part with the same ID but different labels or positions is reused; genuinely different structure is imported under a new ID.
- Active definition and canvas pan/zoom persist in the project. Selection, external drive history, waveform history, waveform pins, and the waveform time range do not; instance positions, wire routes, and colors do. A waveform pin is a per-definition editor-session choice, at most 16 rows are shown even when more nets are pinned, and zooming pauses automatic full-trace fitting until a double-click or simulation reset.
- Numeric formats and lanes can be authored and edited in place; the preview decodes the first lane in the current definition that uses the format, not an arbitrary draft pin set. Lanes appear in the inspector and in a fixed canvas legend capped at seven rows; the legend does not move with the circuit. A lane edit cannot reorder pins independently of declaration order.
- `--set` drives static initial values and `--schedule <tick>:<pin>=<value>` adds timed drives for a one-shot run. Multi-step assertions belong in a `.radixtest` script, which can drive, settle, check pins and numeric lanes at chosen ticks, and wait a bounded number of ticks for values to converge.
- Domain creation in the editor supports single-driver and priority-table policies, and **Policy** on any listed domain edits that policy in place. In-place edits keep the domain ID, value labels, and value count, so pin and net bindings remain valid; only the resolution policy (and its priority table or conflict index) changes. Undo restores the previous policy.
- Window size, maximized state, the active inspector tab, and up to eight recent project paths persist per user. Window position and panel widths are not persisted, and deleting `workspace.json` resets the layout and the recent list. Recent entries whose files no longer exist are skipped at startup.
- Crash recovery keeps one snapshot for the latest dirty project and refreshes it on a 20-second timer, so a crash can lose up to the last 20 seconds of edits. Snapshots are restored without full validation so an intermediate invalid project can be repaired, which means a recovered project may open with diagnostics already present.
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

- Kernel and library tests: **219 passing**.
- Editor tests: **101 passing**, including offscreen Skia screenshots of the light workbench, dark workbench, waveform tab, zoomed waveform, dark waveform tab, numeric-lane inspector, canvas lane legend, lane editor, custom-format editor, domain-policy editor, diagnostics Locate action, net navigator, and interface reorder controls. An opt-in layout benchmark is included but does no work unless `RADIX_LAYOUT_BENCH=1`.
- Tests cover simultaneous events, transport delays, zero-delay divergence, legitimate sequential feedback, hierarchy, ports, custom domains, serialization round trips, malformed projects, built-in and custom numeric formats, exhaustive encode/decode ranges, scripted drive/assert runs with settle windows and malformed-window rejection, determinism, randomized differential comparison against a reference evaluator, binary RAM write/read/hold behavior, ternary 3:1 selector selection, ternary 3×2 and 9×2 RAM writes to every word with the others held, the ternary call machine's nested push/pop trace and both latched stack-fault directions, and the tiny CPU's fetch/decode/execute loop including direct and indirect memory operations, nested calls, stack underflow/overflow flags, a taken branch, and reset.
- Editor tests cover wire geometry (no segments through component bodies), drag-time route reuse with canonical release, simulation survival across presentation edits, bendpoint undo/persistence, theming, workspace preference persistence and clamping, copy/paste internal nets, search, extraction with simulation, net promotion, net navigator filtering/selection/live values, waveform pinning and time-window zoom/pan, interface pin reordering with stable bindings and lane order, numeric-lane decoding and driving, in-place lane editing with stable IDs and cancellation, custom-format authoring and in-place editing, single-driver and priority-domain authoring with validation, in-place driver-policy editing with cancellation and undo, definition duplication with unique IDs and undo, arrange-selection alignment and spacing as a presentation edit, definition back/forward navigation with forward-trail truncation, diagnostic navigation to definitions, instances, and nets, viewport centering, saved viewport restoration, and library import/export collision rules. GUI tests still do not replace human interaction; use [MANUAL_TESTING.md](MANUAL_TESTING.md) for the interactive pass.

## Near-term roadmap

1. Add an incremental compile path: validation and flattening are now close to linear (20,000 synthetic gates compile in ~134 ms), but every structural edit still recompiles the whole project. Measure before optimizing.
2. Make structural layout incremental: drag already reuses unchanged routes, but each edit still rebuilds every box and port. Measure before optimizing.
3. Extend script assertions beyond a single settled tick: stable intervals, glitch limits, and waveform-edge checks would cover properties that a `within` window deliberately does not.
4. Explore an analog boundary as a separate solver sharing the project and UI, not as a new discrete primitive.

RADIX is intentionally a foundation: the interesting architectures should live in projects, not in this engine.
