# Cross-Repo Dogfood Contract Repair Design

**Date:** 2026-07-26  
**Status:** Approved direction; Claude review corrections incorporated; revised specification pending approval  
**Scope:** `julie-extractors`, Miller, the unreleased owned Eros consumer, and
`julie-semantic-sidecar` only if implementation evidence proves its protocol is involved. Eros compatibility is
not a constraint: affected contracts break once and Eros moves to the replacement in the same integration cut.

## Goal

Replace implementation-local compensations with source-owned contracts that give agents correct, lossless,
bounded, and efficient evidence across every Miller tool.

Correctness is the hard floor. Acceptance then measures time to sufficient evidence, calls, tokenizer-counted
tool output, irrelevant rows, retries, recovery, and real task completion.

## Problem

The Phase 10 checks proved that individual implementations matched their current tests and documents. They did
not prove that the contracts themselves were correct across the producer-consumer boundary.

The live dogfood exposed that gap:

- `julie-extractors` emits `language_capability_gaps.status="open_gaps"`, an artifact test uses `"open"`, and
  Miller counts only `"open"`.
- Identifier, relationship, and pending-resolution rows can describe one physical call site with independent
  identities and different spans. Miller therefore cannot deduplicate them without guessing.
- Inspect silently maps an unknown `depth` to `summary`.
- Inspect file listings and free-text Patterns results can omit evidence without a lossless cursor.
- Patterns free-text fan-out orders globally by path, so a small page can contain only one matched pattern
  family.
- Trace JSON repeats every reference in a compatibility union and a tier-specific array.
- Edit errors are raw result strings instead of the common diagnostic model.
- Marker search treats prose that merely names the marker vocabulary as actionable debt.
- A literal NUL compiles in a C# fixture but degrades parser-backed extraction.
- Conceptual routes do not consult lexical evidence, allowing a semantic-only candidate to displace directly
  relevant lexical candidates.

## Decision

Ship a breaking cross-repo contract revision. Compatibility with the defective shapes is not a requirement
because the producer, consumer, and known downstream integrations are all owned together.

The implementation must repair facts at their authoritative owner:

- extraction identity and source-region classification belong to `julie-extractors`;
- agent-tool validation, paging, rendering, diagnostics, and semantic serving policy belong to Miller;
- Eros consumes the revised public process contracts and must not read private Miller artifact internals;
- `julie-semantic-sidecar` remains unchanged unless a protocol-level defect is demonstrated.

## Architecture Quality

**Affected modules:** `julie-extractors` extraction normalization and artifact schema; Miller artifact readers,
tool renderers, continuation codecs, diagnostics, marker consumption, semantic policy, evaluation harness;
Eros Miller process-contract models and parsers; and cross-repo verification.

**Caller-facing interface:** the artifact contract between `julie-extractors` and Miller, plus Miller's nine
existing MCP tools and documented CLI/JSON contracts. No new MCP tool is added.

**Depth/locality check:** source identity is normalized once upstream; Miller consumes typed facts and does not
reconstruct source semantics. Bounded collection behavior is expressed through one paging contract rather than
tool-specific omission workarounds.

**Test surface:** real `julie-extract` artifacts consumed through public Miller tool entry points, supplemented
by focused pure tests for normalization, cursor identity, ordering, diagnostics, and semantic policy.

**Seams/adapters:** the versioned SQLite/JSONL artifact is the Rust-to-C# seam. Miller's public process contracts
are the Miller-to-Eros seam. No shared Rust/C# runtime library is introduced.

**Rejected shortcuts:** accepting both misspelled status values indefinitely; post-hoc span-overlap or
nearest-token deduplication; adding query-refinement hints instead of continuation; keeping duplicate Trace
arrays; marker heuristics inside Miller; compatibility adapters in unreleased Eros; changing semantic weights
around one observed query.

**Architecture risk:** high. This intentionally changes the artifact schema and public tool JSON. Risk is
controlled by cross-repo contract fixtures, all-language extraction, deterministic paging tests, and measured
semantic evaluation.

## 1. Artifact Contract vNext

`julie-extractors` increments:

- SQLite schema `4 -> 5`;
- extract contract `3 -> 4`;
- JSONL schema `3 -> 4`.

### 1.1 Typed capability-gap status

