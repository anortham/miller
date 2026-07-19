# Task 4 report — sqlite-vec Native-AOT spike (P0 program HARD GATE)

**Status:** COMPLETE
**Commit SHA:** none - parallel-lead-commit
**Gate verdict:** **PASS on osx-arm64 (10/10 stages).** osx-x64 / linux-x64 / win-x64: pending CI evidence.

> **Note on this file:** it previously held an unrelated report ("Task 4 — Hardening: Host allowlist +
> CSRF header", from the `dashboard-ux-fixes` plan, committed at `c09b7f3`). `.razorback/sdd/` is reused
> across plans. The old content is tracked and recoverable in git history; overwriting was intentional.

## Implementation

Four created files + one isolated CI job.

### `spike/SqliteVec.AotSpike/` (NOT in `Miller.slnx`)

Console app, `<PublishAot>true</PublishAot>`, `AssemblyName=sqlite-vec-aot-spike`, referencing the
same package versions the product already uses (`Microsoft.Data.Sqlite` 10.0.9,
`SQLitePCLRaw.bundle_e_sqlite3` 3.0.3). Placement/style mirrors `spike/Codesearch.Spike`.

`Program.cs` runs 10 staged checks through a small `StageRunner` that prints `PASS`/`FAIL` + timing +
a one-line detail per stage, stops at the first failure, names the failing stage in the verdict line,
and exits 0 only when every stage passed:

1. `aot-no-jit-fallback` — asserts `RuntimeFeature.IsDynamicCodeSupported == false` **inside the
   process**, so the gate cannot silently pass on a JIT host.
2. `extension-file-present` — rejects a non-absolute path (the design specifies absolute-path load).
3. `open-connection`
4. `load-extension-absolute-path` — `EnableExtensions(true)` → `LoadExtension(abs)` → `EnableExtensions(false)`
5. `vec_version`
6. `create-vec0-table` — `vec0(embedding float[8] distance_metric=cosine)` under `journal_mode=WAL`
7. `insert-integer-rowids` — 5 rows on explicit integer rowids, float32 LE BLOB parameters
8. `knn-match-k3` — `WHERE embedding MATCH ? AND k = ? ORDER BY distance`; asserts hit count, that the
   self-vector ranks first, and that cosine self-distance ≈ 0
9. `delete-then-insert-one-transaction` — DELETE + re-INSERT of the same rowid in one transaction,
   then verifies KNN reflects the *new* vector
10. `wal-two-connection-reader-writer` — a second `Mode=ReadOnly` connection loads vec0 itself,
    is confirmed **not** to see the row before commit, then observes the writer's commit

### `scripts/spike-sqlite-vec.sh`

Detect RID (`uname`, `--rid` override) → resolve pin → download (cached) → **verify sha256, abort loud
on mismatch** → `tar -xzf` the member → `dotnet publish -c Release -r <rid> --self-contained` →
**run the published AOT binary** (explicitly not `dotnet run`) → echo `SPIKE VERDICT [<rid>]: PASS/FAIL`.
Failures emit `::error::` so CI annotates them. Pin parsing prefers `jq` (present on all GitHub runner
images) and falls back to `python3` (a Windows Git Bash `python3` can be the non-functional Store shim).

Downloads/publish output cache under `${SPIKE_CACHE_DIR:-$TMPDIR/miller-sqlite-vec-spike/<ver>/<rid>}` —
**outside the repo**, so no binary lands in the working tree.

### `scripts/spike-pins.json`

`version` + `urlTemplate` + per-RID `{name, member, sha256}` with `{VER}`/`{asset}` placeholders,
modelled on `scripts/julie-pins.json`. Proposed as the `semantic-pins.json` template; it swaps
`archiveInnerPathTemplate` for a per-RID `member` because the inner filename differs by platform.

### `.github/workflows/ci.yml` — one new job

`sqlite-vec-aot-spike`, `fail-fast: false`, matrix `osx-arm64`/macos-15, `osx-x64`/macos-15-intel,
`linux-x64`/ubuntu-latest, `win-x64`/windows-2025, running `bash scripts/spike-sqlite-vec.sh --rid <rid>`.
**No `needs:`, and no job needs it** — verified by parsing the YAML. No existing job was modified.

### `docs/findings/2026-07-19-sqlite-vec-aot-spike.md`

Verdict per RID, pinned-asset table, report-only metrics, seven P2b notes, CI-job description, the
required-branch-check note, reproduction instructions, and an explicit "what this spike does NOT
cover" section.

## Verification

**Invariant:** sqlite-vec loads and functions under Native AOT on osx-arm64; the product build is
untouched.

**Scope:** the spike script end-to-end on osx-arm64 (published AOT binary passes all stages) +
`dotnet build Miller.slnx -c Release` at 0 warnings.

| # | Command | Result |
| --- | --- | --- |
| 1 | `scripts/spike-sqlite-vec.sh` (warm cache) | **PASS**, 10/10 stages, exit 0 |
| 2 | `SPIKE_CACHE_DIR=$(mktemp -d) scripts/spike-sqlite-vec.sh` (cold cache, full download+publish) | **PASS**, 10/10, exit 0 — reproducible |
| 3 | `bash scripts/spike-sqlite-vec.sh --rid osx-arm64` (exact CI invocation form) | **PASS**, 10/10 |
| 4 | sha256 negative test — corrupted archive planted in `SPIKE_CACHE_DIR` | **exit 1**, expected/actual pair printed, publish never attempted |
| 5 | `dotnet build Miller.slnx -c Release` | **Build succeeded. 0 Warning(s), 0 Error(s)** |
| 6 | `scripts/test.sh` (fast suite) | **Passed! Failed: 0, Passed: 3617, Skipped: 1** |
| 7 | `ruby -ryaml` parse of `ci.yml` | 5 jobs; `sqlite-vec-aot-spike` `needs: nil`; matrix = the 4 RIDs |
| 8 | `git status --untracked-files=all -- spike/ scripts/spike-*` | only the 4 source files; **zero binaries in the tree** |

Timestamp: 2026-07-19, local host macOS 15 / arm64, .NET 10.0.10, SQLite 3.50.4.
Worktree: `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`, branch
`worktree-semantic-integration`, base commit `87f9b1d`.

**One caveat on #6:** the fast suite passed with 0 failures but its 30s wall-clock tripwire fired at
90s. This is machine contention — five sibling agents were building and testing concurrently on this
host — not a regression from this task: the spike is outside `Miller.slnx`, no product code was
touched, and no test references `ci.yml` content (`WatchPathFilterTests` uses the path only as a
string literal). Flagging it so the lead can confirm against a quiet machine.

## Hard-gate verdicts

| RID | Verdict | Evidence |
| --- | --- | --- |
| osx-arm64 | **PASS** | Local run, 10/10 stages, this session |
| osx-x64 | pending CI | Job added; branch unpushed |
| linux-x64 | pending CI | Job added; branch unpushed |
| win-x64 | pending CI | Job added; branch unpushed |

**The gate did not fail.** The central unknown — whether `bundle_e_sqlite3` under Native AOT still
exposes `sqlite3_load_extension` — is answered yes, with **zero AOT/trim warnings and zero extra
csproj flags**.

## Report-only metrics (osx-arm64)

| Metric | Value |
| --- | --- |
| AOT binary | 2,763,560 bytes (Mach-O arm64) |
| `libe_sqlite3.dylib` sibling | 1,661,200 bytes |
| `vec0.dylib` | 161,896 bytes |
| Publish dir total | ~15 MB (mostly the `.dSYM`, not shipped) |
| First `LoadExtension` | ~140 ms cold; all later stages ≤ 31 ms |

## Files changed

Created: `spike/SqliteVec.AotSpike/SqliteVec.AotSpike.csproj`,
`spike/SqliteVec.AotSpike/Program.cs`, `scripts/spike-sqlite-vec.sh` (executable),
`scripts/spike-pins.json`, `docs/findings/2026-07-19-sqlite-vec-aot-spike.md`.
Modified: `.github/workflows/ci.yml` (one added job + its comment block; no existing job touched),
`.razorback/sdd/task-4-report.md` (this report; see the note at the top).

No file outside the ownership list was edited.

## Miller calls used

| Call | Confirmed |
| --- | --- |
| `inspect target=spike/Codesearch.Spike` | Returned "No indexed symbols" — the spike tree is not indexed, so csproj conventions were read directly. |
| `search query="PublishAot" mode=source` | Found `MillerExtractContractTests` asserting the release workflow publishes with `-p:PublishAot=true -p:JsonSerializerIsReflectionEnabledByDefault=false`. |
| `search query="SQLitePCLRaw" mode=all-text` | Surfaced `THIRD-PARTY-NOTICES.md` (Microsoft.Data.Sqlite 10.0.9 / SQLitePCLRaw.bundle_e_sqlite3 3.0.3), the v0.5.5 release note pinning that override, and `docs/m1-indexing-design.md`'s "no `Batteries_V2.Init()` — bundled provider auto-inits" finding, which the spike re-confirmed under AOT. |

## API-shape evidence (external — verified live, not from memory)

`GET api.github.com/repos/asg017/sqlite-vec/releases/tags/v0.1.9` (published 2026-03-31) listed 27
assets. Three naming facts contradicted reasonable assumptions and are recorded in the findings doc:

- The **Windows asset is `.tar.gz`, not `.zip`** (unlike `julie-pins.json`'s Windows zip).
- Asset filenames carry **no `v` prefix** (`sqlite-vec-0.1.9-…`) while the URL path does (`/v0.1.9/`).
- Both `loadable-*` and `static-*` variants ship; the `LoadExtension` design needs `loadable-*`.

All four sha256s were computed from the downloads and **matched the release's own `checksums.txt`**
byte-for-byte. Inner members confirmed by `tar -tzf`: `vec0.dylib` / `vec0.dylib` / `vec0.so` / `vec0.dll`.

`macos-15-intel` as the current x86_64 macOS runner label was verified against GitHub's changelog
(macos-13 retired 2025-12-04; macos-15-intel is the last Intel image, supported to ~Aug 2027).

## Judgment calls

1. **Added an extra first stage** (`aot-no-jit-fallback`), not in the brief's stage list. A gate that
   can't prove it ran on an AOT artifact isn't a gate; this makes a misconfigured publish fail loudly
   instead of passing green.
2. **Cache outside the repo** rather than a new gitignore entry — satisfies "no binaries in the tree"
   without editing `.gitignore` (not an owned file).
3. **`jq` first, `python3` fallback** for pin parsing, chosen for the Windows leg specifically.
4. **`--self-contained true` on publish** — required for `-r <rid>` AOT and matches how the release
   workflow publishes `miller`.
5. **Did not add the findings doc to `docs/README.md`'s map.** That file is outside my ownership list
   and is being modified by a sibling task. **Lead action needed:** add the pointer.
6. **Job condition mirrors both existing patterns** (`push`/`pull_request` *and*
   `schedule`/`workflow_dispatch`) so the gate runs on PRs and on the nightly, matching its hard-gate role.
7. **Overwrote the stale unrelated `task-4-report.md`** after confirming it was tracked and recoverable.

## Concerns

1. **The gate is advisory until branch protection changes.** The design calls this a hard gate, but a
   non-required check can be merged past. Adding the four matrix legs to `main`'s required checks is
   user-owned; flagged in the findings doc and here.
2. **Three of four RIDs are unproven.** The osx-arm64 leg is the easiest platform. Windows is the real
   risk (ILC + MSVC link, `.dll` extension load, Git Bash). Treat the gate as OPEN until CI runs green
   on all four legs after push.
3. **`macos-15-intel` sunsets ~Aug 2027**, taking the osx-x64 leg of this gate — and Miller's osx-x64
   release target — with it. Not actionable now; worth a roadmap note.
4. **Scope honesty:** this spike proves *function*, not durability or performance. §5.4's crash tests,
   vec0 corruption/checkpoint tests, metadata-column filtering, the prefiltered manual-distance path,
   and `int8[...]` are all still owed. Enumerated in the findings doc so a green gate is not mistaken
   for broader coverage.
5. **Fast-suite wall-clock tripwire fired at 90s** under parallel-agent contention (0 test failures).
   Recommend the lead re-run on a quiet machine before merge.
