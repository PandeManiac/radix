# RADIX architecture

This document records the design constraints, the implemented simulation model, and the tradeoffs that future work must respect.

## 1. Design constraints

RADIX is a workbench for constructing computational machinery, not a binary computer simulator. The kernel must not assume:

- two-valued signals,
- Boolean interpretation of values,
- two's-complement or any numeric encoding,
- fixed word widths,
- synchronous clocks,
- a particular processor or instruction set,
- a particular memory organization.

The enforceable rule is: **if a concept can be built from lower-level components, the engine should not implement it directly.** The built-in primitive set is intentionally small. Gates, latches, flip-flops, adders, and the example counter all live in project definitions.

Consequences:

- A signal value is an integer index into a domain. It is not a bit.
- A net can hold any finite-state value defined by its domain.
- Clocking is an emergent property of feedback and delays. A `WaveformSpec` is just a source primitive, not a global clock.
- Numeric interpretation is a pure display-side operation.

## 2. Layers

| Layer | Location | Responsibility |
| --- | --- | --- |
| Signal domains | `src/Radix.Core/Signals` | Value sets, driver resolution, undriven marker |
| Circuit model | `src/Radix.Core/Model` | Definitions, pins, nets, port bindings, primitives, validation |
| Compiler | `src/Radix.Core/Compilation` | Hierarchy expansion, net merging, flat immutable arrays |
| Engine | `src/Radix.Core/Simulation` | Deterministic event queue, primitive runtimes, traces, diagnostics |
| Project format | `src/Radix.Core/Serialization` | Versioned JSON, strict mapping, secure loading |
| Interpretation | `src/Radix.Core/Interpretation` | Display-only positional and sign-magnitude formats; project format catalog |
| Standard library | `src/Radix.StdLib` | Reusable project definitions and examples |
| CLI | `src/Radix.Cli` | Headless validation, simulation, reports, benchmarks |
| Test scripts | `src/Radix.Core/Testing` | Deterministic drive/assert experiments over the public engine |
| Input sweep | `src/Radix.Core/Analysis` | Exhaustive, domain-agnostic input/output observation |
| Editor | `src/Radix.Editor` | Mutable project state, layout, rendering, gesture handling |

Dependencies point inward: the editor and CLI depend on Core; Core does not depend on Avalonia or the standard library.

## 3. Signal model

```csharp
public readonly record struct SignalValue(int Index);
public const int UndrivenIndex = -1;
```

A `SignalDomain` is immutable after construction and contains:

- a stable string ID,
- display name and optional description,
- an ordered list of value labels (`ValueCount`, `ValueName`),
- a driver-resolution policy.

The kernel treats `-1` as **undriven**, which is not necessarily a value of the domain. A domain may also define its own high-impedance or unknown symbol, as the four-state domain does. This distinction matters: "no active driver" is a net-resolution state, not a signal value that user logic can inspect directly.

### 3.1 Driver policies

`IDriverResolution` receives all active driver values in compiled pin order and returns `(value, conflict)`.

- `SingleDriverResolution`: one active driver passes through; multiple active drivers produce a deterministic conflict value and a conflict flag. The conflict value is currently the first driver's value for binary-compatible behavior, but the warning identifies the net.
- `PriorityDriverResolution`: a per-domain priority table chooses the winner; if equal-priority drivers disagree, `ConflictValue` is used and `Conflict` is reported.

Resolution depends only on values and their compiled order, never on wall-clock time or insertion order of unrelated events. Per-net conflict diagnostics are emitted once per net per run.

`examples/four-state-bus.radix` exercises that path without any new primitive kind. Two driver lookups on the 0/1/X/Z domain share one bus net. A disabled driver outputs Z (index 3), so it yields to any driven value; two different driven values tie on priority and resolve to X (index 2), which also raises RAD101 once for the net. A one-input lookup turns X into a binary `CONFLICT` observation. The example is deliberately small: it shows that multi-driver policy, tri-state values, and conflict diagnostics are project structure rather than engine features.

### 3.2 Domain creation

Projects own complete domain definitions, so loading a project never depends on a global registry. The editor can create a domain with arbitrary unique labels and either a single-driver policy or a priority table: one non-negative priority per value plus a conflict value index. The form validates the table length and conflict range before constructing the immutable domain, and the inspector lists each domain's values and policy.

A saved domain's policy can be edited in place with **Policy**. Because `SignalDomain` is immutable, the edit constructs a new domain with the same ID, value labels, and count, then replaces the entry in `CircuitProject.Domains`; nets, pins, and numeric formats keep referring to the same ID. Value labels and count are locked during the edit, so no existing signal can become invalid. The whole replacement is one undo step, and any project replacement (new/open/undo/redo) cancels an edit in progress. Policy-neutral structure is deliberately untouched: the compiler re-resolves drivers from the domain on the next compile.

## 4. Hierarchy and flattening

A `CircuitDefinition` has:

- interface pins (`PinDeclaration`), each with a direction and domain;
- instances, each referring to either another definition or an inline primitive spec;
- authored nets with a domain, optional initial value, and pin references;
- port bindings from interface pin ID to net ID.

Definitions can instantiate other definitions, including multiple times. The model stores no separate runtime object per hierarchy level.

`CircuitCompiler`:

1. Runs `CircuitValidator` over the entire project.
2. Detects missing root, missing definitions, and recursive definition cycles before instantiation.
3. Recursively expands the root definition. Each authored net and primitive pin is an element in a union-find structure. Connecting a child port to a parent net unions the corresponding elements.
4. Assigns compiled indices to pin elements and net groups.
5. Rejects conflicting domains, duplicate/unknown pins, initial-value conflicts, and invalid primitive/domain combinations.
6. Produces `CompiledCircuit` with flat arrays and lookup maps from `(path, netId)` and `(path, instanceId, pinId)` to indices.

