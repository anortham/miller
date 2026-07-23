# Miller-to-Julie Takeover Audit and Comparison Matrix

**Date:** 2026-07-22
**Status:** Complete
**Miller baseline:** `codex/semantic-maturity-decision` at `de42147b84bf`
**Julie baseline:** `main` at `37543a0e`
**Artifact:** schema 4, extract contract 3, `julie-extract 2.16.0`, artifact `artifact-1784723645018558000`

## Decision

Do not abandon Miller and do not restart as a fourth project. Miller can become better than Julie, but the next work must be a correctness-and-workflow program rather than another isolated search tweak.

Julie is better today in five agent-critical areas:

1. richer lexical ranking and automatic retrieval routing;
2. task-shaped context that includes implementation bodies and stronger pivot scoring;
3. exact-first reference and relationship workflows;
4. risk-ranked impact with broader test discovery;
5. qualified/live-AST editing workflows that narrow some unsafe name matches.

Miller is already better in six foundation areas:

1. cross-process lifecycle, version-aware leadership, rebuild promotion, and workspace safety;
2. stable JSON/JSONL and CLI/export contracts;
3. persistent external content, log, CI, and web research workflows;
4. correct full-population structural-fact aggregation;
5. atomic multi-file edit transactions with rollback and freshness recovery;
6. confidence-banded, provider-diagnostic cross-language bridge trace.

The right product is the existing Miller surface with deeper shared implementations. All nine MCP tools remain justified. No new MCP tool is required by this audit. `trace auto` should be deprecated after its useful evidence is folded into exact references, and unbounded `content export` should become CLI-only.

## Replacement Standard

Miller replaces Julie only when an agent obtains the evidence needed to act correctly with equal or better relevance, fewer calls, fewer returned tokens, lower wall-clock time, explicit uncertainty, and no correctness loss. Capability presence is not parity.

The product boundary remains:

- Miller owns the local agent workflow and optional local semantic retrieval.
- `julie-extractors` owns parser-backed extraction and reference-resolution coverage.
- `julie-semantic-sidecar` owns shared local embedding generation.
- Eros owns fleet ranking, cross-workspace semantic orchestration, guidance, and commercial workflows.

## Audit Method

- Read current source, tests, public descriptions, guidance, artifact schemas, and live Miller telemetry.
- Do not run or mutate Julie because another active session owns it.
- Do not touch the active `julie-semantic-sidecar` RC3 session or build.
- Run one broad ephemeral Claude review and nine fresh tool-specific reviews with read-only `Read,Grep,Glob` access.
- Accept no Claude claim until local source, artifact, telemetry, or behavior evidence supports it.
- Classify every material claim as accepted, corrected, rejected, or unproven.

## Claude Review Execution Record

Claude participated in every audit pass, not only the broad comparison. Preflight used Claude Code 2.1.218 with a valid Max subscription, no session persistence, strict MCP isolation, and read-only `Read,Grep,Glob` tools. Julie and the semantic-sidecar workspaces were never built, indexed, served, or modified.

| Review session | Scope | Recorded result |
|---|---|---|
| Broad | product boundaries, exact-reference defect, cross-cutting risks | disposition in `Broad Claude Review Disposition` |
| Tool 1 | `search` versus Julie retrieval | `Tool Pass 1` Claude disposition |
| Tool 2 | `inspect` versus `get_symbols`/`deep_dive` | `Tool Pass 2` Claude disposition |
| Tool 3 | `context` versus `get_context` | `Tool Pass 3` Claude disposition |
| Tool 4 | `trace` versus `fast_refs`/`call_path` | `Tool Pass 4` Claude disposition |
| Tool 5 | `impact` versus `blast_radius` | `Tool Pass 5` Claude disposition |
| Tool 6 | `edit` versus Julie editing workflows | `Tool Pass 6` Claude disposition |
| Tool 7 | `content` versus Julie spillover/content workflows | `Tool Pass 7` Claude disposition |
| Tool 8 | `patterns` versus Julie structural facts | `Tool Pass 8` Claude disposition |
| Tool 9 | `workspace` versus `manage_workspace` | `Tool Pass 9` Claude disposition |

The broad review did not satisfy any tool-specific pass. Each tool received a fresh ephemeral review, and the lead independently checked its claims before including them here.

## Priority Summary

