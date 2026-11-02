# RADIX continuation record

Last updated: 2026-11-02. This file is the durable handoff note for agents and
humans; update it at each milestone instead of relying on chat history.

## Where the project stands

- Remote: `origin` = https://github.com/PandeManiac/radix (write access verified).
- Current continuation branch: `agent/radix-20261102-electrical-audit`, pushed to origin.
- Latest verified feature commit: `649444659bfd6d0d351217fa5aff3af4566c36a6`.
- Draft PR #6 is open against `main`: nonlinear DC convergence scaling and
  engineering-suffix entry for authored electrical values.
- Latest local verification: Release solution build clean (0 warnings/errors), Core
  tests 632/632, Editor tests 234/234, and the DC divider CLI example reports the
  expected 5 V midpoint. GitHub CI passed on Ubuntu and Windows for feature commit
  `649444659bfd6d0d351217fa5aff3af4566c36a6`; the branch tip adds only this note.
- Editor tests require fontconfig on Linux. This VM has no system `libfontconfig`,
  but the tests pass when Ubuntu native packages are extracted under ignored
  `.local-native` and `LD_LIBRARY_PATH`/`FONTCONFIG_FILE` are set.
- Recovery: the previous session's work (branch tip `be9ba54`) was merged in after
  preserving the earlier worktree as commit `7188d5f`. The merged tree matched
  `be9ba54` exactly.
- Latest verified code commit: see `git log agent/radix-verification`; benchmark documentation has a fresh seven-sample run recorded below.
- Baseline on this VM (.NET SDK 10.0.100): `dotnet build Radix.slnx` is clean with
  0 warnings; core tests pass 398/398; editor tests pass 178/178.

## Reproducible setup and commands

```bash
# SDK 10.0.100 is installed at ~/.dotnet in this VM
export PATH="$HOME/.dotnet:$PATH"

# One-time (sudo-free) native libraries/fonts for headless Skia tests
scripts/linux-test-natives.sh
export LD_LIBRARY_PATH="$PWD/.local-native/usr/lib/x86_64-linux-gnu"
export FONTCONFIG_FILE="$PWD/.local-native/fonts.conf"

dotnet build Radix.slnx
dotnet test tests/Radix.Core.Tests/Radix.Core.Tests.csproj
dotnet test tests/Radix.Editor.Tests/Radix.Editor.Tests.csproj
dotnet run -c Release --project src/Radix.Cli -- bench --gates 10000 --ticks 50
```

## Completed in this session

1. Recovered the prior session's full work and verified build and tests.
2. Added a configurable pending-event cap (`maxPendingEvents`, default 1,000,000;
   `0` disables it) that halts with `RAD150` and clears the queue. Wired through
   the project model, validator (`RAD005`), serializer, CLI sweep/script runners,
   and the editor's Simulation Settings panel.
3. Bumped the project format to v7 for the new setting; v6 files load with the
   default cap and upgrade on the next save.
4. Added a 64 MiB project/library file-size guard enforced before parsing.
5. Added `scripts/linux-test-natives.sh` and reproduced the engine benchmark on
   this VM (`docs/benchmarks.md`).
6. Capped runtime warnings at 1,000 per run; suppressed warnings are counted by the
   engine and summarized as `RAD151` in the Diagnostics tab.
7. Extended randomized differential tests to ternary, quaternary, balanced-ternary,
   and four-state networks, and added serialization-replay equivalence checks across
   representative examples. Failures report the seed, trial, and step.
8. Reproduced the editor layout benchmark on this VM; results are in
   `docs/benchmarks.md`. Absolute costs are roughly 1.5–2x the reference machine,
   while the route-reuse ratios hold.
9. Cut canonical layout rebuild cost by sharing routing geometry per layout and
   pooling A* state arrays: at 1,000 gates, construct 794 -> 461 ms, ten canonical
   frames 7,849 -> 4,706 ms, and ten canonical placements 6,543 -> 3,881 ms. Route
   output is unchanged and all 178 editor tests pass.
10. Shared the routed-segment index across nets and vectorized obstacle masking.
   At 1,000 gates the canonical path is now 366-368 ms to construct, 3,743-3,801 ms
   for ten canonical frames, and 2,900-2,982 ms for ten canonical placements, about
   54%/52%/55% below the pre-optimization baseline.
11. Replaced per-edge wire-penalty HashSet clearing/lookup with per-router integer
   visitation stamps. Build and all 178 editor tests pass. One benchmark run per
   build: at 1,000 gates, ten canonical frames improved 3,762 -> 3,584 ms and ten
   canonical placements 2,924 -> 2,772 ms (~5%); reported as modest and noisy.
12. Extended the opt-in layout benchmark to 2,000 gates. One run measured 634 ms
   construct, 6,445 ms for ten canonical frames, and 5,743 ms for ten canonical
   placements; doubled-size costs grew about 1.8-2.0x, with no abrupt cliff on this
   synthetic chain. Added a caveated exploratory result to docs/benchmarks.md.
13. Added an opt-in layout sample for the bundled 4-bit accumulator CPU root view
   (23 components, 34 nets). One run: construct 254 ms, ten canonical frames
   615 ms, ten route-reused drag frames 82 ms. Documented the scope limitation:
   root-view layout does not expand child definitions into the canvas.
14. Added an opt-in generated hierarchy benchmark with a fixed two-primitive,
   four-net child repeated 25/100/250 times. It now warms each size and gathers
   seven samples per metric, reporting median/range. Latest sample: construction
   12 [10-29] / 25 [13-57] / 41 [40-47] ms; ten canonical builds
   107 [97-122] / 71 [70-100] / 365 [360-369] ms. A fresh rerun measured
   construction 12 [10-22] / 36 [16-72] / 39 [38-43] ms and ten builds
   114 [101-116] / 99 [72-163] / 367 [359-371] ms. The noisy ranges, especially
   for 100 instances, reinforce that this shared-VM sample is exploratory only;
   profiler tooling is not installed and no representative CPU profile was captured.

## Session 2026-10-29 (agent/radix-verification)

15. Fixed a false non-convergence halt: per-timestamp evaluation counters were
    never reset when the event queue drained, so 65 stimuli applied at the same
    simulated time made the engine report `RAD100` even for a single gate with no
    feedback. The counters now belong to one settle cycle, and a drained queue
    starts the next stimulus with fresh counts. Regression test:
    `EngineTests.RepeatedStimuliAtOneTimestampDoNotAccumulateConvergenceCounters`.
    Documented the precise semantics in `docs/architecture.md` (6.4).
16. Made `SweepHonorsCancellationInsideASingleSettleRow` deterministic. It used a
    30 ms wall-clock deadline and was the repository's one known flaky test. It now
    cancels from a `NetChanged` observer after 64 transitions, wired through a new
    optional `engineObserver` parameter on `InputSweep.Run`. No timing assumption
    remains; the test exercises the same in-row cancellation path via the engine's
    every-1,024-events check.
17. Baseline on this VM (.NET SDK 10.0.403): release build clean (0 warnings),
    core tests 412/412, editor tests 180/180. The image ships no
    fontconfig/freetype/libpng and `sudo` is disabled, so the editor suite needs
    those packages fetched directly from the Ubuntu archive (see Known issues).
18. Added `SerializationFuzzTests`: 600 deterministic mutations (truncation,
    deleted/duplicated lines, corrupted tokens, out-of-range numbers) across five
    bundled example projects. Loading must either succeed or throw only
    `ProjectFormatException`/`CircuitValidationException`; the campaign reports
    source, iteration, mutation, length, and content hash on failure.
19. Added `GeneratorScaleTests`: generated register loads/holds/clears at 64, 65,
    and 128 bits; counter wrap-plus-carry at 64/65; adder carry past bit 64; and a
    128x8 RAM with independent words at the address-space edges. No width defect
    was found. The campaign did expose a semantic sharp edge: lookup primitives
    (including `std.mux2`) hold while *any* input is undriven, so the generated
    counter does not increment when EN=1 but the unused D pins are left floating.
    The tests now drive every input as documented; see Known issues.
20. Added a cumulative event budget (`SimulationOptions.MaxEventsProcessed`, default
    `0` = disabled) halting with `RAD152` at the exact processed count. `radix simulate`
    now defaults to 100,000,000 events so an extreme `--ticks` value cannot run
    effectively forever; `--max-events` overrides it and `0` disables it. The JSON
    report exposes `statistics.eventBudget`. Four tests cover exact-count halting,
    that a halted run ignores further advances, `0` disabling, and `RunUntilIdle`.
    Also deleted the stale committed `SimulationEngine.cs.backup` and the
    conflict-marker artifact `.github/workflows/ci.yml.orig`.
    Verification: release build 0 warnings, core tests 416/416, editor tests 180/180,
    `radix test examples/binary-counter.radix examples/tests/counter-counts.radixtest`
    passes, and a bounded CLI run reports `eventsProcessed=1000` with `RAD152`.
21. Fixed the first Windows-only CI failure. `HierarchyReport.ToText` used
    `StringBuilder.AppendLine`, so the same project rendered CRLF on windows-latest
    and `TreeMarksSharedSubtreeOnceAndHonorsDepthLimit` failed on an exact line
    comparison (Ubuntu passed). The text report now emits `\n` explicitly, and
    `TextReportUsesDeterministicLineFeedEndings` asserts no `\r` and a final `\n`.
    CI runs on both platforms; this is a real portability defect, not a test-only
    normalization.
