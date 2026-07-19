# sqlite-vec under Native AOT — P0 hard-gate spike

**Date:** 2026-07-19
**Gate:** Semantic integration design §5.4 — "the sqlite-vec-on-Native-AOT spike across all four RIDs
is a phase-0 HARD GATE."
**Verdict (local leg):** **PASS on osx-arm64.** Other three RIDs: **pending CI evidence** (the CI job
exists but has not run; this branch is unpushed).

## What was probed

The program bets that sqlite-vec can be loaded as a SQLite loadable extension from an absolute path
via `Microsoft.Data.Sqlite`'s `LoadExtension`, inside Miller's **Native-AOT-published** main binary.
Native AOT is the risk: no JIT, a trimmed/ILC-compiled image, and a native SQLite provider that must
still expose `sqlite3_load_extension`.

Artifacts:

| Path | Role |
| --- | --- |
| `spike/SqliteVec.AotSpike/` | Console app, `<PublishAot>true</PublishAot>`, 10 staged checks. **Not in `Miller.slnx`** (mirrors `spike/Codesearch.Spike`). |
| `scripts/spike-sqlite-vec.sh` | Detect RID → download pinned asset → verify sha256 → `dotnet publish` AOT → **run the published binary** → echo verdict. |
| `scripts/spike-pins.json` | sqlite-vec v0.1.9 per-RID asset name + inner member + sha256 + url template. |
| `.github/workflows/ci.yml` job `sqlite-vec-aot-spike` | Matrix over the 4 release RIDs. |

The script runs the **published AOT artifact**, never `dotnet run` — a `dotnet run` pass would prove
nothing about the gate. Stage `aot-no-jit-fallback` asserts `RuntimeFeature.IsDynamicCodeSupported ==
false` inside the process and fails the run otherwise, so the gate cannot silently pass on a JIT host.

## Pinned inputs (verified against the live release, not from memory)

sqlite-vec **v0.1.9**, published 2026-03-31, from `asg017/sqlite-vec`. Asset names were read from the
GitHub releases API; sha256s were computed from the downloads and **cross-checked byte-for-byte against
the release's own `checksums.txt`**.

| RID | Asset | Inner member | sha256 |
| --- | --- | --- | --- |
| osx-arm64 | `sqlite-vec-0.1.9-loadable-macos-aarch64.tar.gz` | `vec0.dylib` | `8282126333399ddfe98bbbcc7a1936e7252625aac49df056a98be602e46bfd29` |
| osx-x64 | `sqlite-vec-0.1.9-loadable-macos-x86_64.tar.gz` | `vec0.dylib` | `53ad76e400786515e2edcaed2f01271dda846316390b761fadbd2dcf56aa4713` |
| linux-x64 | `sqlite-vec-0.1.9-loadable-linux-x86_64.tar.gz` | `vec0.so` | `b959baa1d8dc88861b1edb337b8587178cdcb12d60b4998f9d10b6a82052d5d7` |
| win-x64 | `sqlite-vec-0.1.9-loadable-windows-x86_64.tar.gz` | `vec0.dll` | `51581189d52066b4dfc6631f6d7a3eab7dedc2260656ab09ca97ab3fb8165983` |

**Naming surprises worth carrying into `semantic-pins.json`:**

- The Windows asset is a **`.tar.gz`, not a `.zip`** — unlike `julie-pins.json`, where the Windows
  asset is a zip. One extraction path (`tar -xzf`) covers all four RIDs.
- Assets are versioned `sqlite-vec-0.1.9-…` with **no `v` prefix**, while the download URL path uses
  the `v`-prefixed tag (`releases/download/v0.1.9/`). `spike-pins.json` keeps `{VER}` unprefixed and
  hardcodes the `v` in `urlTemplate`.
- Upstream ships `loadable-*` (extension) and `static-*` (link-time) variants. The design's
  `LoadExtension` path needs **`loadable-*`**.

## Result — osx-arm64

Command: `scripts/spike-sqlite-vec.sh` (host: macOS 15 / arm64, .NET 10.0.10, SQLite 3.50.4).

```
PASS  aot-no-jit-fallback                  IsDynamicCodeSupported=false (native, no JIT)
PASS  extension-file-present               161,896 bytes
PASS  open-connection                      3.50.4
PASS  load-extension-absolute-path         LoadExtension succeeded
PASS  vec_version                          v0.1.9
PASS  create-vec0-table                    vec0(embedding float[8] distance_metric=cosine)
PASS  insert-integer-rowids                5 rows on integer rowids
PASS  knn-match-k3                         2:0.0000, 1:0.2769, 3:0.3135
PASS  delete-then-insert-one-transaction   delete+insert committed atomically; KNN reflects the new vector
PASS  wal-two-connection-reader-writer     read-only reader loaded vec0 and observed the writer's commit
VERDICT: PASS  (10 stages)
```

Every §5.4 requirement the spike was asked to exercise passed: absolute-path `LoadExtension`,
`vec_version()`, `vec0` with `float[8] distance_metric=cosine`, integer rowids, KNN
`MATCH ? AND k=3`, DELETE-then-INSERT in one transaction, and a WAL two-connection reader/writer smoke.

### Report-only metrics (osx-arm64)

