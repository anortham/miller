# julie-extract 2.33.3 pin adoption

- **Date:** 2026-08-16
- **Pin moved:** `2.33.2` → `2.33.3`
- **Upstream:** [`anortham/julie-extractors` v2.33.3](https://github.com/anortham/julie-extractors/releases/tag/v2.33.3)
- **Tagged commit:** `39572be89fdb7497e6d13da1e265d746773e3906`
- **Supersedes:** [`2026-08-14-julie-extract-2.33.2-adoption.md`](2026-08-14-julie-extract-2.33.2-adoption.md)

## Why this pin

The 2.33.3 producer release is the compatible performance and reliability half of the recovery release:

- eligible scoped resolution finalizes from validated predecessor/base state and bounds work around changed
  files, touched symbols, and dependency-closed relationships;
- batched coverage reads, fixed statement reuse, and request-local validated-base proof reuse remove repeated
  work without disabling relationship extraction or the forced-full escape hatch;
- unchanged and byte-identical imports avoid unnecessary store work while retaining the existing reports and
  idempotency behavior; and
- recovery, fencing, crash cleanup, leases, and cross-platform path identity remain under the existing gates.

The producer release reports the guarded production-volume replay improving from `148.431 s` to `54.814 s`,
with official three-run medians of `29,522 ms` for full resolution and `11,002 ms` for scoped resolution and
zero semantic, applied-row, and row-level differences against the exact oracles.

## Live release facts

These facts were read from the live v2.33.3 GitHub release and its successful workflows:

| Fact | Value |
|---|---|
| Tag | `v2.33.3` |
| Tagged commit | `39572be89fdb7497e6d13da1e265d746773e3906` |
| Published | `2026-08-16T13:09:05Z` |
| Stable | yes; draft=false, prerelease=false |
| CI run | [31947439306](https://github.com/anortham/julie-extractors/actions/runs/31947439306) |
| Release workflow | [31948256383](https://github.com/anortham/julie-extractors/actions/runs/31948256383) |
| Assets | 4 |

## Archive checksums

Recorded in [`scripts/julie-pins.json`](../../scripts/julie-pins.json). The restore gate verifies each downloaded
archive against its recorded SHA-256 before installing `.tools/julie-extract`.

| Target | Asset | SHA-256 |
|---|---|---|
| `aarch64-apple-darwin` | `julie-extract-v2.33.3-aarch64-apple-darwin.tar.gz` | `0985cf472ab3cd2b6fc892c2d1b3ce83ab32512f84b179ba7e23afe742528a09` |
| `x86_64-apple-darwin` | `julie-extract-v2.33.3-x86_64-apple-darwin.tar.gz` | `056318986fe463a3c1b319514c74a7e4e93821cb09b1975cc9cbd37ec7861d11` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.33.3-x86_64-pc-windows-msvc.zip` | `b35f113bc0c43a57648b7a05aed2454866ed3f1da16613e733f8c46c8f3a87ce` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.33.3-x86_64-unknown-linux-gnu.tar.gz` | `a517e9e1a74193a33ef5a3860b324a8e7acaaa03c24cd164397d8f0e0e761014` |

## Miller adoption

Miller 1.19.2 moves the Julie pin, contract constant, pin assertions, and third-party notice to 2.33.3.
Miller's schema, SQLite schema, extract contract, report, JSONL, and hash-algorithm gate values are unchanged.
The semantic sidecar remains pinned at 0.1.0. The recovery release adds no MCP tool or schema surface.

Miller v1.19.2 has not yet been published at the time of this finding; its release gate must restore this exact
producer and verify the integrated package before publication.
