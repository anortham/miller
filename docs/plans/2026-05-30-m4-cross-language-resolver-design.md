# M4 — Cross-language structural resolver + `trace` (design, v3.2)

**Status:** spec for review (v3.1 — revised after a real extract + codex adversarial review + an 8-agent verification
workflow incl. a completeness critic). M4 is Miller's differentiator: deterministic cross-language code tracing
nothing else does, with no embeddings. Captured 2026-05-30.

**Scope decision (from brainstorm):** build it *right* the first time — all three bridge legs + the `trace` tool
against the full chain, not a spine-only MVP. The "do it right" call is the user's explicit instruction.

**Grounding (do not re-derive — read these):**
- **VERIFIED extract reality (READ FIRST):** [findings/m4-extract-reality-28-2.md](../findings/m4-extract-reality-28-2.md)
  — what julie 28/2 *actually* captures, from a real MyraNext extract, triangulated by three independent reviews.
- 3-repo evidence: [findings/cross-language-bridge.md](../findings/cross-language-bridge.md) (NB: its recall numbers
  were measured on *source*; the *contract* captures a subset. Re-measure on the contract before quoting a number.)
- Memory: `xlang-bridge-resolver-design`, `codesearch-viability-verdict`. Seam: [findings/architecture-decision.md](../findings/architecture-decision.md)

**Review history:** v1 assumed column shapes (5 wrong). v2 re-grounded on a real extract but over-graded leg strength
+ got the DbSet join wrong. v3 folded in codex + 7 data-verifier agents. **v3.1** adds the completeness critic's
finding (ASP.NET `[controller]`/`[action]` route-token expansion — a hard break if missed) plus the CreateMap
copy-direction and signal-payload refinements. Corrections flagged inline as **[v3]/[v3.1]**.

---

## 1. What M4 delivers

**The capability:** follow a thread of code *across languages*, deterministically. Two bridge kinds:
1. **Type-correspondence chain** (data model): `IUser.ts → UserDto.cs → ApplicationUser.cs → ApplicationUsers (table)`.
2. **Call bridge** (control flow): a Vue/TS HTTP call ↔ the C# controller endpoint it hits, plus the shape it sends
   (request DTO) and receives (response DTO, when the endpoint exposes one).

**The tool:** `trace`, three modes — `auto` (callers/callees via `SymbolGraph`), `path` (shortest path), `bridge`
(the cross-language chain via the new `BridgeGraph`).

**Why this is the differentiator:** the 3-repo study proved the bridge is recoverable by cheap deterministic signals,
and that **embeddings rescue 0 concepts and would *add* false positives**. The resolver reads what the code *states*;
embeddings guess. **Honesty caveat:** the study's recall was source-measured; the contract captures a subset and each
leg's strength varies (§2/§4). M4 ships measured per-leg grades, not one headline number.

---

## 2. The contract bump (26/1 → 28/2)

M4 consumes julie's `miller-bridge` enrichment (v7.13.0, schema 28 / contract 2). Verified sources + real-data strength:

| Resolver signal | verified julie source (28/2) | strength on real data |
|---|---|---|
| `CreateMap<A,B>` ordered generic args | `type_arguments` via `identifiers.name='CreateMap'` **`kind='type_usage'`** | **STRONG** — 10 calls / 20 rows; ordinal = **copy-source→dest** (not entity→DTO) **[v3.1]** |
| DbSet entity + table name | the **DbContext property symbol** (`kind=property`, signature has `DbSet<T>`) | **STRONG** — 11; **[v3] NOT via use-site `containing_symbol_id` (→ the class)** |
| TS route + verb | `literals` `kind=url`, **frontend-lang + non-test**; verb from `carrier` only if it ends in a verb / `<Verb>Async` | **STRONG on axios.`<verb>`** — 39 TS; **[v3] verb-unknown for fetch/$fetch/ky/got; wrappers dropped by julie** |
| C# endpoint route + verb | `symbol_annotations` (`httpget/...`; route in `raw_text`) + class `[Route]`; **`[controller]`/`[action]` are literal tokens** | **STRONG once tokens expanded** **[v3.1]** |
| C# endpoint **response DTO** | endpoint method's `signature` return type, **balanced-bracket unwrapped** | **[v3.2] PARTIAL — ~55% (39/71 excl. primitives); 1 `Task<bool>` dropped; bare `ActionResult`/`IActionResult` (31/71, 44%) → no edge** |
| request DTO | `[FromBody]`/param type in method `signature` | **[v3] `consumes→` edge** — recovers the mutation case |
| `ToDto(this Entity) → XDto` | extension-method `signature` | **[v3] UNVERIFIED** — 0 in MyraNext; needs a manual-mapping fixture |
| inline `new XDto { ... }` projections | `identifiers` + `code_context` (no structured src→dest) | **[v3] WEAK** — name-overlap, corroborator-only, never High |
| field-sets; C# `record` positional params | child symbols via `parent_id`; records parsed from `signature` | **[v3] records have no property children → parse positional params** |
| `test_role` | `symbols.metadata` JSON | NEW field (not a resolver input; excludes test HttpClient calls + feeds M5) |

**[v3/v3.1] corrections folded in** (all triangulated by codex + workflow + SQL):
- DbSet→table: use the property symbol, not `containing_symbol_id` (→ DbContext class).
- "96 TS calls" = 39 TS + 57 C# test HttpClient — filter url literals by language + test_role.
- verb-from-carrier is not a julie invariant (verb-less carriers + dropped wrappers) → derive conditionally.
- response-DTO is partial (~55%); parse return types by **balanced bracket depth** (`Task<ActionResult>` vs
  `Task<ActionResult<X>>` differ by one token — substring match would misclassify all 25 bare ones).
- CreateMap ordinal encodes **copy data-flow (source→dest), NOT entity→DTO**; use-site `kind='type_usage'` not `'call'`.
- ASP.NET routes use literal `[controller]`/`[action]` tokens (21/23 controllers) — **must expand** (§4/§5).

**`test_role` is NOT a resolver input.** Used to exclude C# test HttpClient calls from the route bridge + feed M5.

**Gate stays exact-equality.** `MillerExtractContract` flips `26→28`/`1→2` (strict `!=`; both dials moved together,
no intermediate pair released). Local re-pin + version tests land now; `julie-pins.json` sha256s + Scale tests wait on
the v7.13.0 release assets.

---

## 3. Architecture — where everything lives

Respects the existing logic↔infra seam.

```
Miller.Core   (pure, ZERO I/O)
  • Contracts: TypeArgument, LiteralRecord, SymbolAnnotation, FieldSet, DbSetProperty
  • Resolver/
      - RouteNormalizer     (verb known/unknown; [controller]/[action]/[area] token expansion; route canonicalization)
      - NameNormalizer      (affix-fold, singular↔plural, I-/_ strip)
      - FieldSetExtractor   (properties via parent_id + C# record positional params)
      - SymbolResolver      (resolve type_name → symbol by NAME; namespace tie-break; ambiguous → drop/low, never High)
      - leg resolvers:      RouteBridge, DtoEntityBridge, EntityTableBridge
      - BridgeScorer        (confidence + corroborator-only invariant; typed Signal payloads)
  • Graph/BridgeGraph                (immutable, built once per index; mirrors SymbolGraph)
  • Graph/SymbolGraph.ShortestPath   (new: BFS path query)

Miller.Indexing
  • SqliteBridgeReader   reads type_arguments / literals (+language) / symbol_annotations + DbContext property symbols
  • SqliteSymbolReader   extend metadata parse to also read test_role
  • RepositoryIndexLoader runs BridgeGraphBuilder after the symbol/graph build, publishes BridgeGraph in
                          MillerRepositoryIndex (rebuilt on FreshnessService.Swap)

Miller.Server
  • TraceTool            (NEW) 3 modes, token-thrifty output, telemetry — [McpServerTool] pattern
```

