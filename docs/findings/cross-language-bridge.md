# Cross-language bridge study (2026-05-28)

The decisive experiment. **Question:** the cross-language entity/path bridge (e.g. `IUser.ts → UserDto.cs →
ApplicationUser.cs → dbo.Users`) was the *only* feature that justified embeddings. Can it be recovered by cheap
deterministic signals (names + structure) instead — making the entire embedding layer (and its MPS/sidecar pain)
unnecessary?

**Answer: yes, across 3 repos / 2 convention style-families. Embeddings rescue 0 concepts.**

## Method

For each repo: extract to SQLite, then run a 12-agent workflow (`spike/xlang-bridge-probe-generic.js`) with 5 phases:
1. **Map** (4 parallel): C# data model · TS/Vue model + API calls · persistence (EF/SQL) schema · C# API surface + mapping.
2. **Gold** (1): build a ground-truth bridge set from **non-name evidence** (routes, mapping methods, EF DbSets,
   field correspondence) — explicitly anti-circular so it can fairly grade name-matchers.
3. **Score** (4 parallel): each cheap strategy implemented over the full symbol set, scored precision/recall vs gold:
   - **S1 exact-name** (baseline) · **S2 affix-normalized** · **S3 route + structural** · **S4 field-set Jaccard**.
4. **Residual** (2 parallel): characterize what the union misses (rule-fixable vs genuinely semantic) + an
   **adversarial audit of the gold set itself** (catches hallucinated/mislabeled bridges).
5. **Verdict** (1): recall by difficulty tier + embeddings recommendation.

Difficulty tiers: **exact** (names identical across layers), **affix** (differ only by I-/Create- prefix,
Dto/Model/Request/Response/View suffix, singular↔plural), **semantic** (names genuinely diverge / fields renamed).

## Results

| repo | convention style | gold (exact/affix/semantic) | union recall | precision | embeddings rescue |
|---|---|---|---|---|---|
| **MyraNext** | AutoMapper `CreateMap` + Dapper `FROM` literals + typed `axios<T>` + NSwag dup-names | 34 (8/14/12) | **97%** (→100% w/ rule fixes) | ~0.95 | **0** |
| **LabHandbookV2** | `ToDto(this Entity)` ext methods + `useApi.get<T>` + EF DbSet pluralization | 23 (8/5/10) | **100%** | 0.90 | **0** |
| **Tycho** | same `ToDto`/`useApi`/EF style as Lab | 21 (2/10/9) | **100%** | **1.00** | **0** |

Per-strategy recall/precision:

| | MyraNext | LabHandbookV2 | Tycho |
|---|---|---|---|
| S1 exact-name | 0.88 / 0.97 | 0.91 / 1.0 | 0.95 / 0.95 |
| S2 affix | 0.94 / 0.84 | 1.0 / 0.77 | 0.95 / 1.0 |
| S3 route+structural | 0.85 / 0.77\* | 1.0 / 0.9 | 1.0 / 1.0 |
| S4 field-set Jaccard | 0.94 / 0.96 | 0.86 / 0.91 | 0.91 / 0.91 |

\* S3's MyraNext precision dip = Dapper multi-map JOIN over-linking (one `FROM` attributed to every type in
`QueryAsync<T1,T2,...>`); a deterministic parser fix, not a semantics gap.

## The key finding — "semantic" ≠ "needs embeddings"

The semantic tier (names with no shared stem) was the expected home of embeddings. It wasn't. Those bridges are
pinned by **explicit, machine-readable code references**, recovered by S3:
- MyraNext: `AutoMapper CreateMap<Account,AccountDto>` (10 verified), `Dapper QueryAsync<ProposedSubaward>(... FROM
  Staging_ProposedSubawards)` (17 tables), and the literal projection `NetworkId = u.Username` that links
  `ApplicationUser → SecurityUser` (**zero shared field names**).
- Tycho/Lab: `ToOccurrenceDto(this CalendarEvent) → CalendarOccurrenceDto`, `ToVersionDto(this ContentBlockVersion)
  → ContentVersionDto`, `ToDto(this AppUser) → UserDto` (extension-method receiver+return captured verbatim in
  `symbols.signature`), EF `DbSet<Entity>` pluralization.