The flattened representation is immutable. That is why the engine has no dictionary lookups in its hot loop and why the editor can keep editing the mutable project while an older compiled snapshot remains untouched.

Maximum hierarchy depth is 64. Recursive cycles are a validation error, not a stack overflow.

### 4.1 Compiled arrays

`CompiledCircuit` contains:

- `CompiledComponent`: instance path/definition/instance IDs, primitive spec, first pin, pin count.
- `CompiledPin`: owning component, local pin index, stable pin ID, direction, net index.
- `CompiledNet`: resolved domain, driver pin indices, distinct sink component indices, initial value, label, representative authored net metadata.
- `CompiledPort`: root interface pin to net index.

Net groups merge multiple authored nets into one compiled net, including nets from inside child instances. Ports of nested definitions do not become runtime entities; they are resolved into compiled nets. Only root ports are exposed for external input and reports.

## 5. Primitive semantics

The built-in primitive set is deliberately small and domain-agnostic.

| Primitive | Inputs | Outputs | Semantics |
| --- | --- | --- | --- |
| `constant` | 0 | 1 | Emits one domain index |
| `lookup` | 0–16 | 1 | Mixed-radix truth table; optional transport delay |
| `delay` | 1 | 1 | Transport delay with explicit initial output |
| `waveform` | 0 | 1 | Cyclic phases with per-phase durations |
| `probe` | 1 | 0 | No semantic effect; named observation point |

The lookup table is indexed by input pin declaration order, first input most significant, using each connected net's domain radix. For a two-input binary lookup the table has four entries; for two ternary inputs it has nine.

Every primitive validates its table/phase/value indices against the connected domains at compile time. A primitive whose table is too small is a compile error, not a silent modulo or fallback.

### 5.1 Why lookup tables instead of hardcoded gates

A lookup table is an explicit behavioral primitive. A NAND is a project definition containing a two-input lookup with the table `[1,1,1,0]`. This keeps the engine honest and lets users create ternary, quaternary, or custom-domain functions without adding code. It also makes standard-library definitions inspectable and editable.

The cost is that a lookup evaluation computes a table index rather than executing a specialized Boolean instruction. Benchmarks show the managed event overhead dominates for event-driven designs, and the tradeoff preserves extensibility. A future optimizing compiler may recognize common lookup shapes without changing semantics.

### 5.2 Delay models

- A lookup's output commit is scheduled at `now + DelayTicks`.
- A delay primitive initializes its output at its declared initial value and schedules each accepted input commit at `now + DelayTicks`.
- Zero-delay lookup outputs commit at the current timestamp, but only after the evaluation event completes. A component is evaluated at most once per wake, and a commit that resolves a net schedules affected components for the same timestamp.

This is a **transport-delay, event-driven** model with zero-delay logical evaluation. It is not an inertial-delay or analog RC model. Users who want sequential feedback construct it with latches/flip-flops and deliberate delays.

### 5.3 Worked example: the tiny program-driven CPU

`examples/tiny-cpu.radix` demonstrates that a processor is ordinary project structure. It contains no new primitive kinds and required no kernel change:

- a 4-bit program counter made from `std.reg-4`, a ripple adder, muxes, and an OR gate;
- eight four-input lookup tables forming a 16-word × 8-bit instruction ROM;
- one four-input lookup per opcode, decoding instruction bits 7–4 into control lines;
- a zero flag decoded from the accumulator and combined with `JZ` to select a branch;
- a 4-bit accumulator with an adder, bitwise AND/OR paths, a four-stage mux network, and `std.reg-4`;
- a registered carry flag updated by the arithmetic instructions and held by other instructions;
- a 16-word × 4-bit RAM built from 64 D flip-flops, a write-address decoder, and a four-level read mux tree;
- a 4-bit stack pointer, an adder that increments or decrements it, and stack-address muxes shared with the RAM port.

Instructions are `opcode(4) | operand(4)`: `0` NOP, `1` LDA #, `2` ADD #, `3` STA addr, `4` AND #, `5` OR #, `6` JMP, `7` JZ, `8` LDA@ addr, `9` ADD@ addr, `10` PUSH, `11` POP, `12` CALL addr, `13` RET, `14` LDA addr, `15` ADD addr. The operand supplies an immediate, a direct RAM address, or, for the `@` forms, a RAM address whose contents are the effective address. SP starts at 0 and points to the next free slot; `PUSH` writes at SP and increments it, `POP` reads at SP-1 and decrements it, `CALL` pushes PC+1 and branches, and `RET` pops a return address into PC. A latched `STACK_ERROR` output compares SP with 0 and 15 and records a `POP`/`RET` underflow or a `PUSH`/`CALL` overflow until reset; it deliberately does not detect a software stack growing into program data. The program is the initializer of the ROM lookups, so a different program produces a different circuit without changing definitions or code. Numeric lanes expose the program counter, accumulator, stack pointer, memory-read word, opcode, operand, and full instruction word for inspection. RAM cells, decoders, muxes, and the stack pointer are ordinary definitions; neither the compiler nor the engine knows what memory, a stack, or an instruction is.

