# julie-extract 2.35.0 adoption (2026-08-22)

Miller's pin moves 2.34.4 → 2.35.0. The release ships one addition: the
`store maintain retire-view --view <id>` verb — caller-named, transactional
removal of one dead view's manifest entries, manifests, and `views` row. It
keeps allocator marks, log rows, receipts, and cursors (identity-reuse guards),
and leaves the view's released versions to ordinary `gc --apply` reclaim. This
is the julie-extract half of the dead-view story whose Miller half is
`StoreSidecarReclaim`.

## Provenance

- Stable release: <https://github.com/anortham/julie-extractors/releases/tag/v2.35.0>,
  published 2026-08-22T04:02:51Z from tag commit
  `05ea9be0cc4699f4e22ea724056ba285e0cef924` on `main`.
- Producer gates on the tag source tree: fmt, xtask, default, contract
  (4,473 tests / 89 suites / 0 failed), strict clippy, preflight, package-list,
  agent-doc sync. Candidate CI run 32549275610 and release workflow run
  32549993741 both succeeded.
- Producer evidence:
  `julie-extractors/docs/release-evidence/2026-08-22-v2-35-0-release.md`.

## Four-platform archive checksums (live outer SHA-256)

| Archive | SHA-256 |
|---|---|
| `julie-extract-v2.35.0-aarch64-apple-darwin.tar.gz` | `9375fbafc9b5e84b082ba1a290811bedf4790c7fcb1657509b5080a1c31ffa29` |
| `julie-extract-v2.35.0-x86_64-apple-darwin.tar.gz` | `d4b7eb17a4ae7f4a1abe7db762c318db77015635df68d046da23e3c6f9a60895` |
| `julie-extract-v2.35.0-x86_64-pc-windows-msvc.zip` | `6240d8fdae4cac9b52d6c83550142c86415f3647059c2b8646623d9d618fb02c` |
| `julie-extract-v2.35.0-x86_64-unknown-linux-gnu.tar.gz` | `6b5a8d7a48e03dae5b63461beb2e40fcd9389667ea99200dbecc2cf2ad2471a5` |

## Contract constants: unchanged

Artifact schema 7, SQLite schema 7, extract contract 4, report schema 3,
JSONL 5, blake3, extraction identity epoch 4. No re-extract is needed; the
consumer action is a binary swap.

## Restored-binary verification

`scripts/restore-julie-extract.ps1` downloaded the Windows archive, verified
the pinned SHA-256, and installed `.tools/julie-extract.exe` reporting
`julie-extract 2.35.0`. A forced `Miller.Server` rebuild passed the
`VerifyPinnedJulieExtractVersion` guard and the output copy reports 2.35.0.
The downloaded binary lists `retire-view` under `store maintain`, and
scan/info on the producer's legacy-v3 rust fixture succeeded (3 files, 0
failed).

## The bump caught a real gap, on purpose

The first scale run after the pins-file bump failed 12 tests with
`The family store requires reader 2.35.0; Miller bundles julie-extract 2.34.4`:
the 2.35.0 binary stamps stores with its own reader floor, and the compiled
`MillerExtractContract.PinnedJulieExtractVersion` constant still said 2.34.4.
The constant and the three tests that pin it by literal
(`MillerExtractContractTests`, `JulieSchemaGateTests`, `CliDispatchTests`
capabilities) were bumped together — which is exactly the lockstep the
`JuliePinsJsonMatchesContractVersion` fast-suite guard exists to force.

## Suite evidence at the new pin

Recorded in this commit's gate run: fast and scale suites on the bumped tree.
