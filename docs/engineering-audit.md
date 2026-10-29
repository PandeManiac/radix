# Engineering audit (2026-10-29)

This audit records what was inspected in the `bbea268` checkout. The checkout is a
single grafted commit (`wip: preserve current work`) with no remote configured, so
it does not provide enough history to attribute regressions to the initial GitHub
publication or compare against earlier commits. The working tree was clean before
this audit. No license file or CI workflow was present.

## Verified behavior

- `dotnet build Radix.slnx -c Release` succeeds on .NET SDK 10.0.403 with zero
  warnings and zero errors.
- Core tests pass: 382/382 after the contention-diagnostic regression test was
  added (the pre-change baseline was 381/381).
- The editor test assembly contains 177 tests. Its non-rendering subset passes
  146/146. A full headless run passed 153 and failed 24 during Skia initialization
  because this VM lacks `libfontconfig.so.1`; no visible windows are created by the
  test host. The new CI workflow installs the native Linux dependencies before
  testing.
- The event queue compares events by simulated timestamp and monotonically
  increasing insertion sequence. Runtime primitives are recreated by `Reset`,
  which clears pending events, input overrides, diagnostics, counters and trace.
- Hierarchical compilation validates the project and flattens leaf primitives;
  union-find merges connected authored nets. A cycle check and a depth limit of 64
  are present.
- Project JSON is versioned, rejects unknown fields and newer format versions, and
  saves through a sibling temporary file followed by replacement.
- The editor test project uses Avalonia Headless and Skia. This provides offscreen
  rendering and does not require desktop interaction.

## Claims not independently reproduced

- The throughput and editor-layout results in `docs/benchmarks.md` are detailed,
  but were measured on another machine and have not been reproduced in this VM.
- The README's full editor screenshot/rendering coverage could not be verified here
  until the Skia native dependency is available.
- No previous commit is available in this clone to determine whether the published
  commit is incomplete or introduced regressions.

## Confirmed defects and gaps

- CI was absent. `.github/workflows/ci.yml` now builds and tests on Linux and
  Windows, validates every example through the CLI, and runs a scripted counter
  simulation. The workflow itself still needs a GitHub Actions run to verify its
  hosted-runner behavior.
- The README reported 170 editor tests, while the current assembly discovers 177.
  README now records the discovered count without claiming that all tests passed
  in this environment.
- The single-driver runtime warning said that drivers "disagree" even when multiple
  equal values triggered the configured single-driver conflict policy. This was a
  diagnostics defect: it contradicted the policy and obscured the reason for the
  warning. The message now reports multiple active drivers violating the named
  policy, and a regression test covers equal-valued drivers.
- The repo has no `LICENSE`, contribution guide, or security-reporting guidance.
  License selection is intentionally left to the owner.
- Loading project files uses `File.ReadAllText` with no explicit file-size limit.
  JSON's parser depth limit helps with nested input, but a very large file can still
  consume substantial memory before model validation. This is a robustness risk for
  untrusted inputs, not a demonstrated failure on normal projects.

## Prioritized risks

1. **High — contention can look like a valid value.** `single` resolution returns
   the first active driver and emits one warning when multiple drivers conflict.
   Downstream primitives continue to consume that value. This policy is
   deterministic, but consumers can mistake an invalid circuit for a valid result;
   arbitrary domains have no guaranteed unknown value. A safe invalid-state design
   needs explicit semantics and a versioned compatibility plan.
2. **High — resource limits are incomplete.** The event heap grows by doubling and
   has no configured event-count cap. Per-component same-timestamp evaluation limits
   catch common zero-delay oscillation, but do not bound long delayed activity,
   total memory, or a waveform with a zero-duration cycle (which is caught only by
   the evaluation guard). User-configurable limits should produce diagnostics rather
   than process-level allocation failures.
3. **Medium — deterministic tie order includes compiled insertion order.** The
   sequence tie-break is explicit and stable for a fixed compiled order, but
   simultaneous effects can depend on the order in which authored components are
   flattened. Reordering components is not universally semantics-preserving when
   driver selection or competing same-tick writes are involved. The supported
   equivalence rules should stay explicit and tested.
4. **Medium — hierarchy depth is fixed at 64.** This is a documented validation
   boundary and recursive code makes unbounded depth unsafe. It is conservative for
   generated designs, but changing it needs iterative traversal or a stack/resource
   analysis rather than simply deleting the check.
5. **Medium — atomic-save durability is not fully specified.** The temporary file is
   created beside the target and replaced, protecting against many interrupted
   writes. There is no explicit flush-to-disk guarantee or backup recovery protocol;
   guarantees vary by filesystem and platform.

## Missing verification

- Full editor tests on Linux with native Skia dependencies and on Windows CI.
- Repeated randomized differential tests across all example signal domains with
  seeds persisted in failure reports.
- Stress coverage for event-queue growth, sequence/time boundary overflow, deeply
  nested but valid hierarchies, very large JSON files, and cancellation during
  high-activity simulation.
- Independent ordering tests for equivalent component insertion/serialization
  permutations, with cases explicitly excluded when single-driver or same-tick
  ordering is part of the model.
- A reproducer benchmark on the current VM and a broader workload than inverter
  chains.

## Missing capabilities and long-term boundaries

- The current kernel is discrete-event over finite indexed domains. It does not
  model voltage/current, continuous state, nonlinear devices, numerical error,
  convergence, electromagnetic fields, or optical wave behavior.
- A credible electrical path should introduce a separate analog model and solver:
  modified nodal analysis, sparse matrix assembly, nonlinear Newton iterations,
  transient integration, tolerances, convergence diagnostics, and explicit device
  models. Digital/analog boundaries need defined threshold, loading, timing, and
  event-coupling rules; the digital engine should remain useful independently.
- Photonic work needs separate model tiers. Geometric ray tracing, classical wave
  optics (complex fields, phase, interference, polarization and material response),
  and photon-counting/quantum models are distinct, with different accuracy and cost.
  They should be separate solver modules with explicit coupling and documented
  assumptions, not digital primitives presented as physical simulation.

## Immediate follow-up

Run the new workflow on hosted Ubuntu and Windows runners, then update test counts
and address the highest-risk runtime safety issue with a regression test before
making semantic changes to contention behavior. Reproduce the benchmark only after
the verification baseline is clean.
