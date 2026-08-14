# julie-extract 2.33.1 pin adoption

- **Date:** 2026-08-13
- **Pin moved:** `2.33.0` → `2.33.1`
- **Upstream:** [`anortham/julie-extractors` v2.33.1](https://github.com/anortham/julie-extractors/releases/tag/v2.33.1)
- **Supersedes:** [`2026-08-13-julie-extract-2.33.0-adoption.md`](2026-08-13-julie-extract-2.33.0-adoption.md)

## Why this pin

`2.33.1` repairs a fourth Windows defect. It broke **every scoped store resolution** on Windows from
`2.32.0` through `2.33.0`, which includes the extractor Miller v1.19.0 shipped with.

Rust's `std::fs::canonicalize` always returns a verbatim path on Windows, so the store path arrived as
`\\?\C:\...`. The prior-overlay attach built a SQLite URI from that path by replacing backslashes and
prefixing `file:`, which produced `file://?/C:/...`. SQLite percent-encoded the `?` and then read the text
between `file://` and the next `/` as the authority, so every attach failed with
`invalid uri authority: %3F`.

The visible result was a family store that served at `level=full` but never reached
`resolution=exact`. A full resolve returns early before `materialize_prior_overlay`, so only the scoped
path touched the broken code — which is why the store looked healthy while incremental resolution never
converged.

The fix strips the `\\?\` and `\\?\UNC\` prefixes before building the URI and emits a correct
`file:///C:/...` form. A regression test canonicalizes the temp directory so the prefix is present; the
earlier test used a bare temp path and never saw it.

## Live release facts

Read from the GitHub API on 2026-08-13, not from the upstream release notes.

| Fact | Value |
|---|---|
| Tag | `v2.33.1` |
| Annotated tag object | `81500f1c0b50aafeb959538478f3ab387ae2b989` |
| Dereferenced commit | `ff8ab576fc916416a58410d2942ef946bd29dbce` |
| Target | `main` |
| Draft | false |
| Prerelease | false |
| Published | 2026-08-13T19:37:27Z |
| Assets | 4 |

## Archive checksums

Recorded in [`scripts/julie-pins.json`](../../scripts/julie-pins.json). Each hash was computed from the
downloaded archive, not copied from upstream.

| Target | Asset | SHA-256 |
|---|---|---|
| `aarch64-apple-darwin` | `julie-extract-v2.33.1-aarch64-apple-darwin.tar.gz` | `1ddbdab59ea002c43014c397b5eb2938aa0660a1db0e9be0abcf066afa2835db` |
| `x86_64-apple-darwin` | `julie-extract-v2.33.1-x86_64-apple-darwin.tar.gz` | `82c9e9808d3c823f4d553f6f97ff00bc8fdddbf79760002c0a23394c91b3304d` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.33.1-x86_64-unknown-linux-gnu.tar.gz` | `a2e168a3599a9c580c3430dfd5c572f2d2c6b299c6d6aceac0ab8e495b602c77` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.33.1-x86_64-pc-windows-msvc.zip` | `06865adca15d936e4813a990090f97a946cd1036b0e677c16f4da78d29db0355` |

The restore script verified the Windows archive against its recorded hash before it installed the binary.

## Restored-binary verification

The restored Windows binary scanned a scratch repository, and `artifact_metadata` was read directly from
the produced `store.db`.

| Key | Observed | Miller pin |
|---|---|---|
| `binary_version` | `2.33.1` | `2.33.1` |
| `schema_version` | `6` | `6` |
| `sqlite_schema_version` | `6` | `6` |
| `extract_contract_version` | `4` | `4` |
| `hash_algorithm` | `blake3` | `blake3` |
| `index_level` | `full` | n/a |
| `reference_resolution_version` | `6` | n/a |

A plain `scan` artifact does not carry `report_schema_version`, so that key was not observed here and is
not claimed.

No artifact contract moved in this pin, so Miller's readers needed no schema work. The
`VerifyPinnedJulieExtractVersion` build guard passed after restore, which proves the copied
`.tools/julie-extract` reports `2.33.1` rather than a stale leftover.

## A related trap that Miller already handles

The scan recorded `root_path = \\?\C:\...`, the same verbatim form that caused the upstream defect. Miller
strips that prefix before it compares roots ([`PathCanonicalizer`](../../src/Miller.Indexing/PathCanonicalizer.cs)
documents the rule, and `BlazorNamespaceCatalog` follows it), so artifact-root identity is unaffected. This
was checked, not assumed.
