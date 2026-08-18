# julie-extract 2.34.1 pin adoption

- **Pin moved:** `2.34.0` → `2.34.1`.
- **Upstream:** [`anortham/julie-extractors` v2.34.1](https://github.com/anortham/julie-extractors/releases/tag/v2.34.1).
- **Tag provenance:** `v2.34.1` resolves to commit `499e708d` (fix commit `18bb7dbd`).
- **Release state:** GitHub reports the release as stable, non-draft, and non-prerelease, published
  `2026-08-18T21:38:38Z`.

## What changed

Julie 2.34.1 is a hotfix for the 2.34.0 store-wedge regression found while
dogfooding Miller's 2.34.0 pin (see
`2026-08-18-julie-extract-2.34.0-adoption.md`, "Dogfood incident"):

- Writer open now reaps retired `reference_resolution.*` rows from
  `language_capability_gaps`, so the capability snapshot count check no longer
  conflicts on stores written by pre-2.34.0 binaries.
- A store already wedged by 2.34.0 self-heals on its next writer open. No
  manual repair, no re-extract.
- No contract changes: artifacts stay schema 7, JSONL contract 5, extract
  contract 4, reports 3, store schema 2. Miller's contract constants are
  unchanged; only the pin string moves.

This pin clears the release blocker recorded against 2.34.0. Miller is
releasable on 2.34.1.

## Four-platform assets

| Target | Archive | SHA-256 |
| --- | --- | --- |
| `aarch64-apple-darwin` | `julie-extract-v2.34.1-aarch64-apple-darwin.tar.gz` | `10456d91bb642d13fae60f5ac29129c6b37c98b63f139ccd81dd7ae8008b2c55` |
| `x86_64-apple-darwin` | `julie-extract-v2.34.1-x86_64-apple-darwin.tar.gz` | `f10cbde28a8fabdd529cbbb83c5acf9a208cbea38cb31e0a894b1a3810ac3250` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.34.1-x86_64-pc-windows-msvc.zip` | `ba6aba6c8ff04a733f5cd371f8d46ab9eb784d51b8dadde85f2530c84dd5c83e` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.34.1-x86_64-unknown-linux-gnu.tar.gz` | `5a6167b9615a89b7755e231684ca1920f438012f1e8901209b67f5210a5268f1` |

## Verification

- GitHub release facts, tag provenance, asset names, and four computed SHA-256 values were checked before pinning
  (release evidence: julie-extractors `docs/release-evidence/2026-08-18-v2-34-1-release.md`).
- The restored Linux binary reports `julie-extract 2.34.1`; the `VerifyPinnedJulieExtractVersion` build guard passes.
- Release build 0 warnings / 0 errors; fast suite green after the bump; Scale suite run against the new binary.
- The reap fix itself was proven end-to-end before release against a copy of this machine's wedged store: the
  2.34.0 binary conflicts, the fixed binary commits and reaps the 109 retired rows.