22. Implemented conservative lookup evaluation for undriven inputs. A lookup now
    completes every undriven input across its connected domain and commits an output
    only when all completions agree; otherwise it holds its previous output. This
    makes an unselected floating mux data input and a controlling gate value usable
    without fabricating a result for genuinely ambiguous inputs. Explicit four-state
    X/Z values remain ordinary table indices. Compiler validation bounds the
    completion space with the 1,048,576-entry table cap. Added
    `ConservativeLookupTests` covering mux, AND/OR, fully undriven constant and
    ambiguous lookups, mixed radix, four-state X, and transport delay; the wide
    counter test now leaves D undriven and proves the original generated-part
    failure is fixed. Verification: release build 0 warnings, core tests 424/424,
    editor tests 180/180, and the counter CLI script passes. The rule is specified
    in `docs/architecture.md` 5.2 and `docs/project-format.md`.
23. Added `RandomizedLookupOracleTests`, a seeded 200-case differential campaign.
    Every case uses two to five inputs with at least two distinct standard domains
    and a random table. Seven patterns per case cover fully driven, partially driven,
    and fully undriven inputs. The independent oracle decodes each table entry,
    rejects entries inconsistent with driven digits, and treats the output as
    determined only when every remaining entry agrees. Expected held outputs are
    tracked across steps, and the test asserts that both determined and ambiguous
    steps occur, so it cannot pass by exercising only one path.
    Verification: core tests 425/425, release build 0 warnings and 0 errors.
24. Extended the oracle campaign to multi-lookup cascades with transport delays and
    feedback. Added `RandomizedLookupCascadeOracleTests`: 80 seeded cases of a ring
    of 3-6 lookup gates. Each gate combines the previous gate's binary output with a
    random external domain (binary, ternary, quaternary, or four-state) and has a
    1-3 tick delay; the last output feeds back through an initialized net. Twenty-
    four stimulus steps cover fully driven, partially driven, and fully undriven
    inputs. An independent event-model oracle schedules evaluations and delayed
    commits, scans each table directly for all completions, and compares every gate
    output at every tick, so oscillatory networks are valid. Coverage assertions
    require determined and ambiguous lookups, output transitions, feedback changes,
    positive-delay undriven outputs, and a multi-tick delay.
    Verification: core tests 426/426, release build 0 warnings and 0 errors.
25. Extended the cascade oracle to explicit `Delay` primitives and multiple feedback
    paths. Added a second seeded campaign of 64 cases over 3-6 binary lookup gates.
    Each gate has two distinct incoming feedback edges, a neighbor tap and a randomly
    offset tap, both implemented as separate `Delay` components with independent 1-3
    tick delays and initialized outputs. Each gate also receives an external input and
    commits after its own 1-2 tick lookup delay. Twenty-eight steps mix fully driven,
    partially driven, and fully undriven external inputs. An independent event model
    tracks pending evaluations, delayed commits, and the exact component order, and
    compares every gate output and every internal delay output at every tick. Coverage
    assertions require determined and ambiguous lookups, gate and delay transitions,
    multi-tick explicit delays, distinct feedback sources, and gate outputs that stay
    undriven until their first delayed commit.
    Verification: core tests 427/427, release build 0 warnings and 0 errors.
26. Extended the explicit-delay oracle to heterogeneous gate domains and wider
    fan-in. Added a separate seeded campaign of 48 cases over 3-6 gates whose output
    domains span binary, ternary, quaternary, and four-state signals. Each gate now
    combines two or three distinct feedback taps with an external input, and every
    tap remains an independent `Delay` primitive with a 1-3 tick delay and initialized
    output. Twenty-eight steps mix fully driven, partially driven, and fully undriven
    external inputs. The independent reference indexes each lookup by per-input
    radices, enumerates undriven completions directly, and compares every gate output
    and every internal delay output at every tick. Coverage assertions require all
    four domains at gate outputs and external inputs, a gate with three feedback taps,
    determined and ambiguous lookups, gate and delay transitions, multi-tick explicit
    delays, and undriven gate outputs at time zero.
    Verification: core tests 428/428, release build 0 warnings and 0 errors.
27. Closed the terminal-drain and multi-undriven gaps in the mixed-domain explicit-delay
    oracle. The original 48-case campaign now advances both the engine and reference
    after the final all-undriven stimulus until both queues are empty, capped at 32
    ticks, and compares every gate output and explicit-delay output at every tail tick.
    Added a second seeded campaign of 16 cases over 3-5 gates with two or three external
    inputs and two or three feedback taps per gate; each external input has its own
    domain-specific port, so a lookup can genuinely evaluate several undriven inputs at
    once. Link delays span 1-4 ticks. The reference now counts multi-wildcard determined
    and ambiguous evaluations. Coverage assertions require a several-undriven-input
    lookup that determines an output, one that holds on disagreement, all four domains
    at gate and external inputs, multi-tick delays, an all-undriven gate stimulus, and
    terminal-drain progress plus at least one fully settled case in each campaign.
    Both campaigns also project the engine's net trace onto gate outputs and delay nets
    and compare the exact time, order, old value, and new value of every transition
    against a reference-side transition log, including the terminal drain.
    Verification: core tests 429/429, release build 0 warnings and 0 errors.
28. Added zero-delay lookup cases between explicit delays. A third seeded campaign of
    16 cases uses 3-6 gates, two or three feedback taps, mixed binary/ternary/
    quaternary/four-state domains, and 1-4 tick delay edges, but every lookup commits
    at the same timestamp as its evaluation. The reference counts zero-delay gate
    commits and requires every gate transition to come from one. Twenty-eight stimulus
    steps plus a bounded 32-tick terminal drain compare every gate and delay output at
    every tick, and the ordered gate/delay trace is compared against the reference.
    Coverage requires determined and ambiguous lookups, zero-delay gate changes,
    explicit-delay transitions, multi-tick delays, trace activity, a settled tail, and
    all four domains at gate outputs and external inputs.
    Verification: core tests 430/430, release build 0 warnings and 0 errors.

29. Added the first structural electrical rule-checking layer. `RAD-ERC-001`
    reports a net with required component inputs but no instance output or input-port
    source; `RAD-ERC-002` reports an input-port net that is also driven internally
    under a single-driver policy. Existing `RAD080` and `RAD081` now carry explicit
    assumptions, suggestions, simulation-validity, and structural/runtime metadata.
    Multiple drivers on priority or multi-driver domains remain valid, not faults.
    The validation JSON report exposes the new metadata, the CLI prints assumptions
    and suggestions, and the editor Diagnostics tab shows them and navigates to the
    owning net. `docs/erc.md` records supported rules and deliberately unsupported
    electrical phenomena. Removed the redundant compiler `RAD119` warning so a
    single-driver conflict is reported once by the authored-model ERC check.
    Verification: release build 0 warnings/0 errors, core tests 437/437, editor
    tests 181/181, and all 24 bundled example projects validated without ERC,
    `RAD080`, `RAD081`, or `RAD119` false positives.

30. Added a separate DC linear electrical solver under `Radix.Core.Electrical`.
    The netlist API supports ideal resistors, independent voltage sources,
    independent current sources, and one explicit ground reference; it solves
    with modified nodal analysis and partial-pivot Gaussian elimination and
    returns node voltages plus element currents, voltages, and absorbed power.
    Construction rejects invalid or duplicate elements; solving reports missing
    ground references, singular systems, and non-finite results. The solver is
    intentionally not connected to the digital engine, `.radix` format, editor,
    or ERC. Added `DcLinearSolverTests` against analytical dividers, parallel
    networks, current-source directions, series sources, a balanced Wheatstone
    bridge, singular and ground-reference failures, invalid inputs, and a
    100-channel network; documented conventions and boundaries in
    `docs/electrical.md`.
    Verification: core tests 451/451, release build 0 warnings and 0 errors.

31. Exposed the DC linear solver through a headless `radix dc <netlist.json>`
    command with text and `--json` reports. Added a strict versioned
    `radix-dc-netlist` input format (resistor/voltage-source/current-source units,
    required terminals, unknown-field rejection, 16 MiB bound) and a
    `radix-dc-report` projection containing nodes, element parameters, terminal
    voltages, currents, absorbed power, and stable solve-failure codes. Malformed
    input exits 2; a singular or otherwise unsolvable system exits 1 with JSON when
    requested; success exits 0. Added `examples/dc-divider.json` and
    `DcNetlistJsonTests` for loading, reports, strict validation, file bounds, and
    failure reports. Documented the format in `docs/electrical.md` and the CLI
    examples in `README.md`.
    Verification: core tests 456/456, release build 0 warnings and 0 errors, and
    manual CLI checks for success, singular failure, and malformed input.

