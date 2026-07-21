### Task 5: Symbol-route canary — assignment flip, arm serving, facts, result hashes

**Files:**
- Modify: `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (`CanaryAssignment.ResolveArm` :166 — flip to `bucket < 50 ? Control : Treatment` per the doc comment)
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (orchestration in `Search` around :400-467; `FusionArm` injection point :430; `RenderSymbolCandidates` :1130-1183 — expose the served page slice; rescue-kind copy at the :450-454 site)
- Create: `src/Miller.Core/Search/CanaryQueryClassifier.cs`
- Test: `tests/Miller.Tests/Core/CanaryQueryClassifierTests.cs`, `tests/Miller.Tests/Server/CanarySearchTests.cs` (new)

**Interfaces:**
- Consumes: `CanaryActivation.FromEnvironment()`, `CanaryTelemetry.Stamp` + `CanaryCallFacts`/`CanaryServedResult` (all exist), Task 3's `SemanticQueryDiagnostics`, `SemanticQueryPolicy.Route`, `RrfFusion`/`FusedCandidate` ranks, `VectorSidecar` state probe (reuse the CLI probe approach at CliDispatch :559-566 / `VectorSidecar.TryOpen`), `TelemetryContext.Current` scope + its row timestamp (align `CanaryCallFacts.UtcDate` with the scope's persisted `ts`).
- Produces: `CanaryQueryClassifier.Classify(string op, string? query, SemanticQueryRoute route) : string` returning exactly one of the six frozen `query_class` values. Mapping (deterministic, fully test-pinned): reason `Empty`/`Short` → `short_token`; `IdentifierLike`/`CodeSyntax` → `identifier`; `PathLike` → `path`; `Prose` → `docs_like` when `op == "content"` or the query contains a word from a small fixed docs-vocabulary set (`readme, docs, documentation, config, configuration, guide, install, setup, changelog, license, tutorial, faq`), else `prose`; `AmbiguousWeakLexical`/`AmbiguousStrongLexical` → `mixed`. Also produces the per-call orchestration helper in `SearchTool` that Tasks 6/7 reuse: computes eligibility ladder → assignment → picks `FusionArm` (treatment ⟹ production `SemanticSymbolFusionArm` with the mode gate bypassed — treatment must behave exactly like `MILLER_SEMANTIC=on`; control ⟹ null) → assembles `CanaryCallFacts` → `Stamp`s on the ambient scope. And the finalize seam: `RenderSymbolCandidates` (or an overload) additionally returns the served page slice so served-result hashes cover exactly the rendered page; parent names for the ≤10 served rows resolved at stamp time via `index.FindBySymbolId(SymbolId).ParentId` → parent's `Name` (one-level `Parent.Member` only).
- Eligibility ladder order (first match wins): canary off ⟹ no keys at all; op outside {auto,text,symbol,content} ⟹ `ineligible_surface`; `MILLER_SEMANTIC=off` ⟹ `ineligible_semantic_disabled`; query class ∉ {prose,docs_like,mixed} ⟹ `ineligible_query_class`; no artifact / building / downloading / disk-blocked ⟹ `ineligible_vectors_unavailable`; fingerprint mismatch ⟹ `ineligible_vectors_incompatible`; circuit open ⟹ `ineligible_circuit_open`; foreign-workspace read with no ready generation ⟹ `ineligible_cross_workspace_no_generation`; else `eligible`.

**Contract inputs:** Contract §Assignment (unit = workspace_id × utc_date × query_class; bucket<50 = control), §Field Reference write conditions (literal), §Ineligible calls (ineligible rows record arm/eligibility/query_class and nothing else semantic; served behavior byte-identical lexical). `query_class` note: the classifier input route must be computed with `LexicalEvidence.None` for classification purposes (class must be recomputable offline from the query alone; evidence only affects the *treatment arm's* internal hybrid/lexical decision, never the class or the assignment). Treatment rows where the policy's evidence check kept the call lexical: `fallback_reason=none`, semantic counters absent (the arm didn't run) — representable per the field table.

**File ownership:** `src/Miller.Server/Tools/SearchTool.cs`, `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (ResolveArm flip), `src/Miller.Core/Search/CanaryQueryClassifier.cs`, the two test files.

**Serialization required:** Yes

**Dependency reason:** Consumes Task 3 diagnostics; edits files owned by Tasks 2/3 in earlier batches.

**What to build:** The experiment goes live on the symbol route (ops auto/text/symbol). With canary off: zero canary keys, zero added work, byte-identical behavior (test-enforced). With canary on: every instrumented call records the contract row; eligible units split 50/50; treatment serves the production hybrid path; control and all ineligible calls serve today's lexical path byte-identically.

**Approach:** TDD with the fake sidecar/store fixtures from P2/P3 tests (contract-faithful). Include the contract's six attribution conformance cases at the stamping level (served-result arrays produce the exact digests the matching rule expects — shared fixture with Task 2's gate tests if convenient). Auto-op rescue: copy the existing `auto_rescue_kind` value into `canary_rescue_kind` (map `rescue==null` to `none`; keep `unavailable` as-is).

**Acceptance criteria:**
- [ ] Canary off ⟹ no `canary_*` keys and byte-identical output (golden test).
- [ ] Eligible call: arm from the frozen derivation (test vectors pin bucket values for fixed inputs); control serves lexical byte-identical; treatment serves fused output identical to `MILLER_SEMANTIC=on` for the same fixture.
- [ ] Facts written per the field table: counters, fallback/backend/warmth/latency buckets, identity fields, the three hash arrays + shared truncation flag (11-result fixture proves the cap and the flag).
- [ ] `CanaryQueryClassifier` table-driven test pins all six classes incl. the docs-vocabulary set and `op=content` promotion.
- [ ] Ineligible rows record exactly arm/eligibility/query_class (+ contract/experiment/assignment/policy version keys) and nothing else.
- [ ] Worker-scope verification passes and the change is committed per commit mode.