| Priority | Finding | Affected tools | Replacement effect |
|---|---|---|---|
| P0 | Reference workflows discard resolved symbol identity | `trace`, `inspect`, `context`, `edit`; graph fallback in `impact` | Wrong homonym results and unsafe rename scope |
| P0 | `context` returns signatures without implementation bodies and weakly scores pivots | `context` | Forces extra calls and spends tokens on lower-value seeds |
| P1 | Symbol search is OR-only and has a thin reranker | `search`, `context` | Lower precision and more mode/query retries than Julie |
| P1 | Success-path hints habitually request another call and cannot express evidence sufficiency | `search`, `inspect`, `trace`, `context` | Agents keep navigating after they already have enough evidence |
| P1 | `inspect` labels every referencer as a caller and lacks typed relationship sections | `inspect` | Misstates evidence and forces extra navigation |
| P1 | Impact lacks risk ranking and broad test linkage | `impact` | Flat walls of results and incomplete test recommendations |
| P1 | Hard failures are returned as successful plain text, including under `format=json` | seven read/lifecycle tools | Automation cannot reliably distinguish success, empty, and failure |
| P1 | Rename can over-rename homonyms and silently under-rename sparse languages | `edit` | Preview-first is insufficient for an authoritative refactor tool |
| P1 | Content inventory/export can produce unbounded agent payloads | `content` | Context flooding; live list average is 67.8 KB |
| P1 | Several read paths bound rows but not delivered tokens; `inspect depth=full` has no continuation | `inspect`, `search`, `trace`, `impact`, `workspace`, `content` | One valid call can still crowd out higher-value context |
| P2 | Qualified names are not consistently resolved | `inspect`, `trace`, `impact`, `edit` | `Type::member` may be treated as an ID or fail |
| P2 | Query/pattern fan-out truncation is not surfaced | `patterns` | Partial structural results can look complete |

## P0-REF-001: Reference Listing Discards Resolved Symbol Identity

### Source Proof

`TraceTool.RunRefs` resolves a target to a concrete symbol and then calls `ExtractReader.ReadReferences(context.IndexDbPath, symbol.Name)`. `InspectTool` and reference-aware `ContextTool` call the same reader. The reader executes:

```sql
SELECT name, kind, path, start_line, containing_symbol_id
FROM identifiers
WHERE name = $name
ORDER BY path, start_line, identifier_id;
```

It ignores:

- `identifiers.target_symbol_id`;
- `identifier_resolutions.target_symbol_id`;
- `relationships.to_symbol_id`;
- `pending_resolutions.target_symbol_id` joined to `pending_relationships`.

The resolved data is not hypothetical. `DeadCodeCandidateReader` already queries the last three exact inbound sources for each symbol ID. `SymbolGraphReader` already reads exact relationships and resolved pending relationships for graph traversal. The agent-facing reference reader simply bypasses them.

### Artifact Proof

| Fact | Count |
|---|---:|
| identifiers | 280,464 |
| identifiers with direct `target_symbol_id` | 23,384 |
| matching identifier-resolution overlay rows | 23,384 |
| relationships | 12,886 |
| resolved pending relationships | 3,308 |
| identifier rows named `Run` | 632 |

Current resolution is highly uneven:

- C# calls: 71,089 rows, 13,973 resolved.
- C# type usages: 23,489 rows, 8,389 resolved.
- C# variable refs: 126,187 rows, 3 resolved.
- C# member accesses: 36,936 rows, 0 resolved.
- Python calls: 4,651 rows, 737 resolved.
- JavaScript calls: 1,037 rows, 132 resolved.

This proves two requirements at once: Miller must consume exact rows now, and fallback must remain typed because upstream coverage is incomplete.

### Two `Run` Cases

- `ContextTool.Run` at line 205 has 632 name rows and zero exact inbound rows. Returning all 632 as if symbol-specific is false; the correct result is an empty exact set plus separately bounded unresolved evidence or an ambiguity diagnostic.
- `JulieExtractRunner.Run` has five exact identifier rows and five relationship rows at the same five sites. The union must canonicalize `call` and `calls` before deduplication or it will double-count.

### Root Cause

The reader interface accepts a name when every caller has already resolved a symbol. Stale comments and tests still claim `target_symbol_id` is always null, contradicting `MillerExtractContract` and the live artifact. The wrong abstraction is shared by several tools, so fixing one renderer would leave the defect elsewhere.

### Required Architecture

Create one deep reference-evidence module whose caller-facing input is a symbol ID and whose output is a bounded, provenance-bearing reference set. It must:

1. read direct identifier resolution, exact relationships, and resolved pending relationships;
2. canonicalize reference kinds before deduplication;
3. deduplicate by path, byte span or line, and canonical kind;
4. preserve source, confidence, containing symbol, and resolution tier;
5. add name-based or naming-variant fallback only as a separately typed low-confidence arm;
6. suppress or explicitly diagnose fallback when multiple definitions make attribution unsafe;
7. serve `trace`, `inspect`, `context`, `impact` graph fallback, and rename through one contract.