32. Added explicit authored DC electrical circuits to project format v8. Each
    circuit owns named nodes, a ground reference, and components with explicit
    `a`/`b` terminal bindings, terminal polarity, a kind-specific parameter, and
    unit. `ElectricalComponentMetadata` defines the reusable terminal contracts
    and units; `ElectricalCircuitValidator` reports stable `RAD-ELEC-*` errors for
    missing/duplicate/unknown terminals, polarity mismatches, invalid values,
    self-loops, missing grounds, and ungrounded circuits. The solver bridge refuses
    invalid circuits and maps authored terminals to `DcNetlist`. `radix dc` now
    accepts a `.radix` project (with `--circuit` when needed) as well as standalone
    JSON, and reports the selected circuit ID. Electrical assets remain separate
    from digital compilation and the event engine.
    Verification: core tests 462/462, release build 0 warnings and 0 errors, and
    manual CLI checks for authored `.radix` and standalone `.json` success plus JSON
    reports.

33. Added the editor's **Electrical** tab for browsing and editing authored DC
    circuits. It creates/removes independent circuits, selects ground, adds and
    renames nodes, adds/removes components, changes component kind while
    preserving node bindings and rebuilding the terminal contract, edits finite
    values, and binds each explicit terminal to a node. Live `RAD-ELEC-*`
    validation, dirty tracking, undo/redo, project save/load, and the existing
    inspector field-commit behavior are wired through. The tab is intentionally a
    text/metadata inspector plus a read-only DC operating-point preview: Solve
    runs the same MNA bridge as `radix dc` and shows node voltages and each
    element's terminal voltage, current, and absorbed power, with explicit
    structural and singularity failures. Results clear on edits. It does not map
    digital nets to volts or change the digital simulation.
    Verification: release build 0 warnings/0 errors; all 9 electrical editor
    tests pass; full editor suite passes 190/190.

34. Added JSON and CSV clipboard export for the Electrical tab's DC solve
    preview. The result rows now retain their raw numeric values, and the export
    builders project those displayed values without re-solving or mutating the
    project. JSON is versioned (`radix-dc-solve`) and carries circuit identity,
    ground, conventions, node voltages (including unreferenced nodes), and element
    values, terminal voltages, currents, and absorbed power. CSV combines node and
    element rows with invariant round-trip numbers and escaped text. Copy actions
    appear only with a successful solve and clear with the preview on edits.
    Verification: release build 0 warnings/0 errors; all 10 electrical editor
    tests pass; full editor suite passes 191/191.

## Known issues and limitations

- Editor tests need fontconfig, freetype, libpng and a font on Linux. This VM's
  image has none of them and `sudo` is disabled; `scripts/linux-test-natives.sh`
  cannot fetch them because apt lists are absent. Verified workaround: download
  the noble debs (`libfontconfig1_2.15.0-2.3ubuntu1`, `libfreetype6_2.13.2+dfsg-1build3`,
  `libpng16-16t64_1.6.43-5build1`, `fonts-dejavu-core_2.37-8`) from
  archive.ubuntu.com, extract with `dpkg-deb -x`, then set `LD_LIBRARY_PATH` to
  the extracted `usr/lib/x86_64-linux-gnu` and `FONTCONFIG_FILE` to a config that
  points at the extracted fonts. Core tests are unaffected.
- Everything under "Explicit limitations" in `README.md` still applies (the DC
  solver remains core-only with a read-only editor preview; the editor does not
  simulate or graphically visualize circuits, no AC/transient/nonlinear solver,
  no JIT, fixed-width generated parts, and so on).

## Next meaningful steps

1. After CI completes for the engineering-suffix change, continue the electrical
   authoring flow by accepting SI suffixes in unit-bearing initial-state and source
   waveform fields, with tests for time/value units and unchanged serialized numbers.
2. For stronger hierarchy scalability data, compare the seven-sample run on a
   quieter host, or use `dotnet-trace` for a bounded CPU profile; the shared-VM
   benchmark ranges are exploratory.

## Session 2026-10-31 (agent/radix-dc-numerics)

This branch is stacked on `agent/radix-erc` (PR #3) because the independent DC
solver it hardens is not merged into `main` yet. It does not change that branch.

29. Repaired two numerical defects in the DC solver's linear algebra:
    - Pivot rejection used one global matrix magnitude as its scale, so a valid
      circuit with a weak branch could be rejected as singular. A divider made of
      two 1 GΩ resistors failed with `SingularMatrix`; now it solves to 0.5 V.
      Pivot candidates are compared with their own row's magnitude (scaled partial
      pivoting), and a pivot is rejected only below `1e-12` of that scale.
    - The solver now computes the relative backward error
      `max|Ax-b| / max(||b||∞, ||A||∞||x||∞)`, exposes it as
      `DcSolution.RelativeResidual`, and refuses a solve whose residual exceeds
      `1e-8` with the new `IllConditioned` failure instead of returning an
      untrustworthy operating point.
30. Added failure classification that was previously collapsed into a generic
    singular-matrix error: `FloatingNetwork` names the nodes in an island with no
    path to ground, `InconsistentSourceConstraints` names a contradicting ideal
    voltage source, and a *consistent but redundant* source loop stays
    `SingularMatrix` because its branch currents are genuinely not unique. The
    headless JSON report and editor failure labels map the new values.
31. Added `DcSolverNumericalTests` (11 tests): PΩ/TΩ and mΩ dividers, extreme
    conductance spread on independent branches, floating-island and conflicting-
    source classification, consistent-redundant-source distinction, KCL at every
    node, KVL around a bridge mesh, absorbed-power conservation, element-order
    invariance, and two seeded randomized ladder campaigns. Expected ladder
    voltages come from a decimal closed-form recurrence, so the solver is not its
    own oracle. The nine-decade campaign asserts `1e-6` forward accuracy and a
    moderate three-decade campaign asserts `1e-10`; both check power conservation
    and require a residual below `1e-12`.

Verification: release build 0 warnings/0 errors; the full Core suite and editor
suite results are recorded in the final handoff for this session.
32. Extended ERC to authored electrical circuits. `DcNetlist.AnalyzeTopology`
    is now a public, solve-independent projection used by both the solver and the
    rule checker, so connectivity is analysed once. `RAD-ERC-101` reports a
    floating electrical island with its node names, `RAD-ERC-102` reports
    contradictory ideal voltage sources with the conflicting source, and
    `RAD-ERC-103` reports a consistent but redundant source loop whose branch
    currents are not unique. They are warnings with paths and assumptions, so
    digital compilation is unaffected. Added `ElectricalCircuitRuleTests`
    covering valid, floating, contradictory, redundant, structurally invalid,
    and serialized projects.
33. Added a Shockley diode model to the independent DC netlist solver, scoped
    to the netlist/JSON/CLI path so no project-format change was required.
    `DcDiode` uses `I = Is(exp(V/(N*Vt))-1)` with anode/cathode terminals, a fixed
    default thermal voltage of 25.852 mV (300.15 K), and no breakdown,
    capacitance, series resistance, or self-heating. Netlists containing diodes
    use a damped Newton loop with pn-junction limiting, a nonlinear KCL/source
    residual check, and a `NonConvergent` failure after 100 iterations; linear
    netlists keep the single-factorization fast path. The versioned JSON netlist
    accepts `type: "diode"` with `saturationCurrent`, optional
    `emissionCoefficient`, and optional `thermalVoltage`; the report exposes the
    model parameters. Added `DcDiodeSolverTests`, including forward, reverse,
    series, emission-coefficient, parameter-validation, JSON, KCL, and power
    checks against an independent scalar bisection rather than the solver.
34. Added optional series resistance to the diode model. `DcDiode.SeriesResistance`
    relates terminal and junction voltage by `Vterminal = Vjunction + Rs*I`;
    junction voltage is recovered by monotonic scalar bisection, and the Newton
    stamp uses the combined conductance `Gj/(1+Rs*Gj)`. `Rs = 0` preserves the
    ideal-junction behavior and the existing tests unchanged. The netlist JSON
    accepts and reports `seriesResistance`; the headless diode example now models
    a realistic 10 Ω series resistance. Added independent bisection tests over
    several resistance values, plus monotonicity and parameter-validation cases.

## Session 2026-11-01 (agent/radix-transient)

Base: `origin/main` at `40bd3a9`. PR #4 (`agent/radix-dc-numerics`) was left
untouched: it hardens the DC solver, while this branch adds a separate analysis
path and changes no `DcLinearSolver` semantics.

35. Added fixed-step transient (time-domain) electrical analysis in
    `Radix.Core.Electrical.TransientNetlist`. Capacitors and inductors are
    integrated with companion models: backward Euler (default, L-stable) and the
    trapezoidal rule (second order, energy preserving on linear LC networks, not
    L-stable). Every step assembles a purely resistive MNA system and solves it
    through the existing `DcNetlist`, so scaled partial pivoting, residual
    rejection, and floating/contradictory/singular classification are shared.
    Diodes remain nonlinear per step through the existing Newton iteration.
36. Solved the initial state in two phases: a DC operating point (capacitors
    open, inductors shorted) supplies defaults, then a state-imposing network
    (each capacitor as an ideal voltage source, each inductor as an ideal current
    source) produces the reported `t = start` node voltages. The first
    implementation reported the DC values while integrating from the preset
    states; two tests caught that inconsistency before it shipped.
    `SetCapacitorInitialVoltage` and `SetInductorInitialCurrent` override
    individual states, and a capacitor with no other DC path is reported as a
    floating network unless its voltage is preset.
