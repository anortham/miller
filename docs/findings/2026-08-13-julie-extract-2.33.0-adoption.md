# julie-extract 2.33.0 pin adoption

- **Date:** 2026-08-13
- **Pin moved:** `2.32.1` → `2.33.0`
- **Adopted by:** Miller v1.19.0
- **Upstream:** [`anortham/julie-extractors` v2.33.0](https://github.com/anortham/julie-extractors/releases/tag/v2.33.0)

## Why this pin

`2.33.0` carries three Windows repairs that were found and fixed while preparing Miller v1.19.0. All three
are producer-side, so Miller could not work around any of them.

1. **Process liveness was never known on Windows.** `process_status` had a Unix implementation only, so
   every liveness probe on Windows returned `Unknown`. Lock and lease call sites read `Unknown` as alive by
   design, so a dead owner's lock was never reclaimed by dead-PID evidence — only by the staleness timers,
   which still fired. There is now a `tasklist`-backed Windows implementation with a 250 ms memoization
   window, plus an explicit fallback for other platforms.
2. **`store import` never retried a blocked drain.** A `LeaseUnavailable` result ended the drain and left the
   command in a passive observer loop until its deadline. It now retries the drain until it wins, the
   deadline passes, or a real error arrives.
3. **A failed resolve leaked its scratch database.** `Drop` for `ResolutionBaseWriter` and
   `ResolutionScratchWriter` unlinked the scratch files while the SQLite connection still held them open —
   Rust runs a drop body before it drops the struct's fields. SQLite's Windows backend opens without
   `FILE_SHARE_DELETE`, so every unlink failed with a sharing violation and the scratch database stayed on
   disk. Both writers now close the connection before unlinking.

## Live release facts

Read from the GitHub API on 2026-08-13, not from the upstream release notes.

| Fact | Value |
|---|---|
| Tag | `v2.33.0` |
| Target | `main` |
| Draft | false |
| Prerelease | false |
| Published | 2026-08-13T16:21:56Z |
| Assets | 4 |

## Archive checksums

Recorded in [`scripts/julie-pins.json`](../../scripts/julie-pins.json). Each hash was computed from the
downloaded archive, not copied from upstream.

| Target | Asset | SHA-256 |
|---|---|---|
| `aarch64-apple-darwin` | `julie-extract-v2.33.0-aarch64-apple-darwin.tar.gz` | `3b615169dfc424cfa77022683e24c10efbdbaa09116111eaf5ce8b6fdaa5a26d` |
| `x86_64-apple-darwin` | `julie-extract-v2.33.0-x86_64-apple-darwin.tar.gz` | `f363b3adcc9347c1af6bd2174e7809ae8fdb43d6be53f5788d75716976d689d1` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.33.0-x86_64-unknown-linux-gnu.tar.gz` | `34a31bb323ebba148ecb7a4a68053b63c02b547c5d55874bc6013bc2238e6cfa` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.33.0-x86_64-pc-windows-msvc.zip` | `9d075c3d8b5431daa46d5753c3721b330463500f30398ba7894bb8d642772a92` |

## Restored-binary verification

The restored Windows binary scanned a scratch repository, and `artifact_metadata` was read directly from the
produced `store.db`. Every value matches the constants Miller pins in
`src/Miller.Indexing/MillerExtractContract.cs`.

| Key | Observed | Miller pin |
|---|---|---|
| `binary_version` | `2.33.0` | `2.33.0` |
| `schema_version` | `6` | `6` |
| `sqlite_schema_version` | `6` | `6` |
| `extract_contract_version` | `4` | `4` |
| `report_schema_version` | `3` | `3` |
| `hash_algorithm` | `blake3` | `blake3` |

No artifact contract moved in this pin, so Miller's readers needed no schema work. The one consumer change
that shipped alongside it is unrelated to the pin bump: `JulieStoreClient.ParseResolutionState` did not know
the `converging` resolution state and threw a contract failure when it appeared.

The `VerifyPinnedJulieExtractVersion` build guard passed after restore, which proves the copied
`.tools/julie-extract` reports `2.33.0` rather than a stale leftover.
