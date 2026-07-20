# Task G1 — semantic-pins.json + restore scripts

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3`
**Branch:** `worktree-semantic-p3` (base commit `98605b1`)
**Status:** COMPLETE — all four acceptance criteria met.

## Deliverables (owned files only)

- `scripts/semantic-pins.json` — two sections: `sidecar` (by rust triple) + `sqliteVec` (by .NET RID)
- `scripts/restore-semantic-sidecar.sh`
- `scripts/restore-semantic-sidecar.ps1`

Nothing else was created or modified. `spike-pins.json`, `julie-pins.json`, and the julie-extract
restore scripts are untouched.

## Miller calls used

| Call | Purpose | Result |
|---|---|---|
| `inspect target='scripts/restore-julie-extract.sh'` | template structure | **Served** — shell IS indexed: returned 3 functions (`read_pin:35`, `verify_sha:179`, `cleanup_staging:202`) + 31 constants with line anchors. Gave the pin-parsing / verify / staging skeleton before reading the file. |

The `.ps1` and the two JSON pin files were read directly (PowerShell/JSON bodies were not needed as
symbol structure — the pin files are 12-line data documents).

## API-shape evidence (where each contract fact came from)

| Fact | Source |
|---|---|
| Pin-file field names `version`/`binary`/`urlTemplate`/`assets.<key>.{name,sha256}`, `{VER}`/`{asset}` placeholders | `scripts/julie-pins.json:1-12` |
| `archiveInnerPathTemplate` key | `scripts/julie-pins.json:4` |
| sqlite-vec pins keyed by **.NET RID** with an extra `member` field; Windows asset is `.tar.gz` not `.zip` | `scripts/spike-pins.json:4-11` (copied verbatim — not re-derived) |
| sqlite-vec member sits at **archive root** | `scripts/spike-sqlite-vec.sh:129` — `tar -xzf "$ARCHIVE" -C "$CACHE_DIR" "$MEMBER"` |
| Sidecar version / asset names (no `v` prefix) / 4 sha256s / binary at archive ROOT | Plan Global Constraints, `docs/plans/2026-07-20-p3-track1-sidecar-pins-plan.md:15-21` — copied verbatim |
| Sidecar sha256 for `aarch64-apple-darwin` | **Independently confirmed at runtime** — the negative test printed the real download's digest `2957b0c857cf0aa6ff6c5288ae3b2035b99f3540efc51978eb21cc755a59f508`, byte-identical to the plan's pin |

## Design decisions beyond the template

1. **Staging outside the repo.** julie-extract downloads into `.tools/` and deletes on mismatch.
   This script stages under `$TMPDIR` instead, so a bad download can never create `.tools/` at all —
   that is what makes "aborts without touching `.tools/`" literally true rather than
   "creates-then-cleans-up".
2. **`MILLER_SEMANTIC_PINS` pin-file override.** Lets the negative test run against a tampered pin
   copy with the committed pin file never modified. Mirrored in the `.ps1`.
3. **Generic JSON reader.** julie-extract's python3 fallback hardcodes a per-key `if/elif` ladder,
   which cannot express this file's nested two-section shape. Replaced with a regex path-walker
   handling both `.a.b` and `.a["k"].b`. Both reader paths were exercised (below).
4. **`--from-source` restores the sidecar only** and says so, since sqlite-vec is a third-party
   upstream binary with no local build path.

## Verification

### 1. Negative test — abort before mutation (run FIRST, `.tools/` did not exist)

```
$ MILLER_SEMANTIC_PINS=$D/bad-pins.json bash scripts/restore-semantic-sidecar.sh
Restoring julie-semantic-sidecar v0.1.0-rc.1 for aarch64-apple-darwin
  url:    https://github.com/anortham/julie-semantic-sidecar/releases/download/v0.1.0-rc.1/julie-semantic-sidecar-0.1.0-rc.1-aarch64-apple-darwin.tar.gz
  sha256: 0000000000000000000000000000000000000000000000000000000000000000
error: sha256 mismatch for julie-semantic-sidecar-0.1.0-rc.1-aarch64-apple-darwin.tar.gz
  expected: 0000000000000000000000000000000000000000000000000000000000000000
  actual:   2957b0c857cf0aa6ff6c5288ae3b2035b99f3540efc51978eb21cc755a59f508
  nothing was installed into .../semantic-p3/.tools
exit=1

$ ls -la .tools/
(.tools does NOT exist — abort-before-mutation CONFIRMED)
```

**Proves:** pin integrity is enforced against the *real* downloaded bytes, and a mismatch leaves the
working tree byte-for-byte unchanged. It also cross-validates the plan's pinned sha256 from an
independent source (the live release).

### 2. Real restore (arm64 mac, jq reader)

```
$ bash scripts/restore-semantic-sidecar.sh
Restoring julie-semantic-sidecar v0.1.0-rc.1 for aarch64-apple-darwin
  sha256 OK
Restoring sqlite-vec v0.1.9 (vec0.dylib) for osx-arm64
  sha256 OK
Installed: .../.tools/julie-semantic-sidecar
julie-semantic-sidecar 0.1.0-rc.1
Installed: .../.tools/vec0.dylib (sqlite-vec 0.1.9)

