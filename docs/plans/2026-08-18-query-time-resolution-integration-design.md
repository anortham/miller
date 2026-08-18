# Query-time resolution integration design

Date: 2026-08-18. Status: DRAFT — user approved the direction ("this is going to have to change");
implementation waits on the aspnetcore-scale spike gates. Evidence:
[whole-stack assessment](../findings/2026-08-18-whole-stack-architecture-assessment.md),
[spike results](../findings/2026-08-18-query-time-resolution-spike.md)
(branch `prototype/query-time-resolution`), active goldfish brief.

## Goal

- A save reaches a correct, queryable index in ≤5 s (extraction + import only; no resolve phase).
- A cold open is bounded by extraction + import, not by an hours-scale first resolve.
- References/impact/trace answers stay byte-compatible in meaning: same targets, same confidence
  semantics, same evidence gating.
- The resolve subprocess, resolution bases (~110 MB each), deltas, rebase policies, validated-base
  proofs, and the scope journal are retired.

## Non-goals

- No change to extraction, the manifest/file-version model, search/content sidecars, or the
  semantic sidecar. No new MCP tools. Lexical-only output stays byte-identical.

## Design

### 1. Resolver core (Miller.Core, pure logic)

Port the spike's resolver as `Miller.Core.Resolution.QueryTimeResolver`: julie's tier policy
(RESOLUTION_VERSION 6) — tier chains per (origin, kind, receiver), tier 1 scope walk, tier 2
import-guided (ts/js), tier 3 receiver/static-type, tier 4 unique-language-global, propagation
precedence (pending, then relationships, span-located, exactly-one rule). The extracted spec
(currently in the spike README lineage) is vendored as
`docs/contracts/resolution-policy-v6.md` so both repos share one written policy.

The spike proved this policy is a pure function of the fact tables (100.000% parity on 475,377
identifiers). Parity against the julie resolver becomes a contract test fixture, not a live
dependency.

### 2. Fact cache (Miller.Indexing, per index identity)

A resident `RevisionFactCache` keyed by the existing index identity (generation + manifest hash),
holding only the SMALL tables, interned and packed:

- symbols: name-interned, kind as enum, int-indexed arrays; by-name, by-parent, top-level indexes
- type_facts, import bindings (parsed once), pending records
- the propagation index (built once per identity; file-local, so a revision delta patches it)

Identifiers — the biggest table — are NOT resident. A refs query streams that name's sites from
`identifiers(name, kind, version_id)` (index already exists) through the resolver. The spike's
naive 719 MB becomes an estimated <150 MB resident at Miller scale; the 350/600 MB budgets gate it.

Invalidation is file-local: a revision delta swaps one version's symbols/facts/imports and its
propagation entries. No global pass exists anywhere in the maintain path.

### 3. Read-path swap (Miller.Indexing.Reads)

`FamilyStoreReadSession`'s resolution TEMP views (base ATTACH + delta overlay) are replaced by
resolver calls producing the same tuples (`identifier_target`, `pending_resolution`,
`relationship`, and the existing unresolved-name fallback). `reason`/confidence semantics are
unchanged, so every downstream consumer (context, impact, trace, references candidates, JSON
contracts) is untouched.

Rollout: direct replacement, no legacy mode. USER DECISION 2026-08-18: "remove the old way
without waiting a release" — the store-resolution read path is deleted, not flagged off. The
current speed breaks the product, so there is nothing worth falling back to. Gates below still
run before the release ships.

### 4. Producer changes (julie-extractors, later phase)

Phase 1 needs ZERO julie changes: Miller stops submitting `StoreResolveRequest`. Saves become
import-only; `views.resolution_state` stays non-exact and nothing reads it. Phase 2 follows
immediately (no soak window — user decision): julie stops writing resolution artifacts, drops the
resolution tables/bases in the next schema bump, and deletes the resolver session, rebase, proof,
and scope-journal code. The store shrinks by the bases (~1.5 GB on the miller repo) and loses its
worst WAL churn. This also restores the intended ownership boundary: julie-extract is a
file-local extractor; workspace-global semantics live in the serving layer.

### 5. Dead-code candidates are REMOVED (user decision 2026-08-18)

Dead-code candidacy is the one product that needs the full edge set ("nothing references X" is a
whole-graph claim). Rather than keep a full-corpus sweep alive to serve it, the feature is
removed: the `references candidates` CLI surface, `docs/contracts/references-candidates-v1.md`
(marked retired, not deleted), the `dead_code_candidate_count`/suppressed-total trend counts on
the dashboard, and their history metrics. Recorded history rows stay readable; new snapshots stop
recording those metrics. Rationale: the user judges dead-code detection better served by an
LSP-class tool, and no other surface needs a whole-graph sweep — impact, trace refs, and trace
path are all per-name or per-hop queries.

### 6. What this deletes from the failure surface

Every resolve-class incident from the last two weeks becomes structurally impossible: the 100.3 s
accumulated-delta incident, the >600 s scoped timeout, crossover blowups, ubiquitous-name scope
expansion, stranded resolve claims, the 4 s coordinator quantum interacting with resolve, the
resolve share of Windows slowness, and the multi-GB `store.db-wal` growth on doc saves.

## Gates (before the default flips to `query`)

1. Parity sweep 100% (or every divergence explained and accepted) on the miller store AND the
   aspnetcore-scale store.
2. Warm refs query p95 ≤500 ms at aspnetcore scale, worst fan-out names included.
3. Resident cache ≤350 MB idle at aspnetcore scale.
4. Save-to-correct-answer ≤5 s measured end-to-end on this machine with resolve submission off.
5. Fast suite green; scale suite green; no read-contract JSON changes.

## Independent fix (do regardless)

julie-extract cold import fails on any repo with C++-flavored `.h` files: discovery/manifest uses
extension-only classification (`detect_language_from_extension` → `c`) while extraction sniffs
content (`detect_language_for_source` → `cpp`); `store_import_publish_manifest` then rejects the
mismatch after the full import has run (111 s wasted on aspnetcore, view left unbound and
unreadable — the wedge also made the next open report `ineligible_extractor`). Fix in
julie-extractors: manifest entries must carry the file-version's language (or discovery must use
the same content sniff). Workaround until then: `.julieignore` with `*.h`.

## Decisions (user, 2026-08-18)

1. No fallback window. The old resolution path is removed outright in both repos; phase 2 follows
   phase 1 immediately. "It's so slow it breaks the whole product."
2. The dead-code report is removed rather than maintained by a batch sweep; dead-code detection
   is delegated to LSP-class tooling. No whole-graph sweep survives anywhere in the system.

## Estimate (agent work)

- Phase 1 (resolver port + fact cache + read-path swap + gates): 2–3 focused sessions.
- julie language-classification fix + release: 1 session.
- Phase 2 (julie store slimming + schema bump + release): 1–2 sessions, after the soak window.
