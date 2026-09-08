# julie-extract 2.42.0 pin adoption

Miller now pins the public [`julie-extract` 2.42.0 release](https://github.com/anortham/julie-extractors/releases/tag/v2.42.0).
GitHub published the non-draft, non-prerelease release on 2026-09-08. Annotated tag `e379c0a7`
resolves to commit `3868ed8c`, which was also the producer repository's `origin/main` when qualified.

## Published assets

Each downloaded archive matched its GitHub digest, contained the expected
`dist/<triple>/julie-extract[.exe]` path, and passed its embedded checksum.

| Target | Archive SHA-256 |
|---|---|
| `aarch64-apple-darwin` | `7d089ffa66940ff1c8607f283f7d93a26929028be70912934d7d4d0b395096bf` |
| `x86_64-apple-darwin` | `a075769a36ddecdd8c697da80eb999e33e2ea7582c179731757a8818eb18ac4f` |
| `x86_64-unknown-linux-gnu` | `100c99c8ef1eccfb226b26111ae5293de5122632bcfe71f02d3808f582aa6b59` |
| `x86_64-pc-windows-msvc` | `77ab5bc243ec7afef72878d1f20f51df68fd1d37cafba1dbbba205ef61b5820a` |

Miller's restore script independently verified the Linux archive. The installed and Release-output
binaries report `julie-extract 2.42.0` and have binary SHA-256
`a9b931384227eb2a9fd02e3dd2f85ba3ad34101e3322943386f64f6743b91686`.

## Compatibility and changed behavior

The SQLite artifact schemas remain 7, extract contract 4, report schema 3, JSONL schema 5,
family-store schema 2, store format epoch 1, and extraction identity epoch 10. A source diff across
the producer's schema and store-layout files was empty, and the producer compatibility job reported
all 18 extraction tables byte-identical to the public 2.41.1 binary.

Fresh 2.42.0 families stamp `min_reader_version=2.42.0`. Existing families retain their prior reader
floor. Miller's independently qualified reader capability therefore advances to 2.42.0 and refuses
2.42.1 and 2.43.0 floors.

The producer now overlaps deep-chunk extraction with store writes. Nonterminal
`store_import_l3_chunk` events may include an additive numeric `prefetched_files` field. Miller's
`StoreLogCursor` excludes nonterminal `*_chunk` rows, so this progress detail cannot advance Miller's
revision or freshness cursor. Miller always supplies an explicit bounded `--jobs` value, so the
release's changed `jobs=0` default does not change Miller's extraction parallelism.

The release uses SQLite 3.53.2. Miller keeps its independently pinned SQLitePCLRaw 3.53.4 runtime.

## Producer qualification

- [Upstream CI run `34269186515`](https://github.com/anortham/julie-extractors/actions/runs/34269186515)
  passed, including the 2.41.1 compatibility comparison.
- [Release run `34272367874`](https://github.com/anortham/julie-extractors/actions/runs/34272367874)
  passed all four platform builds and release verification.
- Focused committed-report, source-change prefetch fallback, busy-lock diagnostics, SQLite engine,
  and mixed-version reader-retention tests passed at producer commit `3868ed8c`.

## Consumer qualification

The first isolated Miller probe confirmed a consumer-side self-lock bug. An explicit-ID blocking read
repeatedly returned `unconfirmed_lock_busy` and named its own Miller PID while workspace status was current.
This proves that defect, but it does not prove the customer's earlier `refresh_pending` report had the same
exact cause. The extractor upgrade alone does not fix Miller freshness reporting.

Miller now routes an explicit ID for its bound root through the resident leader scan and freshness poll.
Different roots retain the cross-workspace path, `ensure_fresh=false` does zero refresh work, and queued or
failed scans remain unconfirmed. The finished background result retains the exact family-store generation
identity needed for `IndexFresh=true`.

The post-fix Release probe used family `0bd1dd3b`, view `77a374d7`, generation `gen-001`. A blocking
`ensure_fresh=true` search returned `freshness: unchanged`. After two source renames, exact search results
tracked the new TypeScript symbols. The default background read first reported queued/unconfirmed, then the
completed resident action settled to `unchanged` on a second call two seconds later. That delayed probe also
found that the background action captured a disposed dependency-injection scope. The registration now captures
the immutable tools root before scheduling work. Final status at store-log sequence 33 reported
`built=latest=33`, `index_fresh=true`, `queue_empty=true`, and all sidecars current at 33.

The same qualification found that content search could report `more_may_exist=false` after an FTS candidate
window saturated and post-filtering removed rows. Miller now propagates the saturation flag and keeps
`more_may_exist=true` even when post-filtering returns fewer rows than the requested limit.

### Reader-process freshness follow-up

An independent two-process check found a second Miller-side defect after the resident fix. A reader that lost
the writer lock did not ask the leader to reconcile source; it only waited for an uncorrelated revision advance.
That could never confirm an unchanged scan and could mistake unrelated work for the requested reconciliation.
The pre-fix run returned `unconfirmed_lock_busy` after the default background wait, returned `lock_busy` for an
explicit blocking read, and served the old TypeScript symbol after an `alpha` to `beta` source edit.

Miller now uses the existing leader request directory for a nonce-bearing `incremental_reconcile` request. The
leader atomically captures a bounded batch, performs one automatic delta scan under the existing backoff and
admission rules, and publishes a completion only with the post-scan generation/revision witness. The reader
accepts success only when its independently read served witness exactly matches that completion, including the
unchanged-revision case. Default background calls retain a timed-out nonce for later pickup; explicit blocking
calls create a fresh nonce after the invocation begins. Queued and not-leader attempts retain the same request;
failed, mismatched, corrupt, and expired results remain unconfirmed and cannot stamp an old snapshot fresh.

The independent pre-fix fixture used leader PID 2034799 and reader PID 2036888 on one isolated family at
revision 6. Its first default read queued, the read two seconds later was
`unconfirmed_lock_busy/background: finished`, and blocking reads stayed lock-busy. After an
`alphaProbe` to `betaProbe` edit, the blocking read missed beta and served the stale alpha near match.

The final nonce-corrected fixture paused only its disposable leader, allowed the reader's background request
to time out as queued, edited alpha to beta, and issued a fresh blocking request. After the leader resumed,
the blocking read returned `refreshed` with exact beta and no exact alpha. No-edit blocking returned
`unchanged`. After the disposable leader exited, a beta to gamma edit still returned `refreshed` with
exact gamma and no exact beta. The reader finished fresh with an empty queue.

Default reads also settled within the five-second background cooldown. The retained result was
`refreshed/background: finished` at revision 12. A later read queued revision 14, and the next observation
returned `unchanged/background: finished` at revision 14. A subsequent cooldown-expired read legitimately
started another cycle; the status was not permanently pending.

Both the leader and reader must restart onto the corrected build. A reader with the new protocol cannot make
an older live leader service an operation it does not understand, and Miller does not kill or force a handoff
from user processes. Every qualification server used a private home, registry, workspace, and family. The
harness stopped all children, copied JSON summaries and raw logs under
`/home/murphy/.cache/miller-dogfood/adoption-2.42.0/`, then deleted the disposable roots and sidecars.

The alternatives were rejected because a live leader does not prove source reconciliation, a newer DB revision
does not correlate with the caller's request, a leadership handoff would disrupt user processes, and a forced
full rebuild changes the requested operation and its backoff/downgrade contract. The protocol is language-neutral
consumer coordination; julie-extract schemas and per-language extraction remain unchanged.

## Miller verification

- `dotnet build Miller.slnx -c Release`: zero warnings and errors.
- Pin, schema, reader-floor, store-log cursor, and version-probe scope: 185 passed, zero skipped or failed.
- Content, guidance, and external-store focused scope: 214 passed, zero skipped or failed. Plugin manifest
  checks passed 14 of 14.
- Resident freshness and served-snapshot scope: 37 passed, zero skipped or failed. The retained reader
  factory after dependency-injection scope disposal passed its focused regression.
- Reader-to-leader protocol and caller scope: 694 passed, one expected skip, zero failures.
- Fast suite: 10,457 passed, nine expected skips, zero failures.
- Scale suite: 233 passed, 38 expected skips, zero failures.
- `miller capabilities --json` reports pin 2.42.0 with schema contracts 7/7/4/3/5.
- The running 2.41.1 Miller family was not reset, rebuilt, or restarted during adoption.

Full final-gate logs are under `/home/murphy/.cache/miller-dogfood/adoption-2.42.0/`.
Miller was not released or pushed by this adoption.