### Acceptance

- Same-name definitions in one file and across files have disjoint exact sets.
- `JulieExtractRunner.Run` yields five exact sites, not 632 name matches and not ten duplicated exact rows.
- `ContextTool.Run` never presents 632 unresolved homonyms as exact references.
- Callers contain call-like evidence only; other uses are rendered as `referenced_by`.
- All rows expose resolution provenance and confidence in JSON.
- Old tests that assert “every identifier with that name” are inverted.
- Per-language fallback coverage is reported rather than silently treated as complete.

## Full Tool Matrix

| Tool | Current Miller | Julie advantage | Miller advantage | Required decision | Priority |
|---|---|---|---|---|---|
| `search` | BM25 + exact-name adjustment, OR-only, separate file/symbol lanes, optional semantic RRF | richer reranker, AND→OR relaxation, mixed file/symbol list, scope and semantic rescue | stronger empty diagnosis, deterministic backends, optional/off-switchable semantics | keep; redesign ranking and auto routing | P1 |
| `inspect` | one file/symbol tool, fresh body slice, compact/JSON, refs/callers/callees | resolved and typed relationships, implementations/types/tests, qualified resolution, kind-aware render | hash-guarded live body, strict ambiguity, JSON, smaller surface | keep; rebuild relationships and enrich typed sections | P0/P1 |
| `context` | token-bounded signatures and neighbours, lexical seeds, optional usage enrichment | bodies, hybrid seeds, pivot scoring, task signals, adaptive depth | deterministic/offline path, copyable next calls, query-affinity neighbours | keep; redesign as pivot/body bundle | P0 |
| `trace` | refs/path/bridge/auto, strong bridge diagnostics, JSON | exact-first refs, confidence, naming variants, call-precise path | bridge providers and honesty flags are substantially better | keep; replace refs, type path, deprecate auto | P0/P2 |
| `impact` | line-precise diff/git seeds, in-memory reverse walk, no-arg post-edit workflow | centrality/edge ranking, broader test linkage, revision range, why reasons | faster architecture, stateless output, honest test-evidence scope | keep; rank, widen tests, expose revision delta | P1 |
| `edit` | seven operations, indexed selectors, atomic multi-file rollback, JSON, self-heal | live AST reparse, qualified symbol recovery, narrower identifier edits | transaction safety, match evidence, one tool, write-through | keep; exact rename, coverage gate, parse/syntax guard | P1 |
| `content` | persistent import/search/read/list/remove/export and cross-workspace corpus | no equivalent; only ephemeral spillover and workspace line search | decisive Miller advantage | keep; bound list, add shape, move export CLI-only | P1/P2 |
| `patterns` | full aggregation, catalog overlay, suggestions, diagnostics, JSON, workspace routing | proper MCP error channel; otherwise weaker and sometimes incorrect aggregation | decisive Miller advantage | keep; surface truncation and clarify directory rollup | P2 |
| `workspace` | status default, lifecycle, registry, leader, health, onboarding, dashboard, JSON | session workspace switch and aggregate stats | decisive Miller advantage in safety/contracts/concurrency | keep; add list totals, preserve fixed binding | P2/P3 |

## Tool Pass 1: `search`

### Findings

- `CollectSymbolCandidates` always calls `index.Search(..., SearchMode.Or)` although both backends implement `And`.
- Symbol ranking is BM25 plus exact-name definition adjustments. Content search has phrase logic, but symbol search lacks Julie's phrase/intent/path-role/generated/vendor/language-affinity reranking.
- Auto chooses a file lane or symbol lane; it does not return a unified list when the query plausibly names both.
- Julie permits a per-call lexical, semantic, or hybrid choice. Miller's semantic activation is process-wide (`off|shadow|on`) and its search schema has no per-call arm override.
- Live 14-day telemetry: 6,729 calls, 31.0% empty, 632 ms average, 2.37 KB average response. Source mode is 41.4% empty and file mode 50.1%; auto is only 2.7% empty.
- Semantic retrieval is correctly local, optional, and off-switchable. It should remain a fused arm rather than leak into reference truth.

### Recommendation

Keep one `search` tool. Add a pure post-BM25 reranker, AND-first/OR-relax with an explicit `relaxed` field, and a mixed file/symbol auto lane. Preserve explicit modes and byte-identical lexical output when semantics are off. Add an advanced per-call retrieval override (`auto|lexical|hybrid|semantic`) inside the existing tool, with automatic routing as the default and the process-wide off switch remaining authoritative. Evaluate path-role, generated/vendor, dominant-language, and phrase-proximity signals on labeled tasks before fixing weights.