### [v3] Cross-file resolution is by NAME (target_symbol_id is NULL at extract — verified 0/1797 type_args, 0/24830 identifiers)
Every leg resolves a `type_name` ("ApplicationUser") and a return-type to a symbol **by string name** across files —
julie ships zero resolved links. CreateMap is entirely cross-file (profile file → entity/DTO defs elsewhere). So
`SymbolResolver` is a named, tested Core component: resolve by name, tie-break by namespace/project; **>1 match with
no tie-break → ambiguous → the edge is dropped or emitted at reduced confidence, never High.** A negative test asserts
two same-named types in different files never auto-resolve to a High edge.

### Storage decision: in-memory, NOT a new SQLite DB
Built in-memory as a layer of `MillerRepositoryIndex`, like `SymbolGraph`, rebuilt on `FreshnessService.Swap()`. No
`bridges.db`.
- **ID-churn safety:** julie IDs are span-derived and churn on edits → always rebuild from the fresh index, never
  persist across an edit keyed on symbol id. (This is why in-memory rebuild, not persistence, is safe.)
- **[v3] Cost driver is name resolution, not the breadcrumb set.** Breadcrumbs are tiny (~10 CreateMap, 11 DbSet, ~71
  controller annotations, 39 TS calls), but the builder resolves names over ~5–7k code symbols (29,663 total). Likely
  sub-second; **unmeasured at monorepo scale.** Measure at `Swap()` (plan Task 9); persist only if over budget, keyed
  by schema+contract+resolver-version+workspace+sorted-file-hashes (never `files.hash` alone), never speculatively.

### Single workspace (one index), by design
M4 resolves bridges within one polyglot tree. Cross-*repo* bridging is out of scope.

---

## 4. The resolver — three legs

All legs emit **typed, evidence-carrying candidate edges**; `BridgeScorer` (§5) assigns confidence. Node kinds:
`TsType`, `CsDto`, `CsEntity`, `DbTable`, `Endpoint`. Each candidate carries: `kind`, `sourceRef`, `targetRef`,
`evidence[]` (file:line), `signals[]` (**typed Signal records — §5**), and optional `sourceFieldSet`/`targetFieldSet`.

### Leg 1 — TS call ↔ C# endpoint (route bridge) — STRONG on axios; conditional otherwise [v3/v3.1]
- **Source (TS):** a `kind=url` literal whose `language` is a **frontend/client language** AND not `test_role`.
  **[v3.2] Filter against julie's OWN language identifier strings** (`typescript`, `javascript`, `vue`, `svelte`, …) —
  julie stores full names, so the literal set `('ts','js','vue')` matches **0** rows. Equivalent language-agnostic
  phrasing: `literal.language != the endpoint language` AND not test. On `/tmp/mn.sqlite` the only two literal languages
  are `csharp=57` (all test HttpClient) and `typescript=39` (all real calls), so this yields exactly the 39 real TS
  routes. `literal_text` is the route (`{}`-folded). Containing TS function = `containing_symbol_id`. **Contract test:**
  the 39 `typescript` url literals survive the filter and the 57 `csharp` ones do not.
- **Verb derivation:** read the verb from `carrier` **only** when its tail-token is an HTTP verb (`axios.post`→POST) or
  `<Verb>Async`. For julie's verb-less url carriers (`fetch`, `$fetch`, `ofetch`, bare `axios`, `request`, `ky`,
  `got`, C# `sendasync`) the verb is in an uncaptured options arg → mark **verb-unknown** (reduced confidence, never
  assume GET). Custom wrappers (`useApi`, `apiClient`) are dropped by julie's bloat gate → **document the recall gap.**
- **Target (C#):** a method with `httpget/...` in `symbol_annotations` (verb from `annotation_key`, route from
  `raw_text`), prefixed by the class `[Route]`. The `parent_id`→class join is 71/71.
