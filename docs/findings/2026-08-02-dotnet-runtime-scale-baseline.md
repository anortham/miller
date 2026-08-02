# 2026-08-02 — Real-repo scale baseline: dotnet/runtime crashes julie-extract 2.21.0

**Status:** in progress — attempts 1–2 complete and analyzed; attempt 3 (full logging + backtrace)
in flight. Findings below are verified from live runs on this machine.

**Purpose:** the [worktree delta-rebind program](../plans/2026-08-02-worktree-delta-rebind-program.md)
and the fleet-safety plan's W10 both need a scale target near the 2026-08-01 field report's ~74k
files. W10 specifies a *synthetic* fixture; this session selected and tested a *real* repo tier —
and the very first run found a crash a synthetic fixture would likely never produce.

## Method

- Machine: 24 cores / 64 GB (Apple Silicon), macOS. julie-extract **2.21.0** (the current pin),
  invoked directly (no Miller orchestration) with `--jobs 4` — the fleet-safety plan's W2 default
  formula (`min(4, cores/2)`).
- Real-repo target: **dotnet/runtime @ `a2f953fe266`** (shallow clone), **58,500 tracked files**
  (32,628 `.cs`, 5,956 `.csproj`, 3,789 `.il`, 2,217 `.h`, 1,757 `.cpp`, 1,425 `.c`, …). Right at
  the reporter's tier and squarely in the .NET wedge.
- Small-repo contrast: the Miller repo itself (2,236 tracked files, 1,330 extracted).

## Results

### Small-repo baseline (Miller repo) — completed, but slow

51.3 s wall / 47.9 s user at jobs=4 → parallelism factor ≈ 0.93 (**effectively serial**), for
1,330 extracted files. At the at-scale throughput measured below (~190 files/s), extraction alone
accounts for ~7 s — so **~44 s is fixed/serial cost** (discovery, artifact write, startup —
unattributed; needs profiling). A ~50 s first-open on a 1.3k-file repo is its own first-impression
problem, independent of monorepo scale.

### dotnet/runtime attempt 1 (default 2 MB thread stacks) — **stack-overflow crash**

- `thread '<unknown>' has overflowed its stack / fatal runtime error: stack overflow, aborting`
  at **186 s**; 474 s user (parallelism ≈ 2.5 at scale); max RSS 2.5 GB.
- **No DB file was ever created** — live confirmation at scale of the W5 progress-blindness
  finding: 3+ minutes of healthy work with zero bytes at the paths Miller's `ProgressStamp` watches.
- The abort skipped `Drop` cleanup and **leaked a 15.4 GB spool** — live confirmation of the W4
  finding, and this machine's temp dir already held ten older dead spools (517 MB + 63 MB + 15 MB +
  a cluster of 0-byte ones, Jul 30–Aug 2) from ordinary local use.

### Spool autopsy (the 15.4 GB from attempt 1)

- **35,328 / 58,500 entries (60%) in 186 s → ~190 files/s sustained** extraction throughput at
  scale, jobs=4. Sustained spool write ≈ 79 MB/s.
- Mean spool cost ≈ **435 KB per file**. Worst single entries are all generated JIT test files:
  `src/tests/JIT/Directed/cmov/*` and `.../nullabletypes/*` at **50–66 MB each**.
  `Double_Or_Op.cs` is 982 KB / 22,169 lines of source → 66.6 MB spool entry = **68×
  amplification**. Top directories by spool bytes: `src/tests/JIT` 1.38 GB,
  `src/libraries/System.Runtime` 0.89 GB, `src/mono/mono` 0.86 GB, CoreLib 0.82 GB,
  `src/coreclr/jit` 0.79 GB.
- Full-run projection ≈ 25 GB of spool for a sub-2 GB checkout (~10× aggregate amplification).
  This also plausibly explains the field reporter's unexplained "14 GB" observation and their
  ~130 GB of accumulated dead spools.
- **Hypothesis (bloat):** per-reference serialization overhead — each reference site /
  `pending_relationship` carries multiple long hash IDs plus verbose JSON; generated repetitive
  code maximizes reference density, so reference-site data dominates spool bytes.
- **Hypothesis (crash):** recursive descent over deeply nested expression chains in those same
  generated files (thousands-long operator chains → parser/extraction recursion exceeds the 2 MB
  worker stack). The bloat and the crash likely share one family of pathological files.

### dotnet/runtime attempt 2 (`RUST_MIN_STACK=32 MB`) — **different failure, graceful**

No abort and no leaked spool (so `Drop` ran — a clean unwind/error path), but the DB contains
schema + `artifact_metadata` and **zero files**: the scan failed before (or during) spool→DB
import. 8.05 T instructions retired (vs 6.19 T for attempt 1's 60%), peak footprint 3.0 GB. The
error text was lost to a logging mistake (`time | tail` pipeline); attempt 3 reruns with
`RUST_BACKTRACE=full` and complete log capture. **Open until attempt 3 reports.**

## Implications

1. **The real-repo tier is mandatory, not optional.** W10's synthetic fixture cannot contain
   generated-code pathologies like the JIT test family. dotnet/runtime @ `a2f953fe266` should be
   the standing scale target (P0 of the delta-rebind program now names it).
2. **Two new julie-extractors bugs**, both with one-command repros on the pinned binary:
   (a) stack-overflow crash at default stacks; (b) ~68× worst-case / ~10× aggregate spool
   amplification. Recommend bundling fixes into the same julie-extractors release as the
   fleet-safety W4–W6 flags (one release, one pin bump).
3. **Cold-start reframe:** at-scale extraction is ~190 files/s — a *healthy* 58k-file extract
   projects to ~5–6 minutes, not the 25–40 min linear extrapolation from the small-repo run. The
   remaining cold-start costs are (a) the ~44 s fixed/serial small-repo overhead, (b) spool I/O
   (79 MB/s of mostly-redundant bytes), (c) the artifact-write phase (unmeasured — attempt 3), and
   (d) sidecar/embedding convergence on top. Fixing the spool bloat is likely a throughput win, not
   just a disk win.
4. **Fleet-safety validations for free:** W4 (spool reaping) and W5 (progress blindness) are now
   reproduced on a second machine, at scale, on a clean clone — no longer reporter-only evidence.

## Open items

- Attempt 3 verdict: error identity + backtrace, and (if it completes) wall-clock for the full
  extract + artifact write and final DB/WAL sizes on a real 58k-file repo.
- Profile the ~44 s small-repo fixed cost (discovery vs artifact write vs startup).
- File both extractor bugs in julie-extractors with the repro (pinned commit + command).