### Acceptance

- Multi-term co-occurrence outranks single-term matches; relaxation is explicit.
- Same-name source definitions outrank vendored/generated/test copies.
- Ambiguous filename/symbol queries return both kinds in one auto response.
- Explicit lexical retrieval performs zero vector work; hybrid/semantic overrides are honored only when globally enabled and otherwise return a typed unavailable diagnostic.
- nDCG@6, MRR, top-1, calls-to-action, tokens-to-action, and wall time improve on a sealed shared corpus.
- In-memory and FTS5 backends remain rank-identical.

### Claude Disposition

Accepted OR-only wiring, thin symbol reranking, mixed-result, per-call retrieval-control, and output-control gaps. Corrected “no phrase boost” to symbol search only because content search already has phrase scoring. Left Julie's claimed centrality/path-prior production effect unproven until a runtime trace or benchmark shows the boosts fire and help.

## Tool Pass 2: `inspect`

### Findings

- P0-REF-001 contaminates refs and callers.
- `DistinctCallers` treats every reference kind as a caller. Type usages, imports, member access, and variable refs can appear under `callers`.
- `ReadCallees` scopes the source by containing symbol but carries target names, losing resolved callee identity.
- `SmartTargetResolver` treats any `::` string as an ID shape, so qualified names can fail before member resolution.
- Julie adds kind-aware sections for implementations, required methods, parameter/return types, exports/dependencies, test locations, and optional semantic neighbors.
- Miller is better at live hash-guarded body slices, strict ambiguity, JSON, bounded omitted counts, and a merged file/symbol surface.
- `inspect depth=full` returns the complete body without a token budget or freshness-bound continuation. Count caps on relations do not bound a large definition body.

### Recommendation

Keep `inspect`. Use the shared reference-evidence module; split `callers` from `referenced_by`; resolve outgoing callees by target ID; add relationship-kind-aware sections and test locations when data exists; support qualified member names before ID heuristics; add end lines and nesting to file structure. Keep semantic neighbors optional and separate. Add a hard output budget and a deterministic, freshness-bound continuation cursor for full bodies instead of a new spillover tool.

### Acceptance

- Caller rows are call-like only.
- Homonyms never cross-contaminate exact refs.
- `Type::member` and `Type.member` resolve consistently.
- Interfaces/classes/callables expose the applicable typed sections.
- File structure shows stable nesting and line spans without dumping all bodies.
- Full-body continuation reconstructs the same ordered body without gaps and refuses after the indexed hash/span changes.

### Claude Disposition

Accepted the mislabeled callers, qualified-name, typed-section, and file-structure gaps. Corrected the claim that Julie refs are simply exact: Julie is exact-first but still merges guarded name-based identifiers. Rejected a proposed new patterns contract because Miller already ships `docs/contracts/patterns-json-v1.md`.

## Tool Pass 3: `context`

### Findings

- Seeds are the first ten OR-mode BM25 hits plus uniquely resolved entry symbols and identifier tokens from test/stack hints.
- Seeds are not reranked by exactness, code role, test/docs penalty, centrality, or semantic evidence.
- Neighbours receive query-affinity scoring, but seeds do not.
- Compact and JSON output contain signatures, not implementation bodies. Follow-up `inspect` is required by construction.
- A fixed twelve-neighbour render cap can omit candidates the token packer already paid to select.
- Ambiguous entry symbols are silently skipped.
- Live telemetry: 710 calls, 2.71 seconds average, 10.6 KB average. `reference_mode=usage` averages 3.35 seconds and 14.1 KB, versus 1.42 seconds and 3.44 KB with references off.

### Recommendation

Keep `context`, but redesign it around a few scored pivots with bounded body snippets and shallow neighbour signatures. Apply code-role/exactness/test penalties to seeds; use optional semantic fusion under the existing off-switch; add `edited_files` and line-aware stack signals; make selected and rendered items identical; expose ignored/ambiguous anchors. Fold typed usage evidence into the same coherent bundle rather than a parallel item schema.

### Acceptance

- A labeled task suite measures one-call answerability and follow-up-call reduction.
- Output always stays within `token_budget` after bodies are added.
- Semantic off is byte-identical to the lexical baseline until an intentional contract version changes it.
- Every selected neighbour renders, and every omitted item is counted.
- Ambiguous anchors produce a diagnostic, not silent loss.

### Claude Disposition