The library scales the same way when a design needs a wider datapath. `std.alu-8` composes the existing `std.ripple-adder-8` with per-bit AND, OR, and XOR gates and a two-level mux tree, selecting one of ADD (00), AND (01), OR (10), or XOR (11) from `op1:op0`. It exposes a `CARRY` flag that carries the adder's carry for ADD and is 0 for logic operations, and a `ZERO` flag computed as a NOR reduction over all eight result bits. `examples/8-bit-alu.radix` wraps the part with named `A`, `B`, and `Y` numeric lanes so the operation and flags are visible on the canvas; `examples/tests/eight-bit-alu.radixtest` drives every operation, both carry outcomes, and the zero flag. The ALU is a definition like any other: the compiler knows nothing about arithmetic, flags, or operand selection, and an editor user can open, copy, or replace it.

The algebra extends without new primitives. `std.mult-4` forms sixteen AND partial products from two four-bit operands and accumulates them with three `std.adder-8` instances shifted by 0, 1, and 2 bits, exposing the full eight-bit product. `examples/multiplier.radix` wraps it with `A`, `B`, and `P` numeric lanes, and the test suite checks every one of its 256 operand pairs; the engine never learns what multiplication is.

This is deliberately a *demonstration*, not an architectural commitment: the definition format, library, and simulator do not privilege x86, binary, fixed-width words, or any particular ISA. A ternary processor or a different instruction encoding is the same kind of project.

The library's sequential parts are not written twice per domain. `std.t-latch`, `std.t-dff`, and `std.t-reg-2` are produced by the same builders that produce `std.bt-latch`, `std.bt-dff`, and `std.bt-reg-2`; only the domain ID, the constituent gate definitions, and the power-on values differ. Both 3-value domains qualify because the construction assumes only minimum, maximum, and complement gates, so adding another three-valued domain is a library wiring choice rather than an engine change. `examples/balanced-ternary-accumulator.radix` uses those balanced parts with `std.bt-adder-2` and `std.bt-mux` to accumulate signed values: LOAD/ADD/HOLD are trits where `+` is asserted and `−` is inactive, and the result wraps modulo 9 across the −4..4 range.

`examples/ternary-branch-machine.radix` is that claim in ternary form. It keeps the 2-trit program counter and register-based state of `ternary-machine.radix`, then adds two things the earlier demonstration lacked: **control flow** and **writable memory**. Four 9-entry lookup ROMs hold a 2-trit opcode and a 2-trit argument per program word; a lookup per control line decodes the opcode into LOAD, ADD, STORE, LOADM, JMP, and BZ. Branching is a ternary mux on the program-counter input, selected by `max(BZ ∧ ACC = 0, JMP)`, and the zero test is `min` of two per-lane lookups. Memory is the reusable `std.t-ram-3x2` part: three `std.t-reg-2` words, one write-enable lookup per word over WE and the address trit, and an asynchronous read port built from one 81-entry lookup per lane. The machine connects STORE to WE, ACC to D, and the instruction argument to ADDR, so the part owns the write and read paths instead of the example.

`std.t-ram-9x2` shows how the same construction scales without a monolithic table. Its two-trit address selects among nine 2-trit registers; each word has its own 27-entry write decoder over WE and both address trits, and the read port is a two-level tree of `std.t-mux3` selectors: three low selectors pick within groups of three words using ADDR0, then one high selector picks a group using ADDR1. Adding a third address trit would add one selector level, not double an exponential table. `std.t-mux3` is deliberately separate from `std.t-mux`: the 2:1 gate treats select 1 as unknown, while a memory read tree must treat all three select values as real addresses. `examples/ternary-memory.radix` exposes the part directly for inspection.

`examples/ternary-call-machine.radix` reuses the 9×2 RAM in a different role: a return stack. CALL writes PC+1 at SP and jumps to the operand, RET reads the word at SP−1 into PC, and a two-trit SP assembled from a register, per-lane muxes, and modulo-9 increment/decrement lookup pairs changes only on those two opcodes. The shipped program calls a routine that calls a second routine, then returns through both levels to the instruction after the original call; a test and a script check the full PC/ACC/SP/read trace. The stack holds nine return addresses; CALL at SP=8 writes the last word and wraps the pointer, so the machine reports it. Decoded CALL-at-SP-8 and RET-at-SP-0 conditions feed a max gate whose output a flip-flop latches as `STACK ERROR` until the simulation restarts. The fault logic is built from the same lookups, muxes, and registers as the data path; a scenario test rewrites the instruction ROMs to make the machine recurse until the stack overflows and to return with an empty stack. Nothing about the machine is special-cased in the engine: CALL, RET, and the fault checks are decoder lookups and mux-select signals like every other opcode.

`examples/ternary-stack-machine.radix` keeps the same parts but changes the architecture from registers to a stack. A 2-trit `std.t-reg-2` holds the top of stack, and the 9×2 RAM holds older words below a two-trit SP. PUSH writes the old TOS at SP and loads the argument, POP reads at SP−1, ADD reads the second operand and leaves the modulo-9 sum, DUP writes a copy without changing TOS, and SWAP writes old TOS at SP−1 while capturing the old word there on the same edge. OUT is another register, JMP is unconditional, and BZ tests TOS for zero without popping. Control is still decoder lookups and mux selects; the stack pointer, fault latch, and write enables are the same kinds of parts used by the call machine. The kernel has no stack, no TOS, and no notion of an operand.

The RAM is a good example of what the library is for. It has no special status: it is a definition containing registers and lookup tables, exported with the project and inspected by double-clicking the instance, and any project can rewrite or duplicate it. Because `std.t-reg-2` is itself a definition, the hierarchy is three levels deep (machine → RAM → register → flip-flop → latch → lookup) without a line of engine code for memory. No kernel or definition-format change was needed; the program stays editable data in the ROM tables, so retargeting the machine means editing nine table entries rather than code.

## 6. Simulation semantics

### 6.1 Reset

On reset:

