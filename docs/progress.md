# RADIX continuation record

Last updated: 2026-10-29. This file is the durable handoff note for agents and
humans; update it at each milestone instead of relying on chat history.

## Where the project stands

- Remote: `origin` = https://github.com/PandeManiac/radix (write access verified).
- Working branch for this session: `agent/radix-verification`, pushed to origin.
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

## Known issues and limitations

- Editor tests need fontconfig, freetype, libpng and a font on Linux. This VM's
  image has none of them and `sudo` is disabled; `scripts/linux-test-natives.sh`
  cannot fetch them because apt lists are absent. Verified workaround: download
  the noble debs (`libfontconfig1_2.15.0-2.3ubuntu1`, `libfreetype6_2.13.2+dfsg-1build3`,
  `libpng16-16t64_1.6.43-5build1`, `fonts-dejavu-core_2.37-8`) from
  archive.ubuntu.com, extract with `dpkg-deb -x`, then set `LD_LIBRARY_PATH` to
  the extracted `usr/lib/x86_64-linux-gnu` and `FONTCONFIG_FILE` to a config that
  points at the extracted fonts. Core tests are unaffected.
- Everything under "Explicit limitations" in `README.md` still applies (no analog
  solver, no JIT, fixed-width generated parts, and so on).

## Next meaningful steps

1. For stronger hierarchy scalability data, compare the seven-sample run on a
   quieter host, or install/use `dotnet-trace` to capture a bounded CPU profile;
   this VM has no profiler tool installed and the repeated runs remain noisy.
2. Continue the roadmap: large-definition scalability, richer hierarchy/library
   workflows, and the documented electrical-simulation boundary.