37. Added piecewise-linear source waveforms (`SetWaveform`): finite, distinct,
    sorted breakpoints with endpoint hold, covering pulses, ramps, triangles, and
    sampled drives. Constant sources are unchanged. Without this, every run
    started at the DC steady state and only DC-op-perturbation responses were
    expressible.
38. Added the strict versioned `radix-transient-netlist` format and the
    `radix-transient-report` projection in `TransientNetlistJson`, plus the
    headless `radix transient` command (`--start`, `--stop`, `--steps`,
    `--method`, `--max-rows`, `--json`). A run keeps the points it solved before a
    failed step and exits 1 with the failing step and its classification.
39. Added `TransientSolverTests` (17 tests) and `TransientNetlistJsonTests`
    (5 tests plus a 14-case rejection theory): closed-form backward-Euler
    recurrences for RC charge, capacitor discharge, and inductor current;
    convergence order 1 for backward Euler and 2 for trapezoidal against the
    analytic RC solution; trapezoidal energy conservation versus backward-Euler
    damping in an LC tank; the analytic underdamped RLC overshoot
    `1 + exp(-πζ/√(1-ζ²))`; per-point absorbed-power conservation; element-order
    and ground-name invariance; initial-condition handling; floating detection;
    deterministic reruns; waveform interpolation; parameter/option validation;
    and strict JSON parsing.
40. Added `examples/transient-rc.json`, `examples/transient-rlc.json`, and
    `examples/transient-pulse.json` and documented the solver, format, failure
    behaviour, and limits in `docs/electrical.md` and `README.md`.
41. Added adaptive step control (`TransientOptions.AdaptiveTolerance`, CLI
    `--tol/--initial-step/--min-step/--max-step/--max-steps`, JSON `tolerance`).
    Control uses step doubling from the same state, normalises by `2^p - 1`, and
    accepts only when every capacitor and inductor state variable is within
    `rtol*|value| + rtol*1e-6`. Accepted steps keep the two half steps; rejected
    steps shrink by `clamp(0.9*err^(-1/(p+1)), 0.2, 4)`. A step that cannot be
    solved at all is retried smaller, so an over-large step is recoverable
    instead of fatal, and failing to meet the tolerance above `minStep` is
    reported as `NonConvergent` rather than a completed run. Six tests cover
    accuracy vs a fixed run, tolerance monotonicity, a 1 us time constant over a
    10 ms interval, determinism, the minimum-step failure, and option validation;
    a JSON test covers loading and reporting adaptive settings.
    The first implementation controlled the capacitor current and inductor
    voltage as well, with an absolute floor of `rtol * 1e-6`. A temporary debug
trace showed that at a drive ramp the estimator never accepted a step: those
    derived quantities do not shrink with the step at a kink, so the normalised
    error stayed around 0.8 while the step collapsed to 4e-12 s. The metric now
    controls only capacitor voltage and inductor current, with
    `AdaptiveAbsoluteTolerance` derived by default from the circuit's source and
    initial-state scale. Adaptive accuracy is asserted against the analytic RC
    solution, not just step counts.
42. Refactored the integrator so the fixed and adaptive loops share one
    `TryAdvance` step routine and an immutable `ReactiveState`, and the CLI text
    report distinguishes fixed and adaptive headers.
43. Added authored `capacitor` and `inductor` components with an optional
    `initialValue` (initial capacitor voltage in volts, initial inductor current
    in amperes, positive `a` to `b`), bumping the project format to 9. Metadata
    drives units (F/H), passive terminal contracts, kind names, and the editor's
    kind list, ID prefixes, defaults, and initial-value field, which accepts a
    blank value for the DC operating point. `ElectricalCircuit.ToDcNetlist`
    keeps its DC meaning (capacitor open and omitted, inductor as a 0 V source
    short) and `ToTransientNetlist()` feeds the transient solver, so
    `radix transient examples/transient-rc.radix --circuit rc-charge` integrates
    an authored circuit and reports its circuit ID in text and JSON. Validation
    adds `RAD-ELEC-015` (initial value on a non-reactive kind) and `RAD-ELEC-016`
    (non-finite initial value).
44. Added authored piecewise-linear source waveforms (`waveform` breakpoint arrays
    on independent sources, project format 10) so an authored `.radix` circuit can
    express a step, ramp, or pulse without hand-writing a JSON netlist.
    `ToTransientNetlist` forwards the breakpoints to the solver's waveform, while
    `ToDcNetlist` and the editor keep using the source's `value`, and the model
    keeps the waveform untouched through editor edits and saves. Validation adds
    `RAD-ELEC-017` (waveform on a non-source), `RAD-ELEC-018` (fewer than two
    breakpoints), `RAD-ELEC-019` (non-finite time or value), and `RAD-ELEC-020`
    (non-increasing times). `examples/transient-pulse.radix` drives an RC step and
    reaches `5*(1 - e^-1)` one time constant after the step.
45. Added an energy ledger to every transient run: source-delivered and
    resistor/diode-dissipated power is integrated over the reported points with the
    trapezoidal rule, stored `v²C/2 + i²L/2` is compared at the first and last
    point, and the residual `delivered - dissipated - stored` is printed with its
    relative size and reported as `energy` in the JSON report. The ledger also acts
    as an independent accuracy signal: it closes to `1.2e-5` relative for an RC
    charge at `h/τ = 0.01`, to `1.0e-3` for a lossless LC tank at `hω = 0.032`,
    refining the step 4x cuts either about 16x, and backward Euler's is first order.
    Tests assert the analytic `C·V²` delivery and `½C·V²` storage, the tank's
    conserved stored energy with zero dissipation, both convergence orders, the
    absent ledger on a failed initial state, and the JSON fields.
46. Added waveform authoring to the Electrical inspector: an independent source now
    exposes a `Waveform` field that reads and writes comma-separated `time=value`
    breakpoints in the source's own unit, validates them (finite numbers, at least
    two breakpoints, strictly increasing times), applies the change as one undo
    step, and clears with a blank value. `ApplyField` keeps an editor-specific
    rejection message instead of overwriting it with a generic one, so a bad
    waveform says why. Tests cover authoring, round-trip formatting, rejection of
    decreasing/repeated/short/garbled input without mutation, clearing, and the
    field's absence on a resistor.
47. Added a transient run to the Electrical tab: **Run transient** integrates the
    selected circuit through `ToTransientNetlist()` with the panel's own stop
    time, step count, and method (view state, not project data), lists solved
    points as time plus node voltages (sampled evenly above 300 points), shows the
    energy ledger, and copies or saves the same `radix-transient-report` JSON the
    CLI writes, including the circuit id. Any circuit edit, circuit switch, or DC
    solve clears the preview so stale numbers cannot be mistaken for current ones;
    a structural or integration failure is reported in the panel and status bar.
    The JSON save helper now takes a label and file suffix instead of hardcoding
    the DC names. Tests cover the point table, summary, energy text, exported JSON
    (circuit id, completion, delivered energy), method normalisation, rejection of
    a non-numeric step count, and invalidation on a settings change.
48. Completed the panel's transient parity with the CLI: a **Transient tolerance**
    field (blank keeps the fixed step count, a positive relative tolerance enables
    adaptive stepping with a bounded trial budget) and **Copy CSV** / **Save CSV…**
    exports. The CSV is a wide `time_s` table with one column per node, invariant
    round-trip numbers, and every solved point; only the on-screen table is sampled
    above 300 points, so the export never depends on display limits. A new read-only
    `ElectricalTransientPointCount` exposes the solved count so the panel summary
    and tests can distinguish the two. Tests cover adaptive mode selection,
    rejection of a non-positive tolerance, the CSV header and first row, the export
    carrying more rows than the sampled table, and returning to fixed stepping.
49. Made waveform breakpoints step boundaries in both integrators. A fixed-step run
    now shortens the step that would straddle a breakpoint, lands on it, and resumes
    at the requested size, so `steps` is nominal and such runs report extra points;
    an adaptive run clamps its trial step the same way. Before this, a pulse narrower
    than the step was sampled only where it is zero and disappeared, and a ramp kink
    inside a step was integrated with a value that never held. The final step also
    absorbs floating-point remainder below the stop time instead of emitting a
    spurious near-zero step (which had added one point to every panel run). Two
    recurrence tests that used a 1e-12 s drive step now start from explicit initial
    state, because a breakpoint is no longer crossed; the shipped-example count
    assertion allows the added landing points. New tests show a 2 us triangle pulse
    is integrated (it was exactly zero before) and lands on its breakpoints in fixed
    and adaptive runs, matching `(Q/tau) * e^(-(T-t_end)/tau)` within 5% and 2%.
50. Added per-node peak, swing, and settling metrics to every transient result: min,
    max, final value, the time of the maximum, and when the node last left a 2% band
    around its own final value or swing (null if still outside, which is what an
    oscillation looks like). `radix transient` prints the table and the JSON report
    carries it under `metrics`. Metrics are read from the reported points, not a
    second solve. Tests check the first-order settling formula including the finite
    final value, the analytic underdamped RLC overshoot and quarter-period peak, a
    pulse peaking where the drive falls below the capacitor voltage, absent metrics
    on a failed run, and the JSON fields.