1. Simulated time is zero, counters and runtime diagnostics are cleared.
2. Every pin is `Undriven`.
3. Every net starts at its declared initial value or undriven.
4. Primitive runtimes are recreated in component order; constants and waveform sources seed their outputs; delays seed their initial outputs.
5. Every component is scheduled for a time-zero evaluation.

The constructor performs a reset. `AdvanceTo(0)` processes all events at time zero, which is how source primitives settle before the first advance.

### 6.2 Event ordering

The event heap orders by:

1. `Time` ascending;
2. monotonic insertion `Sequence` ascending (FIFO for simultaneous events).

This ordering is the entire concurrency model. Simultaneous events do not run concurrently, and no behavior depends on managed thread scheduling or hash iteration order.

Event kinds:

- `WakeComponent`: an input net changed or the component is being initialized.
- `CommitOutput`: a primitive's output value becomes visible at a pin.
- `PrimitiveTimer`: a delayed/timed internal action (used by waveform and delays).

When a component is scheduled more than once before it runs, the evaluation coalesces through a pending flag. This avoids redundant work for many input changes in the same timestamp.

### 6.3 Net propagation

`CommitOutput` updates a pin value. If the pin is on a compiled net:

1. Collect active driver values (and the optional external input value) in compiled pin order.
2. Ask the domain's driver policy to resolve.
3. Report a conflict once per net per run.
4. If the net value changed, record a trace entry and schedule every distinct sink component.

A net transition is the unit of trace history. Pin values that do not change never generate commits.

### 6.4 Convergence and oscillation

- Every component evaluation increments an evaluation count for the current timestamp.
- If a component exceeds `MaxEvaluationsPerComponentPerTimestamp` (default 64), the engine halts with `RAD100` and a message naming the instance and timestamp.
- Legitimate sequential feedback uses delays/latches and does not spin at one timestamp; those designs converge normally.
- A component evaluation that changes no outputs does not schedule more work and naturally settles.

This mechanism catches combinational cycles without delays. It cannot prove the absence of oscillation across timestamps; that is a user/analysis concern, and the editor surfaces runtime diagnostics and traces.

### 6.5 External drives

`SetExternalInput(netIndex, value)` is a testbench/GUI facility:

- one external value is allowed per net;
- the value participates in resolution along with component drivers;
- `-1` clears it;
- changing it resolves the net immediately and schedules downstream components at the current time.

Root port input values map to this facility. A GUI drive sequence is replayed from time zero when the user adds an earlier external event, which keeps the current state deterministic.

## 7. Determinism and traces

Determinism requirements:

- no wall-clock reads in the engine;
- stable event ordering;
- stable component/pin/net index assignment;
- no unordered collection iteration in simulation-sensitive paths;
- no floating-point math in the kernel's discrete path (model edit coordinates are doubles, but they do not affect semantics).

`SimulationTrace` is a bounded ring buffer of `NetChange(time, netIndex, old, new)`. It does not affect simulation results. The editor builds waveforms from it in one pass over the trace, filtering visible nets. When capacity is exceeded, old events are overwritten and `TotalRecorded` still reports the true total.

The visible net set is the definition's first 16 nets in declaration order, unless the user pins nets from the net inspector. Pins are ordered per definition, scoped to the editor session, and filled ahead of the declaration-order remainder; removing a net drops its pin, and duplicated definitions start unpinned because the key is the definition ID. Pinning changes only which trace slices are copied into the immutable `WaveformSnapshot`; it never perturbs compilation or simulation.

`WaveformView` maps that snapshot onto a `WaveformWindow`, a pure absolute-tick range. The window starts in follow mode (0..trace end) and refits on every snapshot; wheel zoom around the pointer or a drag pan leaves follow mode and clamps the range to the data, while double-clicking or a simulation reset returns to follow mode. Zooming changes only X mapping and clipping of already-recorded changes, so the trace, snapshot, and simulation are untouched.

Reproducibility tests compile the same project multiple times, run identical schedules, and compare final values, transition counts, and trace entries.

## 8. Validation model

Validation has two phases because some checks require flattened connectivity.

`CircuitValidator` checks structural/model invariants that can be decided per definition:

- format version, names, settings;
- domain identity and value labels;
- definition identity, root existence, hierarchy cycles;
- interface pins, instance references, primitive parameter ranges;
- net domains, initial values, duplicate pins, pin-on-two-nets, missing pins;
- port bindings and interface pin domain consistency;
- unconnected primitive inputs and multi-driver warnings.

`CircuitCompiler` performs connectivity-dependent checks:

- actual connected domain compatibility across nested boundaries;
- lookup table sizing for the multiply-connected input domains;
- primitive output value validity;
- port reachability and driving warnings;
- initial-value conflicts when merging authored nets.

`radix validate` compiles the project root by default; `--all` flattens every definition as a root and deduplicates diagnostics. That catches a broken part the current root never instantiates without turning every editor mutation into a whole-library compile.

Diagnostics have stable codes (`RAD...`), severity, and optional definition/instance/net/pin/path locations. Errors stop compilation; warnings do not. The CLI exits nonzero for validation/compile failure.

## 9. Project format and security

The format is documented in [project-format.md](project-format.md). Key security choices:

- JSON deserialization uses `UnmappedMemberHandling.Disallow`, so unknown fields fail loudly.
- Primitive kinds are a closed whitelist. No type names, assembly paths, scripts, or code are deserialized or executed.
- All string references are validated against project-owned definitions, domains, instances, and nets. Optional view metadata is presentation-only and cannot alter the circuit or simulation root.
- Loading is size-unbounded in this build; callers should avoid opening untrusted gigantic files until a size limit is added.
- Writes go to a temporary file in the destination directory and then atomically replace the target.