The artifact model defines one closed status vocabulary:

- `open`: an unresolved capability gap that counts toward health;
- `exception`: a documented intentional exception that does not count as open.

The producer emits only those values. Unknown values fail artifact validation and Miller ingestion; they never
silently count as zero.

Kind-coverage JSON retains the field name `open_gaps`. That collection name is not a valid row status.

Both current reference-resolution tier rows are real open work:

- `reference_resolution.tier2_import` is `open` for each language outside the fixture-proven tier-2 set;
- `reference_resolution.tier3_receiver` is `open` for every supported language until receiver type facts meet
  the documented completeness gate.

At the schema-5 cut, the certified inventory is 36 languages and tier 2 is enabled for TypeScript and
JavaScript, so the independent producer invariant is `36 + (36 - 2) = 70` open rows. The test derives the same
formula from the certified language and tier-2 registries and also pins 70 for this contract revision. Adding a
language or closing a tier changes the contract fixture and expected count deliberately.

### 1.2 Canonical reference occurrences

Schema 5 adds a canonical `reference_sites` domain. A row represents one physical source occurrence and carries:

- stable `reference_site_id`;
- file identity and workspace-relative path;
- language;
- containing symbol ID when known;
- exact start/end byte and line/column span;
- occurrence provenance needed to explain normalization.

Identifier, relationship, and pending-relationship rows reference `reference_site_id`.

The normative site rule is source-owned and contains no consumer inference:

1. Every extractor emits the exact target-token span for an identifier, relationship, or structured pending
   relationship. An expression span may be retained separately, but it is not a reference-site span.
2. A spanned site ID is the producer's stable hash of `(file_id, start_byte, end_byte)`. Kind and target are not
   part of physical-site identity.
3. Rows with the same exact target-token span share the same site ID. Two same-name calls on one line remain
   distinct because their byte spans differ.
4. A producer that cannot attest a target-token span emits explicitly spanless evidence with a row-specific site
   ID and `is_exact=false`. It is never merged with a spanned site or exposed as editable evidence.

The extraction API carries the reference-site span through the shared relationship constructors. The CLI mapper
does not choose a nearest identifier, a smallest enclosing span, or an overlapping span after extraction.

Semantic assertions remain separate from physical sites. Agent-facing exact evidence is unique by
`(reference_site_id, target_symbol_id, canonical_kind)`; unresolved evidence is unique by
`(reference_site_id, target_name, canonical_kind)`. Direct identifier resolution, resolved relationship, pending
resolution, and name fallback attach as provenance to that assertion in descending precedence. Distinct targets
or distinct canonical kinds at one site survive as distinct assertions. Provenance never changes site identity.

### 1.3 Actionable marker facts

The shared source-region normalization pass emits one `code.marker.v1` structural fact per actionable physical
line across every supported language.

A marker is actionable when, after comment decoration and whitespace, the first semantic token is one of the
supported marker names. The token may be followed by an owner in parentheses and by a colon, dash, or
whitespace-delimited description. Prose that mentions marker names later in a sentence is not a marker.

The closed marker vocabulary is `TODO | FIXME | HACK | XXX`, matched case-insensitively and normalized to
uppercase. Multi-line block comments and doc comments are evaluated line by line after removing only that
line's comment decoration, including a leading block-comment `*`.

Each fact has:

- `pattern_id="code.marker.v1"`;
- `capture_name="marker"`;
- `node_kind="comment"` or `"doc_comment"`;
- confidence `1.0`;
- a span from the marker token through the semantic end of that physical line;
- metadata keys `marker`, optional `owner`, optional `description`, and `source_region_kind`.

Miller's marker route consumes these facts. It does not rescan raw comments to reinterpret annotation intent.

Language parity is mandatory: the same generic source-region rule runs for every language with comment regions,
and the real extract gate reports per-language fact counts. Languages whose grammar contract has no comment or
doc-comment region are reported explicitly as `not_applicable`; a silent zero is a failure.

## 2. Miller Tool Contract vNext

### 2.1 Strict request parsing

Every enum-like tool parameter follows one rule:

- null or empty selects the documented default;
- a known value selects that value;
- any other value returns a typed `invalid_<parameter>` refusal.

Unknown values never fall through to a default. The invalid-input matrix covers every enum-like parameter on all
nine tools.

