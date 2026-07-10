# julie-extract 2.12 Test-Evidence Consumption Design

**Date:** 2026-07-10
**Status:** Approved design; implementation planning pending
**Scope:** Miller consumption of `julie-extract` 2.12.0. No Eros code changes, Miller release, push, or new MCP tool.

## Goal

Update Miller from `julie-extract` 2.11.0 to the live 2.12.0 release and consume the release's test-evidence contract without inventing runner inventory, semantic completeness, or verdict logic.

The completed slice must:

- restore and verify the released 2.12.0 binary for every packaged target;
- preserve `symbols.is_test`, `symbols.test_container`, and `symbols.test_lifecycle` from SQLite through Miller's indexed-symbol, default search-sidecar, and public-rendering paths;
- expose exact role evidence through the Eros-facing `impact --json`, revision-delta JSON, and `symbols export --jsonl` contracts;
- prove that `workspace health --json` carries `kind_coverage.test_detection` without adding a second capability reader;
- retain all existing compatibility fields and uncertainty boundaries; and
- verify the released binary against Razor, Vue, lifecycle/container cases, and the upstream cross-language capability matrix.

## Upstream Facts

The live [v2.12.0 release](https://github.com/anortham/julie-extractors/releases/tag/v2.12.0) is stable, not a draft or prerelease, and targets commit `3f00c928d056919cc86dcce79b0751eedc9a3767`.

The [test-evidence-v1 contract](https://github.com/anortham/julie-extractors/blob/v2.12.0/docs/contracts/test-evidence-v1.md) defines:

- `test_case`: `is_test = 1` and `test_lifecycle = 0`;
- `test_container`: `test_container = 1`;
- `test_lifecycle`: `test_lifecycle = 1`;
- capability evidence at `kind_coverage.test_detection`; and
- negative-claim gates over capability support, indexed file status, parse diagnostics, and intended artifact/snapshot identity.

SQLite remains schema 4, `extract_contract_version` remains 3, report schema remains 3, and JSONL remains schema 3. The 2.12 capability object is additive.

Published asset digests:

| Target | SHA-256 |
| --- | --- |
| `aarch64-apple-darwin` | `249ed102deece8841c2965d7ad370ef08e63a82d093315a21f374a4457e57812` |
| `x86_64-apple-darwin` | `29ce60fbfc96d636eb1500df3d563c8739dd7bf1ef8097f00bda531c6ca467b5` |
| `x86_64-pc-windows-msvc` | `b4c428bc25638381e9ad46603cc3f30cd5ebb0065f0df83134afdda43b6df9ef` |
| `x86_64-unknown-linux-gnu` | `578946c36965e80407a26f774ea730c0bce9bd536b20ce7e46e96098ed3006a2` |

## Current Miller Gap

Miller already has schema-v4 fixture columns for all three flags and generically parses every `kind_coverage_json` domain for `workspace health`. Therefore the new `test_detection` capability domain requires a regression test and documentation, not a parallel production reader.

The real loss occurs in the symbol path:

1. `SqliteSymbolReader` reads only `is_test`.
2. `IndexedSymbol` carries only `IsTest` and no per-file currency evidence.
3. The default-on `search.db` sidecar persists and rehydrates only `is_test`; adding fields solely to `SqliteSymbolReader` would work with `MILLER_SEARCH_SIDECAR=0` but silently lose them in the shipped default path.
4. `ImpactTool` partitions reached nodes only on `IsTest` and cannot expose container/lifecycle or source-currency evidence.
5. `SymbolExportReader` emits only `is_test`, so Eros's bulk symbol path cannot consume the new roles or distinguish current rows from preserved/diagnostic-affected evidence in a later change.

A version-only pin would pick up Razor/Vue extraction fixes and the generic health row, but it would continue dropping the typed role distinctions at Miller's public boundaries.

## Architecture

### Indexed role and source evidence

Add one small immutable `TestRoleEvidence` value in `Miller.Indexing` with raw producer flags:

- `IsTest`
- `IsContainer`
- `IsLifecycle`

and one derived property:

- `IsCase = IsTest && !IsLifecycle`

The derivation exactly follows test-evidence-v1. Miller must not derive roles from names, paths, annotations, frameworks, or runner configuration.

The value also carries source currency:

- `Status`: `current` only when the owning file has `files.status = indexed` and no relevant `parse_diagnostics` rows; otherwise `unknown`.
- `Reason`: `null`, `file_status`, `parse_diagnostics`, `file_status_and_parse_diagnostics`, or `file_evidence_unavailable`.

`SqliteSymbolReader` derives that status from the same artifact using a bounded per-file lookup/aggregation, then `IndexedSymbol` exposes the value through compatible trailing/defaulted fields. The full `Read` path and incremental `ReadForPaths` path must share the same projection/derivation so single-file sidecar convergence cannot default or drop role evidence. The representation must share or compact per-file evidence rather than allocate one reference object per symbol; Miller routinely loads hundreds of thousands of symbols.

The default-on symbol search sidecar is the production lookup source for impact. Bump `SearchIndexWriter.SchemaVersion` and add compact columns for `test_container`, `test_lifecycle`, evidence status, and evidence reason to `search_symbols`. `SearchIndexWriter` persists the value from `IndexedSymbol`; `FtsSymbolSearchIndex` selects and reconstructs it. The existing schema-version freshness gate makes an old sidecar rebuild even at the same extract revision, so this remains a derived-artifact change rather than a migration of user-owned data.

Do **not** add typed roles to `GraphNode` or `SymbolGraph`. Both impact paths use graph reach only for `(symbol_id, hop)`, then rehydrate the full `IndexedSymbol` through `SymbolLookupBatch` from either the in-memory index or default search sidecar before partitioning and rendering. The graph's existing boolean remains for compatibility callers, but widening it would duplicate unused per-node data and add a false Core test surface.

Existing boolean behavior remains intentionally unchanged for search exclusion, bridge suppression, dead-code suppression, and other compatibility callers. Those surfaces ask whether a symbol is test-related; they do not claim runner-selectable test cases.

### Impact JSON

Both normal `impact --json` results and revision-delta `impact --json` results add this object to every reached row in `impacted[]` and `tests[]`:

```json
{
  "test_evidence": {
    "is_test": true,
    "test_case": false,
    "test_container": false,
    "test_lifecycle": true,
    "status": "current",
    "reason": null
  }
}
```

The object reports producer evidence plus whether that evidence is current enough for positive use. `status=unknown` means even a positive role flag may be preserved or diagnostic-affected evidence and must not authorize scheduling by itself.

Every result-bearing normal or revision-delta impact JSON object also adds:

```json
{
  "test_evidence_scope": {
    "status": "candidate_only",
    "absence": "unknown"
  }
}
```

This object remains present when the reached arrays are empty, so absence cannot be mistaken for a gate-satisfying negative result. Miller does not evaluate runner inventory, role capability completeness, or semantic impact completeness on this surface.

For compatibility, membership and ordering of the existing `impacted[]` and `tests[]` arrays do not change in this slice. In particular, a lifecycle row with `is_test = 1` remains in the legacy `tests[]` partition, but its `test_case` value is false. New consumers must use `test_evidence.test_case` rather than count `tests[]` as runnable cases.

The existing revision-delta top-level fields, traversal object, `returned_count`, and `reached_count` remain unchanged. Compact output remains unchanged for compatibility. Its legacy `likely tests` section may therefore include lifecycle hooks and other test-related, non-runnable symbols; the contract must name that caveat explicitly.

Advertise a separately gated feature named `impact_test_role_evidence` and a JSON contract named `impact_test_role_evidence` at schema version 1. The gate covers the additive nested object on both impact JSON forms. It does not imply completeness.

### Symbol export

`miller symbols export --jsonl` remains schema version 1 and adds these fields to every row:

```json
{
  "test_case": false,
  "test_container": false,
  "test_lifecycle": true,
  "test_evidence_status": "current",
  "test_evidence_reason": null
}
```

`is_test` remains present and unchanged. Source currency is derived from `files.status` and relevant `parse_diagnostics` in the same artifact; no filesystem reread or semantic inference occurs. The export is already parsed as an extensible object by Eros, so the new fields are additive and old consumers can ignore them. The feature/contract documentation must identify symbol export as a second carrier of the exact same evidence.

### Capability and uncertainty evidence

`WorkspaceHealthReader.ParseKindCoverage` already accepts arbitrary domains and `WorkspaceRender.WriteKindCoverageJson` writes them generically. Keep that seam and add tests proving `test_detection` survives from fixture JSON to `workspace health --json` with `supported`, `not_applicable`, and structured `open_gaps` intact.

Current health output aggregates file status and parse diagnostics; it is not a per-path eligibility join. Therefore the impact contract must not describe `workspace health` alone as gate-satisfying. Per-row source currency prevents stale positive evidence from looking current, while `test_evidence_scope.absence=unknown` prevents empty results from becoming negative claims. A future Eros consumer must combine these Miller facts with its intended artifact/capability snapshot and runner inventory before scheduling or verdicts.

Do not join capability rows, file status, or parse diagnostics into a new impact verdict. The public docs must say:

- positive role flags are usable evidence;
- an empty role set or graph candidate set is not proof that no tests exist or are impacted;
- absence remains unknown when capability evidence, indexed status, parse health, or artifact identity is insufficient; and
- Eros owns runner inventory, freshness, scheduling, results, and verdicts.

## Compatibility

- No julie-extract SQLite, extract-contract, or report-schema migration.
- Miller's derived `search.db` schema advances by one version. A schema-stale sidecar rebuilds automatically through the existing revision-plus-schema freshness gate; no in-place migration or user-data conversion is added.
- No new MCP tool or parameter.
- Existing `impact` top-level arrays, array membership, ordering, compact text, traversal evidence, and counts remain compatible.
- Existing `symbols export` fields and ordering remain compatible; new fields are additive.
- Existing callers that construct `IndexedSymbol` without typed role/source evidence keep their current behavior; `GraphNode` and `SymbolGraph` are unchanged.
- Older artifacts that pass Miller's schema-v4 gate still have the typed role columns; role flags default false where no producer evidence exists.

## Error Handling

- The build-time pin guard must fail if `.tools/julie-extract --version` does not match 2.12.0 after the pin changes.
- Restore must verify the selected release archive against its published digest.
- Missing/incompatible artifact columns remain a schema-gate failure; do not silently parse metadata JSON as a fallback.
- A revision-current but schema-stale `search.db` must rebuild before reads; the reader must reject a stale sidecar rather than default role fields to false.
- Malformed `kind_coverage_json` continues through the existing health-reader unavailable/warning path; do not reinterpret malformed evidence as an empty capability.
- Unknown or absent role evidence stays false/unknown. `failed_preserved`, non-indexed, diagnostic-affected, or missing file evidence never renders as current and never becomes a negative completeness claim.

## Testing Strategy

### TDD scopes

1. Pin assertions fail against the old 2.11.0 pin, then pass after all pin surfaces move together.
2. `SqliteSymbolReader.Read` and `ReadForPaths` prove identical raw flags, exact `IsCase` derivation, and `current|unknown` source status for indexed, preserved/non-indexed, diagnostic-affected, combined, and missing-file evidence.
3. Search-sidecar writer/reader tests prove exact role/source-status round-trip, revision-current schema-stale rebuild, stale-reader rejection, full-build sidecar-on/off impact parity, and delta-applied parity after `ApplyFileChanges` replaces a changed file containing case/container/lifecycle symbols.
4. Normal and revision-delta impact JSON prove exact nested fields and always-present candidate-only/absence-unknown scope while preserving existing arrays/counts.
5. Capability negotiation gates the feature and JSON contract independently.
6. Symbol export proves deterministic additive role/source-status fields and unchanged v1 ordering.
7. Workspace health proves exact `test_detection` capability passthrough, including open-gap metadata.
8. A graph regression test proves the implementation did not widen `GraphNode`/`SymbolGraph` or change reach behavior.

### Released-binary and language-parity evidence

After restoring the 2.12.0 binary:

- assert `.tools/julie-extract --version` reports 2.12.0;
- run a real extract containing Razor `[Fact]`, Vue `<script>`/`<script setup>` call-style cases, a test container, a lifecycle hook, and negative controls;
- query the artifact by language and role:

```sql
SELECT language,
       SUM(CASE WHEN is_test = 1 AND test_lifecycle = 0 THEN 1 ELSE 0 END) AS test_case_count,
       SUM(test_container) AS test_container_count,
       SUM(test_lifecycle) AS test_lifecycle_count
FROM symbols
GROUP BY language
ORDER BY language;
```

- verify `julie-extract languages --json` classifies every `test_case`, `test_container`, and `test_lifecycle` cell exactly once across `supported`, `not_applicable`, and `open_gaps` for every published language;
- verify every language appearing in the real extracted fixture corpus is represented in the role-count query; and
- capture positive and negative evidence without treating zero counts as completeness.

Because this work changes the extractor pin and real extract path, run:

- focused unit filters for the affected readers, graph, impact, health, capabilities, and CLI export;
- `scripts/test.sh`;
- `scripts/test.sh scale`;
- `dotnet build Miller.slnx -c Release` with zero warnings/errors; and
- current-binary capability, export, health, and impact JSON assertions.

## Architecture Quality

**Affected modules:** `Miller.Indexing` symbol/health/export readers, `SearchIndexWriter`, `FtsSymbolSearchIndex`, `SymbolSearchSidecar`, `Miller.Server.Tools.ImpactTool`, CLI capabilities, tests, and public contracts. `Miller.Core.Graph` is explicitly unchanged.

**Caller-facing interface:** one nested `test_evidence` value on impact rows, an always-present candidate-only scope object, five additive symbol-export fields, and one negotiated capability/contract name.

**Depth/locality check:** producer flags and per-file source currency are translated once in Indexing; the derived search sidecar preserves the same compact value for production rehydration; impact and export render the same semantics. Capability JSON stays behind the existing generic health seam. Graph reach remains an ID/hop concern.

**Test surface:** SQLite reader, graph, `impact --json`, revision-delta JSON, `symbols export --jsonl`, `workspace health --json`, and `capabilities --json`—the same interfaces callers use.

**Seams/adapters:** no new interface or adapter. The existing SQLite readers and renderers remain the protocol boundaries.

**Rejected shortcuts:** pin-only consumption; metadata/name/path reclassification; widening the graph with unused role data; bypassing or silently degrading the default search sidecar; changing legacy array membership; prose-only uncertainty; duplicating the health capability reader; adding a completeness engine; adding an MCP tool; modifying Eros in the Miller branch.

**Architecture risk:** medium. The change crosses Indexing, a rebuildable search-sidecar schema, and public JSON and adds per-file evidence aggregation, but keeps the new invariant in one value, avoids Core graph expansion, and preserves existing callers.

## Doubt Pass

A read-only Claude review challenged the approved design before implementation.

Surviving objections folded into this revision:

1. **Graph propagation was unconsumed.** Both impact paths rehydrate `IndexedSymbol` before rendering, while symbol export reads SQLite directly. The design now leaves `GraphNode`/`SymbolGraph` unchanged and keeps role evidence in Indexing.
2. **Uncertainty was prose-only.** The design now carries per-row `current|unknown` source evidence and an always-present `candidate_only` / `absence=unknown` impact object.
3. **The default search sidecar would have dropped the new value.** The design now advances the derived sidecar schema and requires writer/reader plus sidecar-on/off parity coverage.
4. **Incremental sidecar convergence used a separate lossy reader.** The design now requires shared derivation in `Read` and `ReadForPaths` plus a delta-applied parity test through `ApplyFileChanges`.

The cap-required confirmation pass verified the `ReadForPaths` → `ApplyFileChanges` → shared `InsertSymbols` path and found no residual gap against the incremental-convergence objection.

Recorded caveats:

- Compact `likely tests` remains a compatibility surface that can include non-runnable lifecycle hooks; docs must say so.
- Live release metadata and hashes were verified from GitHub during design, and restore/build guards must independently enforce them during implementation.

Refuted objection:

- Per-file diagnostic status is derivable: SQLite schema v4 defines both `file_id` and `path` on `parse_diagnostics`, so the Indexing join does not need a language-wide approximation.

## Acceptance Criteria

- [ ] Miller pins and restores the live `julie-extract` 2.12.0 assets with exact digests.
- [ ] Schema 4 / extract contract 3 / report schema 3 remain unchanged and tested.
- [ ] All three producer flags and per-file currency survive full and path-filtered SQLite reads into `IndexedSymbol` and the default search sidecar; graph construction/reach remains unchanged.
- [ ] `IsCase` is exactly `IsTest && !IsLifecycle`; no second classifier exists.
- [ ] Normal and revision-delta impact JSON expose exact role/source evidence plus structural candidate-only/absence-unknown status without changing legacy membership, ordering, counts, traversal, or compact output.
- [ ] Symbol export emits additive role/source-status fields while retaining schema version 1 and deterministic ordering.
- [ ] Search-sidecar schema freshness rebuilds old artifacts, rejects stale reads, and produces role-identical impact JSON with the sidecar enabled or disabled after both full build and incremental file convergence.
- [ ] Workspace health preserves `kind_coverage.test_detection` classifications and gap metadata.
- [ ] Capability negotiation advertises the role-evidence feature and JSON contract independently.
- [ ] Docs preserve the uncertainty and Miller/Eros ownership boundaries.
- [ ] Released-binary Razor, Vue, role, negative-control, and language-matrix evidence is recorded.
- [ ] Focused, fast, scale, Release-build, and current-binary gates pass.
- [ ] No MCP tool, Eros change, push, release, or semantic completeness claim is introduced.
