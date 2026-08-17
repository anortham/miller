# Windows resolution slowness — investigation charter

- **Date:** 2026-08-17
- **Host:** native Windows
- **Workspace:** `C:\source\miller` (`miller-6662d0bd90fe`)
- **Binary:** published `1.19.4+10db3160e82b`
- **Producer:** pinned `julie-extract 2.33.5`
- **Source timings:** [`2026-08-17-windows-dogfood-1.19.4.md`](2026-08-17-windows-dogfood-1.19.4.md)
- **Owning plan:** [`plans/2026-08-13-miller-performance-recovery-plan.md`](../plans/2026-08-13-miller-performance-recovery-plan.md)

This is a follow-up investigation charter. It is not a resolver implementation.
**There is no resolver SQL or pin bump in the read-availability plan.**

## Ownership

Resolution wall time stays on the August 13 recovery plan. The
[`2026-08-17-windows-dogfood-read-availability-plan.md`](../plans/2026-08-17-windows-dogfood-read-availability-plan.md)
records these Windows numbers and keeps search and named inspect serving
while resolve runs. It does not change resolver SQL, crossover, base
rotation, or the julie-extract pin.

Task 1 of the read-availability plan is the user-visible relief: last-good
`search` and named `inspect` while the store is `converging` or `unbound`.
That work does not make resolve faster.

## Windows dogfood phases versus August 13 budgets

First `workspace refresh` on this box, then warm CLI reads after settle.
Phase times come from `indexer_phase_record` on that refresh.

| Work | Measured | August 13 Windows budget | Verdict |
|---|---:|---:|---|
| import | 23 s (`23348` ms) | none as a stand-alone gate | Record only. Import is not the slow arm. |
| resolve | 97 s (`96915` ms) | full resolution 120 s | Inside the full-resolution budget. Still too long to sit on the user-visible tool path. |
| coordinator | 121 s (`121570` ms) | none as a stand-alone gate | Import + resolve plus coordinator wrap. Same session as the 97 s resolve. |
| sidecar | 56 s (`56701` ms) | none as a stand-alone gate | Ran after the tool returned `queued`. Separate from resolve SQL. |
| one-file resolve | not measured here | 10 s | This refresh was a whole-workspace resolve, not the one-file gate. |
| warm inspect | 426 ms | 2 s | Met. |
| warm impact | 7234 ms | 5 s | Missed after settle. |
| warm context | 9501 ms | 5 s | Missed after settle. |
| warm trace | 1897 ms | 5 s | Met. |

The 97 s resolve is inside the 120 s constrained-Windows full-resolution
budget. It is still too long to block `search` or named `inspect`. The
read-availability plan already treats that wait as a serve-last-good
problem, not a resolver rewrite.

Warm `impact` (7234 ms) and `context` (9501 ms) missed the 5 s Windows
warm budgets after the index had settled. Those misses belong to the
August 13 relationship-query work (context evidence batching and impact
graph/SQL). They are not a producer resolve defect and they are not in
scope for the read-availability plan.

## What this plan must not do

- No resolver SQL or pin bump in the read-availability plan.
- Do not change crossover, base rotation, or identifier-resolution SQL.
- Do not raise the 4000 ms coordinator quantum as a Miller fix.
- Do not treat the 56 s sidecar converge as a reason to rewrite the
  producer resolver.

## Next owner

Keep new resolver work on
[`plans/2026-08-13-miller-performance-recovery-plan.md`](../plans/2026-08-13-miller-performance-recovery-plan.md):

1. Full real-Miller resolution versus the 60 s Linux / 120 s Windows
   budgets. This box's 97 s resolve is a new native-Windows data point
   inside the Windows budget, not a close-out.
2. One-file resolution versus the 5 s Linux / 10 s Windows budgets. This
   dogfood session did not run that gate.
3. Warm `context` and `impact` versus the 2 s Linux / 5 s Windows budgets.
   Use the 9501 ms and 7234 ms figures as the current native-Windows miss.

A later pin bump needs its own approval. This charter does not grant it.

## Profile of the 97 s resolve (2026-08-17, this box)

Read-only `coord.db` `requests.result_json` for committed `kind=resolve` rows.
No live store mutation. No second resolve replay.

The 97 s Miller `indexer_phase_record resolve` is **one** `julie-extract store resolve`
process. It is not a Miller N+1 and not a Miller connection-reuse defect.

