# M4 — implementation plan (v3.2)

> Historical status: implementation plan for the original cross-language resolver. Provider-scoped bridge behavior
> is the current direction; see [`2026-06-05-bridge-provider-scope-design.md`](2026-06-05-bridge-provider-scope-design.md)
> and current tool docs before treating any unchecked task below as open.

Companion to [2026-05-30-m4-cross-language-resolver-design.md](2026-05-30-m4-cross-language-resolver-design.md).
**Read the design first, and read [findings/m4-extract-reality-28-2.md](../findings/m4-extract-reality-28-2.md)** —
every task is written against *verified* julie 28/2 output, triangulated by a codex review + an 8-agent verification
workflow (incl. a completeness critic) + direct SQL.

**v3.2 changes (post v3.1 re-review — codex + SQL):** (1) url-literal filter uses julie's FULL language strings
(`typescript`, not `ts`/`js`/`vue` which match 0 rows); (2) response-DTO bucket corrected to **39/71 excl. primitives**
(the lone `Task<bool>` dropped; old 40/71 double-counted it; targets may be interfaces e.g. `IProject`); (3) the
candidate payload now carries a `NameResolution{status, matchCount}` signal so *ambiguous-name → never High* is
decidable by the scorer from the payload alone (Task 4).

**v3.1 changes (post triple-review):**
- **Leg 1 / RouteNormalizer (Tasks 3, 7):** expand ASP.NET `[controller]`/`[action]`/`[area]` route tokens using the
  parent class name BEFORE prefix concat — 21/23 controllers use `Route("api/[controller]")` literally; without
  expansion the flagship route leg matches ZERO endpoints. Plus: filter url literals to frontend-language + non-test
  (39 TS, not 96), derive the verb conditionally (verb-unknown for fetch/ky/got/wrappers), make the response-DTO edge
  conditional (~55%, balanced-bracket unwrap), add a `consumes→` request-DTO edge.
- **Leg 2 (Task 6):** CreateMap use-site is `kind='type_usage'` (NOT `'call'`); ordinal = copy-source→dest (NOT
  entity→DTO) — classify entity/DTO from independent signals; handle ReverseMap. ToDto UNVERIFIED; inline-projection
  WEAK/corroborator-only.
- **Leg 3 (Task 5):** read DbContext property symbols directly (the v2 `containing_symbol_id` join → DbContext class).
- **Task 4:** candidate `signals[]` are TYPED records with payload (fieldCount/Jaccard/name-tier), not bare names —
  else the scorer can't enforce "1-field can't anchor" without leg-side logic.
- **Task 3:** `SymbolResolver` (name-based resolution + collision handling; `target_symbol_id` is NULL at extract).
- **Task 1:** data-gates + two new fixtures (fetch-based frontend, manual-mapping backend) before cross-repo claims.

**Conventions (binding):** TDD (failing test first, against `Miller.Core`); default `dotnet test` < 10s
(`Category!=Scale`); julie-spawning tests `[Trait("Category","Scale")]` + `ScaleTestSupport.RequireJulieServer()`;
build 0 warnings / 0 errors; use Julie/Eros MCP to verify unfamiliar julie symbols; each task ends green.

---

## Task 0 — Re-pin to 28/2 (mechanical; external blocker cleared)

julie **v7.13.0** is tagged; the local binary emits schema 28 / contract 2.
- **Local, now:** `MillerExtractContract.cs:13-14` → `ExpectedSchemaVersion = 28`, `ExpectedExtractContractVersion =
  2`; `PinnedJulieServerVersion = "7.13.0"`; docstring. Flip version-assertion tests (synthesize DBs, no binary):
  `JulieSchemaGateTests`, `ExtractReportParsingTests`, `JulieExtractRunnerTests`, `JulieDbFixture`,
  `IndexBootstrapServiceTests`, `LargeDbWriter`; update the "newer schema rejected" probes.
- **External, when assets publish:** `scripts/julie-pins.json` ← v7.13.0 + 4 sha256s; `scripts/restore-julie-server.sh`;
  confirm restored binary reports 28/2.

