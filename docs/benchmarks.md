# Benchmarks

These are reproducibility notes, not universal performance claims. The benchmark is implemented in `src/Radix.Cli/Program.cs` (`RunBench`) and runs a synthetic chain of zero-delay inverters. One simulated tick makes the chain ripple once, creating event traffic without running an unrelated workload.

## Environment

- CPU: AMD Ryzen 9 9950X3D, 16 cores / 32 threads
- OS: Linux, headless execution
- Runtime: .NET 10.0.113, Release build
- Date: 2026-10-28

## Command

```bash
dotnet run -c Release --project src/Radix.Cli -- bench --gates 2000 --ticks 50
dotnet run -c Release --project src/Radix.Cli -- bench --gates 10000 --ticks 50
dotnet run -c Release --project src/Radix.Cli -- bench --gates 20000 --ticks 50
```

The benchmark reports validation separately from flattening so a scaling regression can be attributed to the right phase. `compile (incl. validate)` is the total compile cost the editor pays; `flatten` is the remainder after validation. Editor layout has a separate opt-in benchmark described under [Editor layout](#editor-layout).

## Results

### 2,000 gates, 50 ticks

```text
validate:             14.5 ms
compile (incl. validate): 34.7 ms
flatten:              20.2 ms
simulate:             21.2 ms
events:               200,150
component evals:      104,053
net transitions:      102,051
events/sec:           9,441,617
evaluations/sec:      4,908,461
halted:               false
```

### 10,000 gates, 50 ticks

```text
validate:             67.3 ms
compile (incl. validate): 80.3 ms
flatten:              13.0 ms
simulate:             97.9 ms
events:               1,000,150
component evals:      520,053
net transitions:      510,051
events/sec:           10,217,696
evaluations/sec:      5,312,947
halted:               false
```

### 20,000 gates, 50 ticks

```text
validate:             96.8 ms
compile (incl. validate): 134.2 ms
flatten:              37.4 ms
simulate:             170.0 ms
events:               2,000,150
component evals:      1,040,053
net transitions:      1,020,051
events/sec:           11,763,872
evaluations/sec:      6,117,066
halted:               false
```

### This VM (2026-10-29)

Re-measured on a different host to separate engine behavior from one machine's
speed: an 8-vCPU cloud VM with 15 GiB RAM, .NET SDK 10.0.100, Release build, and
the same commands.

| Gates | Validate | Compile | Flatten | Simulate | Events/sec | Evals/sec |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2,000 | 34.1 ms | 90.5 ms | 56.5 ms | 47.7 ms | 4,194,504 | 2,180,618 |
| 10,000 | 160.9 ms | 159.9 ms | -1.0 ms | 186.6 ms | 5,361,148 | 2,787,663 |
| 20,000 | 227.9 ms | 243.5 ms | 15.6 ms | 261.5 ms | 7,649,796 | 3,977,798 |

These absolute numbers are slower than the reference machine above, which is
expected on a shared virtual host. The near-linear growth in compile time and the
flat per-event simulation cost match the earlier results. A negative `flatten`
value is timer noise, because it is computed as a difference of two stopwatches.

## Validation scaling fix

Before the validation index change, the same 20,000-gate run took **~2,036 ms to compile, of which ~1,985 ms was validation**. Two nested scans were responsible:

1. every net pin reference resolved its instance with a linear scan of the definition's instances;
2. every net re-counted its output drivers by resolving each pin instance again, even for single-driver nets that could not have a conflict.

Validation now builds one instance lookup per definition and reuses it for pin resolution and driver counting, and nets with fewer than two pin references skip driver counting entirely. Validation at 20,000 gates dropped from ~1,985 ms to ~97 ms (about 20×), and total compile from ~2,036 ms to ~134 ms (about 15×). Warning and error behavior is unchanged; the full test suite covers duplicate connections, unknown instances/pins, domain mismatches, and multi-driver warnings.

At 2,000 and 10,000 gates the earlier runs are not directly comparable because they did not report a separate validation time; the old documented compile times were 65.0 ms and 537.5 ms respectively, versus 34.7 ms and 80.3 ms now on the same machine.

## Editor layout

The editor has an opt-in benchmark for layout and routing responsiveness. It is implemented in `tests/Radix.Editor.Tests/LayoutBenchmarkTests.cs` and does nothing unless `RADIX_LAYOUT_BENCH=1`. It builds a synthetic chain of 200, 500, or 1,000 single-input lookup gates laid out 20 per row, nudges the middle gate 10 times with the arrow-key path, drags it by 10 increments, places ten unconnected gates into empty space, and prints:

- **construct**: creating the view model and its first layout;
- **canonical frames**: 10 frames through `SetInstanceState`, which each rebuild all geometry and routes, the path used before route reuse;
- **nudge frames**: 10 arrow-key `NudgeInstance` calls, which take the presentation-only reuse path;
- **reused frames**: the same 10 frames inside `BeginDrag`, where unchanged nets keep their routed geometry;
- **final rebuild**: `EndDrag`, which commits the current valid routes (affected connections have already rerouted during the last frame);
- **canonical placements**: ten direct model additions followed by a full `CircuitLayout.Build`, the comparison path that discards every route;
- **incremental placements**: ten `PlaceLibraryComponent` calls at empty space, which preserve every still-valid route and reroute only affected nets.

### Command

```bash
RADIX_LAYOUT_BENCH=1 dotnet test tests/Radix.Editor.Tests/Radix.Editor.Tests.csproj \
  -c Release --filter FullyQualifiedName~LayoutBenchmarkTests \
  --logger "console;verbosity=detailed"
```

On PowerShell, set `$env:RADIX_LAYOUT_BENCH="1"` for the session first. The benchmark asserts nothing about time; copy its output into any performance discussion.

### Results

Measured 2026-10-28 on the same machine and .NET 10 runtime as the engine benchmark, Release build, after a 50-gate warmup. Construct and drag values take the minimum of repeated runs; the remaining columns are single runs.

| Gates | Construct | 10 canonical frames | 10 nudge frames | 10 reused frames | Final rebuild | 10 canonical placements | 10 incremental placements |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 200 | 145 ms | 552 ms | 45 ms | 11 ms | 0 ms | 337 ms | 31 ms |
| 500 | 111 ms | 1,206 ms | 48 ms | 49 ms | 1 ms | 1,638 ms | 46 ms |
| 1,000 | 420 ms | 4,512 ms | 70 ms | 20 ms | 0 ms | 3,661 ms | 43 ms |

These are interaction-latency measurements, not pure router time: they include layout of component boxes and ports, view-model refreshes, and canvas invalidation. Absolute values vary with GC, JIT, and which node is moved, so individual rows are not monotonic; use the orders of magnitude, not adjacent differences. The nudge and incremental-placement columns measure route reuse: all boxes and ports are still rebuilt, but valid cached wires are retained, so the dominant route search runs only for nets whose terminals or obstacles changed.

### This VM (2026-10-29)

Same command on the 8-vCPU cloud VM used for the engine reproduction above,
Release build, with the headless Skia natives from `scripts/linux-test-natives.sh`:

| Gates | Construct | 10 canonical frames | 10 nudge frames | 10 reused frames | Final rebuild | 10 canonical placements | 10 incremental placements |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 200 | 280 ms | 905 ms | 87 ms | 25 ms | 1 ms | 541 ms | 35 ms |
| 500 | 217 ms | 2,487 ms | 114 ms | 114 ms | 4 ms | 2,759 ms | 93 ms |
| 1,000 | 791 ms | 8,213 ms | 104 ms | 39 ms | 0 ms | 6,856 ms | 85 ms |

Absolute costs are roughly 1.5–2x the reference machine, consistent with the slower
engine numbers above, but the reuse ratios hold: at 1,000 gates a ten-frame drag
costs ~39 ms with route reuse versus ~8.2 s when every frame rebuilds routes, and
ten incremental placements cost ~85 ms versus ~6.9 s. The one-time construct pause
at 1,000 gates is ~0.8 s on this host.

### Routing-context optimization (2026-10-29)

A canonical rebuild previously rebuilt the obstacle list and sorted axis
candidates for every net and allocated fresh A* state arrays per search. Those
are now shared per layout (obstacles and sorted axis candidates) or pooled
(`ArrayPool` state arrays), and axis candidates are merged instead of reinserted
into a `SortedSet`. The midpoint and truncation rules are unchanged, and the
editor suite's exact-route assertions still pass.

Same host and command, one baseline run and two runs after the change:

| Gates | Construct before -> after | 10 canonical frames before -> after | 10 canonical placements before -> after |
| ---: | ---: | ---: | ---: |
| 200 | 272 -> 94-96 ms | 1,059 -> 638-825 ms | 657 -> 302-313 ms |
| 500 | 269 -> 83-84 ms | 2,620 -> 832-855 ms | 2,887 -> 1,732-1,734 ms |
| 1,000 | 794 -> 459-461 ms | 7,849 -> 4,706-4,734 ms | 6,543 -> 3,881-3,954 ms |

At 1,000 gates that is about 42% less construct time, 40% less for ten canonical
frames, and 41% less for ten canonical placements; the 500-gate construct and
canonical-frame costs fell by about two thirds. Reuse-path columns are unchanged
within run-to-run noise, and the 200-gate frame row is visibly noisy across runs.
A second pass shared the routed-segment spatial index across nets (it had been
rebuilt per net) and replaced per-cell obstacle loops with span fills. Comparing
the first optimized build with the second, over two runs each:

| Gates | Construct | 10 canonical frames | 10 canonical placements |
| ---: | ---: | ---: | ---: |
| 200 | 94-96 -> 95-98 ms | 638-825 -> 672-693 ms | 302-313 -> 363-370 ms |
| 500 | 83-84 -> 83 ms | 832-855 -> 632-661 ms | 1,732-1,734 -> 1,556-1,575 ms |
| 1,000 | 459-461 -> 366-368 ms | 4,706-4,734 -> 3,743-3,801 ms | 3,881-3,954 -> 2,900-2,982 ms |

Cumulatively, at 1,000 gates the canonical path is now 794 -> 366-368 ms for
construct (-54%), 7,849 -> 3,743-3,801 ms for ten canonical frames (-52%), and
6,543 -> 2,900-2,982 ms for ten canonical placements (-55%). The 200-gate
placement row and the reuse-path rows remain noisy; at 500 and 1,000 gates the
frame and placement gains are consistent across runs.

A canonical rebuild of 1,000 gates still costs roughly 0.35 s. A temporary phase
profile (removed after measurement) attributed most routing time to A* expansion
and obstacle marking, so further work should target the search rather than the
shared setup. On the same VM, replacing the per-candidate `HashSet<int>` used to
deduplicate wire-penalty segments with integer visitation stamps reduced the
1,000-gate ten-frame result from 3,762 ms to 3,584 ms, and ten canonical
placements from 2,924 ms to 2,772 ms (about 5%). Construct measured 360 ms before
and 344 ms after; one run per build, so small differences are subject to noise.
This preserves segment iteration order and penalty values while avoiding a hash
clear/lookup for every candidate edge. The full editor suite (178 tests) and
Release solution build pass; exact-route tests remain unchanged.

An exploratory extension of the opt-in layout benchmark to 2,000 gates ran on the
same VM (single run): construct 634 ms, ten canonical frames 6,445 ms, ten nudges
147 ms, ten reused drag frames 84 ms, final rebuild 1 ms, ten canonical placements
5,743 ms, and ten incremental placements 130 ms. Doubling from 1,000 to 2,000
increased construct and canonical frames by about 1.8x, canonical placements by
2.0x, and reused drag by 2.2x. This shows no abrupt scaling cliff at 2,000 gates
for the synthetic chain.

A bundled hierarchical workload was added to the same opt-in test and measured
once on this VM: the 4-bit accumulator CPU root view has 23 components and 34 nets;
construct took 254 ms, ten canonical frames 615 ms, and ten drag frames with route
reuse 82 ms. This gives a representative authored/hierarchical-layout point beside
the regular synthetic chain. It is a single timing sample; root-view layout does
not expand child definitions into the canvas, and the result is not a universal
performance guarantee.

### Generated repeated-child hierarchy (2026-10-29)

The opt-in `GeneratedHierarchyLayoutBenchmark` creates one fixed child definition
(two cascaded 2-input binary lookup primitives; four child nets) and repeats it in
the root, on a 20-column grid. On this VM (.NET SDK 10.0.100, Release, headless
Skia), one run reported:

| Repeated child instances | Root instances | Root nets | Child primitives | Child nets | Construct | 10 canonical root builds |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 25 | 25 | 26 | 2 | 4 | 176 ms | 148 ms |
| 100 | 100 | 101 | 2 | 4 | 60 ms | 632 ms |
| 250 | 250 | 251 | 2 | 4 | 153 ms | 536 ms |

The benchmark then added a separate warmup model and three measured runs per size,
reporting minima. It now collects seven samples each for construction and ten
canonical builds, reporting median and min–max range. Latest run on this VM
(.NET SDK 10.0.100, Release, headless Skia):

| Repeated child instances | Root instances | Root nets | Child primitives | Child nets | Construct median [range] | 10-build median [range] |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 25 | 25 | 26 | 2 | 4 | 12 ms [10–29] | 107 ms [97–122] |
| 100 | 100 | 101 | 2 | 4 | 25 ms [13–57] | 71 ms [70–100] |
| 250 | 250 | 251 | 2 | 4 | 41 ms [40–47] | 365 ms [360–369] |

The earlier single-run and best-of-three samples above are retained as history;
this seven-sample result is a better variability summary, but remains one benchmark
session on a shared VM and is not a universal scaling guarantee. The canvas lays
out root instances only—the child primitives and nets are included in the project
but are not expanded into per-instance child geometry in the root view.


### Interpretation

- At 1,000 gates, ten drag frames cost ~20 ms with reuse versus ~4.5 s when every frame rebuilds the layout and routes. Ten arrow-key nudges cost ~70 ms, and ten placements cost ~43 ms instead of ~3.66 s. The first layout still costs roughly 0.42 s; that is the remaining pause on a design of this size.
- Reuse applies to an active drag, its release, keyboard nudges, arrange-selection alignment and spacing, bendpoint edits, and structural placement/deletion/connection edits. Undo/redo and project replacement rebuild canonically. A placed or moved body that intersects a cached segment always invalidates that net.
- A reused route is valid only while its ordered endpoints, authored bendpoints, and all changed component/port bounds are unchanged. A new or moved body that intersects a cached segment invalidates that net.
- The router marks blocked grid nodes and edges once per obstacle instead of scanning all obstacles at every A* expansion. That keeps the canonical rebuild practical, but it is not separately timed by this benchmark.
- This is still not an incremental layout engine: every edit rebuilds all component boxes and ports, and changed-obstacle validation scans every cached route segment. The benchmark exists to show when that becomes the dominant cost.

## Interpretation

- Event throughput is roughly 9–12 million scheduled events per second in this managed single-threaded engine on this machine.
- Compile time now scales close to linearly with flattened topology in this synthetic chain. Validation and flattening are both explicit and outside the simulation loop.
- The synthetic chain is extremely regular and cache-friendly. Hierarchical designs with many distinct nets and larger lookup tables will differ.
- The benchmark is not a wall-clock fidelity claim: simulated ticks have no relation to hardware nanoseconds, and the chain has no physical delays.
- These numbers were measured with the engine as committed. Any optimization work should rerun this command and record both compile and simulate times.

## Reproducing and comparing

When changing the engine:

1. Run the same command on the same machine before and after.
2. Keep the Release configuration and avoid debugger attachment.
3. Record all reported fields, not only events/sec.
4. Add a larger circuit or a real example if the change targets hierarchy or trace overhead.
5. Do not claim a speedup without these before/after measurements.

The current design intentionally favors correctness, deterministic ordering, and simple data structures. A JIT, parallelism, or incremental compilation should be justified by measured workloads before being added.
