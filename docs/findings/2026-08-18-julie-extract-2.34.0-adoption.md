# julie-extract 2.34.0 pin adoption

- **Pin moved:** `2.33.7` → `2.34.0`.
- **Upstream:** [`anortham/julie-extractors` v2.34.0](https://github.com/anortham/julie-extractors/releases/tag/v2.34.0).
- **Tag provenance:** `v2.34.0` resolves to commit `91d5ff94122a1a5c466a9272ca8a7a3a28ebf64e`.
- **Release state:** GitHub reports the release as stable, non-draft, and non-prerelease, published
  `2026-08-18T19:53:08Z`.

## What changed

Julie 2.34.0 is the producer half of the query-time resolution plan (Plan B,
`docs/plans/2026-08-18-query-time-resolution-phase1-plan.md`). The producer no
longer materializes resolution:

- The `store resolve` subcommand is removed. Store reports no longer carry a
  `resolution` object.
- Exported artifacts move to sqlite schema `7` and JSONL contract `5`: no
  `identifier_resolutions`, resolution bases, or resolution deltas.
- `store export` no longer requires an `exact` view, so Miller's
  `MILLER_INDEX_STORE=off` export path works again.
- Family stores stay store schema `2` and migrate in place on first writer
  open. The `views` table keeps its `resolution_state`/`resolution_base_id`
  columns (default `unbound`), so Miller's visibility reads stay valid.

Miller-side adoption in the same change:

- `MillerExtractContract`: schema `6` → `7`, JSONL `4` → `5`; extract contract
  stays `4`, report schema stays `3`.
- The parse-only resolution report DTOs (`StoreResolutionResult`,
  `StoreResolutionState`, `StoreResolutionResultDto`) are removed. The parser
  still ignores a legacy `resolution` object and still parses `"resolve"` in
  recorded reports.
- The live store round-trip Scale test drops its `store resolve` leg. The
  query-time parity gate skips with a reason because the pinned binary no
  longer ships `resolve`; its recorded 2.33.7 ground truth stands in
  `docs/findings/2026-08-18-query-time-resolution-phase1-gates.md`.

## Four-platform assets

| Target | Archive | SHA-256 |
| --- | --- | --- |
| `aarch64-apple-darwin` | `julie-extract-v2.34.0-aarch64-apple-darwin.tar.gz` | `d1ab1e918b1687310ccfc902564fb8f32c94ac3eb34fa8847f14869cb138dfa7` |
| `x86_64-apple-darwin` | `julie-extract-v2.34.0-x86_64-apple-darwin.tar.gz` | `799bef25384be4ef635381bc0c0e5b37eab182ba7338e11e1048792dd906cc15` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.34.0-x86_64-pc-windows-msvc.zip` | `b724f51fc044e531e4261b27dc99d53fa91b112628c6646ce2e18070f778a7e5` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.34.0-x86_64-unknown-linux-gnu.tar.gz` | `07804d3e82497a31a2aa874a04e5dc6dde0659a5b9cb83c74c0e514f41f251df` |

## Dogfood incident: capability snapshot conflict on existing stores (RELEASE-BLOCKING)

Dogfooding the pin on this machine's real family store wedged immediately: every
import failed with `store_import_write_l1: capability snapshot conflict for
extraction epoch 1`, Miller recorded 3 consecutive `IncrementalReconcile`
failures, and the served view went stale.

- Root cause (julie-extractors): 2.34.0 stopped declaring the 109
  `reference_resolution.*` capability gap rows but kept
  `EXTRACTION_IDENTITY_EPOCH = 1`. The writer's capability snapshot count check
  (`sync_capability_snapshot`) then conflicts forever on any store written by an
  earlier binary (old epoch-1 rows: 128 gaps; new snapshot: 19). Exports already
  filter these rows; the import-side check did not.
- Why no test caught it: every Miller Scale test and the off-mode smoke create
  FRESH stores, which never carry the old rows. Only a store with pre-2.34.0
  history conflicts.
- Local repair (this machine, 2026-08-18): deleted the epoch-1 rows from the four
  capability tables in the wedged `store.db`; the next import committed and the
  workspace converged (fresh, vectors ready).
- Root-cause fix (julie-extractors, commit `18bb7dbd`, unpushed): writer open now
  reaps retired `reference_resolution.%` gap rows, mirroring the export filter.
  Verified end-to-end against a copy of the wedged production store: the released
  2.34.0 binary conflicts, the fixed binary commits and reaps the rows.
- **Release implication: Miller must NOT release with the 2.34.0 pin.** Any
  existing user workspace store would wedge exactly this way on upgrade.
  RESOLVED 2026-08-18: julie-extract v2.34.1 shipped the reap fix and Miller
  re-pinned to it (`2026-08-18-julie-extract-2.34.1-adoption.md`).

## Verification

- GitHub release facts, tag provenance, asset names, and four supplied SHA-256 values were checked before pinning.
- The restored Linux binary reports `julie-extract 2.34.0`; the `VerifyPinnedJulieExtractVersion` build guard passes.
- Fast suite green after the bump; Scale suite run against the new binary.
- Off-mode smoke: a store-mode workspace served search in store mode, then
  `MILLER_INDEX_STORE=off` exported `symbols.db` from the view and served
  `search` and `trace refs` with query-time evidence.
