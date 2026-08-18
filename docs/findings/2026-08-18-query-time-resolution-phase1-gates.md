# Query-time resolution Phase 1 gates

Date: 2026-08-18. Pin: `julie-extract 2.33.7`. Branch: `worktree-query-time-resolution`.
HEAD at measurement: `f27cbe1b` plus this task's Scale/docs commit.

Ground truth: pinned `julie-extract store import` + `store resolve`, then
`identifier_resolutions` + max-generation delta overlay (and pending replace/tombstone
overlay). Query-time uses `RevisionFactCache` + `QueryTimeResolver` (policy v6), including
pending then relationship span propagation.

## Hard gates

| Gate | Command | Result | Verdict |
|---|---|---|---|
| Fixture parity | `dotnet test --filter FullyQualifiedName~StoreExtractAndResolve_QueryTimeMatchesJulieGroundTruth` | identifiers 11/11, pendings 3/3, 0 under-resolved, 0 unexplained; graph tuples matched reconstructed store edges; evidence/export labels `identifier_resolution` and were ordered/deduped | **PASS** |
| Warm refs p95 at aspnetcore | `dotnet test --filter FullyQualifiedName~AspnetcoreSnapshot_ParityWarmP95AndMemory` on `/tmp/qtr-aspnet-snapshot/` | p50 0.1 ms, **p95 96.8 ms**, max 1064.5 ms (160 names: top-40 fan-out + 120 random) | **PASS** (≤500 ms) |
| Whole-host memory at aspnetcore | same test, after cache load + one query pass | idle **332.7 MB PSS**, peak **494.8 MB PSS**; idle RSS 377.8 MB, peak RSS 539.8 MB; `GC.GetTotalMemory` 290.0 MB; cache estimate 134.7 MB | **PASS** (idle ≤350, peak ≤600) |
| Save-to-correct-answer | same fixture test: `store update` of one file, no resolve, then `ReferenceEvidenceReader` | **29.9 ms** | **PASS** (≤5 s) |

`RevisionFactCacheMemoryTests.LoadSnapshot_StaysWithinWholeHostMemoryBudgets` on the same
aspnetcore snapshot: idle 336.3 MB PSS, peak 445.8 MB PSS. Same verdict.

## Snapshot parity (local-only, skip if the directory is absent)

| Corpus | Path | Identifiers | Pendings | Under-resolved | Unexplained |
|---|---|---:|---:|---:|---:|
| Miller | `/tmp/qtr-spike-snapshot/` | 475,377 / 475,377 | 105,935 / 105,937 | 2 pendings | 0 |
| aspnetcore | `/tmp/qtr-aspnet-snapshot/` | 2,152,928 / 2,152,935 | 479,202 / 479,204 | 7 identifiers + 2 pendings | 0 |

Every under-resolution is store `missing` and query-time `resolved`. That is the producer
mini-index cap pattern from the spike. No other divergence class appeared.

Aspnetcore identifier under-resolutions (all `tier3_static_type` 0.70):

- `NotifyLocationChanged`, `NotifyLocationChangingAsync` (version 1863)
- `Get` (7030), `Post` (7548), `CustomConventionMethod` (9033)
- `parse` × 2 (13556) plus the two matching pending rows

Miller pending under-resolutions: two `Scan` rows at versions 2152 and 2153, query-time
`tier3_receiver` 0.65.

## Report-only

| Measurement | Miller snapshot | aspnetcore snapshot |
|---|---:|---:|
| Cold cache load | 1.85–2.44 s | 7.88 s |
| Cold first-query | 332–340 ms | 790 ms |
| Full-sweep time | 5.46–5.54 s | 23.1 s |
| Peak RSS during cache+query | 536.0 MB (shared process) | 539.8 MB |
| Cache resident estimate | 29.9 MB | 134.7 MB |
| Warm p50 / p95 / max | 0.1 / 23.4 / 199.5 ms | 0.1 / 96.8 / 1064.5 ms |

Multi-workspace eviction (`RevisionFactCacheStore` at 100 MB): Miller cache 29.9 MB, then
aspnetcore load evicted it. Scope count 1. Resident 134.7 MB.

Pinned `julie-extract store --help` still lists `resolve`. The Scale parity test skips with
that reason when a later pin drops the verb.

## Commands

```bash
# fixture extract + resolve + query-time parity
dotnet test --filter "FullyQualifiedName~LiveReferenceResolutionScaleTests"

# frozen snapshots (skip when the directory is missing)
dotnet test --filter "FullyQualifiedName~QueryTimeResolutionSnapshotScaleTests"

# cache-only aspnetcore memory
dotnet test --filter "FullyQualifiedName~RevisionFactCacheMemoryTests"
```

## Concerns

RESOLVED 2026-08-18 (post-gate review fixes): the legacy-artifact arm now keys versions
by `files.rowid` instead of `CAST(file_id AS INTEGER)`, so text `file-<hash>` ids no
longer collapse multi-file artifacts into one slice. The same review pass restored
`reference_sites` as the source for `site_provenance`, `is_exact`, span columns, and
the containing symbol on the evidence and export surfaces (both arms), removed the
fabricated pending span constants, and dropped export relationship rows whose target
symbol row is absent — matching the retired SQL. `QueryTimeResolutionParity.SerializeExport`
now serializes the full documented column set, so export column shape is gated.

## Verdict

All four hard gates **PASS**. Phase 1 query-time resolution matches julie 2.33.7 ground
truth on the fixture and on both frozen snapshots, with only the known producer
under-resolutions.