Accepted signature-only output, weak seed scoring, semantic-disconnection, adaptive-depth, and task-signal gaps. Corrected the implied “body is always better” conclusion: live telemetry already shows high response cost, so bodies must replace low-value breadth and pass tokens-to-action gates rather than simply expand output.

## Tool Pass 4: `trace`

### Findings

- P0-REF-001 makes `refs` both imprecise and incomplete.
- Graph `path` traverses all dependency kinds and compact output does not identify each hop's edge kind; it is not necessarily a call path.
- Graph edges discard confidence and some provenance.
- `auto` is explicitly documented as subsumed by `inspect depth=full`, yet remains the default mode.
- Live telemetry: 1,089 calls; 1,068 are refs, only 13 auto and 5 path. Refs average 785 ms and 1.93 KB; path is 80% empty in the tiny observed sample.
- Bridge mode is better than Julie's web path: broader providers, confidence bands, ambiguity flags, and provider-specific failure diagnostics.

### Recommendation

Keep `refs`, `path`, and `bridge`. Rebuild refs over the shared evidence module. Add a path-kind contract with call-like default and broad dependency override, render edge kinds and confidence, and support multi-target ambiguity without false attribution. Deprecate `auto` after `inspect` and exact refs cover its workflows. Do not add semantic-similar symbols to a reference answer.

### Acceptance

- Exact and fallback refs are separately typed and deduplicated.
- Call path excludes import-only/type-only paths unless broad dependency mode is requested.
- Every path hop exposes edge kind and confidence/provenance.
- Bridge output and provider diagnostics remain unchanged.
- Default behavior reflects observed usage rather than an unused redundant mode.

### Claude Disposition

Accepted the exact-reference, confidence, path-kind, ambiguity, and redundant-auto findings. Corrected Claude's proposed `graph.Dependents(seed)` implementation: the graph alone lacks reference-site spans and enough provenance, so the fix belongs in a DB-backed evidence reader, with the graph consuming its normalized edges where appropriate.

## Tool Pass 5: `impact`

### Findings

- Miller's no-arg working-tree diff and line-precise unified-diff seeding are better than Julie.
- Reached nodes are ordered by hop and symbol ID, with no relationship priority, centrality, or reached-via explanation.
- Likely tests are only graph-reached symbols whose extractor `IsTest` flag is true.
- File seeding includes every symbol kind, including low-value fields/variables/constants.
- Revision-delta logic exists in a CLI-only core and is not exposed through MCP `impact`.
- Normal compact output has weaker truncation evidence than the revision-delta envelope.
- Live telemetry: 952 calls, 1.69 seconds average, 5.9 KB average; changed-path responses average 14.5 KB.

### Recommendation

Keep the in-memory reverse graph and no-arg diff workflow. Normalize graph edges through exact resolution, preserve kind/confidence/provenance, rank within hop by relationship priority and centrality, add metadata-linked and clearly labeled heuristic tests, filter file seeds to actionable code, and expose the existing revision range through additive `impact` parameters. Add bridge impact only if dogfood proves a frequent gap.

### Acceptance

- Homonym targets create one resolved edge, not one edge per same-name definition.
- Every impact row explains hop, edge kind, and reached-via seed.
- Metadata-linked tests appear without being mislabeled as graph proof.
- MCP and CLI revision-delta results agree.
- Truncation and evidence scope are explicit in normal and delta JSON.

### Claude Disposition

Accepted ranking, test-linkage, exact-identifier, revision, and provenance gaps. Corrected ranking to keep hop as the primary safety signal; centrality must rank peers, not bury direct callers behind distant hubs. Left real `reference_score` value and test-linkage coverage unproven pending artifact data.

## Tool Pass 6: `edit`

### Findings

- Miller's one tool, exact preview, indexed selectors, JSON, freshness self-heal, TOCTOU guard, atomic temp moves, and multi-file rollback are stronger than Julie.
- Workspace rename reads every identifier by name and includes homonyms by design, despite now-available exact resolution.
- Sparse identifier coverage can under-rename without a coverage warning.
- Symbol operations trust indexed spans guarded by hashes; Julie re-parses the live file and rejects parse errors in the target region.
- Disk-verified `replace_text` still pays the index-freshness gate, creating avoidable refusals.
- Current telemetry “errors” mostly represent correct safety refusals, especially stale target, no match, and target not found.

### Recommendation

Keep one `edit` tool. Route rename through the shared exact-reference evidence module; show exact, fallback, and uncovered-language counts; refuse an authoritative workspace rename when coverage is unsafe unless the user explicitly chooses a labeled fallback. Add syntax/parse-diagnostic or bracket-balance evidence for symbol rewrites. Let disk-verified literal replacement rely on TOCTOU rather than unrelated index freshness. Add compact next-step guidance to run impact/tests after apply.

