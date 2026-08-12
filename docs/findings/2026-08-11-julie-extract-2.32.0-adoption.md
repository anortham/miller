# julie-extract 2.32.0 adoption — 2026-08-11

Miller pins the stable producer release published from
`d50e1359426db76e4232ff8533ffa99be3e47ca8`:
<https://github.com/anortham/julie-extractors/releases/tag/v2.32.0>.
The Release Binaries workflow run
[`31549665153`](https://github.com/anortham/julie-extractors/actions/runs/31549665153)
completed successfully at that commit. Post-release CI run
[`31550593363`](https://github.com/anortham/julie-extractors/actions/runs/31550593363)
also completed successfully at `076db37d1921013468b9b1882c23707a01341c07`.

## Pinned archives

| Target | SHA-256 |
|---|---|
| `aarch64-apple-darwin` | `8643bf19db98af7942785454aa3b774cac300b22650aeae87a86b1bd69ca3648` |
| `x86_64-apple-darwin` | `ad0f7e9abde86ce919c01088f551c908425d07f4060d80fb85ce85efc56946bf` |
| `x86_64-unknown-linux-gnu` | `aa7280999d561a7a2a6385f416870503fe29aaa54443d2bfeef393b6bdd56fad` |
| `x86_64-pc-windows-msvc` | `4d42f077e5f118b31178350b5881e5738b34c9d63ce5e520c98b7fd39884be6b` |

The repository restore script downloaded the published Linux archive, verified
its digest, installed `.tools/julie-extract`, and the binary reported
`julie-extract 2.32.0`. A separate fresh download produced the same Linux digest.

## Measured compatibility

A fresh one-file C# scan with the released binary reported standalone schema
`6`, SQLite schema `6`, extract contract `4`, report schema `3`, JSONL schema
`4`, hash algorithm `blake3`, and resolver-output epoch `6`. A fresh family-store
import recorded `store_sqlite_schema_version=2` and `store_format_epoch=1`.
Miller's public capabilities test continues to report semantic query policy `2`.

The top-level CLI still exposes `store`, `scan`, `update`, `delete`, `info`,
`export`, `languages`, and `rebind`. The producer report vocabulary is unchanged
apart from additive scoped-resolution telemetry, so Miller needs no schema,
migration, parser, or MCP-tool change.

## Producer behavior adopted

- Scoped family-store resolution is now the default. Set
  `JULIE_STORE_RESOLUTION_DELTA=off` to force the previous full-resolution path.
- Exact results rebase when cumulative replacements exceed one quarter of the
  ready base or exact-gap storage exceeds 64 MiB; equality does not rebase.
- Recovery recognizes a current store left with partial resolution and safely
  resumes it instead of treating the store as irrecoverable.
- Windows publication closes the current pointer before rename and uses
  Windows-compatible durability synchronization.

This is the pin and contract proof only. Controlled recovery, consumer
compatibility, performance, and sidecar convergence are recorded by the
subsequent dogfood tasks before Miller release readiness is decided.