### 2.2 Lossless bounded collections

Every agent-facing collection is either complete or supplies an opaque continuation cursor. A response may
recommend narrowing for efficiency, but narrowing is never the only way to retrieve omitted evidence.

All MCP responses retain the universal 12 KiB UTF-8 wire ceiling after diagnostic and next-step attachment.
Tool-specific logical bounds may be stricter: Context retains `token_budget`, Content retains its line/window
bounds, and paged tools retain row limits. Hitting any active bound is lossless.

Continuation identity binds:

- a discriminated continuation kind such as `inspect_body`, `inspect_file`, `patterns`, or `trace_refs`;
- workspace;
- normalized operation and filters;
- a SHA-256 fingerprint of the relevant canonical ordered population;
- page limit;
- next stable position.

The population fingerprint is computed from the row identity keys for that operation, not the workspace's global
revision. An unrelated file edit therefore does not invalidate a cursor; a change to the paged population does.
Supplying one operation's cursor to another returns `continuation_kind_mismatch`. A changed population returns
`stale_continuation`. Page renderers advance by the count of rows actually emitted, never by rows considered or
discarded while fitting the byte budget.

### 2.3 Inspect

Inspect validates `depth` as `summary | overview | full`.

File listings page through the existing `continuation` parameter. Their canonical order is
`(start_byte, end_byte, kind, symbol_id)`. The `inspect_file` cursor binds file path, optional kind filter,
population fingerprint, page size, and next offset. Compact and JSON page the same ordered population.

Full-body paging retains its source-span/extractor-hash identity under the distinct `inspect_body` discriminator.

### 2.4 Patterns

Exact `pattern_id` searches retain path/start-byte/fact-ID order.

Free-text fan-out uses deterministic fair ordering:

1. rank within each matched pattern ID by path, start byte, and fact ID;
2. order the union by rank-within-pattern, then pattern ID, path, start byte, and fact ID.

This returns one row per matched family before a second row from any family when the page is large enough.
Patterns returns a continuation cursor over that stable order. JSON and compact output expose the same recovery
path; the `limit=1` self-retry guidance is removed.

### 2.5 Trace

Trace reference JSON removes the redundant `references` array. The vNext shape contains:

- `exact_references`;
- `fallback_references`;
- tiered coverage;
- continuation.

Reference rows use the assertion keys defined by the artifact contract:
`(reference_site_id, target_symbol_id, canonical_kind)` for exact evidence and
`(reference_site_id, target_name, canonical_kind)` for fallback evidence. Exact evidence precedes fallback
evidence. Pages use the shared stateless continuation contract.

Trace's current local 16 KiB candidate-page mechanism is replaced by the shared 12 KiB envelope with reserved
space for diagnostics and continuation. The cursor advances only across reference rows actually rendered.

### 2.6 Diagnostics

All nine tools use the common `ToolDiagnostic` outcome model.

Edit and Content service results carry an optional typed diagnostic instead of encoding errors only as strings.
Expected no-match, ambiguity, refusal, unsupported behavior, unavailable dependencies, corruption, and internal
failure retain distinct class/outcome/error-channel behavior through compact output, JSON, telemetry, and CLI
exit handling.

Successful Edit payload fields remain operation-specific; diagnostic rendering is centralized.

### 2.7 Health and source hygiene

Workspace health parses the closed capability-gap status vocabulary exhaustively. Its rendered open count must
equal the direct SQL aggregate over `status='open'`, while the producer contract independently proves the
schema-5 expected count of 70 from the certified language/tier inventory.

The literal NUL in `FakeSemanticSidecar` becomes an escaped runtime delimiter. A repository gate scans tracked
text source for disallowed binary control bytes and fails with file/offset evidence.

Miller hard-requires schema 5 / extract contract 4 before opening any tool route. There is no legacy marker
compatibility branch or marker-specific pre-upgrade diagnostic: a schema-4 artifact is rejected at the existing
schema gate with the full-rebuild instruction.

## 3. Semantic Serving Policy vNext

The demonstrated defect is candidate admission. The existing fusion weights may also reorder the retained
lexical population, so rerank quality is measured separately rather than assumed harmless.

Routing and admission become separate pure decisions:

