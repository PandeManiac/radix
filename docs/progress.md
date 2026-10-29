# RADIX continuation record

Last updated: 2026-10-29. This file is the durable handoff note for agents and
humans; update it at each milestone instead of relying on chat history.

## Where the project stands

- Branch: `freebuff/7f9464929ff01fc3fe083290`. **No git remote is configured**, so
  every commit is local to this workspace.
- Recovery: the previous session's work (branch tip `be9ba54`) was merged in after
  preserving the earlier worktree as commit `7188d5f`. The merged tree matched
  `be9ba54` exactly.
- Latest verified code commit: `bbc79e7` (generated repeated-child hierarchy benchmark); documentation of its measurement is in progress.
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
   four-net child repeated 25/100/250 times. One run reports construction 176/60/153
   ms and ten canonical root builds 148/632/536 ms; these are explicitly noisy,
   non-monotonic exploratory measurements rather than a scaling claim.

## Known issues and limitations

- No remote; pushes are impossible until one is configured.
- `SweepHonorsCancellationInsideASingleSettleRow` is timing-sensitive (30 ms
  deadline); it failed once under a full parallel run and passed on reruns.
- Everything under "Explicit limitations" in `README.md` still applies (no analog
  solver, no JIT, fixed-width generated parts, and so on).

## Next meaningful steps

1. For stable hierarchy scalability data, repeat measurements with warmup and
   multiple runs or use a bounded CPU-profile export; this one-run sample is noisy.
2. Continue the roadmap: large-definition scalability, richer hierarchy/library
   workflows, and the documented electrical-simulation boundary.
