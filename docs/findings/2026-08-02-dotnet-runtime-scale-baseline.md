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

### Small-repo baseline (Miller repo) — completed, but slow, and now fully attributed

First run: 51.3 s wall / 47.9 s user at jobs=4 → parallelism ≈ 0.93 (effectively serial). A second
run with `--json` produced the phase profile (1,518 files scanned, total 44.2 s):

| phase | ms |
|---|---|
| discovery | 21 |
| extraction_spool | 4,537 |
| **artifact_write** | **39,630** |

**~90% of a small repo's cold start is SQLite artifact write, not parsing.** Extraction of the
whole repo takes 4.5 s; writing the rows takes 39.6 s (~26 ms/file). This is the dominant
first-impression cost for every normal-sized repo — fixing artifact-write throughput would turn a
typical ~50 s first open into ~10 s or less, before any progressive-indexing design. It also
compounds with the spool/reference bloat: most written rows are reference-site/identifier data, so
the amplification fix shrinks the write phase too.

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
error text was lost to a logging mistake (`time | tail` pipeline).

### dotnet/runtime attempt 3 (64 MB stacks, `RUST_BACKTRACE=full`, full log) — **deterministic "failed"**

- Exit 1 after **341 s** (614 s user, max RSS 5.3 GB). No stack overflow, no panic, no backtrace —
  the **entire log is the single word `failed`** plus the time report. A 5.7-minute run with one
  word of diagnostics is itself a reporting bug (third extractor issue).
- **Deterministic failure point:** attempts 2 and 3 retired near-identical instruction counts
  (8.045 T vs 8.044 T) — both ran the complete extraction/spool phase (bigger stacks DO clear the
  crash), then failed at the same boundary, cleaned the spool, and left the DB with schema +
  metadata but zero files. The failure sits at the end of the spool phase or the start of spool→DB
  import; candidates include per-file parse-failure aggregation tripping an abort threshold, or an
  import-side limit on the 50–66 MB spool entries.
- Timing data point: the full spool phase over 58,500 files at jobs=4 ≈ **341 s (~5.7 min)** —
  consistent with the ~190 files/s projection from the attempt-1 autopsy.

### dotnet/runtime attempt 4 (Miller's exact argv, `--json`) — **root cause captured**

Structured report (schema v3), exit 1 at 313 s:

```json
"errors":[{"code":"db_write_failed",
  "message":"SQLite artifact write failed: reference_site identity conflict",
  "recoverable":false}]
```

- **The blocker is a reference-site identity collision during spool→DB import.** Spool entries use
  content-derived "spanless" reference-site IDs (`reference_site_spanless-<hash>`); dotnet/runtime's
  generated JIT tests are thousands of near-identical files packed with identical expressions, and
  two distinct sites hash to the same identity → uniqueness violation → the ENTIRE import aborts
  (`recoverable: false`), zero rows written. Duplicated generated code is exactly the corpus that
  maximizes collision probability.
- Phase profile (finally measurable): discovery **4.2 s**, extraction/spool **226 s**, artifact
  write reached **82 s** before the conflict; total 312 s. A healthy end-to-end run projects to
  **~6–8 minutes** at jobs=4.
- Per-file failures are tolerated correctly (1 C#, 2 PowerShell, 5 XML files failed and were
  recorded per-file without aborting) — so the abort is the identity conflict, not a failure
  threshold. C# dominates extraction cost: 32,602 files, 339 MB, 412 worker-seconds.
- The W5 progress-file design is validated by the phase split: under today's binary, Miller's
  `ProgressStamp` would see zero bytes at its watched paths for the entire 226 s extraction phase.

## Implications

1. **The real-repo tier is mandatory, not optional.** W10's synthetic fixture cannot contain
   generated-code pathologies like the JIT test family. dotnet/runtime @ `a2f953fe266` should be
   the standing scale target (P0 of the delta-rebind program now names it).
2. **Four new julie-extractors bugs**, all with one-command repros on the pinned binary:
   (a) stack-overflow crash at default thread stacks (likely deep recursion on generated
   expression chains); (b) ~68× worst-case / ~10× aggregate spool amplification; (c) **the
   blocker: `reference_site identity conflict` on duplicated generated code aborts the entire
   import non-recoverably** — dotnet/runtime cannot be indexed at any stack size; (d) the
   non-JSON error path reports a bare "failed" with no diagnostics for a 5.7-minute run.
   Recommend bundling fixes into the same julie-extractors release as the fleet-safety W4–W6
   flags (one release, one pin bump).
3. **Cold-start reframe:** at-scale extraction is ~190 files/s — a *healthy* 58k-file extract
   projects to ~6–8 minutes end-to-end, not the 25–40 min linear extrapolation from the small-repo
   run. The cost structure is now attributed: **artifact write dominates small repos** (39.6 s of
   44.2 s on 1.5k files) and is material at scale (82 s reached before the abort); spool I/O moves
   ~10× redundant bytes at 79 MB/s; sidecar/embedding convergence sits on top. The two highest-
   leverage cold-start fixes are artifact-write throughput and the reference/spool amplification —
   both extractor-side, both now measured, and both shrink every repo's first open, not just
   monorepos.