$ ls -la .tools/
-rwxr-xr-x  7212016  julie-semantic-sidecar
-rwxr-xr-x   161896  vec0.dylib

$ .tools/julie-semantic-sidecar --version
julie-semantic-sidecar 0.1.0-rc.1
```

**Proves:** acceptance criterion 1 exactly — both artifacts restored, sha256-verified, exec bit set,
`--version` prints the pinned string. G2/G3 have the `.tools/` layout they depend on.

### 3. python3 fallback reader (jq masked off `PATH`)

```
$ rm -rf .tools && PATH="/usr/bin:/bin:/usr/sbin:/sbin" bash scripts/restore-semantic-sidecar.sh
  sha256 OK ... sha256 OK
Installed: .../.tools/julie-semantic-sidecar
julie-semantic-sidecar 0.1.0-rc.1
Installed: .../.tools/vec0.dylib (sqlite-vec 0.1.9)
```

**Proves:** the no-jq path (CI images without jq, plain macOS) resolves the same nested pins. This was
new code, so it needed its own run rather than inheriting the template's credit.

### 4. Syntax / structural checks

- `bash -n scripts/restore-semantic-sidecar.sh` → **OK**
- `python3 -c json.load(semantic-pins.json)` → **OK**
- `.ps1`: **pwsh is NOT installed on this host** (`command -v pwsh` → absent). Evidence is structural
  review only: the `.ps1` mirrors the `.sh` flag-for-flag — `-FromSource`/`-SourcePath` ↔
  `--from-source`, `MILLER_SEMANTIC_SIDECAR_SOURCE`, `MILLER_SEMANTIC_PINS`, same pin field reads,
  sha256-verify-before-extract ordering, staging under `GetTempPath()` with `finally` cleanup, install
  into `.tools\` only after both archives verify and extract. Windows-specific divergences are
  deliberate and match the plan: sidecar `.zip` via `Expand-Archive`, sqlite-vec `.tar.gz` via bsdtar
  (`tar`, shipped with Windows 10+), `.exe` suffix, `vec0.dll`.

  **Proves:** cross-platform mirror parity by inspection. Not executed — stated plainly rather than
  implied.

### 5. Worker ceiling — `scripts/test.sh` (regression tripwire)

**Could not produce a clean signal. Not caused by this task.** The suite fails at *compile*:

```
tests/Miller.Tests/Server/HybridSearchTests.cs(284,34): error CS0246: 'ISymbolFusionArm' not found
tests/Miller.Tests/Server/HybridSearchTests.cs(287,20): error CS0246: 'SemanticSymbolFusionArm' not found
```

`git status` shows a concurrent Track 2 worker's **uncommitted, mid-implementation** F3 code in this
shared worktree: ` M src/Miller.Server/Tools/SearchTool.cs`, `?? src/Miller.Core/Search/RrfFusion.cs`,
`?? tests/Miller.Tests/Server/HybridSearchTests.cs` (Task F3 "RRF fusion at executor seam", still in
progress). Those files are not mine and were left untouched — no stash, no revert.

Isolation evidence that G1 cannot be the cause:
- `grep -rl "semantic-pins\|restore-semantic-sidecar" src/ tests/` → **zero hits**. The scripts are not
  referenced by any C# file, csproj, or MSBuild target (the csproj guard is G2's work, not landed).
- G1 adds three files under `scripts/` and touches no `.cs`, no `.csproj`, no `.slnx`.

A retry was not run: `CS0246` is a deterministic compile error, not the known-flaky IndexerService
timing failure the retry rule targets, so a second run adds no information.

### 6. Repo hygiene

`git check-ignore -v` confirms both restored binaries are covered by `.gitignore:24 (.tools/)`. No
binary is staged or committed.

## Gate invariants — what each check proves

| Invariant | Proven by |
|---|---|
| **Pin integrity** — installed bytes are exactly the pinned artifact | §1 (real digest surfaced and compared) + §2 (both archives verify against committed pins) |
| **Abort before mutation** — a bad pin/tampered download cannot corrupt `.tools/` | §1 (`.tools/` still absent after failure; staging lives outside the repo) |
| **Cross-platform mirror parity** — Windows hosts get the same layout | §4 structural review (execution blocked: no pwsh on this host) |
| **Optionality preserved** — semantic stays opt-in | §5 isolation evidence: scripts are inert to the build; no machine is required to run them |

## Concerns

1. **Shared-worktree contention (needs lead attention).** G1 was briefed as
   `serial-worker-commit`, but a Track 2 F3 worker has uncommitted C# in this same worktree right
   now. That made the worker-ceiling gate unusable for me and means the next worker to run
   `scripts/test.sh` will hit the same wall until F3 compiles. Serial commit discipline held on my
   side (I staged only my three files), but the *verification* lane is not actually serialized.
2. **`.tools/julie-extract` is absent in this worktree.** Pre-existing and out of scope, but G3's
   Scale tests will skip on the julie-extract side until `scripts/restore-julie-extract.sh` is run
   here.
3. **`--from-source` was not executed** — no local `julie-semantic-sidecar` checkout on this host. The
   path is a structural mirror of the julie-extract equivalent; first real exercise will be whoever
   next bumps the RC before assets publish.