51. Made the settling band configurable and surfaced metrics in the app: the band is
    now `TransientOptions.SettlingBand` (default 0.02, validated strictly between 0
    and 1), `--settling-band <fraction>` on the CLI, and a **Settling band** field in
    the panel, so a typo like `2` is rejected instead of turning every node into an
    instantly settled one. The panel also lists each node's minimum, maximum, final
    value, peak time, and settling time above the solved-point table. Tests cover
    the custom band's settling formula, rejection of 0, 1, and NaN, the panel rows,
    the field's validation message, and invalidation of the previous run.
52. Drew the transient run in the Electrical tab. The geometry lives in
    `Radix.Editor.Rendering.TransientPlot`, a plain Avalonia-free model, so scaling is
    unit-testable; `TransientPlotView` only draws the points it is handed (frame,
    axes, grid, zero line, labelled ticks, node names). All nodes share one time and
    value axis so traces stay comparable, the value axis is padded by 10% of the
    span, and a flat trace falls back to `max(|v| * 0.1, 0.5)` rather than collapsing
    to a zero-height scale. Runs longer than 600 points per node are decimated by
    even stride and always keep the last sample, because that is the settled value,
    while the axis labels still report the true first and last solved times. A
    single sample, an empty run, or a non-finite value yields no plot and an explicit
    empty state. Tests check shared axes, padding, corner mapping, the flat-trace
    fallback, the decimation cap with a preserved last sample, tick coverage, and
    that a run produces three drawable node series and clears when settings change.
53. Added a step-refinement accuracy study, so a run can be judged without a closed-form
    reference. `TransientRefinement.Analyze` solves the circuit at the requested step
    count and at each doubled count, compares every finer run against the coarser one at
    the finer run's times (interpolating the coarse trace), and infers the measured order
    `p = log2(d_prev / d_now)`. The finest run's error is estimated as `d / (2^p - 1)`
    relative to the node peak (Richardson). `radix transient --refine` prints the table,
    the estimate, and the worst node; a failed level reports the same `DcSolveFailure` as
    the base run, an order near zero is reported rather than rounded away, adaptive runs
    are rejected because there is no nominal step to halve, and the studied baseline is
    the run already reported so the two cannot disagree. Measured on the RC example:
    backward Euler settles to order `0.989` with a `5.8e-3` relative estimate, trapezoidal
    to `1.819` with `6.8e-4`. Tests cover both orders, the estimate against the analytic
    solution within an order of magnitude, baseline reuse, failure propagation, the
    step-limit guard, level bounds, and a breakpointed drive.
54. Put the accuracy study in the app: **Check accuracy** beside the transient result refines
    the displayed run through the same `TransientRefinement` code and reports the estimate, the
    measured order, and the node that moved most (for the RC example at 200 steps: `5.8e-3`
    relative, order `0.989`, node `out` moving `0.0285 V` at `0.000969 s`). It is an explicit
    action because it costs three solves, it refuses to run before a transient or on a failed
    one, it explains that an adaptive run has no step to halve, and its text is cleared with
    the run by any edit or settings change so it cannot describe stale numbers. Tests cover the
    refusal, the reported order and worst node, and the clear-on-change behavior.
55. Added a time probe to the transient plot, so a curve can be read as numbers. **Probe at
    (s)** plus **Probe** reads every node at a chosen time and draws a dashed marker at that
    time (`TransientPlot.FractionOf`, clamped to the frame and safe for a non-finite time). A
    time that is not a solved sample is interpolated between the two points that bracket it and
    the readout names them, so an interpolated value is never presented as a solved one; a time
    outside the run is rejected with the valid interval, a non-numeric entry says so, and the
    probe clears with the run on any edit. Tests cover the clamped marker fractions, the
    interpolated value against the analytic charge (`5*(1 - e^(-t/tau))` within 2%), the
    bracketing readout, both rejections, the exact-sample case, and the clear-on-change case.
56. Added SVG export for the transient plot. `TransientPlotSvg.Render` is a pure function over
    the plot geometry that reproduces the control's frame, ticks, zero line, series colours,
    legend, and probe marker as a standalone document; **Copy SVG** and **Save SVG…** expose it
    beside the JSON and CSV exports. Node names are XML-escaped so an unusual id cannot produce
    a malformed file, a marker outside the plotted range is omitted rather than clamped into a
    misleading place, and a size with no room for the axes is rejected. Tests parse the output,
    check one polyline per node with every coordinate inside the view box, escaped names that
    still parse, the presence and absence of the marker, and the size guard; the panel test also
    checks the export exists for a run and disappears with it.
57. Listed the refinement study per level in the panel instead of one summary line: each row
    gives the step count, the uniform step, the deviation from the coarser level, and the order
    that deviation implies, so the point where the order stops improving is visible rather than
    hidden. The coarsest row reads `reference` instead of a zero deviation, since it is what the
    others are compared against, and the table is cleared with the run like every other view of
    it. The panel test checks three rows, the reference row, and a parseable order near one for
    backward Euler, plus the empty state after a settings change.
58. Put the refinement study in the JSON report. `TransientNetlistJson.ToReportJson` takes an
    optional study and emits a `refinement` object: `completed`, `levels` (step count, step,
    deviation, and the order once two deviations exist), the finest deviation, the study's
    measured order, the Richardson estimate, and the worst node. A failed study keeps the levels
    that solved and its `failure`, so "not checked" and "checked and failed" stay distinguishable;
    the field is omitted when no study is given. `--refine --json` now works instead of being
    refused, and the panel's JSON export includes the study exactly while its accuracy check is
    current. Tests parse the report for the levels, the missing order on the reference level, the
    measured order and estimate, the worst node, and the absent block without a study; the panel
    test checks the export gains `refinement` after a check and cannot be built once an edit has
    invalidated the run.
59. Made the refinement depth configurable instead of fixed at three levels: `--refine-levels
    <2-5>` on the CLI and a **Refine levels** field in the panel, both validated (a count of one
    cannot imply an order and six is beyond the supported range), with the study's step-limit
    guard still refusing a run whose finest count would exceed 10^6. On the RC example at 40
    steps a fourth level moves the measured order from `0.989` to `0.995` and halves the estimate
    from `5.8e-3` to `2.9e-3`, which is the honest way to show a stiff circuit converging. Tests
    cover the rejected count, the four-row study reaching 1600 steps, the reference row, and the
    CLI's exit 2 for an out-of-range flag.

60. Measured transient cost and fixed the time-grid defect the measurement exposed.
    `TransientBenchmarkTests` is an opt-in benchmark (`RADIX_TRANSIENT_BENCH=1`) over a series RC
    ladder; on an AMD Ryzen 9 9950X3D it measures 21.7 us/step at 5 nodes and 56.3 us/step at 50
    nodes over 5,000 steps, and the ratios show per-point work (companion assembly plus the four
    dictionaries behind each `TransientPoint`) dominating the cubic factorisation at these sizes.
    The benchmark's `steps + 1` invariant then failed: with `stop = 0.01` and 1,000 steps the run
    reported 1,002 points, because the remainder tolerance `|stop| * 1e-14` was smaller than the
    rounding accumulated over 1,000 additions, so a sliver final step survived. Both the fixed and
    adaptive loops now scale that tolerance with the number of points, which stays below 1% of a
    real step even at the 10^6-step limit. `TransientTimeGridTests` pins the grid over seven
    stop-time/step-count pairs and a charged initial state, and [docs/benchmarks.md](benchmarks.md)
    records the baseline and its caveats.

61. Added initial-condition notes. A preset `initialValue` is legitimate, but when it disagrees
    with the circuit's own DC operating point the run begins with a discontinuity that the
    sources do not explain. The solver now compares every preset with the operating point across
    that element and records a note such as `'C1' starts at 2 V but its DC operating point is 5 V;
    the run begins with a 3 V discontinuity.` The comparison is relative (`max(|v|) * 1e-9`), so a
    matching preset stays silent and a millivolt circuit is judged like a 5 V one; a note never
    changes the result, the failure classification, or the time grid. `radix transient` prints the
    notes before the point table, the JSON report carries them as `notes` (omitted when empty),
    and the panel shows them above the energy ledger, cleared with the run. Tests cover both
    element kinds, the silent case, the unchanged grid, and the report field.

62. Made the plotted traces individually visible. Every node gets a checkbox under the plot, so
    a designer can read a 50 mV ripple next to a 5 V step instead of choosing between them.
    `TransientPlotSeries` now carries a palette `colorIndex` assigned from the full node list, so
    hiding one trace does not repaint the remaining ones and a half-read picture keeps its
    meaning; the control and the SVG export both colour by that index. Hiding every trace yields
    the same explicit empty state as a run with nothing to draw, and a new run shows them all
    again. Tests hide a trace, check the surviving count and the untouched palette slots, hide
    everything to get no plot, restore all three, and confirm a rerun resets visibility.

63. Added `radix transient --csv` and shared its writer with the panel. `TransientCsv.Write`
    emits a `time_s` header plus one column per node in ordinal order and one row per solved
    point, with invariant round-trip numbers so a reader parses back the same doubles, and quoted
    node names whose own quotes are doubled so a comma cannot break the table. The panel's CSV
    export now calls the same writer, so the two cannot drift apart; `--csv` is refused alongside
    `--json` and with `--refine` rather than silently preferring one. Tests check the header
    order, one row per point, exact round-trip of the final time, a comma-containing node name,
    an embedded quote, and the mutual-exclusion exit code.