4. **Fleet-safety validations for free:** W4 (spool reaping) and W5 (progress blindness) are now
   reproduced on a second machine, at scale, on a clean clone — no longer reporter-only evidence.

## Root-cause update (2026-08-02, five-track code investigation — supersedes the hypotheses above)

A five-agent read-only investigation of the julie-extractors source verified all five defects and
**disproved two hypotheses recorded earlier in this doc**. Authoritative fix plan:
julie-extractors `docs/plans/2026-08-02-scale-fixes-plan.md` (branch `scale-fixes`).

- **Identity conflict — NOT a hash collision, and NOT the generated JIT files.** Exact-span
  reference sites deliberately share one id (blake3 of file_id + span) across three extraction
  passes; a BEFORE INSERT trigger aborts the whole single-transaction import when the same site
  arrives with ANY divergent column. The divergence is `containing_symbol_id`, computed via
  different code paths per pass: (1) PowerShell's identifier pass filters containment to multi-line
  symbols only, so a one-line `function F { G }` yields NULL vs the pending pass's F — this is what
  breaks `~/.hermes/hermes-agent` (scripts/install.ps1); (2) C multi-declarator statements emit
  equal-span variable symbols and the shared helper's tie-break stops at (kind, size), so
  HashMap-vs-Vec iteration order picks different winners per pass (verified in
  src/native/containers simdhash benchmark.c). The cmov JIT directory scans CLEAN — the
  "near-identical generated code collides" hypothesis was wrong.
- **Stack overflow — three unguarded walkers, not general recursion.**
  `blazor_navigation::collect_receiver_declarations`/`collect_navigation_calls` (run on EVERY C#
  file) and `complexity_metrics::collect_stats` lack the crate's existing 1024-depth guard; macOS
  crash reports pin the blazor walkers. Trigger: `src/tests/JIT/Regression/JitBlue/GitHub_10215.cs`
  — 17,602 `+` operators in one statement, a file whose own header says it exists to overflow
  recursive tree walkers. It reproduces the abort alone at default stacks.
- **Spool bloat — ~47% is a verified-dead field.** Every identifier row carries a 7-line formatted
  `code_context` snippet that julie's resolver loads but never reads and Miller never queries;
  the rest is JSON key envelope + long hash-ID strings, and the import re-parses the whole spool
  three times. Fix approved: drop `code_context`, re-frame the spool as binary frames.
- **Write phase — structural, per strong hypothesis pending instrumentation:** live maintenance of
  54 secondary indexes during the one-transaction import, the full-workspace resolution pass timed
  inside `artifact_write`, triple spool parse, and the WAL checkpoint. Fix: instrument sub-phases,
  then a fresh-artifact bulk-load mode (deferred indexes + rebuild pragmas, safe under
  promote-not-merge).
- **Contract decisions (user-approved 2026-08-02):** drop `identifiers.code_context` (JSONL export
  emits null); demote the identity trigger to first-write-wins + recorded recoverable warning.

## Scale-fixes branch validation (2026-08-02, closes the open items above)

The five defects were fixed on julie-extractors branch `scale-fixes` (T1–T5) and validated end to
end (T6, `docs/findings/2026-08-02-scale-fixes-validation.md` in that repo). The first-ever
complete dotnet/runtime artifact answers the blocked open item:

- **Full scan completes at default stacks**: 3 h 51 m wall, artifact **22.84 GiB**, WAL 0 at
  finish, peak spool 3.18 GiB (vs 15.4 GB leaked at 60% on 2.21.0), no spool residue. Extraction
  is now 274 files/s and 1.5% of the run; **artifact_write is 98.4%** (resolution 61%,
  file/symbol insert 28%, child rows 7%). Peak RSS 30.6 GiB, tracking DB size ~1:1.
- Exit 1 is by design: the corpus ships 8 non-UTF-8 files (per-file `read_failed`, recoverable);
  exit 0 is unreachable on this corpus for any extractor version. The baseline's "swallowed panic"
  hypothesis was wrong — the lone C# failure is invalid UTF-8, made legible by the T1 rendering fix.
- Identity-conflict residual: 4,237 recoverable warnings across 28 C files (multi-declarator
  own-scope class); PowerShell zero (hermes-agent scans clean). Fix tracked for the next cycle.
- Small-repo win held: Miller repo cold start 22.09 s vs 44.2 s on the pin (2.00×).
- **New wall, new work**: the write phase at 58k files is hours, not minutes — random hash-text
  PKs thrash the page cache and the in-RAM bulk journal grows with the DB. T7 (disk journal +
  scaled cache + PK-sorted staging inserts) is in flight on the branch; release held for its
  re-validation. Row counts also settle the progressive-levels direction: identifiers 12.86M +
  reference_sites 15.5M vs symbols 2.58M — the reference layer is ~10× the symbol core that
  serves 83% of Miller tool calls (search + inspect, per live telemetry).

The four bugs were fixed directly on the branch rather than filed as issues; repro artifacts
(clone, logs, structured reports, the 22.8 GiB artifact) live in this session's scratchpad.
