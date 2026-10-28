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

The benchmark reports validation separately from flattening so a scaling regression can be attributed to the right phase. `compile (incl. validate)` is the total compile cost the editor pays; `flatten` is the remainder after validation.

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

## Validation scaling fix

Before the validation index change, the same 20,000-gate run took **~2,036 ms to compile, of which ~1,985 ms was validation**. Two nested scans were responsible:

1. every net pin reference resolved its instance with a linear scan of the definition's instances;
2. every net re-counted its output drivers by resolving each pin instance again, even for single-driver nets that could not have a conflict.

Validation now builds one instance lookup per definition and reuses it for pin resolution and driver counting, and nets with fewer than two pin references skip driver counting entirely. Validation at 20,000 gates dropped from ~1,985 ms to ~97 ms (about 20×), and total compile from ~2,036 ms to ~134 ms (about 15×). Warning and error behavior is unchanged; the full test suite covers duplicate connections, unknown instances/pins, domain mismatches, and multi-driver warnings.

At 2,000 and 10,000 gates the earlier runs are not directly comparable because they did not report a separate validation time; the old documented compile times were 65.0 ms and 537.5 ms respectively, versus 34.7 ms and 80.3 ms now on the same machine.

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