### Acceptance

- Homonym fixtures rename only the resolved target under exact mode.
- Coverage is measured per supported language and identifier kind.
- Multi-file injected failure changes zero files.
- Symbol rewrite refuses or warns on parse-invalid target spans.
- A second immediate edit self-heals without manual refresh.
- JSON distinguishes refusal, error, preview, and apply.

### Claude Disposition

Accepted Miller's transaction advantage and rename/parse/freshness gaps. Corrected the claim that Julie rename is fully semantic: it depends on `fast_refs`, which still has name fallback and can misattribute unqualified identifiers. Rejected Julie's regex import rewriting and partial per-file rename commits as patterns to copy.

## Tool Pass 7: `content`

### Findings

- Julie has no persistent external content workflow. Its spillover store pages prior results in memory for fifteen minutes; it does not import or search logs.
- Miller persists external files and web markdown, searches them, reads bounded windows, supports cross-workspace search, removes sources, and exports JSONL.
- `content list` is unbounded and defaults to external files only. The 14-day live average is 67.8 KB for the six observed list calls.
- MCP `content export` can return the entire corpus as one payload, even though export is an Eros process/CLI workflow.
- `read` requires a line, so a first-look “what is this log?” workflow requires search or `line=1`.
- Import buffers the whole file, and an explicit `max_bytes` can raise the default 25 MB cap without changing that allocation strategy.

### Recommendation

Keep `content`. Make bare list a bounded per-kind inventory with totals and filters; add an `inspect` or `shape` operation that returns head, tail, line count, kind, and bounded severity summary; keep bounded read. Remove `export` from the MCP operation list and preserve it on CLI/JSONL contracts. Stream imports when callers intentionally raise the size cap. Do not add an ephemeral spillover tool.

### Acceptance

- Bare list stays under a documented byte/row cap and reveals external and web counts.
- One shape call orients on a log without a full read.
- No MCP operation can emit an unbounded full corpus.
- CLI export remains contract-compatible for Eros.
- Import memory remains bounded above the default cap.

### Claude Disposition

Accepted Miller's decisive advantage and list/export/shape issues. Corrected generic spillover from “capability to port” to a rejected fit for the stated goal: it adds state and another call. Corrected export remediation from an MCP continuation protocol to CLI-only ownership.

## Tool Pass 8: `patterns`

### Findings

- Miller and Julie consume the same structural-fact concept, but Miller's consumer is more correct.
- Miller list/summary aggregate the full population. Julie summary aggregates at most the result limit, and Julie list first caps observed rows at 10,000.
- Miller adds catalog metadata, fuzzy suggestions, empty reasons, next actions, path pushdown, richer telemetry, and stable JSON.
- Miller silently caps free-text query fan-out at 25 pattern IDs.
- Directory summary deliberately rolls up to two path segments but does not make that semantic obvious.
- Hard failures return ordinary text instead of the MCP error/JSON channel.
- Live telemetry: 84 calls; search is 34.5% empty and 3.84 KB average, list is 4.26 KB average.

### Recommendation

Keep `patterns` unchanged in shape. Surface matched-pattern truncation, name the directory behavior `top_directory` or switch to full parent directory, remove hardcoded catalog counts from descriptions, and use the shared structured failure contract. Preserve Miller's aggregation and diagnostics; do not port Julie's caps.

### Acceptance

- Query fan-out reports the total matched pattern count and truncation.
- Directory grouping has one documented, tested meaning.
- Summary equals direct SQL totals above 500 facts.
- List includes all observed pattern IDs above 10,000 facts.
- Existing `patterns-json-v1` remains the public JSON contract or is intentionally versioned.

### Claude Disposition

Accepted Miller's advantage and its query/directory/error gaps. Rejected Claude's request to create a patterns contract because `docs/contracts/patterns-json-v1.md` already exists. The artifact schema and catalog population remain subject to real-extract parity checks.

## Tool Pass 9: `workspace`

### Findings

- Miller's zero-argument status, structured refresh outcomes, cross-process locking, version-aware leadership, full-rebuild promotion, prune preview, safe removal, onboarding, and JSON contracts are substantially ahead.
- Julie's real session switch avoids passing `workspace_id` on every cross-workspace call, and its stats operation provides aggregate registry totals.
- Miller intentionally keeps one workspace binding per process; `open` primes another workspace but never silently rebinds the current server.
- Workspace health compact output averages 8.61 KB across 171 calls; status averages 837 bytes.
- Named cross-workspace reads default to refresh-first. Refresh/convergence failures in unrelated derived sidecars can prevent otherwise-readable operations; recent telemetry includes current-version inspect failures caused by stale search sidecars.
- Hard exceptions return ordinary text even for JSON requests.