64. Stamped exported files with deterministic provenance. `TransientStamp.From` records the
    source, circuit, method, interval, steps, step size, adaptivity, settling band, tolerance,
    point count and completion as ordered key/value pairs, and both the CSV writer and the panel's
    export emit them as `# key: value` comment lines ahead of the table. There is deliberately no
    wall-clock timestamp, so two exports of one run are byte-identical and a diff stays meaningful;
    numbers use the same round-trip format as the body, and a comment value cannot contain a line
    break, so a label can never forge a header row. Tests check the fields and their determinism,
    the comment block ahead of an intact table, the no-stamp case, and the newline-is-folded case;
    the panel test now finds the header after the stamp and checks the circuit name.

65. Stamped the SVG export the same way as the CSV. `TransientPlotSvg.Render` takes an optional
    `TransientStamp` and writes it as a `<metadata><provenance/>` block using the same keys, plus a
    caption line under the axis labels naming the circuit, method, step count and point count. The
    caption reserves 14 px of its own so it cannot cover a tick, values are XML-escaped so a label
    with `<`, `&` or a quote still yields a well-formed document, and two renders of one run are
    byte-identical. The panel passes the displayed circuit's stamp, so a picture carried into a
    document names its run as precisely as the table. Tests parse the metadata, check the caption,
    the escaped value, the determinism, and the absence of the block without a stamp.

66. Fingerprinted the solved netlist. `TransientNetlist.Fingerprint()` hashes a canonical text
    form of the circuit — ground node, every element with its type, nodes and values (including
    diode saturation current, emission coefficient, thermal voltage and series resistance), the
    reactive elements, presets and waveforms, ordered by id — with SHA-256 and keeps 16 hex
    characters. `TransientResult` records it, the stamp publishes it as `netlist`, and the JSON
    report carries the same value, so CSV, SVG and JSON all name the same circuit. Tests prove an
    order-independent match, a mismatch for a changed resistance (both directions), a preset, a
    waveform and a differently named ground, and that the value reaches the stamp, the CSV and the
    report.

67. Added a parameter sweep: `TransientNetlist.Clone()` and `WithElementValue(id, value)` copy a
    circuit and change exactly one element, and `TransientSweep.Run` solves the copies, reporting a
    failed run as a row rather than dropping it. The CLI gained
    `--sweep <id>=<v1,v2,...>` / `--sweep <id>=<from>:<to>:<count>` printing peak, final value and
    settling time per node, with ground suppressed and a non-zero exit if any row failed. Values
    must be positive and invertible for reactive and resistive elements; a diode is refused because
    one value cannot describe four parameters. Tests pin the clone (identical solution, originals
    untouched), the physical trend (larger R settles later), the linear source sweep, and the
    rejection of bad values, unknown ids, diodes, and unusable ranges.

68. Made a sweep archivable: `TransientSweepCsv.Write` emits a long table (value, netlist,
    completed, node, peak, final, settle, failure, message) under a `#` comment block, and
    `TransientSweepJson.ToReportJson` emits a versioned `radix-transient-sweep` report with one
    object per value; both refuse an empty study and both reuse the single-run writer's quoting and
    comment sanitiser, so the two cannot drift. A failed value keeps its row with a failure name and
    no nodes. The CLI accepts `--csv`/`--json` with `--sweep` (it previously refused them) and still
    refuses `--refine` alongside a sweep. Tests cover the comment block and row count, determinism of
    both writers, the failed-row shape, the JSON field set, and a label containing a comma and a newline
    staying on one comment line.

69. Made studies comparable: `TransientSweepReport.Load`/`LoadFromString` reads an archived
    `radix-transient-sweep` report (strict on format and version, tolerant of extra fields so a newer
    archive still loads), and `TransientSweepComparison.Compare` matches rows by exact swept value
    and reports per-node differences in peak, final value, settling time and completion, plus values
    and nodes present on only one side. The CLI gained `--compare <archived.json>`, which refuses a
    study of a different element, a report of another format or version, and `--json`/`--csv` output
    for a comparison. Tests cover the round-trip load, both rejections, an identical study, doubled
    capacitance settling later at every value, added and removed values, a value that stopped solving,
    and deterministic ordering independent of the current study's order.

70. Closed two gaps in the study record: each row now carries the run's energy-ledger residual (a
    physical check on that value, written as `energy_residual` in CSV and `energyResidual` in JSON),
    and comparison accepts a relative tolerance (`--compare-tolerance`, 0 by default = exact) so a
    last-bit difference between builds does not read as a circuit change, while a structure change
    (added/removed value, a node on one side, a run that stopped completing) is never excused. An
    archive written before the field existed still compares as identical instead of manufacturing a
    ledger difference. Tests cover the ledger column and round-trip, an old archive, noise suppressed
    at 1e-6 but a real change kept, ledger differences, and rejection of a tolerance outside [0, 1).
    The relative form has one deliberate hole: a metric at exactly zero has no scale, so a change
    there is always reported.

71. Brought studies into the panel: the Electrical tab gained a **Sweep** row (element id plus a
    comma list or `from:to:count`) and a table of each node's peak, final value and settling time per
    value. The sweep reuses the displayed run's netlist and `TransientOptions`, so a study cannot
    mix settings; a value that fails keeps a row; ground is omitted; and any edit that clears the run
    clears the sweep too. Parsing is local to the view model and reports a sentence for a typo rather
    than throwing. Tests cover the row count and value-once-per-block layout, an unknown element and
    an unparsable value, and invalidation by an edit.

72. Exported panel studies through the shared writers: the view model keeps the last sweep's
    `TransientSweepRow` list (not the formatted table), and `TryBuildElectricalSweepCsv/Json` call
    `TransientSweepCsv.Write` and `TransientSweepJson.ToReportJson` with the circuit id, so the app and
    the command line cannot describe one study differently. Copy/Save buttons appear only with a
    study, the export refuses when there is none, and an edit that clears the run clears the export
    with it. Tests cover the CSV comment block and column list, the JSON format/element/circuit and
    row count, the empty case, and disappearance after an invalidating edit.

73. Brought comparison into the panel: **Compare…** reads an archived report through a file picker
    and `CompareElectricalSweepWithArchived` runs the same `TransientSweepComparison` as `--compare`,
    so the two front ends agree on what changed. Differences are listed per value, node and metric
    with `-` for a side that lacks the row, plus explicit `removed`/`added` rows for values held by
    only one study; an `rtol` box sets the relative tolerance and is validated before any report is
    read; a foreign format/version or a study of a different element is refused with a sentence.
    Editing anything that clears the run clears the comparison with it. Tests cover an identical
    study, added and removed values, foreign reports, a mismatched element, an invalid tolerance, and
    invalidation by an edit.

74. Made comparisons archivable: `TransientSweepComparisonCsv.Write` and
    `TransientSweepComparisonJson.ToReportJson` write a `radix-transient-sweep-comparison` document
    naming the element, the study compared against, whether the studies are identical, every
    difference per value/node/metric, and the added and removed values. `--compare` now accepts
    `--csv`/`--json` (it refused them before), and the panel gained **Copy diff CSV** and
    **Save diff JSON…**, both disabled until a comparison exists and cleared with the study. Tests
    cover the comment block and row count, added/removed rows, the JSON field set, byte-identical
    repeats, an identical comparison exporting zero rows, and a run-level ledger difference being
    labelled `(run)` in both formats.

Verification: release build 0 warnings/0 errors; core tests 630/630; editor
tests 224/224; `radix transient` runs all three examples with exit 0 and
physically consistent results (RC reaches 4.73 V of 5 V after 3τ, the RLC
example rings and settles to 10 V, the pulse example charges then decays).
Not modelled: junction capacitance, transformers, dependent sources, switches,
AC/small-signal analysis, gmin continuation, and plotting more than one run or
exporting a plotted image.

## Session 2026-11-02 (agent/radix-20261102-electrical-audit)

Recovered the branch from GitHub before further implementation. Local and remote
branch heads matched at `ee5e4e383f6f6412f89207f8cc58151fedb0ba07`; the working tree
was clean. PR #6 is open as a draft. Other existing worktrees include DC source
sweeps (`agent/radix-dc-numerics`) and electrical ERC (`agent/radix-erc`), so this
branch avoids duplicating those changes.

75. Fixed nonlinear DC residual normalization. Each KCL row now uses its own sum of
    incident current magnitudes, and each ideal voltage-source row uses that source's
    own voltage scale. Previously, a disconnected `1e12 A` branch shifted a
    reverse-biased diode divider from its analytical 4 V operating point to about
    3.12 V; the new regression requires the operating point to remain at 4 V.
76. Added a small-source representability regression. A 1 uV ideal-source difference
    at a 1e12 V common mode is below double precision's voltage spacing and must fail
    as `NonConvergent`, rather than being silently accepted under the unrelated large
    source's voltage tolerance. Documented the per-equation scaling in
    `docs/electrical.md`.

