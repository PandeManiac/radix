# Generated hierarchy layout benchmark design

## Goal

Measure how the editor's current root-view layout cost changes as a generated project contains more repeated instances of a fixed child definition. This extends the existing opt-in layout benchmark with a hierarchy workload rather than changing layout or expanding child definitions in the canvas.

## Scope

- Keep the benchmark opt-in under the existing `RADIX_LAYOUT_BENCH=1` switch.
- Generate a small fixed child definition with internal primitive instances and nets, then create a root definition containing a configurable series of instances referring to that child definition.
- Vary only the number of child instances in the root. Preserve the child topology and per-child internal wiring across data points.
- Report the number of root instances, root nets, and child primitives/nets, plus initial `EditorViewModel` construction/layout time and repeated canonical root-layout build time. Include a route-reuse measurement only if the generated workload naturally supports an unambiguous moved-instance case.
- Use a small, bounded set of sizes so the opt-in test completes within a few minutes. Normal test runs remain unaffected.
- Record environment, command, and measured values in `docs/benchmarks.md`; label results exploratory and clarify that children are not expanded into the root canvas.

## Non-goals

- No production layout/router changes or hierarchy expansion feature.
- No benchmark assertions on wall-clock time.
- No generated project files or new dependencies.
- No claim that root-view timings measure internal child-definition layout.

## Verification

Run the new opt-in benchmark and confirm its reported topology counts and timings. Then run the Release solution build and editor test suite to ensure the added benchmark remains inert in normal runs and does not affect existing behavior. Review `git diff --check` before committing the implementation and update `docs/progress.md` with the measurement and next action.