- **[v3.1] Route token expansion (load-bearing — the flagship-leg break if missed):** ASP.NET class routes are the
  literal token `Route("api/[controller]")` (21/23 controllers) and methods use `[action]`. **Before** prefix
  concatenation, RouteNormalizer must substitute `[controller]`→ parent class name minus trailing "Controller",
  lowercased; `[action]`→ method name lowercased; (`[area]` too). Without this, every C# endpoint normalizes to
  `api/[controller]/…` and matches **zero** TS literals like `/api/appsettings`. The normalizer therefore takes the
  **parent class name** as input, not just the raw route string.
- **Match:** `RouteNormalizer` → `(verb, normalized-route)`: token-expand → absolute-override > controller-prefix
  concat → `${param}`/`{param}`/`:param`→`{}` → trailing-slash + case fold + query strip. A verb-unknown TS call
  matches on route alone at reduced confidence. **Collision guard:** two controllers with identical method templates
  (`{id}`) must normalize to **distinct** routes (`api/appsettings/{}` vs `api/objectcodemappings/{}`), never merge.
- **Response DTO edge — PARTIAL:** `Endpoint —responds→ CsDto` only when the `signature` return type **unwraps (by
  balanced bracket depth)** `Task<>`/`ActionResult<>`/`IEnumerable<>` to a **named user type — class, interface, or
  record** (e.g. `AppSetting`, and interface/collection-element targets like `IProject` from `Task<IEnumerable<IProject>>`).
  **[v3.2] 39/71 excluding primitives (~55%)**; the unwrap also produces **one primitive — `Task<bool>`
  (`GetAwardFamilyHasSubawardExpenses`) — which is dropped**, so the earlier "40/71" double-counted that `bool`. Bare
  `Task<ActionResult>`/`Task<IActionResult>` (31/71, ~44%, mostly mutations) → **no edge, no penalty to the route
  match.** Optional recovery: parse `Ok(dto)`/`CreatedAtAction(…,dto)` from `code_context`.
- **Request DTO edge:** `TsCall/Endpoint —consumes→ CsDto` from the method's `[FromBody]`/parameter type in `signature`.

### Leg 2 — C# DTO ↔ Entity — STRONG only for CreateMap [v3/v3.1 re-graded]
- **AutoMapper `CreateMap<A,B>` (STRONG):** `type_arguments` where `identifiers.name='CreateMap'` and
  **`kind='type_usage'`** (NOT `'call'`), grouped by `identifier_id`, read ordinal 0/1. **[v3.1] ordinal = copy-source
  → copy-dest (AutoMapper declared order — a julie-tested invariant); do NOT infer entity-vs-DTO from ordinal.** All 10
  MyraNext maps are entity→DTO read maps *by luck of the fixture*; an inbound `CreateMap<CreateOrderRequest, Order>`
  legitimately puts the DTO at ordinal 0. Resolve both sides, then classify which side is the entity from independent
  signals (namespace/folder, `Dto/Request/Response/VM` suffix, or DbSet membership), and tag the edge source→dest.
  Detect a sibling `ReverseMap` and emit the inverse edge.
- **`ToDto(this Entity) → XDto` (UNVERIFIED):** 0 in MyraNext; parse extension-method `signature` when present, but
  **ungraded until a manual-mapping fixture exists** (Task 1).
- **inline `new XDto { ... }` projections (WEAK):** no structured source→dest signal; source entity by name-overlap
  over `code_context`. **Corroborator-only, never High.**
- **`[JsonProperty("x")]` renames:** `symbol_annotations` (arg from `raw_text`; affects field-set matching).

### Leg 3 — C# Entity ↔ DB table — STRONG via the DbSet PROPERTY symbol [v3 corrected]
- **Primary:** enumerate `symbols WHERE kind='property' AND signature LIKE '%DbSet<%'`. **Table = the property name;
  entity = the `DbSet<T>` generic arg** — both from one symbol row (11/11 correct). Do **NOT** follow the DbSet
  use-site identifier's `containing_symbol_id` (it points to the DbContext class → "MyraNextContext" for every table).
  **Guard:** if a symbol reached via `containing_symbol_id` has `kind!='property'`, never use its name as the table.
