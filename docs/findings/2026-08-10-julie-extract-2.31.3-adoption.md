# julie-extract 2.31.3 adoption evidence (2026-08-10)

Miller pins the stable `julie-extract` 2.31.3 release. The legacy SQLite, extraction, report, and JSONL
contract versions remain 6, 4, 3, and 4. The family-store contract remains store contract 1, SQLite store
schema 2, format epoch 1, and report schema 1.

Upstream release: <https://github.com/anortham/julie-extractors/releases/tag/v2.31.3>.

This patch hardens concurrent multi-worktree writers and maintenance freezes without changing Miller's
consumer contract. Store-writer lease acquisition, coordinator mutations, generation promotion, rollback,
resolve, and import paths recheck fenced maintenance ownership. Capacity decisions also re-probe live free
space before destructive or staging work. Miller adopts the patch as the producer baseline for default-on
family-store operation; it does not redesign Miller's family/view/read-session boundaries.

## Published archive pins

| target | archive SHA-256 |
|---|---|
| `aarch64-apple-darwin` | `5256bc23fa2219d4a975df13688b52306e408dfebdd4d62a7af23f162d727b23` |
| `x86_64-apple-darwin` | `aa3a5fa52053f88a5b4062a5442554c7845eb2bd0333ffe6b7a26525b6976217` |
| `x86_64-unknown-linux-gnu` | `c3a74005f82b0013cf2a3c40312a7a4fe2dd53e9d37bf3107dbd8dd2fe7e078a` |
| `x86_64-pc-windows-msvc` | `c9f662533574d3c1e295322f0319d2070a25ca791b76442703b497d4258ce9c1` |

The four archive digests are the values published by GitHub for v2.31.3. Miller's restore scripts download
these exact assets and verify their checksums before the build guard accepts a binary.

## Verification

- The annotated `v2.31.3` tag resolves to producer commit
  `4e07f5e9da7b59e82fce95f7e36c661708a68574`.
- Producer main CI run `31421238301` passed before the stable four-platform release was published.
- The restored Linux binary reports `julie-extract 2.31.3` and matches the published archive checksum.
- The build-time pin guard, CLI capability contract, and `MillerExtractContract.PinnedJulieExtractVersion`
  agree on 2.31.3.
- Miller's fast, Scale, build, Windows CI, and package gates are recorded against the final release source in
  the v1.18.0 release verification evidence.
