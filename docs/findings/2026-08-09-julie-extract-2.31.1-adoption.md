# julie-extract 2.31.1 adoption evidence (2026-08-09)

Miller now pins the stable `julie-extract` 2.31.1 release. The legacy SQLite, extraction, report, and JSONL
contract versions remain 6, 4, 3, and 4. The family-store contract remains store contract 1, SQLite store
schema 2, format epoch 1, and report schema 1.

Upstream release: <https://github.com/anortham/julie-extractors/releases/tag/v2.31.1>.

## Why this patch is required

The 2.31.0 producer release used a different resolution-base identity when importing an exact standalone
artifact than ordinary store resolution used for the same rows. Miller's migration and restart path depends on
those operations sharing one canonical identity. Julie 2.31.1 contains that fix and its public contract test.

## Published archive pins

| target | archive SHA-256 |
|---|---|
| `aarch64-apple-darwin` | `68a90760a1988a0703f530b5d43abcdebdf9e737859f96e31942098b07f56370` |
| `x86_64-apple-darwin` | `d32f8f938c9778f55724c6826641d93fc282c69c32b7a1cd13458489c93e845d` |
| `x86_64-unknown-linux-gnu` | `0a0e2c5379837d4bd7aba91db90b09704e043013460edbfd509ddb7e974e636d` |
| `x86_64-pc-windows-msvc` | `38e379c385f0bc800ce4e2aace55ce7e3a6c0e12ad474b9bc5b3937a4d1ba049` |

All four archives were downloaded from the live release. Their GitHub digests matched, every embedded binary
checksum passed, and the Apple Silicon binary reported `julie-extract 2.31.1`.

## Miller verification

- The direct version contracts failed first against the 2.31.0 constant, then passed after the pin update.
- `scripts/restore-julie-extract.sh` downloaded and verified the published archive into the task worktree.
- The restored binary version and the build-time pin guard agree on 2.31.1.
- The full fast and Scale suites, plugin tests, Release build, agent-doc sync, and diff checks remain the Miller
  release gate.