A future plugin system would need an explicit trust model and separate versioning; it must not piggyback on the JSON loader.

## 10. Editor architecture

### 10.1 Separation

- `EditorViewModel` owns the mutable `CircuitProject`, current definition, selection, undo stacks, and a `SimulatorHost`.
- `SimulatorHost` compiles the **current definition** as the root, replays external drives, and exposes read/format values. It never renders.
- `CircuitLayout` is a pure derivation of the current definition: component boxes, pin visuals, port pills, wires, junctions, and content bounds.
- `CircuitCanvas` renders layout snapshots and translates pointer gestures into view-model calls. It owns only transient interaction state and the viewport transform.
- `WaveformView` renders immutable `WaveformSnapshot` data.

No simulation state lives in the canvas, and the kernel is usable without Avalonia.

### 10.2 Editing transactions

Undo/redo stores serialized project snapshots plus the current definition and selection IDs. This gives correct handling of:

- adding/removing definitions, instances, pins, and nets;
- net merges from wiring;
- primitive parameter changes;
- domain changes;
- component movement.

A drag captures one snapshot on the first movement, not one per pointer move. Structural edits invalidate the running engine because compiled indices may change.

Editor-side structural rules:

- Two endpoints with declared but different domains are rejected with a status message.
- Two existing nets must have the same domain to merge.
- Connecting a primitive pin to a different-domain net is allowed if the primitive pin has no declared domain; compile-time validation then decides whether its table fits the actual domains.
- Removing a component removes its pin references and then removes empty nets that are not bound to ports.
- A net bound to an interface pin cannot be deleted directly; remove the pin first.
- Extracting a selection moves wholly-internal nets into the new definition, turns boundary nets into directional pins, and replaces the selected instances with one instance. A boundary net with active drivers on both sides is refused because the interface model has no inout direction.
- Promoting a net binds the existing net to a new interface pin; it does not create or merge nets.
- Interface declaration order is presentation order: nets and lane pin lists reference stable pin IDs, so moving a pin changes canvas layout and the default order offered to new lanes but never reconnects anything.
- Paste and library import are captured as one undo step. Both operate on deep copies so a refused operation cannot leave half-mutated connectivity.

### 10.3 Simulation root choice

The editor compiles the **currently edited definition** as the root. This lets a user navigate into `std.dff`, drive `D` and `CLK`, and observe `Q` without constructing a wrapper. The project root remains the simulation root used by the CLI and the fallback when saved view metadata names a definition that no longer exists.

Consequence: navigating to a subcircuit does not show the parent's surrounding state. This is intentional but should be made visible in UI copy; the definition header identifies the simulated root.

Diagnostic navigation is read-only. A `Diagnostic` may carry definition, instance, net, and pin IDs; the Diagnostics tab turns those into a **Locate** action that opens the owning definition, selects the named instance or net, and centers the canvas on it. Project-wide messages have no action. Navigation never mutates the project or the simulation, and a target that disappeared is reported instead of guessed.

The same tab can run a `.radixtest` file through `SimulationScriptRunner` against the current in-memory project, so unsaved edits are covered. Scripts can assert authored nets at any hierarchy path, using the same immutable `NetLocation` map the compiler exposes for diagnostics, so a test can observe an internal carry or decoder node without widening the interface. The runner compiles and simulates on its own engine; the editor's live simulation, drive history, and project are untouched. Pass/fail summaries and failed steps are view-model presentation only and are cleared when another project is loaded, created, recovered, or restored from undo.

The Inspect tab's **NETS** navigator is a filtered projection of `Definition.Nets`. Choosing a row calls the same selection path as clicking a wire; the list reads net identity, domain, pin count, and live engine values, but never writes connectivity. Its row cap keeps very large definitions from building an unbounded visual tree while the filter still searches the full net set.

Selecting a lookup primitive also shows a **LOOKUP TABLE** grid. It derives each row's address by decomposing the flat table index in the mixed radix of the connected input nets' domains, with the first input outermost. Editing a cell parses and range-checks the entire grid against the output domain before constructing a new immutable `LookupSpec`, so a partially invalid grid never mutates the project; a commit is one undo step and refreshes the grid from the model. The raw `Table` field remains available for bulk edits, and both paths converge on the same primitive mutation.

### 10.4 Visual model

Automatic layout derives:

- a fixed-width box whose height grows with the larger of input/output pin counts;
- title and instance ID in the header;
- a left input column and right output column;
- interface ports in left input and right output gutters;
- one routed path per authored net, independent of the number of endpoints.

Wire routing uses a sparse orthogonal grid with A* search and a bend penalty. Component bodies are inflated obstacles, pin stubs may not run through neighbouring bodies, and the search connects terminals through a Steiner-like incremental tree so multi-terminal nets do not need a single shared trunk. Blocked grid nodes and edges are marked once per obstacle rather than scanned at every A* expansion. Crossing segments are drawn with small gaps; junction dots are emitted only at real branches. This distinguishes a crossing from an electrical join without relying on color.

Author bendpoints are stored as ordered world coordinates on the net and guide an orthogonal path through the requested points. If no automatic path exists, the router still draws an explicit orthogonal fallback rather than a diagonal or a phantom connection. Wire colors can be domain/signal-derived or overridden per net; both are presentation state and never enter compilation.

Pin values are rendered as small off-box badges; wire segments carry the current net value as a label at sufficient zoom. Domain-specific colors distinguish binary low/high, ternary values, and other finite domains. Selection and hover halos plus junction glyphs mean color is never the sole connection/state cue.

