# Task G4 — release workflow packaging (P3 Track 1)

**Status:** COMPLETE. Commit `f97ec01` on `worktree-semantic-p3`.
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3`, branch `worktree-semantic-p3`,
parent HEAD `58a4957`. Clean before and after (only the two owned files staged). No push, no workflow
dispatch.

## Files changed (owned only)

- `.github/workflows/release.yml`
- `docs/release-process.md`

## Miller calls + API-shape evidence

- `mcp__miller__search query="release workflow packaging julie-extract restore" mode=content` — surfaced
  the plan's G4 section (`docs/plans/2026-07-20-p3-track1-sidecar-pins-plan.md:124`), the CLAUDE.md/AGENTS.md
  "Release packaging" rule (4-target matrix kept in step with `scripts/julie-pins.json`), and
  `README.md:599` "Release archives". Confirmed the workflow file is `.github/workflows/release.yml`.
- Read-level evidence for the shapes I depended on:
  - Matrix keys (`.github/workflows/release.yml:44-57`): `target` / `rid` / `runner` — `aarch64-apple-darwin`
    → `osx-arm64` / `macos-14`; `x86_64-apple-darwin` → `osx-x64` / `macos-15-intel`;
    `x86_64-unknown-linux-gnu` → `linux-x64` / `ubuntu-24.04`; `x86_64-pc-windows-msvc` → `win-x64` /
    `windows-2025`. **Host platform equals matrix target on every leg**, so the restore scripts' host
    detection resolves the correct pinned asset — no cross-compile hazard.
  - Existing step names used as the template: `Restore julie-extract (Unix)` / `(Windows)`,
    `Verify packaged Unix extractor`, `Verify packaged Windows extractor`, `Package Unix artifact`,
    `Package Windows artifact`.
  - Restore-script flags/behaviour (`scripts/restore-semantic-sidecar.sh:28,187-220,262-269`;
    `.ps1:34,121-148,192-197`): pin file `scripts/semantic-pins.json`, `set -euo pipefail` /
    `$ErrorActionPreference='Stop'` (a failed restore exits non-zero → fails the job), installs exactly
    `.tools/julie-semantic-sidecar[.exe]` and `.tools/<sqliteVec.assets[rid].member>`; `--from-source` /
    `-FromSource` are the local-only escape hatches and are NOT used in CI.
  - Packaging seam (`src/Miller.Server/Miller.Server.csproj:132-157`): G2's `Content` items for the
    semantic pair carry metadata byte-identical in shape to julie-extract's (`:60-74`) —
    `Link` + `CopyToOutputDirectory=PreserveNewest` + `Visible=false`, with `Exists()` conditions as the
    optionality contract and a `vec0.*` wildcard. So `dotnet publish` already places all three under
    `<publish>/.tools/`; the workflow only needs the restore + assertions.

## What the workflow now does

1. **Restore steps** (after the julie-extract restore, before Publish): `bash scripts/restore-semantic-sidecar.sh`
   on Unix, `./scripts/restore-semantic-sidecar.ps1` on Windows. No URLs or checksums in the workflow.
2. **`Resolve pinned sqlite-vec member`** (pwsh, all platforms): reads `scripts/semantic-pins.json` via
   `ConvertFrom-Json`, looks up `sqliteVec.assets[<matrix.rid>].member`, throws if absent, exports
   `SEMANTIC_VEC_MEMBER` to `GITHUB_ENV`. pwsh (not jq) because the Publish/version steps already prove
   pwsh is the one shell available on all four runners; this avoids assuming jq on the Windows image.
3. **Unix verify**: `test -x .tools/julie-semantic-sidecar`, `test ! -e .tools/julie-semantic-sidecar.exe`,
   `test -f .tools/${SEMANTIC_VEC_MEMBER}`, `test ! -e dashboard/.tools/julie-semantic-sidecar`, plus
   `.tools/julie-semantic-sidecar --version` next to the existing julie-extract smoke.
4. **Windows verify**: presence of `.tools/julie-semantic-sidecar.exe` and `.tools/$env:SEMANTIC_VEC_MEMBER`,
   absence of the Unix-named sidecar, and `& $windowsSidecar --version`. (The dashboard `.tools` guard already
   throws on any `dashboard/.tools`, so it covers the sidecar too.)
5. **Unix package**: `chmod +x` now also covers `.tools/julie-semantic-sidecar`.

**Failure policy (G2's lesson honored):** absence never fails a *local* build — the csproj `Exists()`
conditions keep that true and I did not touch them. In the RELEASE workflow the restore is explicit, so a
failed restore fails the leg loudly; a release archive silently missing the sidecar is the worse outcome.
A comment in the workflow states this so a future reader does not "fix" it into a soft failure.

**Windows archive shapes:** the sidecar asset is `.zip` (`Expand-Archive`) and the sqlite-vec asset is
`.tar.gz` (`tar`, bsdtar shipped with Windows 10+) — both already handled inside `restore-semantic-sidecar.ps1`;
the workflow just calls it. Tool availability the scripts need is already relied on by existing steps:
`shasum`/`sha256sum` (Unix package step uses `shasum -a 256`), `tar` (Unix package step), `curl` (ubiquitous on
all four images), `Get-FileHash`/`Expand-Archive`/`tar` on windows-2025.

## docs/release-process.md

- New **"Pinned Binaries (julie-extract and the semantic pair)"** section: pin-file → artifacts → script
  table, the never-hardcode-URLs statement, host==target note, the explicit fail-loudly rationale, and a
  3-step semantic pin-bump flow (edit pins → re-run restore, `VerifyPinnedSemanticSidecarVersion` catches a
  stale binary → `scripts/test.sh scale` re-checks the encoder fingerprint).
- New **"Required notes for the first release that ships the semantic pair"** under Release Notes:
  (a) `encoder_fingerprint` changed because `ModelRevision` was corrected to the HF repo revision `main`
  (commit `f68dad8`), so a pre-existing local `vectors.db` reclassifies `incompatible` and rebuilds
  automatically; (b) semantic stays optional/off-switchable — `MILLER_SEMANTIC=off` is a permanent zero-work
  guarantee, lexical-only output byte-identical (ADR-0003).
- New guardrail bullet referencing (not restating) the CLAUDE.md tag-push 403 rule, since this change
  touches `.github/workflows`.

## Verification (static; no workflow runs, no dispatch)

| Check | Result |
| --- | --- |
| `actionlint .github/workflows/release.yml` | 1 finding — SC2129 style at the pre-existing `Resolve release tag` step. **Baseline `git show HEAD:` of the same file reports the identical single finding**, so zero new findings introduced. (`python3 -c "import yaml"` unavailable — no PyYAML; actionlint parses the YAML and was the preferred option per the acceptance criterion.) |
| Restore references the pin file, not literal URLs | `grep -nE "releases/download\|[0-9a-f]{64}" release.yml` → **none**. Only `scripts/restore-semantic-sidecar.{sh,ps1}` and `scripts/semantic-pins.json` appear. |
| Packaging paths match what the restore installs | Script installs `${TOOLS_DIR}/julie-semantic-sidecar` (`.sh:263`), `${TOOLS_DIR}/${VEC_MEMBER}` (`.sh:264`), `$ToolsDir\julie-semantic-sidecar.exe` (`.ps1:194`), `$ToolsDir\$vecMember` (`.ps1:195`) — exactly the paths the verify/package steps assert. |
| Publish dir really carries them (stronger than a dry check) | Ran `dotnet publish src/Miller.Server -c Release -r osx-arm64 --self-contained -o <tmp>`: `<publish>/.tools/` contains `julie-extract`, `julie-semantic-sidecar` (mode `-rwxr-xr-x`, so the Unix `test -x` passes), `vec0.dylib`. |
| `scripts/test.sh` (regression tripwire) | First run: 1 failure — `IndexerServiceScanTests.StartAsync_WhenEnabledLeader_BuildsSearchSidecarAfterStartupScan`. Reran that test alone → **Passed**; reran the full fast suite → **4153 passed / 0 failed / 2 skipped / 4155 total, 22s**. Flake under parallel load, unrelated: this task touches zero C#. |
| `dotnet build Miller.slnx -c Release` | **Not run, deliberately** — no C# changed (workflow YAML + markdown only). The Release publish above compiled `Miller.Server` and its dependencies cleanly, which is stronger evidence than the skipped build would have been. |

## Concerns

- The `IndexerServiceScanTests` sidecar-convergence test flaked once under parallel load and passed both in
  isolation and on the full-suite rerun. Not mine, but worth a note if it recurs in the branch gate.
- The workflow change is genuinely untested against a real runner (no dispatch, per the constraint). The
  first real validation is the next package-only run; the highest-risk step is
  `Resolve pinned sqlite-vec member` on `windows-2025` (pwsh `ConvertFrom-Json` + `PSObject.Properties`
  lookup of a dashed RID key). It fails loudly with a named error rather than silently, which is the right
  failure mode, but it is the one line no local check can exercise.