- routing decides whether the semantic arm runs and which fusion class applies;
- admission decides whether semantic-only candidates may enter and which lexical candidates are protected.

Admission does not reuse `LexicalEvidence.IsStrong`. Its closed policy is:

- zero lexical hits: expand from semantic evidence;
- one lexical hit: allow expansion but protect that lexical hit from semantic-only displacement;
- two or more lexical hits with a positive runner-up and top-to-runner-up ratio at least `1.25`: rerank only the
  lexical population; semantic-only candidates cannot enter;
- all other multi-hit populations: rerank and expand;
- identifier, path, short-token, empty-query, and code-syntax routes remain lexical-only before admission.

The rule applies uniformly to every hybrid fusion class, including Conceptual. Conceptual's current
`(0.5 lexical, 1.0 semantic)` weights can reorder lexical candidates in rerank-only mode, so the evaluation has
separate hard gates for candidate admission, protected-rank preservation, and lexical-population rerank quality.
The weights remain unchanged only if those gates pass; “unchanged” is not treated as proof of isolation.

The observed output-budget query becomes a visible labeled evaluation row. Production promotion requires the
revised policy to pass the full open evaluation and a newly frozen sealed slice; one query cannot authorize a
weight change.

`SemanticQueryPolicy.Version` is the single integer source of truth and moves to `2`.
`CanaryCallFacts.PolicyVersion`, shadow defaults, cohort keys, exports, and evaluation rows receive that value
explicitly; no call-site default remains. The semantic policy version stays separate from encoder identity,
fusion profile, and the sidecar's unrelated instruction-policy version.

## 4. Owned Eros Integration

Eros is unreleased and owned. Compatibility with its current Miller adapters is not preserved.

The same integration cut replaces every affected public process contract and updates Eros directly:

- `patterns --json` moves to schema 2 for continuation-bearing result envelopes;
- `references export --jsonl` moves to schema 2 and replaces identifier-row identity with
  `reference_site_id`, target identity, canonical kind, resolution tier, span, and provenance;
- `capabilities --json` advertises those exact versions;
- Eros replaces its exact version constants, models, parsers, fixtures, and tests with the vNext shapes.

No compatibility parser, dual-write field, fallback to `identifier_id`, or version range is added. Miller and
Eros must be green together before the cut is accepted.

Eros remains on Miller's documented process/JSON contracts. It does not gain direct access to schema-5 SQLite
internals.

`julie-semantic-sidecar` requires no change for the candidate-admission policy because it returns ranked vector
hits and does not decide which candidates Miller serves.

## 5. Delivery Order

1. Implement schema 5 / contract 4, canonical reference sites, typed gap status, and marker facts in
   `julie-extractors`.
2. Publish the authoritative schema-5 DDL/contract fixture from `julie-extractors`; producer tests compare the
   writer-created SQLite catalog with that checked-in authority.
3. Produce a real all-language artifact and verify per-language/reference-site/status invariants.
4. Build `julie-extract` from the current producer worktree, bump Miller's pin, restore the binary, update
   contract fingerprints/fixtures from the authoritative producer contract, and force a full artifact rebuild.
   Miller fixture guards compare normalized DDL and contract fingerprints so hand-maintained drift fails.
5. Consume the new artifact without legacy misspelling, nearest-span, or overlap-deduplication fallbacks.
6. Implement strict input parsing, shared paging, Inspect, Patterns, Trace vNext, diagnostics, health, and source
   hygiene.
7. Implement semantic policy vNext and update visible evaluation data.
8. Replace Eros's affected Miller contract models and parsers in the same cut, with no compatibility lane.
9. Run cross-repo contract, fast, build, scale, all-language, semantic evaluation, Eros, and live nine-tool dogfood
   gates.

No tag, push, package publication, deployment, or release occurs without explicit user approval after the clean
verified state is reported.

## 6. Verification Strategy

### Producer gates

- `julie-extractors` unit and contract tests prove the closed status vocabulary and schema/JSONL versions.
- The writer-created SQLite catalog matches the checked-in authoritative schema-5 DDL and contract fingerprint.
- Golden extraction proves canonical reference-site identity across identifier, relationship, and pending rows
  without post-hoc overlap matching.
