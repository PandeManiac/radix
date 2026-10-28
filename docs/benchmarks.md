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
```

## Results

### 2,000 gates, 50 ticks

```text
compile:              65.0 ms
simulate:             21.4 ms
events:               200,150
component evals:      104,053
net transitions:      102,051
events/sec:           9,349,527
evaluations/sec:      4,860,586
halted:               false
```

### 10,000 gates, 50 ticks

```text
compile:              537.5 ms
simulate:             98.2 ms
events:               1,000,150
component evals:      520,053
net transitions:      510,051
events/sec:           10,187,472
evaluations/sec:      5,297,231
halted:               false
```

## Interpretation

- Event throughput is roughly 9–10 million scheduled events per second in this managed single-threaded engine on this machine.
- Compile time scales with flattened topology and dominates the 10,000-gate case. Compilation is explicit and outside the simulation loop.
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