Verification: Release solution build clean (0 warnings/0 errors); Core tests 632/632;
the `radix dc` divider JSON example reports the expected 5 V; Ubuntu and Windows CI
passed on commit `ee5e4e383f6f6412f89207f8cc58151fedb0ba07`. The local Editor suite's
25 native Skia startup failures are environmental (`libfontconfig.so.1` absent), not
code failures; 199 Editor tests did pass in that run.

77. Added invariant-culture SI suffix parsing to the Electrical inspector's component
    value field: `p`, `n`, `u`/`µ`, `m`, `k`, `M`, `G`, and `meg`. `m` and `M` retain
    their standard milli/mega distinction; results must remain finite. Unknown and
    overflowing values leave the component unchanged. The editor stores the scaled
    numeric value in the existing project format; no unit strings or schema changes
    were introduced. Added headless tests for eight valid suffix forms and rejection
    of an unknown suffix and overflow; documented the syntax in `docs/electrical.md`.

Verification for this feature commit: Release solution build clean (0 warnings/0
errors); focused Electrical editor tests 40/40; full Editor tests 234/234 and Core
tests 632/632 passed after extracting the existing Ubuntu native test libraries into
the ignored `.local-native` directory. Ubuntu and Windows CI both passed. Feature
commit: `649444659bfd6d0d351217fa5aff3af4566c36a6`.

78. Extended the SI parser to capacitor/inductor initial voltage/current and both
    time and value fields in independent-source waveform breakpoints. Existing
    numeric `.radix` representation is preserved: the headless test saves a
    capacitor's suffixed initial voltage and a three-point waveform, then verifies
    that values and times are JSON numbers with the scaled SI magnitudes. Invalid
    and overflowing suffixes leave initial state and waveform unchanged. Hints now
    describe the accepted units and suffixes.

Verification: focused `ElectricalEditorTests` passed 43/43; Release solution build
completed with 0 warnings/errors; full suites passed, Core 632/632 and Editor
237/237. GitHub CI passed on Ubuntu and Windows for branch tip
`bd0d130f3a9f0df45208175bf4181b70e2dab876`.

79. Completed the Electrical inspector's SI entry across the transient controls:
    stop/probe times and comma-list or range sweep values now accept the same finite
    suffix syntax as component values, initial state, and source waveforms. The
    view-model inputs remain text and project serialization remains numeric. Updated
    control hints and `docs/electrical.md`; headless tests exercise `10m`, `1.2345m`,
    `500,1k,2k`, and `1k:2k:3` in the existing run/probe/sweep flows.

Verification: focused `ElectricalEditorTests` passed 43/43; Release solution build
completed with 0 warnings/errors; full suites passed, Core 632/632 and Editor
237/237. `radix dc examples/dc-divider.radix --json` solved at the expected 5 V
midpoint, and `radix transient examples/transient-rc.radix --json` completed.
GitHub CI passed on Ubuntu and Windows for code commit
`d4aef6d1526a55f4871e7f19177ff41c33ad1688` and documentation-only tip
`859b42646b1fe7e6b3182105f43866bfb19cadf5`.

80. Tightened Electrical inspector parsing so consecutive or trailing separators
    in source waveform and sweep-value lists are errors instead of being silently
    removed. The error identifies the empty entry, and editor mutations are
    rejected without changing the existing waveform or running a partial sweep.
    Headless regressions cover doubled and trailing commas in both input paths.

Verification: focused `ElectricalEditorTests` passed 43/43; Release solution build
completed with 0 warnings/errors; full suites passed, Core 632/632 and Editor
237/237. GitHub CI is pending for this validation change.

81. Corrected SI prefix case handling in the Electrical editor parser. Only the
    documented symbols now scale: lowercase `p`, `n`, `u`, `m`, `k`; uppercase
    `M`, `G`; and `meg` as a case-insensitive compatibility alias. Previously
    undocumented uppercase/lowercase lookalikes such as `P` and `g` were accepted
    with the wrong physical scale. Added rejection cases and documented exact case.

Verification: focused `ElectricalEditorTests` passed 48/48; Release solution build
completed with 0 warnings/errors; full suites passed, Core 632/632 and Editor
242/242. GitHub CI is pending for the prefix-case correction.

82. Added an optional reverse-breakdown branch to the Shockley diode for DC and
    transient JSON netlists. `breakdownVoltage` and `breakdownCurrent` must be a
    finite positive pair; the documented exponential branch keeps the diode's
    current sign convention and terminal power accounting. Transient copies and
    fingerprints include both parameters, and DC reports serialize them. Added an
    independent scalar bisection oracle, conductance finite-difference check, KCL/
    power checks, invalid-pair cases, transient JSON coverage, and
    `examples/dc-zener.json`.

Verification: focused diode/transient serialization/fingerprint tests passed
46/46; Release solution build passed with 0 warnings/errors; Core passed 637/637;
headless Editor passed 242/242 using the documented Linux native-library setup.
`radix dc examples/dc-zener.json --json` solved to -5.108295403806094 V and
reported equal resistor/diode branch currents (-6.8917 mA) with element powers
summing to zero within the solver's numerical tolerance. The published code
checkpoint is `19073dd5df9e847880cfb0ee8c11cab9642bea6c`; GitHub CI was still
running when checked. Draft PR #6 has since been updated with this milestone's
summary and full local verification results; see the PR for the latest CI state.

83. Extended the independent reverse-breakdown regression to include nonzero diode
    series resistance. The oracle bisects the internal junction equation using the
    combined external and junction resistance, then independently reconstructs the
    terminal voltage. The test also checks the nested junction solve, terminal
    conductance against finite differences, KCL, power conservation, and the solver
    residual. Focused `DcDiodeSolverTests` pass 16/16; after this addition the
    full Core suite passes 638/638. This test-only follow-up is checkpointed on the
    same feature branch.

84. Added a transient clamp regression: a piecewise-linear source ramps from 0 V to
    -12 V over 1 us through a 1 kΩ resistor into a 10 uF capacitor and the reverse-
    breakdown diode. The test checks the capacitor starts near zero at the ramp
    boundary, observes discharge, then verifies the final voltage and branch-current
    balance are in the Zener clamp region. Focused test passes 1/1; the full Core
    suite passes 639/639.

85. Reverified the current branch after the transient regression: Release solution
    build completed with 0 warnings/errors, Core tests passed 639/639, and headless
    Editor tests passed 242/242 with the Linux native-library setup. GitHub CI for
    `580006aeb7e05e49f25e3f6e86068800489c4089` was still running when checked.

86. Added an 80-case seeded DC reverse-breakdown differential campaign. Each case
    varies BV, IBV, external resistance, diode series resistance, and negative supply
    over bounded linear/logarithmic ranges. An independent scalar bisection solves
    the combined resistor/junction equation; the test compares terminal voltage and
    diode current and checks power balance plus the solver residual. Failures include
    the fixed seed and case parameters. Focused campaign passes 1/1; after adding
    it, the full Core suite passes 640/640.

87. Corrected diode exponential overflow limiting. The current law previously hard-
    clamped `exp(x)` above x=80 while the Jacobian continued to report the nonzero
    exponential slope, so Newton's current and conductance disagreed. Forward and
    reverse-breakdown branches now use a linear tangent continuation above the
    threshold, with the matching constant derivative. Documented the continuation's
    numerical purpose and physical limitation. A finite-difference regression checks
    both branches at and above the threshold; focused diode and Zener transient tests
    pass 19/19. Broader results are recorded in item 88.

88. Verified the exponential-continuation fix end to end: Release solution build
    completed with 0 warnings/errors; Core passed 641/641; headless Editor passed
    242/242 with the documented Linux native-library setup. The DC Zener CLI example
    still solves to -5.108295403806094 V with balanced branch current and power.

89. Corrected the public diode model's XML documentation to describe the tangent
    continuation accurately and made its derivative propagate NaN inputs instead of
    returning the finite cap slope. Extended the conductance regression to assert
    NaN propagation for both Shockley and breakdown branches. The focused test passes.

90. Reverified the updated diode edge handling: Release solution build completed
    with 0 warnings/errors, Core passed 641/641, and headless Editor passed 242/242
    with the documented Linux native-library setup.

91. Extended the RC/Zener transient regression to run both fixed-step and adaptive
    step-doubling analyses across the source breakpoint. Both analyses must complete,
    finish at the clamp operating point, and agree within 20 mV with balanced final
    branch currents; the adaptive energy-ledger relative residual must also be lower
    than the fixed-step run. Focused adaptive clamp test passes 1/1; Release build passed
    with 0 warnings/errors, Core passed 641/641, and headless Editor passed 242/242.

92. Reran the full Core suite after adding the energy-ledger comparison; all 641
    tests passed. No production code changed in this test-only follow-up.

93. Corrected `docs/electrical.md` to match the implemented diode solver: it uses
    pn-junction step limiting, and the exponential overflow guard is a linear tangent
    continuation with matching conductance above the threshold. Clarified that this
    is a numerical continuation rather than a physical high-current prediction, and
    reflowed the solver description for readability. Documentation-only; no tests run.

94. Fixed the nonlinear DC stopping test so its voltage-update tolerance is scaled
    only by node and diode-junction voltages. MNA source branch currents are in
    amperes and no longer inflate that voltage tolerance. A regression adds a
    disconnected 1e12 A ideal-source branch beside an ultra-low-current diode; the
    old solver stopped at 0.974 V instead of the independent scalar reference
    0.573 V. The regression demonstrated the bug before the fix.