**Invariant:** maintainable polyglot code leaves machine-readable type-correspondence breadcrumbs (a mapping
method, a typed call, an EF DbSet). The *spelling* varies completely; the *machine-readability* does not.
Embeddings guess at what the code already states — and on the hardest case (`ApplicationUser→SecurityUser`,
disjoint fields) an embedding over names/fields would *miss* what the literal assignment *nails*.

**Embeddings would actively harm precision** (Tycho): semantically-close infra pairs (`ApiResponse`/`ApiError`,
`MediaFile`/`MediaFileData`, `RevisionEntry`/`DocumentRevisionDto`) score high cosine similarity → manufacture
the cross-concept merges that exact-name + structure cleanly avoided.

**The only true semantic residual** (MyraNext): per-COLUMN mapping inside ~5 legacy KC/KFS abbreviation tables
(`FIN_OBJECT_CD→FinancialObjectCode`, `SUBAWD_NBR→Number`). These are non-words with no useful embedding
neighborhood, the entities are *already* bridged at the table level, and it's better solved by a ~200-entry
closed-vocabulary abbreviation dictionary than by vectors. Not the entity-bridge task.

## S3 (structure) is load-bearing

Name/affix (S1/S2) "cover" a concept via the trivial TS↔C# DTO-name leg, but the **entity↔DTO and entity↔table
legs** — the ones you need to actually *trace a path* — come only from S3. So the **structural cross-reference
resolver is the core deliverable**, not an optional add-on. Spec in "The resolver to build" below.

## The resolver to build

Deterministic C#, mining julie's extract DB (`symbols.signature`, `code_context`, `identifiers`) + light source reads:

- **Entity ↔ DTO**: AutoMapper `CreateMap<A,B>` · `ToXDto(this Entity)→XDto` extension signatures (disambiguate
  overloads by field-set) · inline `new XDto{...}` projections in controller/service bodies (resolve source by
  property overlap) · field-renaming projections (`entity.A => dto.B`).
- **Entity ↔ table**: EF `DbSet<Entity>` pluralization (deterministic, no string match) · `ToTable`/`[Table]` ·
  Dapper `QueryAsync<T> ... FROM Table` (distinguish FROM-table from JOINed tables — precision bug otherwise).
- **TS ↔ C# DTO**: exact/affix name (strip I/_ prefixes; Dto/Model/Request/Response/View/VM/Entity suffixes;
  singular↔plural) — safe finisher · typed call edge `axios.get<T>(url)` / `useApi.get<T>('/route')` matched to
  controller `[Route]`+`[Http*]`+`[FromBody]` by (verb, normalized route).
- **field-set Jaccard**: corroborator ONLY, never sole signal (1-field/generic shapes cause FPs).

Hardening rules learned: normalize routes (absolute overrides, trailing `${param}` interpolation); transitive
return-type resolution (type nested in a wrapper, e.g. `SearchResultDto` inside `SearchResultSet.Results`); parse
positional C# `record` parameter lists for field-sets (records emit 0 property children); treat 1-field field-set
matches as route-anchored-only; include an adversarial precision/gold check (each probe's gold set had 2–3
hallucinated/mislabeled bridges the audit agent caught).

## Honest caveats (don't over-claim)

- Tycho + Lab share scaffold lineage → 2 distinct style-**families** tested, not 3 independent.
- All 3 repos have **disciplined naming** (TS DTO names mirror C# DTO names), so the TS↔C# leg is trivial-by-name.
  **Untested stress case:** a repo where TS type names do NOT mirror C# *and* route/type discipline is loose —
  that could expose a real semantic residual.
- For the **user's own style** (disciplined .NET+Vue, his repos) the decision is settled. For a general/commercial
  tool, one undisciplined-repo probe remains prudent.
- Per-repo verdict confidence ~89/100.

## Reproduce

```
# extract
~/source/julie/target/release/julie-server extract scan --root ~/source/<REPO> --db /tmp/<repo>.sqlite --standalone --json
# probe (Workflow tool)
scriptPath: spike/xlang-bridge-probe-generic.js
args: {"repo":"<REPO>","db":"/tmp/<repo>.sqlite","root":"/Users/murphy/source/<REPO>","conventions":"<discovered hints; agents verify>"}
```
NB: pass `args` as a real JSON object; an early run lost args (delivered as a string) and the agents wandered onto
the wrong DB — the script now parses string-args and throws if `repo/db/root` are missing.
