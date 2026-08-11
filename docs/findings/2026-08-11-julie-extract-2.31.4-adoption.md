# julie-extract 2.31.4 adoption — 2026-08-11

Miller 1.18.1 pins the stable producer release published from
`16f25f2f8c9ed25e0e37644411db1340eb3df37c`:
<https://github.com/anortham/julie-extractors/releases/tag/v2.31.4>.

## Pinned archives

| Target | SHA-256 |
|---|---|
| `aarch64-apple-darwin` | `9c40e660bb58f747cd79be08ca7338b9f3d0523e9c17f7ce5fa9850efe1b305b` |
| `x86_64-apple-darwin` | `35a6dd5cfeb5bf4012dbc1bb0898e44117ec1c5c00f0642d0a1b2a1bb1f2c1d0` |
| `x86_64-unknown-linux-gnu` | `f729e61fce0d49f0d3b590d3d9fb25648ea64d6670c5ff2bf1ce3085a0a7c04f` |
| `x86_64-pc-windows-msvc` | `38c3c75c92702f36d9d3906f955c2784cb733c0387280c364cfda515ee5ce01d` |

Fresh downloads matched GitHub's published digests. The Linux binary reports
`julie-extract 2.31.4`, and its release workflow passed all four targets.

## Consumer impact

The producer patch preserves the family-store schemas, manifests, resolver
epoch, resolution bases, and standalone artifact contract. On the Miller
repository replay, exact resolution improved from 6m20.55s to 2m24.54s while
producing the same 37,965 gaps across 98 files and 416,361 identifiers.

Miller therefore needs no compatibility or migration change: only the pinned
version, archive names, checksums, and restored local binary change.