- **`[Table("X")]` (when present):** `symbol_annotations` `annotation_key=table`, arg from `raw_text`. `ToTable` is
  not captured. Pluralizer is a last resort only (would mis-map `Preferences`/`AppSettings`).
- **Dapper `FROM` (opportunistic only):** a `kind=sql` literal whose text contains `FROM` (rare on stored-proc repos —
  MyraNext: 2/15), paired to its `T` by span-proximity within `containing_symbol_id`. FROM-table only, never JOINs or
  multi-map. Never the primary anchor.

### The TS↔C# DTO finisher (name leg)
- `NameNormalizer`: strip `I`/`_`; strip `Dto/Model/Request/Response/View/VM/Entity`; singular↔plural. Exact-then-affix.
  The "safe finisher" — **never the sole signal** for an entity↔DTO or entity↔table edge.

---

## 5. Confidence scoring — the trust contract

Every bridge edge carries a score; `trace mode=bridge` shows it. The **corroborator-only invariant** is load-bearing.

**[v3.1] `signals[]` carries TYPED payloads, not bare rule names.** Each Signal = `{rule, value, evidence}` where:
field-set signals carry `fieldCount` + Jaccard; name signals carry match-tier (`exact|affix`); structural signals
(`CreateMap`, `DbSetProperty`, `RouteVerbMatch`, `RouteOnlyMatch`, `ReturnTypeDto`, `FromBodyDto`, `DapperFrom`) carry
a boolean + evidence. The closed rule set is defined in plan Task 4 *before* any leg emits, so the §5 rules are
decidable by the scorer from the candidate alone — no leg-side precision logic, no Task-5 retrofit. **[v3.2] Name
ambiguity rides in the payload too: each side carries a `NameResolution{endpoint, status: resolved|ambiguous|unresolved,
matchCount}` signal from `SymbolResolver`, so the *ambiguous-name → never High* rule (below) is enforced from the
candidate alone; the scorer never re-queries the resolver, and `unresolved` ⇒ no edge.**

- **High (≥0.9):** an explicit structural breadcrumb fired — `CreateMap`, `DbSetProperty`, or a `(verb, route)` match
  with a known verb (after token expansion). `DapperFrom` is High only when a real `FROM` literal is present.
  **An edge whose name resolves ambiguously (>1 symbol, no tie-break) can NEVER be High.**
- **Medium (~0.7–0.85):** exact/affix name match with ≥1 corroborator; or a route-only match (verb-unknown).
- **field-set Jaccard is NEVER a sole signal** — it only raises an existing edge. A 1-field/generic shape can't anchor
  (kills the `RevisionEntry ↔ DocumentRevisionDto` class of false positives). The scorer reads `fieldCount`/Jaccard
  from the Signal payload to enforce this — a bare `signals:["fieldset"]` could not. **field-set source = properties
  via `parent_id` PLUS C# `record` positional params from `signature`** (records have no property children → naive
  query gives an empty set and misfires the corroborator).
- **Multi-signal boost:** N independent signals score higher and are marked as such.

**Hardening rules (each closed a real precision/recall bug):** transitive return-type unwrap (balanced brackets);
record positional params; Dapper `FROM` vs JOIN; route normalization; **[v3.1] `[controller]`/`[action]` token
expansion + route collision guard; verb-unknown handling; name-collision suppression; test HttpClient exclusion;
CreateMap copy-direction (classify entity/DTO independently, handle ReverseMap).**

---

## 6. The `trace` tool

```
trace <target> [mode=auto|path|bridge] [to=<symbol>] [depth=N] [limit=N] [format=compact|full]
```
- **default / `auto`** — callers + callees from `SymbolGraph` (depth-bounded).
- **`to=<symbol>`** — shortest path via new `ShortestPath` (BFS + parent reconstruction; deterministic tie-break by id).
- **`bridge`** — walk the `BridgeGraph`: TS type → DTO/entity/table chain; TS call → endpoint + consumed/returned
  shapes; entity → its DTOs/TS types/table.