| Request | Wall | Mode | Fallback | Files | Identifier rows | `resolution` | `scope` | `diff` | Phase sum |
|---|---:|---|---|---:|---:|---:|---:|---:|---:|
| `20b9772a` 12:08 UTC (the 97 s refresh) | 95.8 s | full | `resolution_scope_crossover` | 1533 | 582,172 | 58.1 s | 11.6 s | 2.0 s | 71.7 s |
| `b89977ef` 16:55 UTC | 88.9 s | full | `resolution_scope_crossover` | 1541 | 600,244 | 55.8 s | 8.0 s | 1.5 s | 65.2 s |
| `d176e5fe` 12:10 UTC | 34.6 s | scoped | none | 16 | 16,687 | 29.6 s | 3.3 s | 0.5 s | 33.5 s |
| `97b9fbfa` 17:06 UTC | 29.7 s | scoped | none | 45 | 57,826 | 22.3 s | 0.5 s | 0.5 s | 23.4 s |
| one-file scoped (8 samples, 12:32–12:39) | 5.5–6.8 s | scoped | none | 1 | 0 scoped rows | 0.9–1.1 s | 0.4 s | 0.3 s | 1.5–1.7 s |

`DELTA_SCOPE_CROSSOVER` is **0.7**. One changed file never promotes. More than one
file promotes to full when the scoped identifier estimate is at least 70% of the
visible identifier set (~810k on this store). Crossover then runs the **whole**
resolver, not 70% of it.

Unattributed wall on the 95.8 s run is ~24 s (95.8 − 71.7): process start, claim,
scratch databases, WAL I/O, and publish. The live `store.db-wal` was 3.4 GB beside
a 1.8 GB `store.db` when sampled.

One-file scoped resolve on this box **meets** the 10 s Windows budget.
The 97 s number is a crossover-full pass, not a typical save.

The `resolution` bucket is still a single timer around
`run_resolution_session`. Query counts inside that 58 s are not in the report.

## Profile inside `run_resolution_session` (2026-08-17)

Copied the family store to `%TEMP%\miller-resolve-profile` (db+wal only).
Checkpointed the **copy**. Did not write the live store.
Unbound the copy view so resolve would run a full pass instead of the exact short-circuit.
Local `julie-extract` 2.33.5 with `JULIE_RESOLUTION_PROFILE=1` (env-gated stderr timers).

Both copy runs used `resolution_mode=full` and
`fallback=resolution_prior_overlay_unavailable` (fresh scratch overlay).
That is not identical to the live crossover path, which still has a prior overlay.
It is the same `resolve_full` binder over ~600k identifier rows.

| Phase | Cold copy (112 s wall) | Warm copy (89 s wall) | Chunks | Items |
|---|---:|---:|---:|---:|
| `open_pass` | 0.06 s | 0.06 s | 0 | 0 |
| `prepare_shadow` | 0 | 0 | 0 | 0 |
| `resolved_pending` | 0 | 0 | 0 | 0 |
| `resolved_identifiers` | 0 | 0 | 0 | 0 |
| `pending` | **23.6 s** | **13.3 s** | 352 | 105,468 |
| `relationships` | 0.6 s | 5.3 s | 72 | 21,331 |
| `identifiers` | **71.9 s** | **54.2 s** | 1472 | **441,548** |
| `verify_shadow` / `aggregate_report` | 0 | 0 | 0 | 0 |
| report `resolution` timer | 96.2 s | 72.8 s | | |
| report `diff` | 0.8 s | 1.2 s | | |

The generic identifier chain (`ResolutionPhase::Identifiers`) is **75%** of the
resolution session on the warm copy. Pending resolve is the next cost.
Recheck of an existing overlay did not run here (scratch overlay was empty).

Warm rate: about **8,100 identifier items/s** and **7,900 pending items/s**.
Chunk size is the configured 300-row window (1472 × 300 = 441,600).

The next hunt, if we want one, is `resolve_identifier_items` (and then
`resolve_pending_items`), not Miller and not coordinator connection reuse.

## Query families inside `resolution` (warm copy, 89 s)

Same snapshot, `JULIE_RESOLUTION_PROFILE=1`. 441,548 identifier items.
Store has **810,279 identifiers and only 22,745 distinct names**.

| Family | Executions | Rows read |
|---|---:|---:|
| TopLevelNamed | 180,052 | 45,916 |
| FilteredByName | 141,236 | 41,456 |
| ChildrenNamed | 119,810 | 1,048,265 |
| SymbolById | 111,295 | 206,283 |
| TypeFacts | 97,968 | 38,521 |
| LocateIdentifier | 12,560 | 12,594 |
| FilteredNameSummary | 10,589 | 4,959 |
| Imports | 5,011 | 3,684 |
| IdentifierHydration | 1,472 | 441,548 |
| PrimeWindow | 352 | 97,014 |
| PendingHydration | 352 | 105,468 |
| RelationshipHydration | 72 | 21,331 |
| **scratch EXISTS** | **441,548** | (in-memory) |