| Metric | Value |
| --- | --- |
| AOT binary (`sqlite-vec-aot-spike`) | 2,763,560 bytes (2.76 MB), Mach-O arm64 |
| `libe_sqlite3.dylib` (sibling, from SQLitePCLRaw) | 1,661,200 bytes (1.66 MB) |
| `vec0.dylib` (sqlite-vec extension) | 161,896 bytes (162 KB) |
| Publish dir total | ~15 MB (dominated by the `.dSYM` bundle, which release packaging does not ship) |
| Extension load | ~140 ms first call (cold dylib load); every later stage ≤ 31 ms |

## Notes that feed P2b directly

1. **No AOT/trimming flags were needed.** The spike csproj is plain: `<PublishAot>true</PublishAot>`
   plus `Microsoft.Data.Sqlite` 10.0.9 and `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 — the same versions
   `Miller.Server.csproj` already references. Publish emitted **zero trim/AOT warnings**, so no
   `NoWarn` entry is needed (contrast the existing Serilog `IL2104` suppression). No
   `SQLitePCLRaw.Batteries_V2.Init()` call was required; the bundle's module initializer runs under
   AOT, consistent with `docs/m1-indexing-design.md`.
2. **`EnableExtensions(true)` is mandatory and is the whole load mechanism.** The sequence is
   `Open()` → `EnableExtensions(true)` → `LoadExtension(absolutePath)` → `EnableExtensions(false)`.
   The stock `bundle_e_sqlite3` build does **not** omit `sqlite3_load_extension`, which was the
   central unknown; it is now answered.
3. **The extension must be loaded on EVERY connection, including read-only readers.** vec0 is a
   virtual-table module registered per-connection. The WAL stage proves a `Mode=ReadOnly` reader can
   itself load the extension and then query a vec0 table the writer is mutating — that is the shape
   Miller's `WorkspaceIndexProvider` readers will need.
4. **e_sqlite3 stays a sibling native file under AOT — it is not linked into the image.** The publish
   output carries `libe_sqlite3.dylib` next to the executable. So sqlite-vec is the *second* native
   sibling, not the first; the packaging pattern the design calls for (csproj copy layout next to the
   binary, release-archive assertions, packaged runtime smoke) has a working precedent to follow.
5. **Vectors were passed as raw float32 little-endian BLOBs** via `AddWithValue`, and distances came
   back as `double`. The JSON-array form was not needed. Cosine self-distance was exactly 0 to within
   `1e-4`, so a self-match assertion is a viable cheap health check at runtime.
6. **One extraction path covers all four RIDs** (`tar -xzf`), which simplifies the restore-script
   section relative to `restore-julie-extract.sh`'s tar/zip split.
7. **`spike-pins.json`'s shape is the proposed template for `semantic-pins.json`**: top-level
   `version` + `urlTemplate`, per-RID `{name, member, sha256}`, `{VER}`/`{asset}` placeholders. It adds
   a `member` field that `julie-pins.json` expresses as `archiveInnerPathTemplate`; per-RID `member` is
   better here because the inner filename differs by platform (`.dylib`/`.so`/`.dll`).

## CI job

`.github/workflows/ci.yml` gains one job, `sqlite-vec-aot-spike`, matrixed over
`osx-arm64` (macos-15), `osx-x64` (macos-15-intel), `linux-x64` (ubuntu-latest), `win-x64`
(windows-2025), with `fail-fast: false` so one RID's failure does not hide the others. It has **no
`needs:` and nothing needs it** — an isolated island, per the plan, so a gate failure is a loud signal
rather than a block on the product build. No existing job was modified.

- `macos-15-intel` is the current x86_64 macOS label (`macos-13` retired 2025-12-04). GitHub has
  announced it as the **last** Intel macOS image, supported to ~August 2027. When it goes, the
  osx-x64 leg of this gate — and of Miller's release matrix — needs a plan.
- **Making this job a required branch check is user-owned.** Branch protection was not touched. The
  design calls this a hard gate; a hard gate that can be merged past is advisory. Recommend adding
  `sqlite-vec-aot-spike (osx-arm64 | osx-x64 | linux-x64 | win-x64)` to the required checks on `main`.

## Reproducing

```bash
scripts/spike-sqlite-vec.sh                 # detect RID
scripts/spike-sqlite-vec.sh --rid linux-x64 # explicit
```

Downloads cache under `${SPIKE_CACHE_DIR:-$TMPDIR/miller-sqlite-vec-spike/<ver>/<rid>}` — outside the
repo, so no binary ever lands in the working tree. A sha256 mismatch aborts before publish with the
expected/actual pair and leaves the archive for inspection (verified with a deliberately corrupted
archive: exit 1, no publish attempted).

## Scope this spike does NOT cover

Deliberately out of scope for the P0 gate, and still owed later:

- The §5.4 release-gated crash tests (post-inference, mid-vec0-mutation, pre-cursor-advance,
  post-model-swap, post-promotion) and vec0 DELETE/checkpoint/**corruption** tests on all four RIDs.
- vec0 **metadata columns** / partition keys and the prefiltered manual-distance path.
- `int8[...]` element type — only `float[8]` was exercised. The quantization lane is benchmark-gated.
- Real dimensionality and real corpus size; 8 dims and 5 rows prove function, not performance.
