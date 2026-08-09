# julie-extract 2.31.0 adoption evidence (2026-08-09)

Miller now pins the published `julie-extract` 2.31.0 release. The legacy consumer contracts remain
SQLite schema 6, extract contract 4, JSON report schema 3, and JSONL schema 4. The new family-store
contracts are separate inputs for the Ph3 wiring work and do not alter the legacy artifact reader.

Upstream release: <https://github.com/anortham/julie-extractors/releases/tag/v2.31.0>.

## Pin evidence

The four public release archives were downloaded and their embedded binaries were smoke-tested before
the Miller pin changed. The checked-in SHA-256 values are:

- `aarch64-apple-darwin`: `2265be55ec682b9079995aff34841d29b82a9be3a5d8161629bf79353e00ec4f`
- `x86_64-apple-darwin`: `552521d19d65e42362c72f55cbe9dbe2a04648632854af4e36d03de72c10f58f`
- `x86_64-unknown-linux-gnu`: `ba9f5f151546aec2f33c5bdc244d1c897793f9158ec4f3e40e6cfc7c7c0f6334`
- `x86_64-pc-windows-msvc`: `9e978620f578830cd53a778e5e5780b9a3daef4a0debca4a3b26c567783bcf8d`

`scripts/restore-julie-extract.sh` downloaded the Apple ARM archive from the public release, verified
the checked-in digest, installed the ignored worktree-local binary, and reported
`julie-extract 2.31.0`.

## TDD and verification

- RED: the three direct version contracts expected 2.31.0 and failed against the old 2.30.0 constant.
- GREEN: the focused pin/schema/capabilities scope passed 35/35 with the restored binary and no build
  escape hatch.
- `dotnet build Miller.slnx -c Release --no-restore` passed with 0 warnings and 0 errors; the pinned
  binary and semantic-sidecar build guards both ran against real restored tools.

No Miller release, tag, push, or store-default change is part of this adoption slice.