Token-thrifty `compact` default:
```
UserDto (C# DTO, Api/Dtos/UserDto.cs:7)
  ←name──────  IUser            (TS,     web/src/models/user.ts:3)              0.85
  ←CreateMap─  ApplicationUser  (C# entity, Domain/User.cs:12)  Profile.cs:24  0.98
               ApplicationUser
                 ←DbSet──────  ApplicationUsers (table; AppDbContext.cs:18, DbSet property)  0.97
```
`full` adds firing signals per edge + competing lower-scored candidates. Each edge ends in its score; **verb-unknown
route edges and ambiguous-name edges render with their reduced score + a flag** — nothing is presented as certain when
it isn't. Target resolution: smart-string (reuse inspect's resolver). Telemetry via the existing interceptor/`Measure`.

---

## 7. Testing

TDD throughout; the resolver is pure `Miller.Core`, unit-tested in milliseconds.

- **Normalizers (table-driven `[Theory]`):** `RouteNormalizer` (verb-known vs verb-unknown carriers; **`[controller]`/
  `[action]` expansion**; absolute-override; param folding); `NameNormalizer`; `FieldSetExtractor` (incl. C# record
  positional params); `SymbolResolver` (name collision → drop/low). Every §5 rule = ≥1 row.
- **BridgeScorer (focused):** field-set-only candidate → no edge; 1-field wrapper never anchors; ambiguous-name never
  High; multi-signal outscores single — fed by typed Signal payloads (`fieldset{count=1}`→no edge;
  `fieldset{count=8,jaccard=0.6}`+structural→scores). Proven before any leg exists.
- **Legs (in-memory fixtures) with mandatory negatives:**
  - Leg 3: `ApplicationUser` → table `ApplicationUsers` (**not** `MyraNextContext`); `AppSetting`→`AppSettings`.
  - Leg 1: `Route("api/[controller]")` on `AppSettingsController` + `HttpGet("{id}")` → `api/appsettings/{}`;
    **collision negative** — two controllers' `{id}` templates → two distinct routes; bare `Task<ActionResult>` → no
    `responds→`; `[FromBody]` → `consumes→`; a `fetch`/verb-less carrier → route-only (not assumed GET); a C# test
    HttpClient url literal is excluded.
  - Leg 2: CreateMap directional via copy-source/dest with **independent entity/DTO classification** (an inbound
    `CreateMap<XRequest,Entity>` is NOT mislabeled); inline projection corroborator-only; ToDto only via the
    manual-mapping fixture.
- **BridgeGraph + ShortestPath:** combined fixture yields the full chain; an ambiguous type name → no High edge; path
  correctness incl. no-path + tie-break.
- **Infra (contract tests):** `SqliteBridgeReader` vs a synthesized 28/2 DB; reader→Core mapping + populated
  `BridgeGraph` after `Load()`. Fast suite, no binary.
- **trace tool:** `Run()` unit tests for all three modes + output shape (incl. flags).
- **Scale (opt-in, `[Trait("Category","Scale")]`):** extract a polyglot fixture via `RequireJulieServer()`; assert a
  known bridge set + scores. Skips without the binary.
- **Honesty probe (acceptance gate):** run on a deliberately undisciplined fixture; record precision; confirm
  corroborator-only + ambiguous-name-never-High hold. **Recall measured on the CONTRACT, PER LEG**, written into this
  doc's appendix. No unmeasured recall claim ships.

Fast suite stays < 10s (`Category!=Scale`).

---

## 8. Risks & honest caveats

1. **Precision on undisciplined repos.** Value prop is "trust the score." If the corroborator-only, name-collision, or
   route-collision guards leak, the tool emits confident garbage. Mitigation: each unit-tested in isolation; honesty
   probe is an acceptance gate.