About **681k store.db queries** for 442k identifier items. The candidate
window and resolution cache reset every 300-row page. Scratch EXISTS is
one per identifier and cheap (in-memory). The wall time is the store.db
round trips (~80 µs each).

Identifier pages only prime **children**, not names. Pending pages prime
names (`PrimeWindow` 352 = pending pages only).

Tried pending-style name prime on identifier pages: **worse**.
`identifiers` 57 s → 116 s. `PrimeWindow` read 538,566 symbol rows
(LIMIT 300 × 1,472 pages; hot names such as `Assert` fill the page).
TopLevelNamed/FilteredByName fell only ~30%. Reverted.

Next measured options (not done): persist file+name / type-id caches
across window reset without loading 300 symbols per name; or batch
lookups per page without the unbounded name JOIN.

## Mini-index implementation (2026-08-17, copy store)

Work lives in the julie-extractors branch
`fix/resolution-per-version-index`. It is not pinned into Miller.

A 2048-symbol cap covers every version that has identifier rows
(1119 / 1119). Larger versions are symbol-only and stay on SQL.

The first single-slot index was **worse**. File-local lookups hop
across versions, so one slot reloaded 72,423 times and read 37M
rows. `resolution` was 190 s.

A 256-slot LRU fixed the reload. Same copy, same unbound full pass,
`JULIE_RESOLUTION_PROFILE=1`. Two runs:

| Run | `pending` | `identifiers` | `resolution` | Mini-index loads | Mini-index rows |
|---|---:|---:|---:|---:|---:|
| Baseline warm copy (no mini-index) | 13.3 s | 54.2 s | 72.8 s | 0 | 0 |
| LRU first | 9.9 s | 33.4 s | **44.1 s** | 2409 | 429,366 |
| LRU after checkpoint | 19.6 s | 52.4 s | 72.8 s | 2409 | 429,366 |

Query counts were identical on both LRU runs. Wall time is noisy
on this box after a WAL checkpoint. The query drop is stable:

| Family | Baseline warm | LRU |
|---|---:|---:|
| TopLevelNamed | 180,052 | 0 |
| TypeFacts | 97,968 | 0 |
| Imports | 5,011 | 0 |
| SymbolById | 111,295 | 4,961 |
| ChildrenNamed | 119,810 | 9,606 |
| FilteredByName | 141,236 | 141,236 |
| VersionMiniIndex | 0 | 2,409 |

Workspace-wide `FilteredByName` did not move. That is the next
measured target (pass-lifetime name cache or a batched name
lookup). Do not raise timeouts. Do not change crossover first.

### One-file save

Live one-file scoped resolve on this box was 5.5–6.8 s wall, but
only 0.9–1.1 s sat in `resolution`. The rest is extract, claim,
and publish. Mini-index can only cut that 1 s slice.

A copy-store one-file update of `ScanIntent.cs` then resolve was
**not** that path. The copy had no usable prior overlay, so
name-scope expanded to 86 files / 144,546 rows and
`resolution` was 56 s (`resolution_mode=scoped`). Treat the live
5.5–6.8 s number as the save-path baseline, not this copy run.

## Whole-pass name cache (2026-08-17, copy store)

Added on top of the 256-slot file index. Same unbound full pass.

Most remaining name searches asked "is this type name unique in the
repo?" and walked rows each time. That path now uses the count query
and keeps the answer for the whole pass. Complete small name lists
are also kept.

| Family | Mini-index only | Plus name cache |
|---|---:|---:|
| FilteredByName (walk) | 141,236 | **0** |
| FilteredNameSummary (count) | 9,982 | 9,195 |
| `pending` | 9.9–19.6 s | 15.1 s |
| `identifiers` | 33.4–52.4 s | 50.5 s |
| `resolution` | 44.1–72.8 s | 66.4 s |

The walk queries are gone. Wall time is still noisy after a
checkpoint. The remaining cost is child-name SQL (~9.6k queries /
1.0M rows), file-index loads (2,409), and identifier hydration.

## Related notes

- Parent dogfood list: [`2026-08-17-windows-dogfood-1.19.4.md`](2026-08-17-windows-dogfood-1.19.4.md) §2 and the post-settle table
- Separate stall, not slowness: [`2026-08-17-julie-extractors-windows-lock.md`](2026-08-17-julie-extractors-windows-lock.md)
- Linux full-resolve baseline still over its 60 s budget:
  [`2026-08-14-performance-recovery-baseline.md`](2026-08-14-performance-recovery-baseline.md)
