# julie-extract 2.32.1 adoption — 2026-08-12

Miller pins the stable producer release published from
`815dd5c896460ed08e2adbf585cee3fce4326423`:
<https://github.com/anortham/julie-extractors/releases/tag/v2.32.1>.
Release workflow
[`31598688941`](https://github.com/anortham/julie-extractors/actions/runs/31598688941)
completed successfully at that commit.

## Pinned archives

| Target | SHA-256 |
|---|---|
| `aarch64-apple-darwin` | `06fe6b44cdaeb9d213d801f1e6179820d48d935faccce99c2bd7e61435ed6f8d` |
| `x86_64-apple-darwin` | `661213733a338ba3653717abcc1b4ce3391b48eb390667b7d18416cfebb27b14` |
| `x86_64-unknown-linux-gnu` | `642f7073c7fc82504d5f90f8511db4382a18181dd29177f2cbed3f1a5a2b5aea` |
| `x86_64-pc-windows-msvc` | `620980df747b97c83b626c6bf07cfe550da2b59ef639e3edbf14063bc6273398` |

Fresh downloads matched GitHub's published digests. The Linux package's embedded checksum passed and its binary
reported `julie-extract 2.32.1`.

## Compatibility and behavior

The versioned store remains schema v2 and format epoch 1. Manifests, resolution bases, the standalone v3 artifact,
resolver-output epoch, crash boundaries, and CLI report contracts are unchanged, so Miller needs no schema,
migration, parser, or MCP-tool change.

The producer patch renews leases during long indivisible imports, reuses validated byte-identical imports, selects
scope crossover before expensive work, and caches the exact writer's identifier insert statement. Its faithful
Miller replay preserved the canonical digest, 392,526 identifiers, 10,804 pending rows, and 1,538 source versions
while reducing exact identifier-row publication from 14.304 to 7.547 seconds.
