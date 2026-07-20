# fix-cx4 — third-party attribution for the bundled semantic artifacts

**Finding (codex, real-improvement / high — release compliance):** release archives bundle
`julie-semantic-sidecar` and the sqlite-vec loadable extension (`vec0.*`), but `THIRD-PARTY-NOTICES.md`
attributed neither. Published archives would redistribute both without attribution.

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3`, branch `worktree-semantic-p3`,
base HEAD `511602d`.

## Facts established (sources actually read)

| Component | Version | License | Source read |
| --- | --- | --- | --- |
| sqlite-vec | 0.1.9 (pinned) | Apache-2.0 **OR** MIT (dual), Copyright (c) 2024 Alex Garcia | `gh api 'repos/asg017/sqlite-vec/contents?ref=v0.1.9'` → both `LICENSE-APACHE` and `LICENSE-MIT` exist at the pinned tag; `LICENSE-MIT` blob decoded for the copyright line. `gh api repos/asg017/sqlite-vec/license` → spdx `Apache-2.0`. |
| julie-semantic-sidecar | 0.1.0-rc.1 (pinned) | MIT, Copyright (c) 2026 Alan Northam | `/Users/murphy/source/julie-semantic-sidecar/LICENSE` (local clone) and its `Cargo.toml` (`license = "MIT"`). |
| llama-cpp-2 / llama-cpp-sys-2 | `=0.1.151` | MIT OR Apache-2.0 | `license = "MIT OR Apache-2.0"` in each crate's `Cargo.toml` under `~/.cargo/registry/src/index.crates.io-*/`. |
| llama.cpp + ggml | vendored by llama-cpp-sys-2 0.1.151 | MIT, Copyright (c) 2023-2026 The ggml authors | Vendored tree confirmed at `.../llama-cpp-sys-2-0.1.151/llama.cpp` (with `ggml/` subtree); the crate package strips upstream `LICENSE`, so the license text was read from `https://raw.githubusercontent.com/ggml-org/llama.cpp/master/LICENSE`. |

The pinned versions come from `scripts/semantic-pins.json` (`sidecar.version`, `sqliteVec.version`).
The sidecar's `Cargo.toml` declares `llama-cpp-2` / `llama-cpp-sys-2` with `default-features = false`;
the binary is statically linked, so redistributing it redistributes llama.cpp/ggml.

`vec0.dylib` itself carries no embedded license string (`strings .tools/vec0.dylib | grep -i licen`
returned only error messages), which is exactly why the notices file has to carry the attribution.

## Changes

**`THIRD-PARTY-NOTICES.md`** — added two subsections under "Bundled tooling":
- `### julie-semantic-sidecar` — MIT first-party entry, plus what the statically linked binary *embeds*
  (llama-cpp-2/llama-cpp-sys-2, and the vendored llama.cpp/ggml with their MIT terms).
- `### sqlite-vec` — dual Apache-2.0 OR MIT, pinned version, and the per-platform archive paths
  (`.tools/vec0.dylib|.so|.dll`).

Also corrected a stale fact in the existing `### julie-extract` entry: it claimed pin **2.5.1**;
`scripts/julie-pins.json` says **2.16.0**.

**`.github/workflows/release.yml`** — finding items 3 and 4 were already partly satisfied: both package
legs already `cp`/`Copy-Item` the notices file into the package dir and already assert its presence
(Unix `test -f`, Windows `Test-Path`/`throw`). Presence alone does not catch the regression the finding
describes — bundling a new native component without attributing it. So both legs now additionally assert
the packaged notices file *mentions* each redistributed component
(`julie-extract`, `julie-semantic-sidecar`, `sqlite-vec`, `llama.cpp`) and fail loud with the component
name otherwise.

Deliberately **not** added to the "Verify packaged … extractor" steps: those run against the publish dir
*before* packaging, where `THIRD-PARTY-NOTICES.md` does not yet exist. The package step is the correct
and only place the assert can be true.

Restore scripts (`restore-semantic-sidecar.sh` / `.ps1`) were **not** touched — the notices-file approach
covers the obligation without reshaping extraction.

## Verification

- YAML parses: `ruby -ryaml -e 'YAML.safe_load(...)'` → OK. (`python3` has no `yaml` module here;
  `actionlint` is not installed on this machine.)
- Every `shell: bash` step in the workflow extracted and `bash -n`'d → all 5 steps OK, including the
  edited "Package Unix artifact".
- The new guard simulated against the real file: all four components report `present`.
- **`pwsh` is not installed locally**, so the Windows package leg's added PowerShell block could not be
  executed or syntax-checked — it uses only `Get-Content -Raw`, `foreach`, `-notmatch`,
  `[regex]::Escape`, and `throw`, mirroring the block directly above it.
- `dotnet build` **not required and not run** — no C# files touched.
- `scripts/test.sh` tripwire: first run **4154 passed / 0 failed / 2 skipped** (27s test duration; the
  37s wall-clock warning is build time, not a slow test). Retry per protocol failed to *compile* on
  `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (`CliDispatch.RunForcedArm` missing) — foreign
  noise from a parallel fix worker mid-edit on files I do not own, not attributable to this change.

## Concerns

- The Windows notices assert is unexecuted locally (no `pwsh`); first real exercise is a release run.
- llama.cpp/ggml license text was read from upstream `master`, not from a tag matching what
  llama-cpp-sys-2 0.1.151 vendored — the crate strips the upstream `LICENSE` file, so no in-tree copy
  exists to cite. The project has been MIT throughout; the risk is only that the copyright *year range*
  quoted may not match the exact vendored commit.
- The notices entries are attribution text, not bundled license copies. If stricter compliance is wanted
  later, the next step is packaging upstream `LICENSE` files into `.tools/` via the restore scripts.
