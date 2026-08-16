# julie-extract 2.33.4 pin adoption

- **Pin moved:** `2.33.3` → `2.33.4`.
- **Upstream:** [`anortham/julie-extractors` v2.33.4](https://github.com/anortham/julie-extractors/releases/tag/v2.33.4).
- **Tag provenance:** `v2.33.4` resolves to commit `e8e63afe764eeb9e22d0a0fcd300c7543d59da0f`.
- **Release state:** GitHub reports the release as stable, non-draft, and non-prerelease, published
  `2026-08-16T22:23:59Z`.

## What changed

Julie 2.33.4 is the producer half of the v1.19.3 recovery follow-up. It keeps the existing CLI, report, schema,
and versioned-store contracts while adding three correctness-preserving latency fixes:

- accumulated scoped resolution work now compacts into a fresh exact base before a small update inherits an
  oversized overlay;
- ordinary writer open reapplies the safe additive symbol read indexes on upgraded stores;
- queued resolve waiters return the durable committed, acknowledged, or failed result immediately instead of
  waiting for the request timeout.

The accumulated-work fixture published 79 sequential transitions, rebased once at 33.05% unique identifier
coverage, left an empty delta, and matched the full oracle digest. Its broad resolve took 3,040 ms; three following
one-file updates remained scoped with a 740 ms p95 wall time. These are deterministic fixture measurements, not a
cold whole-repository extraction guarantee.

## Four-platform assets

| Target | Archive | SHA-256 |
| --- | --- | --- |
| `aarch64-apple-darwin` | `julie-extract-v2.33.4-aarch64-apple-darwin.tar.gz` | `88f4ab9f84fb536d5ee47bb79260cb5597f266e1b1dc40641fce8b701fb41240` |
| `x86_64-apple-darwin` | `julie-extract-v2.33.4-x86_64-apple-darwin.tar.gz` | `694133a35fe20de6c8b046c62870c30f615a1b6331342e76e0d1b667b2a6dcd3` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.33.4-x86_64-pc-windows-msvc.zip` | `2f11c1746af08dd9cc4c6a2831b979570593aae1279be4e3ad016db1a284501b` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.33.4-x86_64-unknown-linux-gnu.tar.gz` | `58e7dfad74d86e34f90a93054062884ce4fc7df65ca185bd03299a2d96e8e597` |

## Verification

- GitHub release facts, tag provenance, asset names, and four supplied SHA-256 values were checked before pinning.
- `scripts/restore-julie-extract.sh` downloaded the Linux archive, matched its SHA-256, and installed a binary
  reporting `julie-extract 2.33.4`.
- The Julie producer gates recorded 28 store-connection contract tests and 33 store-resolution contract tests,
  plus formatting and whitespace checks, before Miller adopted the pin.

Miller v1.19.3 consumes this exact producer pin. No Miller public schema, report, CLI, or MCP surface version moves.