### Recommendation

Keep fixed process binding and one `workspace` tool. Add aggregate totals to `list` instead of a new stats operation. Make compact health a true summary with deep detail in JSON/markdown. Decouple refresh results so a healthy `symbols.db` can still serve non-search reads while unrelated sidecar failures are surfaced as warnings. Use the structured failure contract. Do not port Julie's primary-session switch without measured trajectory evidence.

### Acceptance

- Status remains a one-call identity/freshness/leader/sidecar answer.
- List totals equal registered row totals without hydrating all indexes.
- Non-search reads survive search-sidecar failure with a typed warning when symbol data is fresh.
- Compact health has a fixed token/byte budget.
- Current binding never changes during cross-workspace operations.

### Claude Disposition

Accepted Miller's lifecycle advantage and Julie's aggregate/session-switch differences. Corrected the missing session switch from a defect to an unproven tradeoff because silent rebinding conflicts with Miller's process model. Added locally observed sidecar-failure coupling that Claude flagged only as an uncertainty.

## Cross-Cutting Findings

### P1-BUDGET-001: Row Limits Are Not Output Budgets

`search`, `inspect`, `trace`, and `impact` mostly cap result counts, not delivered tokens. `inspect depth=full` can return an arbitrarily large symbol body; `content list`, compact health, and changed-path impact have already produced large live payloads. Julie's stateful spillover is not the right architecture because it adds session state and another MCP tool, but the underlying recoverability advantage is real.

Add one deterministic read-budget contract to the existing tools:

- compact and JSON responses publish or accept an explicit budget where the workflow can be large;
- truncation exposes omitted counts and a stable continuation cursor over the same ordered result;
- body cursors bind to workspace, symbol ID, extractor hash, and span so stale continuations refuse safely;
- exhaustive export remains CLI/JSONL rather than MCP;
- `context` keeps its existing token-budget contract and does not gain a second pagination model.

### P1-GUIDANCE-001: Success Hints Encourage Unnecessary Calls

The shared `NextStepHint` renderer can only emit a `next:` tool call; decisions in `search`, `inspect`, and `trace` use it to recommend more navigation. The visible paired baseline classified seven of twelve candidate losses as guidance failures: useful evidence was already present, but the agent kept inspecting or cited redundant evidence.

Keep one-line recovery guidance when evidence is missing or ambiguous. For successful composed results, emit a typed sufficiency disposition with concise reasons and no next call when the requested evidence is present. The disposition must describe evidence coverage, not claim that the agent's eventual answer is correct. Prove the policy through calls-to-action and wrong-action metrics before expanding it to every tool.

### P1-ERR-001: Hard Failures Masquerade as Successful Text

`search`, `inspect`, `context`, `trace`, `impact`, `patterns`, and `workspace` catch every exception and return strings such as `search failed: ...`. Under `format=json`, those strings are still not JSON. Julie generally returns classified MCP errors.

Create one shared diagnostic/error contract:

- expected not-found, ambiguity, filtered-empty, and unsupported cases remain successful typed empty responses;
- infrastructure, corruption, schema, permission, and invariant failures use the MCP error channel or a versioned JSON diagnostic envelope;
- compact and JSON render the same diagnostic code and next actions;
- telemetry classification is generated from the same typed failure, not re-derived per tool.

### P1-ARCH-001: Reference Policy Is Scattered

Reference identity, graph edges, dead-code counts, edit sites, callers, and exports are implemented by separate readers with different truth rules. The deletion test favors a deep normalized evidence module: removing it would force exact/fallback/dedup/confidence policy back into five callers. This is a strong architecture candidate and the first implementation slice.

### P1-EVAL-001: Presence Gates Hide Agent Cost

The June foundation matrix proved that expected anchors were often present, not that the first result was relevant or that one call was enough. The takeover gate must measure labeled relevance and action efficiency:

- nDCG, MRR, top-1, false-positive rate, and exact-reference precision/recall;
- calls, input/output tokens, and wall time to first correct action;
- one-call task completion for context/inspect/impact;
- correct refusal and uncertainty, not only successful answer count;
- semantic-off, BGE-small, and CodeRankEmbed arms on identical tasks once RC3 is available.

### Language Parity

