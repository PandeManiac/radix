# Generated Hierarchy Layout Benchmark Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in generated repeated-child hierarchy workload to the editor layout benchmark and record its root-view scaling measurements.

**Architecture:** Extend the existing benchmark test in `LayoutBenchmarkTests.cs` with a helper that builds a fixed child definition and a root containing repeated instances of that definition. Measure `EditorViewModel` construction and repeated canonical layout builds for a bounded set of sizes; keep all generated-workload behavior behind `RADIX_LAYOUT_BENCH=1`. Record measured topology and timings in `docs/benchmarks.md` and update the durable continuation record.

**Tech Stack:** C#/.NET 10, existing Radix.Core model builders, Radix.Editor layout and view-model APIs, xUnit opt-in benchmark, Markdown documentation.

## Global Constraints

- Keep the benchmark opt-in under the existing `RADIX_LAYOUT_BENCH=1` switch.
- Vary only the number of repeated child instances; preserve fixed child topology and internal wiring.
- Do not change production layout/router behavior or add hierarchy expansion.
- Do not assert wall-clock performance thresholds.
- Do not add generated project artifacts or dependencies.
- State clearly that root-view measurements do not represent internal child-definition layout.
- Use a bounded size series and preserve normal editor-suite runtime.

---

### Task 1: Generate and benchmark repeated-child projects

**Files:**
- Modify: `tests/Radix.Editor.Tests/LayoutBenchmarkTests.cs`

**Interfaces:**
- Consumes: existing `ProjectBuilder`/`DefinitionBuilder`, `CircuitProject`, `EditorViewModel`, `CircuitLayout`, and opt-in environment convention.
- Produces: a private generated-project helper taking a repeated-child count, and an opt-in benchmark output containing root instance/net and child primitive/net counts, construct time, and ten canonical root-layout build time.

- [ ] **Step 1: Add a private fixed-topology hierarchy builder**

Add a private method to `LayoutBenchmarkTests` that accepts `int childInstanceCount`. Define a child circuit with two binary input pins, one binary output pin, and two `LookupSpec(2, ...)` primitives connected in sequence. Define a root circuit with a binary output port and add `childInstanceCount` instances of the child definition, laid out in a fixed 20-column grid. Connect the root output to the last child's output using the existing definition builder APIs. Build and return the project rooted at `main`. Keep the child definition and its nets identical for every count.

- [ ] **Step 2: Add an environment-gated hierarchy benchmark**

Add a `[Fact]` benchmark method which returns immediately unless `RADIX_LAYOUT_BENCH` equals `"1"`. For bounded counts `25`, `100`, and `250`, build one generated project; time one `EditorViewModel` construction and ten canonical root layout builds using `CircuitLayout.Build(viewModel.Definition, viewModel.Project)`. Print root instance count, root net count, child instance/primitive count, child net count, construct milliseconds, and ten-build milliseconds. Do not mutate model state or assert any time thresholds.

- [ ] **Step 3: Run the focused hierarchy benchmark**

Run:

```bash
export PATH="$HOME/.dotnet:$PATH"
export LD_LIBRARY_PATH="$PWD/.local-native/usr/lib/x86_64-linux-gnu"
export FONTCONFIG_FILE="$PWD/.local-native/fonts.conf"
RADIX_LAYOUT_BENCH=1 dotnet test tests/Radix.Editor.Tests/Radix.Editor.Tests.csproj -c Release --filter FullyQualifiedName~GeneratedHierarchyLayoutBenchmark --logger 'console;verbosity=detailed'
```

Confirm the test passes, all three instance counts are printed, child topology counts are constant, and execution remains bounded.

- [ ] **Step 4: Commit the benchmark implementation**

```bash
git add tests/Radix.Editor.Tests/LayoutBenchmarkTests.cs
git commit -m "Benchmark generated repeated-child hierarchy layouts"
```

### Task 2: Record and verify results

**Files:**
- Modify: `docs/benchmarks.md`
- Modify: `docs/progress.md`

**Interfaces:**
- Consumes: measured output from Task 1 and the existing benchmark environment/command documentation.
- Produces: a dated, caveated hierarchy scaling table and a continuation note pointing to the next useful measurement.

- [ ] **Step 1: Document measured hierarchy data**

Record the actual VM and .NET environment, the opt-in command, fixed child topology, each tested root count, topology counts, construction time, and ten canonical layout build time. Label it an exploratory one-run measurement; state that child internals are not expanded onto the root canvas and that timings are not a universal guarantee.

- [ ] **Step 2: Update continuation status**

Add the benchmark milestone and latest verified code commit to `docs/progress.md`; identify representative expanded hierarchy layout as unavailable/currently outside the measurement rather than imply it was tested.

- [ ] **Step 3: Verify the complete change**

Run:

```bash
export PATH="$HOME/.dotnet:$PATH"
export LD_LIBRARY_PATH="$PWD/.local-native/usr/lib/x86_64-linux-gnu"
export FONTCONFIG_FILE="$PWD/.local-native/fonts.conf"
dotnet build Radix.slnx -c Release --no-restore
dotnet test tests/Radix.Editor.Tests/Radix.Editor.Tests.csproj -c Release --no-build
git diff --check
```

Expected: Release build has zero warnings/errors, editor tests pass, and diff check is clean. The focused opt-in benchmark is run in Task 1.

- [ ] **Step 4: Commit measurement and handoff documentation**

```bash
git add docs/benchmarks.md docs/progress.md
git commit -m "Record generated hierarchy layout measurements"
```