**Test:** version-assertion suite green at 28/2 (fast suite).

---

## Task 1 — Data verification FIRST (no resolver code against an unverified column)

- [x] Extract MyraNext (local 7.13.0); inspect real rows → [findings/m4-extract-reality-28-2.md](../findings/m4-extract-reality-28-2.md).
      Triangulated by codex + 8-agent workflow + SQL.
- [ ] Extract **Tycho** + **LabHandbookV2**; confirm the same column shapes + signal presence; **confirm the
      `[controller]` token-routing pattern holds** (or record where it differs).
- [ ] **New cross-repo fixtures (gate):** obtain/build (a) a **fetch-based** frontend repo and (b) a **manual-mapping**
      (non-AutoMapper, `ToDto`/projection) backend repo. The fetch and ToDto legs are UNVERIFIED until these exist; do
      not grade them STRONG before.
- [ ] **Per-repo data gates (record the numbers, gate the grades):** % controller actions with a concrete
      (balanced-unwrappable) return type vs bare `ActionResult`/`IActionResult`; count of `ToDto` methods + inline
      `new XDto{}` projections; url-literal language split (TS vs C# test HttpClient) + carrier verb-vs-verbless
      distribution; DbSet property path yields name+entity (and `containing_symbol_id` → class, not property); route
      raw_text token distribution (`[controller]`/`[action]` vs absolute).
- [ ] Every Core input field in Task 2 traces to a verified column.

**Exit:** the reality doc is the binding contract for Tasks 2+.

---

## Task 2 — Core input contracts (pure types)

`Miller.Core/Contracts/`, each field traceable to a verified column:
- [ ] `TypeArgument` (identifier_id, ordinal, parent_arg_id, type_name, file_path). `target_symbol_id` NULL → resolve
      by `type_name`.
- [ ] `LiteralRecord` (literal_text, kind, **carrier = callee**, arg_position, **language**, containing_symbol_id, span).
- [ ] `SymbolAnnotation` (symbol_id, ordinal, annotation, annotation_key [lowercased], **raw_text**, carrier).
- [ ] `DbSetProperty` (property symbol id, property name = table, entity type from `DbSet<T>`, file:line) — parsed from
      `symbols` (kind=property, signature has `DbSet<…>`), NOT from type_arguments.
- [ ] `FieldSet` (owner id, ordered field names+types) — source = child symbols via `parent_id` PLUS C# record
      positional params parsed from `signature`.
- [ ] Extend symbol detail with `TestRole?` from `symbols.metadata`. For controller methods, carry the **parent class
      name** (needed for `[controller]` expansion in Task 3).

---

## Task 3 — Pure normalizers + SymbolResolver (table-driven; precision lives here)

`Miller.Core/Resolver/`. Case table first.
- [ ] `RouteNormalizer` → `(verb, normalizedRoute)`.
      - **verb-known** (carrier tail = HTTP verb or `<Verb>Async`) vs **verb-unknown** (fetch/$fetch/ofetch/bare
        axios/request/ky/got/sendasync → route-only, reduced confidence, never assume GET).
      - **[v3.1] token expansion BEFORE prefix concat:** `[controller]` → parent class name minus trailing
        "Controller", lowercased; `[action]` → method name lowercased; `[area]` → area. The normalizer takes the
        parent class name as input, not just the raw route string.
      - route cases: controller-prefix concat, absolute-override, `{param}`/`${param}`/`:param`→`{}`, trailing slash,
        case fold, query strip.
- [ ] `NameNormalizer` → canonical stem (I/_ strip; Dto/Model/Request/Response/View/VM/Entity strip; singular↔plural).
- [ ] `FieldSetExtractor` → `FieldSet`. Cases: class properties via `parent_id`; **C# record positional params from
      `signature`**; `[JsonProperty]` rename from `raw_text`; wrapper unwrap for transitive returns.
- [ ] **`SymbolResolver`** → resolve a `type_name` to a symbol by NAME (target_symbol_id NULL). Tie-break by
      namespace/project; **>1 match with no tie-break → ambiguous (caller drops or lowers confidence, never High).**

**Test:** one `[Theory]` per normalizer; rows for **`[controller]`/`[action]` expansion**, the **route-collision
negative** (two controllers' `{id}` → distinct routes), verb-unknown carriers, record positional params, and the
name-collision case.

---

## Task 4 — BridgeScorer + full candidate contract, BEFORE the legs

- [ ] **Candidate-edge model:** `{kind, sourceRef, targetRef, evidence[], signals[], sourceFieldSet?,
      targetFieldSet?}`, no final score yet. Field-sets MUST be on the tuple (the §5 Jaccard corroborator needs them).
      **[v3.2] `sourceRef`/`targetRef` each carry the SymbolResolver outcome `{status: resolved|ambiguous|unresolved,
      matchCount}`** so ambiguity is visible in the payload (it backs the `NameResolution` signal + the scorer's
      ambiguous-never-High rule).
- [ ] **[v3.1] `signals[]` are TYPED Signal records, not bare names:** `{rule, value, evidence}` where field-set
      signals carry `fieldCount`+Jaccard, name signals carry match-tier (`exact|affix`), **[v3.2] a `NameResolution`
      signal carries `{endpoint, status, matchCount}` per edge side**, and structural signals
      (`CreateMap, DbSetProperty, RouteVerbMatch, RouteOnlyMatch, ReturnTypeDto, FromBodyDto, DapperFrom`) carry a
      boolean+evidence. The §5 rules — **including ambiguous-name-never-High (scorer reads `NameResolution.status`,
      never re-queries the resolver)** — must be decidable from the payload alone.
- [ ] `BridgeScorer`: §5 bands — High for explicit breadcrumbs (CreateMap, DbSetProperty, RouteVerbMatch after token
      expansion); Medium for name+corroborator or RouteOnlyMatch; **FieldSetJaccard NEVER sole**; **1-field shapes
      (read `fieldCount`) can't anchor**; **ambiguous-name edge NEVER High**; multi-signal boost.

**Test:** corroborator-only invariant proven from the PAYLOAD: `fieldset{count=1}` → no edge;
`fieldset{count=8,jaccard=0.6}` + a structural signal → scores; ambiguous-name never High; multi-signal outscores
single — all against synthetic candidates before legs exist.

---

## Task 5 — Leg: Entity ↔ table (build first; strongest, fewest deps) [v3 corrected]

`Miller.Core/Resolver/EntityTableBridge`. Emits candidates; scorer scores.
- [ ] **Primary:** consume `DbSetProperty` records (Task 2) → `CsEntity —stored_in→ DbTable` where table = property
      name, entity = generic arg. **Do NOT use the DbSet use-site `containing_symbol_id`** (→ DbContext class).
      **Guard:** if a symbol reached via `containing_symbol_id` has `kind!='property'`, never use its name as the table.
- [ ] `[Table("X")]` when present (`symbol_annotations` key=table, arg from `raw_text`). Pluralizer last-resort only.
- [ ] Dapper `FROM` opportunistic: `kind=sql` literal containing `FROM`, paired to its `T` by span-proximity; FROM
      table only, never JOINs/multi-map.

**Test:** `ApplicationUser` → table `ApplicationUsers` (**not** `MyraNextContext`); `Preferences`→`Preferences`,
`AppSetting`→`AppSettings` (proves property name, not pluralized entity); stored-proc SQL literal with no FROM → no
table edge.

---

## Task 6 — Leg: DTO ↔ Entity [v3/v3.1 re-graded]

`Miller.Core/Resolver/DtoEntityBridge`.
- [ ] **CreateMap (STRONG):** `type_arguments` where `identifiers.name='CreateMap'` and **`kind='type_usage'`** (NOT
      `'call'`), grouped by `identifier_id`, read ordinal 0/1. **[v3.1] ordinal = copy-source→copy-dest, NOT
      entity→DTO** — resolve both sides via `SymbolResolver`, classify which is the entity from independent signals
      (namespace/folder, `Dto/Request/Response/VM` suffix, DbSet membership), tag the edge source→dest. Detect a
      sibling `ReverseMap` and emit the inverse edge. (An inbound `CreateMap<XRequest,Entity>` must NOT be mislabeled.)
- [ ] **ToDto (UNVERIFIED):** parse extension-method `signature` when present; **leg ungraded until the manual-mapping
      fixture exists** (Task 1). Behind a feature check, not claimed STRONG.
- [ ] **inline projection (WEAK):** DTO ctor from `identifiers`; source entity by name-overlap over `code_context`;
      **corroborator-only, never High.**
- [ ] field-renaming projection + `[JsonProperty]` as field-level corroborators.

**Test:** CreateMap directional incl. the zero-shared-field case; an **inbound `CreateMap<XRequest,Entity>` is tagged
correctly** (entity/DTO from independent signals, not ordinal); inline projection never High alone; ToDto exercised
only by the manual-mapping fixture.

---

## Task 7 — Leg: TS call ↔ C# endpoint (route bridge) [v3/v3.1 re-grounded]

`Miller.Core/Resolver/RouteBridge`.
- [ ] TS side: `kind=url` literal **filtered to a frontend/client `language` (julie's FULL strings: `typescript`/
      `javascript`/`vue`/…, NOT `('ts','js','vue')` which match 0) AND not test_role** — equivalently
      `literal.language != endpoint language`; on MyraNext = the 39 `typescript` literals, excluding the 57 `csharp`
      test HttpClient literals. Contract test: 39 survive, 57 do not. Verb via `RouteNormalizer` verb-known/unknown. Containing TS function from
      `containing_symbol_id`. No type argument read.
- [ ] C# side: verb from `annotation_key`, route from `raw_text`, prefixed by class `[Route]`; **wire the parent class
      name into `RouteNormalizer` so `[controller]`/`[action]` expand** (parent_id→class join is 71/71).
- [ ] Match `(verb, route)`; verb-unknown TS calls match on route alone at reduced confidence. Emit `TsCall —hits→
      Endpoint`.
- [ ] **`responds→` (PARTIAL):** only when the endpoint `signature` return **unwraps by balanced bracket depth** to a
      **named user type (class/interface/record, incl. collection element like `IProject`)** — **39/71 excl. primitives
      (~55%); the lone `Task<bool>` is dropped (old 40/71 double-counted it)**; bare `ActionResult`/`IActionResult`
      (31/71) → no edge, no penalty.
- [ ] **`consumes→`:** request DTO from the method's `[FromBody]`/parameter type in `signature`.

**Test:** `Route("api/[controller]")`+`HttpGet("{id}")` on `AppSettingsController` → `api/appsettings/{}`;
**collision negative** — two controllers' `{id}` → distinct routes; near-miss route does NOT match; a `fetch`/verb-less
carrier → route-only (not assumed GET); a C# test HttpClient url literal is excluded; bare-return endpoint → no
`responds→`; `[FromBody]` → `consumes→`.

---

## Task 8 — BridgeGraph assembly + queries (Core)

- [ ] `Miller.Core/Graph/BridgeGraph`: nodes (`TsType`/`CsDto`/`CsEntity`/`DbTable`/`Endpoint`), typed
      evidence+score edges; adjacency + `Walk(start,…)`. Mirror `SymbolGraph` immutability/atomic build.
- [ ] `BridgeGraphBuilder`: run all legs → candidates → `SymbolResolver` (name resolution + ambiguous handling) →
      scorer → graph. Deterministic ordering.
- [ ] `SymbolGraph.ShortestPath(from,to,maxDepth)`: BFS + parent reconstruction, deterministic tie-break.

**Test:** combined fixture yields the `UserDto → IUser / ApplicationUser → ApplicationUsers` chain; an ambiguous type
name does NOT produce a High edge; `ShortestPath` correctness incl. no-path + tie-break determinism.

---

## Task 9 — Indexing: read new tables + DbContext properties + test_role (infra)

`Miller.Indexing`.
- [ ] `SqliteBridgeReader`: SELECTs for `type_arguments`, `literals` (with language), `symbol_annotations`, and
      **DbContext property symbols** (`kind=property, signature LIKE '%DbSet<%'`) → Task 2 records (relative paths,
      opaque ids, annotation args parsed from `raw_text`). Carry controller methods' parent class name.
- [ ] `SqliteSymbolReader`: extend metadata parse to read `test_role` alongside `is_test`.
- [ ] `RepositoryIndexLoader.Load()`: after symbol/graph build, run `BridgeGraphBuilder`, publish `BridgeGraph` in
      `MillerRepositoryIndex`. `FreshnessService.Swap()` refreshes it for free.
- [ ] **Measure** BridgeGraph build cost at load — the cost driver is name resolution over the code-symbol set
      (~5–7k), not the breadcrumb set (design §3/§8). Persist only if over budget; key =
      schema+contract+resolver-version+workspace+sorted-file-hashes.

**Test:** contract tests against a synthesized 28/2 DB; assert reader→Core mapping + populated `BridgeGraph` after
`Load()`. Fast suite.

---

## Task 10 — `trace` MCP tool (Server)

`Miller.Server/Tools/TraceTool.cs`, `[McpServerToolType]`/`[McpServerTool]` + pure `Run()`.
- [ ] Inputs: `target`, `mode=auto|path|bridge`, `to`, `depth`, `limit`, `format=compact|full`; smart-string target.
- [ ] `auto`→`SymbolGraph` callers+callees; `path`→`ShortestPath`; `bridge`→`BridgeGraph.Walk`.
- [ ] Token-thrifty output (design §6): compact chain w/ scores; **verb-unknown + ambiguous-name flags shown**; full
      adds firing signals + competing candidates.
- [ ] Telemetry via the existing interceptor/`Measure`; `WithToolsFromAssembly`.

**Test:** `Run()` unit tests on in-memory fixtures for all three modes + output shape (incl. flags). No MCP/DI.

---

## Task 11 — End-to-end Scale validation + honesty probe (needs published binary)

- [ ] `[Trait("Category","Scale")]`: extract a polyglot fixture via `ScaleTestSupport.RequireJulieServer()`; assert a
      known bridge set resolves with expected scores.
- [ ] **Honesty probe (design §7/§8):** run on a deliberately mismatched-naming fixture; record precision; confirm
      corroborator-only + ambiguous-name-never-High + route-collision guards hold. **Measure recall on the CONTRACT,
      PER LEG** (CreateMap / DbSet / route / response-DTO separately — they differ widely). Write results into the
      design appendix. The "shippable to unknown users" gate.

**Test:** `scripts/test.sh scale` green with the binary; skips cleanly without it.

---

## Sequencing

```
Task 0 (re-pin)   Task 1 (verify data + new fixtures/gates)
Task 2 (input records incl. DbSetProperty + parent class name) → Task 3 (normalizers + token expansion + SymbolResolver)
  → Task 4 (scorer + typed-signal candidate contract)
    → Task 5 (entity↔table via property) → Task 6 (dto↔entity) → Task 7 (route)   [legs emit candidates; scorer scores]
      → Task 8 (BridgeGraph + SymbolResolver wiring + ShortestPath)
        → Task 9 (infra read) → Task 10 (trace tool)
          → Task 11 (Scale + per-leg honesty probe; needs published binary)
```
Tasks 1–8 are pure Core, TDD, no binary. Data-verify precedes everything; scorer (with the typed-signal candidate
contract) precedes the legs.

## Out of scope (do not silently include)
- Cross-repo / multi-workspace bridging.
- Persisting the BridgeGraph (in-memory; persist only if Task 9 measurement demands it, full cache key).
- `test_role` *consumers* (impact likely-tests) — M5; M4 only ingests + uses it to exclude test HttpClient calls.
- Grading ToDto / inline-projection / fetch-route / Dapper-FROM as STRONG before the Task 1 fixtures measure them.
- Embeddings / semantic anything (settled: rescues 0, adds FPs).