- Two same-name calls on one line remain distinct.
- Same-site assertions with different targets or kinds remain distinct.
- All supported languages with comment regions exercise single-line, block-line, doc-comment, actionable, and
  prose-only marker examples; commentless grammars report `not_applicable`.
- A real all-language extract reports `language`, fact/reference kind, and count.
- Schema-5 capability gaps contain exactly 70 open rows for the pinned 36-language/two-tier2-language inventory.

### Consumer gates

- Miller fixtures carry the authoritative producer contract fingerprint, and a guard proves their normalized DDL
  matches the producer-owned schema-5 authority.
- A real artifact from the current `julie-extractors` worktree passes Miller health, references, patterns, and
  marker probes.
- Health's rendered open-gap count equals the SQL aggregate.
- Every invalid enum request returns the expected typed refusal.
- Every truncated collection returns a valid discriminated continuation; replay survives unrelated revisions;
  relevant population changes and continuation-kind mismatches refuse with their typed diagnostics.
- No MCP response exceeds 12 KiB; every JSON response parses; no result row is duplicated across compatibility
  fields.
- Focused TDD tests prove each repaired behavior before production code changes.

### Semantic gates

- The observed output-budget query keeps directly relevant lexical evidence in the served top set and excludes
  unrelated semantic-only injection under decisive multi-hit evidence.
- A single lexical hit remains first while semantic expansion stays available.
- Weak and empty lexical cases still gain semantic-only candidates.
- Conceptual rerank-only cases satisfy the lexical-population ordering gate under the existing weights.
- Identifier/path lexical byte parity remains exact.
- Open evaluation hard gates pass before a new sealed slice is run.
- The sealed slice determines promotion or rejection of policy vNext; weights are not tuned after sealed data is
  visible.

### Branch gates

- Miller: `scripts/test.sh`, Release build, and `scripts/test.sh scale`; `scripts/test.sh all` before final
  integration handoff.
- `julie-extractors`: repository-documented fast, contract, language-parity, and release-build gates.
- Eros: repository-documented affected and broad gates are mandatory because its Miller contracts change.
- Final live dogfood exercises success, empty, invalid, pagination, output-budget, and cross-workspace paths for
  all nine Miller tools.

## 7. Acceptance Criteria

- [ ] The producer emits only typed capability-gap statuses and Miller rejects unknown values.
- [ ] Health reports the same open-gap count as direct artifact SQL.
- [ ] Duplicate projections of one site/target/kind assertion produce one agent-facing row.
- [ ] Distinct same-line occurrences remain distinct.
- [ ] Distinct targets and kinds at one physical site remain distinct.
- [ ] Marker results exclude vocabulary prose and retain actionable annotations across supported languages.
- [ ] Invalid Inspect depth and every other invalid enum-like parameter return typed refusals.
- [ ] Every omitted agent-facing collection has a discriminated, population-bound deterministic continuation.
- [ ] Free-text Patterns pages represent matched pattern families fairly.
- [ ] Trace JSON contains no duplicate compatibility union and stays within 12 KiB.
- [ ] Edit and Content use the common diagnostic model end to end.
- [ ] Tracked source contains no disallowed binary control bytes.
- [ ] Semantic admission never treats a one-hit/zero-runner-up population as decisive.
- [ ] Decisive multi-hit evidence prevents semantic-only top-set injection while a single lexical hit is
      protected and still permits expansion.
- [ ] Weak/empty lexical semantic recall remains available and measured.
- [ ] Cross-repo real-artifact, all-language, fast, build, scale, semantic, and live dogfood gates pass.
- [ ] No owned integration remains on the replaced contract.

## Rejected Alternatives

### Miller-only compensation

Accepting multiple status spellings, clustering overlapping spans, and filtering marker prose downstream would
keep extraction ambiguity alive and force every future consumer to repeat Miller's guesses.

### Preserve defective compatibility arrays

Keeping duplicate Trace fields spends tokens and lets stale consumers prevent a better owned contract. Eros is
unreleased and moves atomically with the replacement.

### Shared Rust/C# runtime library

A common runtime package would couple release trains and implementation languages. The versioned artifact and
process contracts are sufficient seams.

### Immediate semantic weight tuning

One organic query is enough to expose a policy defect but not enough to select new weights. Candidate admission
must be corrected first and evaluated independently.
