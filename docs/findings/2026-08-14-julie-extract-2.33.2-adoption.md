# julie-extract 2.33.2 pin adoption

- **Date:** 2026-08-14
- **Pin moved:** `2.33.1` → `2.33.2`
- **Upstream:** [`anortham/julie-extractors` v2.33.2](https://github.com/anortham/julie-extractors/releases/tag/v2.33.2)
- **Tagged commit:** `e227fa59327fe8a6ba60de6e3867565f078e8c68`
- **Supersedes:** [`2026-08-13-julie-extract-2.33.1-adoption.md`](2026-08-13-julie-extract-2.33.1-adoption.md)

## Why this pin

The 2.33.2 producer change set addresses the reliability and latency failures observed in Miller's family-store
workflow:

- Read-only SQLite operations retry the whole lazy read when the locking protocol is transiently rejected. Writer
  lease mutations are not retried on that signal.
- Lease acquisition samples the clock after it obtains SQLite's write lock, so time spent waiting cannot create an
  immediately expired lease. Heartbeat renewal retries transient errors and respects the fencing token.
- A single changed file remains a scoped resolution even when its identifiers are common across the repository,
  avoiding an accidental whole-store resolve.
- Windows root-relative path joins normalize slash direction before opening the target.

These are producer-side fixes; Miller consumes them through the pinned binary and does not duplicate their SQLite
write behavior.

## Live release facts

The following facts are from the live v2.33.2 release:

| Fact | Value |
|---|---|
| Tag | `v2.33.2` |
| Tagged commit | `e227fa59327fe8a6ba60de6e3867565f078e8c68` |
| Published | `2026-08-14T01:09:03Z` |
| Assets | 4 |

## Archive checksums

Recorded in [`scripts/julie-pins.json`](../../scripts/julie-pins.json). The restore gate must verify each downloaded
archive against its recorded SHA-256 before installing `.tools/julie-extract`.

| Target | Asset | SHA-256 |
|---|---|---|
| `aarch64-apple-darwin` | `julie-extract-v2.33.2-aarch64-apple-darwin.tar.gz` | `c4b4379833e193150657f2bb12df2b98f4e7fd6d16ef27a2bbd0c90d877ff54e` |
| `x86_64-apple-darwin` | `julie-extract-v2.33.2-x86_64-apple-darwin.tar.gz` | `dc37de257d280d9925bd49a41ad8b0d753057cea9155e6db08a34680b3de1278` |
| `x86_64-unknown-linux-gnu` | `julie-extract-v2.33.2-x86_64-unknown-linux-gnu.tar.gz` | `04ab2760b7fbddf935ae0de494d65dd7072ab6395037cedf55858738d6331035` |
| `x86_64-pc-windows-msvc` | `julie-extract-v2.33.2-x86_64-pc-windows-msvc.zip` | `ec9cb5e2a04a535606bc3e28cf466e4150ac2e4bfae2c0ffbfde54105780ae89` |

## Miller adoption

Miller 1.19.1 moves the Julie pin, version constants, pin assertions, and third-party notice to 2.33.2. Miller's
release and plugin metadata move to 1.19.1. Miller's schema, SQLite schema, extract
contract, report, JSONL, and hash-algorithm gate values are unchanged in this pin; the release gate must still
verify the restored binary before publication.

Miller v1.19.1 has not been published at the time of this finding.