This Miller artifact proves exact resolution only for the languages and kinds present in this repo. It does not prove all 36 extractor languages. Every feature that depends on exact references, test linkage, typed relationships, or rename coverage must run against a committed all-language extract fixture and report missing capability by language/kind. A sparse language cannot silently look authoritative.

## Live Miller Telemetry, Last 14 Days

| Tool | Calls | Empty | Error | Average ms | Average bytes |
|---|---:|---:|---:|---:|---:|
| inspect | 13,641 | 6.0% | 0.6% | 468.5 | 3,320 |
| search | 6,729 | 31.0% | 0.8% | 632.0 | 2,367 |
| workspace | 1,172 | 2.3% | 0.0% | 2,192.8 | 2,207 |
| trace | 1,089 | 17.5% | 0.2% | 774.7 | 1,921 |
| impact | 952 | 9.7% | 0.2% | 1,685.3 | 5,900 |
| context | 710 | 0.0% | 2.4% | 2,712.1 | 10,600 |
| edit | 270 | 2.2% | 21.5% | 457.6 | 782 |
| content | 211 | 24.2% | 12.8% | 41.2 | 5,700 |
| patterns | 84 | 23.8% | 0.0% | 405.6 | 3,950 |

The edit rate is dominated by intentional refusals; content errors are mostly unsupported encodings and invalid kind values. These are not equivalent to crashes. The evaluator must distinguish refusal, empty, hard error, and wrong answer.

## Broad Claude Review Disposition

### Accepted

- P0-REF-001 and its propagation through trace, inspect, callers, and context.
- The stronger proof that `DeadCodeCandidateReader` already contains exact inbound queries.
- Callee target identity loss.
- Stale “target_symbol_id always null” comments and tests.
- Julie exact-first reference behavior and Miller's better bridge/atomic/lifecycle foundations.

### Corrected

- Exact data exists globally but not for every symbol. `ContextTool.Run` has zero exact refs; the fix must diagnose/suppress unsafe fallback instead of promising a precise set.
- Julie is exact-first, not exact-only; it still contains name-based fallback and sometimes assigns fallback rows to the first definition.
- A graph-only fix is insufficient because the graph discards site spans and confidence.

### Rejected

- Semantic-similar symbols inside a reference answer.
- Copying Julie's tool count, stateful spillover, partial rename commits, regex import rewriting, or capped structural aggregation.

### Unproven

- Julie-versus-Miller ranking quality from source constants alone.
- Cross-language benefit of naming variants.
- Centrality and test-linkage value distributions on real artifacts.
- End-to-end token/call/latency superiority until paired trajectories run.

## Tool Surface Decision

| Surface | Decision | Reason |
|---|---|---|
| Nine MCP tools | Keep | Each owns a distinct agent intent and earns its interface. |
| `trace auto` | Deprecate after migration | Duplicates inspect and is almost unused. |
| `content export` MCP operation | Remove after contract review | Unbounded process/export workflow belongs on CLI/JSONL. |
| New refs/call-path/blast-radius tools | Do not add | Existing `trace` and `impact` can become deep enough. |
| Generic spillover tool | Do not add | Adds state and another call; fix primary bounds instead. |
| Deterministic continuation on existing read tools | Add where output can exceed its budget | Preserves bounded, recoverable evidence without session state or another tool. |
| Workspace session switch | Do not add now | Conflicts with fixed binding and lacks measured benefit. |

## Takeover Gates

Miller is ready to retire Julie only when:

1. exact-reference and rename P0/P1 correctness gates pass;
2. all-language resolution coverage is measured and gaps are explicitly typed;
3. search/context/inspect/impact beat Julie on sealed task relevance and action efficiency;
4. semantic-off remains deterministic and local semantic arms pass BGE-small/CodeRankEmbed comparisons;
5. hard failures are machine-readable and never masquerade as successful JSON;
6. compact output budgets eliminate current context, impact, health, and content-list payload spikes;
7. Miller retains its lifecycle, content, patterns, bridge, contract, and atomic-edit advantages;
8. a paired agent run reaches the correct action with fewer calls, tokens, and wall time at an equal or lower wrong-action rate.

## Final Product Judgment

Miller is not a failed replacement. It is a stronger platform with several shallow or incorrect agent-facing paths. Julie's advantages are concrete and portable without copying Julie's architecture. The highest-risk mistake now would be continuing to polish retrieval while leaving reference identity, context composition, impact ranking, rename coverage, and error semantics unresolved.

The remediation order is:

1. reference truth and structured failures;
2. inspect/trace/edit consumers;
3. context composition;
4. search ranking and routing;
5. impact ranking and tests;
6. content/pattern/workspace bounds and cleanup;
7. sealed paired takeover decision.
