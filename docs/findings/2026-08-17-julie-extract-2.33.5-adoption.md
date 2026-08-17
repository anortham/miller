# julie-extract 2.33.5 pin adoption

- **Pin moved:** `2.33.4` → `2.33.5`.
- **Upstream:** [`anortham/julie-extractors` v2.33.5](https://github.com/anortham/julie-extractors/releases/tag/v2.33.5).
- **Tag provenance:** `v2.33.5` resolves to commit `707c8a47f9272ffca8b3066d38d99cb86e4c3f90`.
- **Release state:** GitHub reports the release as stable, non-draft, and non-prerelease, published
  `2026-08-17T02:44:11Z`.

## What changed

Julie 2.33.5 is the producer half of the v1.19.4 incremental-index follow-up. It keeps the existing CLI,
report, schema, and versioned-store contracts while fixing one-file incremental resolve:

- a journal with one changed file no longer promotes to a full-corpus resolve on name collisions;
- private locals and imports are omitted from `touched_names`;
- Full+Crossover now carries `rebase_after_exact` into the resolve pipeline so the overlay actually
  compacts.

## Four-platform assets

| Target | Archive | SHA-256 |
| --- | --- | --- |
| `aarch64-apple-darwin` | `julie-extract-v2.33.5-aarch64-apple-darwin.tar.gz` | `248e88736f8405aeaf2cb479d3c1ef042872ccacc6bd9af0021a27154d69cd21` |
| `x86_64-apple-darwin` | `julie-extract-v2.33.5-x86_64-apple-darwin.tar.gz` | `71446b923bd5314187473b3578a415f7682d4038a4686dbe37ca2d83713ffcfa` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.33.5-x86_64-pc-windows-msvc.zip` | `ca369064ac76f6067700ad7d24b32d87c94bab9dbb10f0fc682cb31a86f6b816` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.33.5-x86_64-unknown-linux-gnu.tar.gz` | `77a3bd426fb5df75bb8f2bcf5ea7c8dddf436c0ddbf96f06fec4c48ede0030a8` |

## Verification

- GitHub release facts, tag provenance, asset names, and four supplied SHA-256 values were checked before pinning.
- The producer contract gate for one-file default resolve now expects `scoped`, not `full`.
- Miller v1.19.4 consumes this exact producer pin. No Miller public schema, report, CLI, or MCP surface version moves.
