# Semantic production-readiness repair design

**Status:** Approved for implementation on 2026-07-21 by the user's direction to fix the audited defects and determine whether semantic retrieval is helping.

## Decision

Keep lexical retrieval as the production default while repairing the semantic experiment. Semantic stays optional and off-switchable. Promotion requires clean evidence from a corpus without nested-worktree duplication; failure to show useful lift ends the expansion rather than moving the gate.

This slice fixes the causal experiment, semantic candidate recall, vector freshness, process-local model lifecycle, operational visibility, CLI parity, telemetry attribution, and package smoke coverage. It then repeats the evaluation with production-shaped requests. It does not add a new MCP tool, change extraction ownership, add fleet semantics, or extend semantic retrieval into `context`, `inspect`, `impact`, `trace`, clones, or dead-code analysis.

## Serving contract

### One serving decision for the complete request

The canary assignment selects one serving policy before the search pipeline starts:

- `control`: lexical serving only, including every rescue path.
- `treatment`: the configured production hybrid arm may serve and rescue.
- `shadow`: semantic execution may be measured, but returned output is lexical and byte-identical.
- `ineligible`: existing non-canary behavior.

The assignment is carried through primary search and rescue. A control request can never call semantic rescue. Shadow work must not replace lexical output. `MILLER_SEMANTIC=shadow` is therefore genuinely non-serving; treatment serving requires `MILLER_SEMANTIC=on`.

### Content candidate union

Content hybrid becomes retrieval, not reranking-only. The semantic arm may introduce chunks absent from lexical results, including a lexical-zero rescue.

`ITextContentSearchIndex` stays the lexical contract. A narrow optional content-candidate lookup interface materializes `TextContentSearchHit` rows by semantic chunk ID while applying the same content-kind and test filters. The FTS implementation owns the chunk metadata needed for that lookup. Search unions lexical and semantic membership, then applies the existing fusion and deterministic ordering. If the optional interface is absent or vectors cannot serve, lexical output remains byte-identical.

## Vector correctness and lifecycle

### Freshness

Opening a ready vector generation is not sufficient evidence that it matches the live `symbols.db`. Before embedding, semantic query execution compares the generation's artifact identity and relevant cursor with the current workspace search context. After embedding, it repeats the check before KNN results can serve. An artifact change or unacceptable cursor lag returns the typed `VectorsStale` fallback. Other non-ready classifications retain their own typed fallback instead of collapsing to `VectorsMissing`.

### One process-local semantic session

One singleton broker owns the lazy sidecar session, circuit/restart state, and disposal for a Miller process. Both query and convergence paths borrow that broker. Concurrent convergence and query demand must launch at most one child. Semantic off constructs no session and performs no model work.

The longer-term machine-wide shared service remains deferred. This repair removes the accidental two-child cost inside one server process without changing cross-process ownership.

### Rebinding

Workspace-bound generation cleanup state is recreated when the bound workspace changes so retained generations are collected only under the correct root.

## Corpus freshness

Miller's repository ignores `.claude/worktrees/` in `.julieignore`, and `WatchPathFilter` rejects the `.claude/worktrees` segment pair for incremental events. This keeps the full-scan and watcher scopes aligned for this repository. A general extractor-level hard exclusion belongs to `julie-extractors` and is not added here.

The acceptance rebuild must demonstrate zero indexed files, symbols, content chunks, symbol vectors, and chunk vectors beneath `.claude/worktrees/`.

## Operational and CLI contract

Current-workspace `workspace status`, `workspace health`, and onboarding facts include vector state and pending convergence. Unhealthy or stale vector state produces truthful warnings and actions without changing lexical-only output.

`miller workspace refresh` for the current workspace advances vectors when semantic is enabled, or explicitly reports that a resident leader is required. Foreign-workspace refresh keeps the no-generation rule.

Normal CLI search follows the same production serving policy as MCP search. Forced `--arm` modes remain explicit evaluation surfaces. CLI treatment/control traffic records the same privacy-preserving canary facts where the CLI executes an eligible production-shaped search.

## Telemetry and promotion gate

The gate is causal and reproducible:

- identifier-shadow non-inferiority participates in the promotion verdict;
- export units split by exact Miller version and complete semantic identity rather than taking the first mixed value;
- content reads hash the resolved path used in served-result hashes and attribute the resolved workspace;
- fallback reasons preserve stale, building, incompatible, disk-blocked, missing, timeout, and circuit-open distinctions;
- zero-to-nonzero rescue, semantic contribution, follow-up attribution, success, and warm latency remain visible without storing query text or result content.

No gate may pool incompatible versions, encoders, schemas, or fusion profiles.

## Packaging

Each release RID exercises the exact packaged semantic payload before upload: load the packaged sqlite-vec extension, start the packaged semantic sidecar with Miller's pinned encoder, complete one embedding round trip, write/query one vector, and fail loudly on identity or dimension mismatch.

## Acceptance decision

After a clean rebuild, run the existing frozen retrieval corpus plus production-shaped replay and canary export. Semantic continues only if all of the following hold:

1. lexical-off, control, and shadow outputs remain byte-identical to lexical baseline;
2. semantic produces real zero-to-nonzero rescues and those rescued results receive accepted follow-up evidence;
3. fused quality improves over lexical under the pre-registered unit-aware analysis without a material identifier regression;
4. the exact-version canary gate passes success, identifier-shadow, and warm-latency criteria;
5. memory, latency, disk, and rebuild costs remain within the existing pinned BGE budget;
6. no stale generation serves across edits or full-rebuild promotion.

If the clean replay remains neutral or negative, or cannot convert lexical misses into useful followed results, Miller stays lexical-first and semantic expansion stops. The implementation may remain as an off-by-default evaluation capability, but it is not promoted on architectural promise alone.