The active definition, canvas viewport, and per-definition waveform pin sets are stored as optional top-level project view metadata. `CircuitCanvas` applies a saved viewport after layout instead of fitting, and reports every later pan/zoom to the view model. Waveform pins are stored against definition IDs and net IDs, and both are filtered against the current project so a deleted item cannot leave a phantom row. View changes are deliberately excluded from dirty-state comparison: moving the camera or pinning a net must not make a saved circuit look modified. Selection, drive history, waveform history, and the waveform time window remain transient.

Component boxes and ports are rebuilt after every layout-affecting mutation. During an active drag, keyboard nudges, arrange-selection alignment/spacing, and bendpoint edits, the editor passes the previous layout as a route cache: a net reuses its routed geometry when its ordered pin/port endpoints, authored bendpoints, and every cached segment relative to changed body bounds are unchanged. Adding, moving, or removing a body only invalidates nets whose routes intersect the changed bounds; a net whose terminals or bendpoints changed always reroutes. `EndDrag`, structural edits, and undo/redo rebuild canonically, so a structural change or committed drag never inherits a stale route. This is route reuse, not a fully incremental layout engine: boxes and ports are still rebuilt, and structural edits canonicalize every route. Measured behavior is in [benchmarks.md](benchmarks.md#editor-layout).

Definition navigation keeps a session-only trail: `OpenDefinition` records each newly opened definition, `NavigateBack`/`NavigateForward` move the index without adding entries, and opening a definition after backing up drops the forward tail. Trails are reset whenever the project object is replaced (new, open, recovery, import, undo/redo, definition creation or removal) so a stale ID from one project cannot navigate another. The trail is intentionally not part of the project format; it is a navigation convenience, not document state.

Presentation-only mutations do not call `SimulatorHost.Retarget`. Instance positions and labels, arrange-selection alignment and spacing, wire colors and bendpoints, numeric lane/format definitions, and project/definition metadata mark the project dirty while leaving the compiled circuit, simulated time, and external-drive history intact. Position and bendpoint edits reuse valid routes; metadata and wire-color changes skip layout entirely because geometry and cached route data did not change. Connectivity changes, interface or domain changes, net initial values, and primitive parameters take the invalidating path. This keeps a running experiment alive while arranging or annotating it; undo/redo restores a whole-project snapshot and therefore starts a fresh simulation.

### 10.5 Theming

`Themes/ThemeTokens.axaml` defines one semantic token set in light and dark theme dictionaries: window/toolbar/panel surfaces, text hierarchy, borders, accent, success/danger, canvas grid and component bodies, pins, badges, tooltips, signal colors, waveform grid/bands, and diagnostic severities.

XAML uses `DynamicResource` for those keys. Custom-drawn `CircuitCanvas` and `WaveformView` resolve the same keys with `ThemeBrushes.Get` at the start of each render, so there is no second hardcoded palette. Both controls subscribe to `ActualThemeVariantChanged` and invalidate. `ThemeService` maps Auto/Light/Dark to `Application.RequestedThemeVariant` and persists the manual choice under the platform application-data directory; Auto delegates to the OS and follows live system changes. Diagnostics use classes plus `DynamicResource` rather than cached brush strings.

`WorkspacePreferences` stores window size, maximized state, the active inspector tab, and recent project paths in `workspace.json` beside `theme.txt`. Both files are per-user presentation state, never project data. Missing, malformed, or out-of-range values fall back to defaults or are clamped, and write failures are ignored so a read-only home directory cannot block startup or shutdown.

`RecoveryService` keeps one crash snapshot (`recovery.radix` plus `recovery.json` for the origin and timestamp) in the same directory. The app lifetime snapshots only dirty projects every 20 seconds; each write goes to a temporary file and is then moved over the previous snapshot, so a crash during autosave cannot leave a truncated project. On launch the snapshot is loaded with validation disabled and presented as a dirty project with its original path, so an intermediate invalid circuit is recoverable and repairable. A successful save clears the snapshot through the `ProjectSaved` event, and an explicit Discard clears it directly; recovery is best-effort and never blocks editing or shutdown.

### 10.6 Hierarchy editing and libraries

Forking an existing definition is a plain model copy: `CircuitDefinition.Clone(id, name)` deep-copies pins, instances, nets (including route points and wire colors), port bindings, and numeric lanes. Instance, net, pin, and lane IDs are definition-local, so they are preserved and the copy is independently editable; child definition references and primitive parameters are shared because both are immutable values of the project. The copy is inserted next to its source with a unique `<id>-copy` ID and a counting name, becomes the active definition, and is one undo snapshot. Because it reuses child parts, a fork never duplicates deeper hierarchy.

`HierarchyEditor` owns extraction and promotion as model transformations:

- extraction validates all boundary nets before mutation, creates a new `custom.<slug>` definition, clones internal instances/nets, generates unique pin IDs and human names, replaces selected instances in the parent, and rewires boundary parent nets to the replacement's pins;
- promotion binds an existing unbound net to a new pin and leaves the net's identity and internal connectivity untouched;
- removing a pin prunes every parent net reference that addressed that pin through an instance of the edited definition. Empty unbound nets are removed, bound nets stay so a broken parent interface remains visible, and the whole edit is one undo snapshot.

Numeric lanes are definition presentation, not hierarchy structure. They are cloned by library export, validated on load, and ignored when two definitions are compared for reuse. Custom numeric formats are project assets; export keeps only those referenced by the exported closure, and import renames collisions while remapping lanes.

`ComponentLibraryService` exports a definition's transitive dependency closure using the ordinary project serializer, so exported libraries are validated by the same strict loader. Import merges domains only when their values and driver policy match. Definitions are compared structurally (pins, ports, instance primitives, net membership/initial values) while ignoring labels, coordinates, colors, and routes; equivalent definitions are reused, conflicting IDs are renamed, and newly introduced validation errors roll the whole merge back.

Both services are independent of Avalonia. The view model supplies selection, undo snapshots, and file paths only.

### 10.7 Headless GUI testing

`tests/Radix.Editor.Tests` uses `Avalonia.Headless` with offscreen Skia. It:

- constructs the full window and view model;
- runs simulation through the view model;
- captures a `RenderTargetBitmap` of the entire window;
- saves screenshots under ignored `artifacts/editor/`;
- exercises empty-project rendering;
- renders both light and dark variants and samples a background pixel for luminance;
- checks that no automatic wire segment crosses a component body and that junction dots only appear at real branches.

These tests do not create an OS window. They are the required safe visual test path. See [MANUAL_TESTING.md](../MANUAL_TESTING.md) for interaction checks.

## 11. Performance design

Implemented:

- flat compiled arrays and precomputed sink lists;
- binary event heap with stable sequence numbers;
- pending-evaluation coalescing;
- no allocations in event objects (struct heap entries) and no per-event LINQ;
- bounded trace with ring storage;
- primitive runtimes created once per reset, not per evaluation;
- one-pass waveform snapshot construction;
- one indexed validation pass per definition for instance, pin, and driver checks;
- explicit compilation: the editor recompiles on Begin or after invalidation, not per frame;
- route reuse during drag and presentation-only position/bendpoint edits: unchanged nets keep routed geometry, changed terminals/bendpoints or intersecting moved bodies invalidate, and structural edits or release rebuild canonically;
- single-pass obstacle marking per route grid, instead of an obstacle scan at every A* expansion.

Not implemented:

- JIT/expression specialization;
- multithreaded event processing;
- incremental recompilation;
- incremental structural layout: position-only edits reuse routes, but place/delete/connect edits still rebuild all boxes, ports, and routes;
- spatial indexing for very large editor canvases beyond O(n) hit tests;
- waveform downsampling for millions of transitions.

Before the validation index, net pin references and driver counts each scanned the definition's instance list, making validation quadratic. A 20,000-gate synthetic chain spent ~1,985 ms in validation; it now spends ~97 ms, and total compile is ~134 ms, close to linear in flattened topology. The benchmark reports validation and flattening separately so future regressions can be attributed. See [benchmarks.md](benchmarks.md) for exact runs and caveats.

Measured baseline: 2,000-gate chain at 50 ticks → 200,150 events in ~21 ms (~9.4M events/s); 20,000-gate chain → 2,000,150 events in ~170 ms (~11.8M events/s) on an AMD Ryzen 9 9950X3D with .NET 10.

On the editor side, a synthetic 1,000-gate chain in Release takes ~0.41–0.45 s to construct or canonically rebuild after a drag, but ten drag frames take ~14 ms when unchanged routes are reused (versus ~4.48 s with a full rebuild each frame), and ten keyboard nudges take ~71 ms instead of ~4.5 s. These are interaction latency including view-model refresh, not isolated router time. See [benchmarks.md](benchmarks.md#editor-layout).

## 12. Extension points and non-goals

### Adding a primitive kind

Requires:

1. a `PrimitiveSpec` subtype,
2. compiler shape/validation support,
3. a `PrimitiveRuntime` implementation,
4. serializer DTO support,
5. a project-format version review/tests.

This is deliberate: primitives are the kernel trust boundary. User-defined components do not require code and are the intended extension path for architectures.

### Analog/electrical simulation

Not implemented. The discrete engine is the wrong substrate for continuous electrical analysis: nets would need energy state, integration timesteps, nonlinear device models, and a different convergence solver. A future physical layer should:

- reuse the project and UI but introduce an explicit physical-model asset type and unit system;
- use a numerical solver (modified nodal analysis or similar) with documented integration and tolerances;
- translate digital/analog boundaries explicitly, not by pretending domain indices are voltages;
- keep the discrete event engine unchanged for digital designs.

This is an architectural boundary in the project format and UI, not a promise that discrete signals approximate electrical behavior.

### Buses and numeric encodings

There is no first-class bus. Users can create a definition with N pins and use lookup tables; the compiler intentionally does not impose width or encoding. Numeric interpretation (`PositionalFormat`, `SignMagnitudeFormat`) operates on an ordered span of value indices for display/reporting only. Complement conventions are documented on the format classes and in tests; they never affect net values.

A `NumericLane` persists a named, ordered list of **interface pin IDs** plus a format ID. The first pin is least significant by contract. Lanes exist to make an interface readable and to let a reusable part carry its numeric presentation; they are deliberately not compiler input. The compiler flattens circuits exactly as if every lane were absent, and deleting a lane cannot change simulation. Lanes are validated (known format, known/unique pins) and pruned when a pin is removed. This avoids two common design errors: turning a group of pins into a hidden vector whose width and encoding the kernel must understand, and storing numeric state separately from the nets it describes.

`NumericFormatDefinition` makes the format set project-owned. A format is a descriptor for digit values, radix, weighting, orientation, and optional sign index; `NumericFormatCatalog` resolves built-in IDs first and project custom IDs second. Validation rejects reserved or duplicate IDs, unknown nominal domains, and invalid digit/sign combinations. Library export includes formats used by exported lanes, and import renames conflicting format IDs while rewriting the affected lanes. None of this is visible to the compiler: a project with no lanes or formats compiles to exactly the same circuit as one with many.

Editing a saved format replaces its descriptor while retaining the stable ID, so existing lanes and exported libraries do not need re-pointing. The editor form validates continuously and previews the first lane in the current definition that uses the format with current simulator values; saving is one undo snapshot, and cancelling leaves the project untouched.

Editing a saved lane follows the same boundary: the lane keeps its ID while its name, format, and ordered pin set are replaced in place, so saved projects, library exports, and the canvas legend continue to resolve it. The draft is validated before mutation and committed as one undo snapshot; cancelling, undoing, or removing the lane exits edit mode without leaving a pending draft.

Encoding follows the same boundary. An `INumericFormat` can encode a value into signal indices; for a lane whose pins are all inputs, the editor encodes, checks every pin's domain, and then applies one batch of ordinary external drives. The batch is validated before mutation so a failed lane drive cannot leave a partially applied value. No vector signal, bus driver, or numeric net state reaches the compiler or engine.

The canvas renders a definition's lanes as a fixed-size, screen-space legend at the lower-left. It deliberately does not use world coordinates: at the initial fit zoom, a world-scaled legend would shrink below legibility and would add empty space to `CircuitLayout.ContentBounds`. The legend is capped at seven rows, remaining rows stay in the inspector, and pointer presses inside its bounds are consumed so the circuit underneath is not accidentally selected. Like the lane data itself, it is presentation only and cannot affect compilation or routing.

### Concurrency

The engine is single-threaded by design so event order remains obvious and reproducible. Parallel deterministic simulation would require a partitioning/island model and a proof of independence; it is not present.

## 13. Testing strategy

- **Unit tests** for domains, resolution, model validation, engine semantics, serialization, numeric formats, and script parsing.
- **Structural tests** for compiler flattening, port mapping, hierarchy, all-definition validation, and diagnostics.
- **Behavioral circuits**: NAND truth table, delay timing, DFF edge capture/hold, ternary half/full adders and both the balanced and unsigned hierarchical 2-trit adders over every trit pair, ternary latch hold/capture, trit flip-flop edge sampling, the ternary mux over all select values, the two-trit ternary register's load/hold behaviour, the two-trit ternary counter over all nine states, the ternary accumulator's load/add/hold/wrap sequence, the ternary program machine's ten-edge instruction loop, the branch machine's nine-edge store/branch/load-memory loop, the balanced ternary latch/flip-flop/register load and hold behaviour, the balanced accumulator's signed load/add/hold/wrap sequence, the ternary RAM writing every word and holding the others, both the 3:1 selector's 81 input combinations and the 9×2 RAM's nine-word write/read/hold path, the call machine's nested CALL/RET trace with its stack-pointer walk, the stack machine's push/swap/add/dup/pop/output loop, branch-on-zero behaviour, and both fault directions, the four-state bus's Z/value/X resolution and conflict observation, full adder, four-bit counter.
- **Exhaustive sweeps**: `InputSweep` compiles a definition, drives every combination of its input-port values using each pin's own domain, resets the engine per row, advances an explicit settle time, and reads the output ports. It is exposed as `radix sweep` and as the workbench's **Sweep** tab; both render through `InputSweepTable`, so a row is formatted identically in either place. The row limit is checked before allocating or simulating. The editor runs the sweep on a separate engine, leaves the live simulation untouched, and clears the table when a structural edit commits. It is observation over the public engine, not a second evaluator, and it makes no combinational assumptions: sequential definitions are swept from their power-on state on every row. A permitted sweep still runs synchronously on the UI thread; the limit bounds the work but does not make it asynchronous.
- **Scripted experiments**: `.radixtest` files drive inputs and assert interface pins, numeric lanes, or authored internal nets at absolute ticks, within a bounded settle window, across a stability window, with a transition-count window, a filtered-edge window, or an ordered-edge sequence. An internal-net reference is hierarchy-relative (`netId` at the simulated definition or `instance/netId` below it) and resolves through the compiler's `(path, netId)` map, so merged nets and nets bound to ports are all addressable without adding a runtime lookup to the engine. The same reference form addresses a signal in transition, filtered-edge, and ordered-sequence windows, so glitch counts and ordering can be checked inside a part rather than only at its interface. A setup window samples every tick before the step's drives, so a clock edge can require correct data before it is applied without the script retaining history. A settle window advances tick by tick and requires all its expectations to hold together at one tick, which tests convergence without freezing an exact delay. A stability window instead requires every tick through its deadline to match, so it tests hold behaviour and reports the first divergence. A transition window subscribes to the engine's synchronous net-change hook and counts resolved value changes through its deadline, so tests can require an exact clock toggle count or prove that a state changed only once. A filtered-edge window counts only transitions that match a `from`/`to` filter, so a rising clock edge, a falling edge, and an unrelated data change are distinguishable without an expression language. A sequence window searches the same ordered change stream and requires its filtered edges to occur in the declared order, which expresses handshake and clock-before-capture properties without coupling them to exact ticks. The runner uses the public compiler/engine API, disables trace storage, and treats an unexpected halt as a failure. The CLI and the editor's Diagnostics tab both call the same runner; the editor tests unsaved project state on a separate engine. Shipped scripts under `examples/tests/` are part of the core suite, so a timing change to an example cannot silently invalidate its demonstration.
- **Determinism tests**: repeated runs produce identical values and traces.
- **Differential tests**: randomized combinational designs compare the event engine against a reference evaluator.
- **Round-trip tests**: projects serialize, reload, validate, and simulate identically; malformed/unknown data is rejected.
- **Headless GUI tests**: view-model editing, wire geometry, clipboard/extraction/library behavior, saved viewport restoration, theme variant switching, and offscreen render captures in both themes.

Tests are the primary correctness evidence. The manual test document covers what automation intentionally cannot: human interaction, visual judgment, and window-manager behavior.