95. Verified the dimensional stopping-criterion correction: the focused electrical
    numerical tests pass 44/44; the Release solution build passes with 0 warnings and
    errors; Core passes 642/642; and headless Editor passes 242/242 using the
    pre-extracted `.local-native` libraries. The native package download helper could
    not resolve packages from this VM's apt sources, but the existing extracted files
    were sufficient for the full Editor run.

96. WIP: added a regression for the diode's series-resistance junction solve at
    extreme but finite scales (1e100 V terminal, 1e308 ohm series resistance). The
    existing fixed 120-step bisection returned about 3.76e63 V instead of the
    small-signal reference near 2.59e-199 V, violating the terminal relation. The
    test currently fails and is published before changing the solver.


97. Replaced the fixed 120-step junction bisection with bounded bracket expansion
    and bisection through representable floating-point values. The old fallback that
    returned terminal voltage when bracketing failed is now an explicit numerical
    failure, so it cannot silently produce a fabricated operating point. The new
    1e100 V / 1e308 ohm regression passes alongside all 20 `DcDiodeSolverTests`;
    broader verification remains pending.

98. Verified the scale-safe diode junction solve end to end: focused diode/transient
    tests passed 70/70; Release solution build passed with 0 warnings/errors; Core
    passed 643/643; headless Editor passed 242/242. The DC Zener CLI remains at
    -5.1082954 V with balanced branch currents and power, and the resistor divider
    still reports its expected 5 V midpoint. GitHub CI for this checkpoint is pending.

99. WIP: added a seeded 96-case series-resistance equation sweep. It exposed a
    second numerical issue at small forward voltage: direct `exp(x) - 1` cancellation
    rounds diode current to zero. Case 4 had 0.00010045 V terminal voltage,
    2.0e22 ohm series resistance, and 7.2e-9 A saturation current; reconstructed
    terminal voltage was effectively zero. The regression currently fails and is
    published before changing the Shockley current evaluation.


100. Fixed low-voltage Shockley cancellation discovered by the seeded series-resistance
     equation sweep. The forward branch now uses a small-argument polynomial and a
     stable hyperbolic/exponential identity rather than subtracting one from `exp(x)`;
     the tangent continuation above exponent 80 is unchanged. The seeded 96-case
     terminal-equation sweep and all 20 diode solver tests pass. Broader verification
     remains pending.

101. Verified cancellation-safe Shockley evaluation and the seeded series equation
     sweep: focused diode/DC/transient tests passed 73/73; Release build passed with
     0 warnings/errors; Core passed 644/644; headless Editor passed 242/242 using
     the existing `.local-native` extraction. The Zener CLI still reports -5.1083 V
     with near-zero total element power, and the divider reports 5 V. GitHub CI for
     the latest code checkpoint is pending.

102. WIP: added a regression showing that individually finite positive diode
     emission coefficient and thermal voltage can multiply to zero or infinity.
     `DcNetlist.AddDiode` currently accepts both, leaving an unusable junction
     voltage in the Shockley model. Focused test fails in both cases as expected;
     implementation and broader verification remain pending.

103. Fixed the derived diode-scale validation exposed in item 102. `AddDiode` now
     rejects emission/thermal parameter pairs whose product is non-finite or zero,
     before an unusable junction model can enter MNA. Both overflow and underflow
     regression cases pass, as do all 23 diode solver tests; broader verification
     remains pending.

104. Verified the derived diode-scale guard end to end: Release solution build
     passed with 0 warnings/errors; Core passed 646/646; headless Editor passed
     242/242 with `.local-native`; the Zener CLI still solves at -5.1082954 V
     with balanced branch currents and the divider reports 5 V. The new regression
     was previously demonstrated failing for both underflow and overflow, then
     passed after the validation was added.

105. WIP: added a fixed/adaptive transient-options regression for time steps too
     small to advance a large absolute start time in double precision (start
     1e16 s, 2 s span, 100 steps). Both cases currently pass validation even though
     repeated time addition leaves the simulation clock unchanged. Implementation
     and broader verification remain pending.

106. Fixed transient time-grid representability. Validation now rejects fixed and
     adaptive starting steps that cannot advance the absolute clock; an explicitly
     configured adaptive initial step is checked too. Both integration loops detect
     any later step that rounds to no time progress and return `NonConvergent` with
     a precise message. The three focused regressions and all 26 transient solver
     tests pass; broader verification remains pending.

107. Verified the transient clock-resolution guard: Release solution build passed
     with 0 warnings/errors, Core passed 649/649, and headless Editor passed 242/242
     with `.local-native`. `radix transient examples/transient-rc.json --json`
     completed all 61 points, ending at 0.003 s with a finite RC state and energy
     ledger. The earlier failing fixed/adaptive representability tests now pass.

108. WIP: added two diode validation regressions for finite positive parameters
     whose zero-bias forward or reverse-breakdown conductance overflows to
     infinity. Both parameter sets are currently accepted by `AddDiode` and fail
     the new expectations. Implementation and broader verification remain pending.

109. Fixed diode slope validation: the netlist now rejects parameters when either
     `Is / (N * Vt)` or the optional `IBV / (N * Vt)` is non-finite. The forward
     and reverse regression cases and all 25 `DcDiodeSolverTests` pass; broader
     verification remains pending.

110. Verified diode slope validation: Release solution build passed with 0
     warnings/errors, Core passed 651/651, and headless Editor passed 242/242
     with `.local-native`. The Zener CLI still solves to -5.1082954 V with
     approximately -6.8917 mA diode current and a total power residual of
     3.75e-16 W. The overflowed forward and reverse-slope regressions pass.

111. WIP: added a digital-engine regression showing that `SetPortValue` changes
     an input net after the event budget halts the engine, even though subsequent
     advances are ignored and the partial state is presented as halted. The test
     reproduces a value change from 1 to 0 without processing another event;
     implementation and broader verification remain pending.

112. Fixed external drive behavior after halt. `SetExternalInput` and therefore
     `SetPortValue` now retain normal net/value validation but ignore valid drives
     once halted, preserving the frozen partial simulation until `Reset`. The
     regression and all four event-budget tests pass; broader verification remains
     pending.

113. Verified frozen halted-state behavior end to end: Release build passed with
     0 warnings/errors, Core passed 652/652, and headless Editor passed 242/242.
     The binary-counter CLI script passed all 3 steps (3,574 events). The halted
     external-drive regression and all four event-budget tests pass.

114. WIP: added direct `SimulationOptions` constructor tests for negative or
     zero-invalid runtime limits and the documented zero-disabled settings. The
     engine currently accepts invalid direct options (settings loaded through
     `FromSettings` are normalized), and the new validation test fails. Guard and
     broader verification remain pending.

115. Added `SimulationOptions.Validate` and call it before engine initialization.
     The component settle limit must be at least one; optional event, diagnostic,
     and trace caps may be zero to disable them but cannot be negative. Settings
     normalization remains unchanged. Both direct-construction validation tests
     pass, including the zero-disabled cases; broader verification remains pending.

116. Verified runtime-option validation: Release solution build passed with 0
     warnings/errors, Core passed 654/654, and headless Editor passed 242/242 with
     `.local-native`. Both option tests pass: invalid direct limits are rejected,
     and zero correctly disables the optional runtime caps.

117. WIP: added event-budget regressions for `Step` and `StepTimestamp`. If the
     configured budget is reached before a pending event, `Step()` currently
     returns `true` despite processing nothing, and `StepTimestamp()` includes
     the unprocessed attempt in its count. Focused `EventBudgetTests` reproduce
     both failures (2 failed, 4 passed). Implementation and broader verification
     remain pending.

118. Fixed event-budget stepping semantics. `ProcessNextEvent` now reports whether
     it consumed an event; `Step()` returns that result, and `StepTimestamp()` only
     counts consumed events. Consumed stale wake events still count as processed.
     All six focused `EventBudgetTests` pass, including both regressions; broader
     verification remains pending.

119. Verified budget-limited stepping end to end: Release solution build passed
     with 0 warnings/errors, Core passed 656/656, and headless Editor passed
     242/242 with `.local-native`. The new `Step` and `StepTimestamp` budget
     regressions and all prior event-budget coverage pass.

120. GitHub Actions verified both Ubuntu and Windows checks successfully for
     `8a995ca78ce7a9ca9fb050ca20f6811c47d61c55` (two completed runs per platform,
     all successful). Updated the `Step` API comment to describe its false result
     in terms of whether an event was processed, including budget-limited halt.

121. WIP: added two transient-waveform interpolation regressions. With finite
     opposite-sign endpoints near `double.MaxValue`, the current `right - left`
     interpolation delta overflows even when the interpolated voltage should be
     finite. Opposite-extreme signed timestamps also produce an infinite interval
     and lose the expected midpoint. Focused test execution and implementation are
     pending.

122. Fixed transient waveform interpolation overflow. For an overflowing timestamp
     interval, interpolation now computes the normalized fraction after scaling
     both endpoints and the query time. If an overflowing value delta spans
     opposite signs, interpolation uses a convex weighted sum; ordinary ranges keep
     the original delta form. Both extreme-scale regressions pass (2/2); broader
     verification remains pending.
