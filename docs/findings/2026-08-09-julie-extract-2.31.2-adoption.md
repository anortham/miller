# julie-extract 2.31.2 adoption evidence (2026-08-09)

Miller now pins the stable `julie-extract` 2.31.2 release. The legacy SQLite, extraction, report, and JSONL
contract versions remain 6, 4, 3, and 4. The family-store contract remains store contract 1, SQLite store
schema 2, format epoch 1, and report schema 1.

Upstream release: <https://github.com/anortham/julie-extractors/releases/tag/v2.31.2>.

The canonical imported-resolution-base identity fix first shipped in 2.31.1 and remains part of the pinned
2.31.2 line. This 2.31.2 patch adds physical-byte maintenance and retention accounting, capacity preflight,
safe artifact-import validation, rollback and mixed-version hardening, resolution-adapter updates, and the
full supported 38-language contract coverage. These are the reasons Miller advances its pin beyond 2.31.1;
the upstream release notes and preparation ledger record the scope and gates.

## Published archive pins

| target | archive SHA-256 |
|---|---|
| `aarch64-apple-darwin` | `3e04c3a12156ef6a278534fe589650a991a3b21a6cbfb41fcc62dc955157c727` |
| `x86_64-apple-darwin` | `90bfd19fbff0c3acd591219626772a8e5173591fb2e346fc0a3d99f517b1a27f` |
| `x86_64-unknown-linux-gnu` | `3117b2380c19cb76df47b9ca4a02b150d23658ffcfa17be77b7aa17e798d5422` |
| `x86_64-pc-windows-msvc` | `a47e5e26597aa636fe6263842bae8b0347c9b2a3814e433f8adee68ecb21be64` |

The four archive digests are the values published by GitHub for v2.31.2. The Miller restore script must
download these exact assets and verify their checksums before the build guard can accept the binary.

## Verification

- The restored binary reports `julie-extract 2.31.2`.
- Each archive's embedded binary checksum matches the extracted binary.
- The build-time pin guard and `MillerExtractContract.PinnedJulieExtractVersion` agree on 2.31.2.
- The published tag points to `b9d7eefcb1fc03eb51cc770ca4c2b832568ffbed`; the upstream release workflow and
  release-creation job passed, and the v2.31.2 preparation ledger records the Rust contract, preflight, and
  four-target package-manifest gates at the exact release-prep head.
- Miller's Ph3 store acceptance was rerun against the restored 2.31.2 binary; local timing observations remain
  report-only and are not hosted-CI performance gates.