2. **Single-repo generalization (the big one).** Every "STRONG" grade is verified only on MyraNext. Gaps MyraNext does
   NOT exercise: fetch/`$fetch`/ky/got verb coverage; custom wrappers dropped by julie's bloat gate (zero route
   recall); `ReverseMap`/inbound-map direction; `ToDto`/inline-projection legs (0 / no-signal here); name collisions
   across namespaces; whether `[controller]` token routing holds elsewhere. Mitigation: Task 1 extracts Tycho +
   LabHandbookV2 + needs a fetch-based and a manual-mapping fixture before any cross-repo strength claim.
3. **Route token expansion is on the critical path [v3.1].** 21/23 controllers use `[controller]`; miss it and the
   flagship leg yields zero edges; do it wrong (drop the controller segment) and two controllers' `{id}` templates
   collide into a High-confidence wrong link. Mitigation: expansion in the core normalizer + the collision negative test.
4. **Response-DTO is partial (~55%).** Mutations mostly return bare `ActionResult`. No edge when bare; recover via
   `consumes→` from `[FromBody]`; parse return types by balanced bracket depth, never substring.
5. **CreateMap direction [v3.1].** Ordinal = copy-source→dest, not entity→DTO; inbound maps invert. Classify
   entity/DTO independently; handle ReverseMap. Hardcoding entity=0 would emit confident reversed edges.
6. **Name-based cross-file resolution.** `target_symbol_id` NULL → string-name resolution; collisions → drop/lower,
   never High.
7. **BridgeGraph build cost unmeasured at scale.** Cost driver is name resolution over ~5–7k code symbols, not the
   breadcrumb set. Measure at `Swap()`; persist (full cache key) only if over budget; monorepo unvalidated.
8. **Symbol-ID churn / single workspace / read-only.** Always rebuilt from the fresh index; cross-repo out of scope;
   no new process, no writes to julie's DB.

---

## 9. Acceptance criteria

- [ ] `MillerExtractContract` = 28/2; gate rejects any other pair with a typed error; version tests updated;
      `julie-pins.json` updated once assets publish.
- [ ] Read layer ingests `type_arguments`, `literals` (+language), `symbol_annotations`, DbContext property symbols,
      and `test_role`; contract tests on a synthesized 28/2 DB pass.
- [ ] `Miller.Core` resolver is pure (zero I/O); table-driven tests for every normalizer, `SymbolResolver`, and every
      §5 hardening rule.
- [ ] **Leg 3 resolves table = DbSet property name (not the DbContext class).**
- [ ] **Leg 1: `[controller]`/`[action]` tokens expanded via parent class name; route collisions stay distinct;
      bare-return endpoints emit no `responds→`; `[FromBody]` emits `consumes→`; url literals filtered to
      frontend-language + non-test; verb-less carriers are verb-unknown (never assumed GET).**
- [ ] **Leg 2: CreateMap STRONG via copy-source→dest with independent entity/DTO classification + ReverseMap handling;
      use-site `kind='type_usage'`; inline projection corroborator-only; ToDto only via a manual-mapping fixture.**
- [ ] Candidate-edge contract carries typed Signal payloads (fieldCount/Jaccard/name-tier) + source/target field-sets
      (props via `parent_id` + record positional params); scorer's corroborator-only + ambiguous-name-never-High
      invariants unit-proven from the payload, not the rule name.
- [ ] `BridgeGraph` in-memory inside `MillerRepositoryIndex`, rebuilt on `Swap()`, never persisted across an edit;
      `SymbolResolver` ambiguity policy enforced; `SymbolGraph.ShortestPath` deterministic.
- [ ] `trace` tool: all three modes, token-thrifty `compact`/`full`, verb-unknown + ambiguous flags shown; telemetry.
- [ ] Fast suite < 10s and pure; Scale tests tagged + gated on `RequireJulieServer()`; build 0 warnings / 0 errors.
- [ ] **Honesty probe run; precision recorded; recall measured on the contract PER LEG; numbers written into this doc.**
      The "shippable to unknown users" gate — without it, M4 is not done.
