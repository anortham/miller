export const meta = {
  name: 'xlang-bridge-probe',
  description: 'Test whether cross-language entity bridges (TS/Vue ↔ C# ↔ SQL/EF) are recoverable by lexical+structural signals alone, or genuinely need embeddings. Repo-parameterized via args {repo, db, root, conventions}.',
  phases: [
    { title: 'Map', detail: 'parallel readers map C# model, TS/Vue model+API, SQL/EF schema, C# API surface' },
    { title: 'Gold', detail: 'synthesize a ground-truth bridge set from non-name evidence (anti-circular)' },
    { title: 'Score', detail: 'score 4 cheap strategies (exact / affix / route+structural / field-set) vs gold' },
    { title: 'Residual', detail: 'characterize what the cheap stack misses + adversarial gold audit' },
    { title: 'Verdict', detail: 'recall by difficulty tier + embeddings recommendation' },
  ],
}

// args may arrive as an object OR as a JSON-encoded string depending on delivery — handle both.
const A = (args && typeof args === 'object') ? args
  : (typeof args === 'string' && args.trim().startsWith('{')) ? JSON.parse(args)
  : {}
const REPO = A.repo
const DB = A.db
const ROOT = A.root
const CONV = A.conventions || ''
// Fail LOUDLY rather than let agents wander onto the wrong DB (the prior bug).
if (!REPO || !DB || !ROOT) {
  throw new Error('Workflow args not delivered (need {repo,db,root}). Got: ' + JSON.stringify(args))
}

const CONTEXT = `
PROBE GOAL: Determine whether cross-language ENTITY BRIDGES can be recovered WITHOUT embeddings, using only
lexical (name/affix) + structural (route strings, manual mapping projections, EF entity mapping, field sets)
signals. A "bridge" groups the artifacts in different languages/layers that represent the SAME real-world
concept, e.g. a TS interface FooDto ↔ C# FooDto ↔ C# Foo entity ↔ DB table Foos. The user wants this to trace
call/data paths across language borders in a polyglot repo. If lexical+structural recovers ~all real bridges,
embeddings (a ~GB f32 vector index + a Python MPS sidecar) are NOT worth it. If there is a meaningful residual
only semantics can catch, they might be.

THIS IS A GENERALIZATION TEST. A prior probe on a different repo (MyraNext) found the cheap stack recovered 97%
because that repo had rich structural signals — AutoMapper CreateMap<A,B>, Dapper "QueryAsync<T> FROM Table"
literals, and typed axios.get<T>(url). THIS repo (${REPO}) was chosen BECAUSE IT DOES NOT USE THOSE PATTERNS.
Do NOT assume those mechanisms exist. DISCOVER how THIS repo actually maps types across layers and calls the API.

CODEBASE: ${REPO} — .NET (C#) backend + Vue/TypeScript frontend.
- Source root: ${ROOT}
- Extracted symbol DB (julie-server extract → SQLite), query with: sqlite3 ${DB} "SELECT ..."
  * symbols(id,name,kind,language,file_path,signature,doc_comment,visibility,code_context,parent_id,start_line,end_line)
  * identifiers(name,kind['type_usage'|'member_access'|'call'],language,file_path,containing_symbol_id,target_symbol_id,code_context) — target_symbol_id is NULL (unresolved); identifiers are raw name occurrences with code_context.
  * relationships table — check if useful in THIS repo (it was sparse elsewhere); verify before relying on it.
  * properties of a class: SELECT name FROM symbols WHERE parent_id=(class id) AND kind IN ('property','field').
  * EXCLUDE noise languages (json/css/yaml/markdown/html/powershell) and **/tests/**, *.spec.ts, *.test.ts for DATA-MODEL purposes.
DISCOVERED CONVENTIONS FOR THIS REPO (verify against source; they differ from other repos):
${CONV}
GROUND EVERY NUMBER IN REAL DATA (sqlite queries / grep / reading files). Do not estimate or invent. You have
Bash (sqlite3, grep, python3), Read, Grep. Write a throwaway python3 script if it helps compute over the full set.
`

// ---------- schemas ----------
const MAP_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['facet', 'count', 'items', 'observations'],
  properties: {
    facet: { type: 'string' },
    count: { type: 'integer' },
    items: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        required: ['name', 'language', 'kind', 'detail'],
        properties: {
          name: { type: 'string' },
          language: { type: 'string' },
          kind: { type: 'string', description: 'entity|dto|request|response|view|enum|interface|type|table|controller|apiclient|mapping|other' },
          file: { type: 'string' },
          detail: { type: 'string', description: 'freeform: property names, route+verb+returnType, columns, projection target, etc.' },
        },
      },
    },
    observations: { type: 'string', description: 'naming patterns, layering, mapping/calling mechanisms actually used, anything that helps or defeats bridging' },
  },
}

const GOLD_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['bridges', 'notes'],
  properties: {
    bridges: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        required: ['concept', 'ts', 'csharp', 'sql', 'difficulty', 'evidence'],
        properties: {
          concept: { type: 'string' },
          ts: { type: 'array', items: { type: 'string' } },
          csharp: { type: 'array', items: { type: 'string' }, description: 'include both DTO and domain-entity names where they differ' },
          sql: { type: 'array', items: { type: 'string' }, description: 'EF entity-backed table name(s); may be empty/EF-convention if no .sql files' },
          difficulty: { type: 'string', description: 'exact | affix | semantic' },
          evidence: { type: 'string', description: 'NON-name evidence preferred: manual .Select(=>new Dto) projection, EF DbSet/entity config, controller route + api-call URL, confirmed field correspondence, explicit assignment' },
        },
      },
    },
    notes: { type: 'string' },
  },
}

const STRAT_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['strategy', 'description', 'matchedPairCount', 'coveredGoldConcepts', 'missedGoldConcepts', 'falsePositiveExamples', 'precisionEstimate', 'recallVsGold', 'notes'],
  properties: {
    strategy: { type: 'string' },
    description: { type: 'string' },
    matchedPairCount: { type: 'integer' },
    coveredGoldConcepts: { type: 'array', items: { type: 'string' } },
    missedGoldConcepts: { type: 'array', items: { type: 'string' } },
    falsePositiveExamples: { type: 'array', items: { type: 'string' } },
    precisionEstimate: { type: 'number' },
    recallVsGold: { type: 'number' },
    notes: { type: 'string' },
  },
}

const VERDICT_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['combinedRecall', 'combinedPrecision', 'goldByDifficulty', 'residual', 'embeddingsVerdict', 'recommendation', 'confidence'],
  properties: {
    combinedRecall: { type: 'number' },
    combinedPrecision: { type: 'string' },
    goldByDifficulty: { type: 'string', description: 'counts exact/affix/semantic + union recall within each tier' },
    residual: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        required: ['concept', 'whyMissed', 'fixableByRule', 'needsSemantics'],
        properties: {
          concept: { type: 'string' },
          whyMissed: { type: 'string' },
          fixableByRule: { type: 'boolean' },
          needsSemantics: { type: 'boolean' },
        },
      },
    },
    embeddingsVerdict: { type: 'string' },
    recommendation: { type: 'string' },
    confidence: { type: 'integer' },
  },
}

// ---------- Phase 1: Map ----------
phase('Map')
const maps = await parallel([
  () => agent(`${CONTEXT}\n\nYOUR FACET — C# DATA MODEL. Enumerate every C# class/record/struct/enum representing a DATA TYPE: domain entity, DTO, request, response, view-model. EXCLUDE controllers, services, repositories, EF config classes, middleware, framework types. For each set kind=entity|dto|request|response|view|enum|other and put KEY PROPERTY NAMES + namespace + whether it's an EF entity (appears as DbSet<> / has a DbContext mapping) in 'detail'. Use the DB (parent_id property lookup) AND read representative files. Note especially DTO↔entity name relationships (e.g. AnnouncementDto vs Announcement). Be thorough.`,
    { label: 'map:csharp-model', phase: 'Map', schema: MAP_SCHEMA }),
  () => agent(`${CONTEXT}\n\nYOUR FACET — TS/Vue DATA MODEL + API CALLS. Two parts:\n(1) Data model: every TS interface/type/class used as a data shape (kind=interface|type), with property names in 'detail'. Look in ClientApp/src/types/*.ts and elsewhere.\n(2) API calls (kind=apiclient): FIRST read the HTTP client wrapper (e.g. composables/useApi.ts) to learn how calls are made, THEN enumerate every call site. For each put in 'detail': HTTP verb, the URL string, and — since calls may NOT carry a generic <T> — how the response is typed/assigned at the call site (the TS type the result flows into), if determinable. Read actual .ts/.vue files.`,
    { label: 'map:ts-model+api', phase: 'Map', schema: MAP_SCHEMA }),
  () => agent(`${CONTEXT}\n\nYOUR FACET — PERSISTENCE SCHEMA. This repo likely uses EF Core code-first with FEW/NO .sql files. Enumerate the persistence layer: every EF entity (kind=table) — from DbSet<> declarations and the DbContext — with its column/property names in 'detail', plus the resolved table name (EF convention pluralization or explicit ToTable/[Table]). Capture FK/navigation relationships (HasMany/HasOne/navigation properties). If any .sql files exist, include those tables too. Read the DbContext + entity config classes.`,
    { label: 'map:ef-schema', phase: 'Map', schema: MAP_SCHEMA }),
  () => agent(`${CONTEXT}\n\nYOUR FACET — C# API SURFACE + MAPPING. Two parts:\n(1) For every controller: route prefix and each action's HTTP verb + route template + the request DTO (from [FromBody] params) AND the response DTO. NOTE: actions may return bare Task<IActionResult>, hiding the response type — recover it by reading the method body (Ok(x), the .Select(=> new XDto), the variable returned). Put verb+route+requestDto+responseDto in 'detail'.\n(2) MAPPING (kind=mapping): since there is likely NO AutoMapper, find how entities become DTOs — manual '.Select(e => new XDto { ... })' projections, constructor mapping, or explicit field assignments. For each, put in 'detail' the source entity → target DTO and any RENAMED fields (entity.A => dto.B). These projections are the structural bridge evidence.`,
    { label: 'map:csharp-api+mapping', phase: 'Map', schema: MAP_SCHEMA }),
]).then(r => r.filter(Boolean))

log(`[${REPO}] Map done: ${maps.length}/4 facets, ${maps.reduce((s, m) => s + (m.count || 0), 0)} items`)

// ---------- Phase 2: Gold ----------
phase('Gold')
const mapsJson = JSON.stringify(maps)
const gold = await agent(
  `${CONTEXT}\n\nYou are building the GROUND-TRUTH ("gold") set of real cross-language bridges. The four facet maps:\n${mapsJson}\n\n` +
  `TASK: Construct the set of REAL cross-language/cross-layer entity bridges. A bridge groups the TS, C# (DTO and entity), and persistence artifacts representing the SAME concept.\n` +
  `ANTI-CIRCULARITY RULE: this gold set grades NAME-MATCHING strategies, so do NOT build it by name-matching. Establish each bridge from NON-NAME evidence by reading source: the api-call URL ↔ controller route ↔ controller body's response DTO chain; manual '.Select(e => new XDto{...})' projections (entity↔DTO, incl. renamed fields); EF DbSet/entity↔table mapping; confirmed field-set correspondence; explicit assignments. Use names only as a final tiebreak.\n` +
  `For each bridge: concept; ts[]; csharp[] (DTO + entity names, even if they differ); sql[] (EF table name; may be EF-convention); difficulty = "exact" (names identical across layers), "affix" (differ only by I-prefix / Dto/Model/Request/Response/View suffix / singular-plural / Create/Update prefix), or "semantic" (names genuinely differ with little/no shared substring, OR fields renamed in the projection); evidence (the non-name proof).\n` +
  `Be CONSERVATIVE — only bridges confirmable by reading. AGGRESSIVELY HUNT hard "semantic" cases: TS type vs C# DTO vs entity that don't share a substring, and projections that RENAME fields (entity.X => dto.Y) — these decide whether embeddings are needed. Report the honest difficulty distribution even if semantic cases are rare. Aim for 20-40 bridges.`,
  { label: 'gold:synthesize', phase: 'Gold', schema: GOLD_SCHEMA })

log(`[${REPO}] Gold: ${gold.bridges.length} bridges (${['exact','affix','semantic'].map(d => d + '=' + gold.bridges.filter(b => b.difficulty === d).length).join(' ')})`)
const goldJson = JSON.stringify(gold.bridges)

// ---------- Phase 3: Score ----------
phase('Score')
const STRATS = [
  { key: 'exact-name', prompt: `STRATEGY S1 — EXACT NAME MATCH (naive baseline). Group symbols across languages/layers whose names are identical case-insensitively (kind in class/interface/struct/record/type/enum/table). Score precision (real bridge?) + recall vs gold.` },
  { key: 'affix-normalized', prompt: `STRATEGY S2 — AFFIX-NORMALIZED MATCH. Normalize each name to a canonical key then group across languages. Strip prefixes [I, _, Create, Update, New] and suffixes [Dto, Dtos, Model, Request, Response, View, ViewModel, Vm, Entity, Dao, Detail, Details], normalize singular/plural and lowercase. Group by canonical key. Score precision + recall vs gold; report which gold concepts S2 gets that S1 cannot.` },
  { key: 'route-structural', prompt: `STRATEGY S3 — ROUTE + STRUCTURAL BRIDGE (NOT names). Use the code's explicit links discovered for THIS repo: (a) api-call URL+verb ↔ controller route ↔ the response/request DTO read from the controller body; (b) manual '.Select(e => new XDto{...})' projections and constructor mapping linking entity↔DTO (including renamed fields); (c) EF DbSet/entity↔table. Build the bridge graph from these. This strategy must work even when names diverge. Implement it concretely (grep projections, parse routes, read useApi wrapper). Score precision + recall vs gold.` },
  { key: 'field-set', prompt: `STRATEGY S4 — FIELD-SET / SHAPE MATCH. For each data type (TS interface, C# DTO/entity, EF table) collect its member-name set, normalize members (camel/Pascal/snake → lowercased tokens). Match types across languages by Jaccard similarity of normalized field-name sets (report threshold, e.g. >=0.6). Catches concept matches via STRUCTURE when names differ. Score precision + recall vs gold; note false positives (different concepts, similar fields).` },
]
const stratResults = await parallel(STRATS.map(s => () =>
  agent(`${CONTEXT}\n\nGOLD BRIDGE SET (answer key):\n${goldJson}\n\n${s.prompt}\n\nImplement the matcher concretely over the FULL symbol set (python3 against ${DB} and/or grep source — do NOT eyeball a sample). A gold concept is "covered" if your strategy links >=2 of its cross-language/cross-layer members. Report coveredGoldConcepts and missedGoldConcepts by their exact gold 'concept' strings, plus precision with concrete falsePositiveExamples.`,
    { label: `score:${s.key}`, phase: 'Score', schema: STRAT_SCHEMA })
)).then(r => r.filter(Boolean))

log(`[${REPO}] Scored ${stratResults.length}/4: ${stratResults.map(s => s.strategy.split('—')[0].trim() + '=' + (s.recallVsGold != null ? Math.round(s.recallVsGold * 100) + '%' : '?')).join(' ')}`)

// ---------- Phase 4: Residual + gold audit ----------
phase('Residual')
const stratJson = JSON.stringify(stratResults)
const [residual, audit] = await parallel([
  () => agent(`${CONTEXT}\n\nGOLD:\n${goldJson}\n\nSTRATEGY RESULTS:\n${stratJson}\n\n` +
    `TASK — RESIDUAL ANALYSIS. Compute UNION coverage (a gold concept is covered if ANY strategy covered it). List concepts MISSED BY ALL strategies. For each: read source and characterize whyMissed; fixableByRule (which deterministic rule); needsSemantics (only embeddings could bridge it). Then ADVERSARIALLY re-verify: confirm 5 covered concepts are genuinely correct (not lucky FPs), and re-check 3 falsePositiveExamples from the lowest-precision strategy. Report honest combinedRecall + combinedPrecision + residual. Resist declaring victory; be skeptical.`,
    { label: 'residual:analyze', phase: 'Residual', schema: VERDICT_SCHEMA }),
  () => agent(`${CONTEXT}\n\nGOLD SET TO AUDIT:\n${goldJson}\n\n` +
    `TASK — ADVERSARIAL GOLD AUDIT. If the answer key is wrong, every number is wrong. By reading source: (1) any HALLUCINATED bridges (members that aren't the same concept, or names that don't exist)? (2) obvious REAL bridges MISSING from gold, especially hard semantic ones (renamed fields, diverging names)? (3) is difficulty labeling honest (something tagged 'semantic' that's really affix)? Read controllers, DbContext/entities, projections, ClientApp/src/types/*.ts. Put findings in residual[] (concept, whyMissed=finding, flags), put hallucination/missing/mislabel counts + overall gold trustworthiness in embeddingsVerdict+recommendation, and confidence.`,
    { label: 'residual:gold-audit', phase: 'Residual', schema: VERDICT_SCHEMA }),
]).then(r => r.filter(Boolean))

// ---------- Phase 5: Verdict ----------
phase('Verdict')
const verdict = await agent(
  `${CONTEXT}\n\nAll evidence:\nGOLD:\n${goldJson}\n\nSTRATEGY SCORES:\n${stratJson}\n\nRESIDUAL:\n${JSON.stringify(residual)}\n\nGOLD AUDIT:\n${JSON.stringify(audit)}\n\n` +
  `TASK — FINAL VERDICT for repo ${REPO}. Reconcile residual + audit (if audit found the gold untrustworthy, adjust). Produce: combinedRecall (union vs trustworthy gold); combinedPrecision; goldByDifficulty (recall within exact/affix/semantic — the crux); residual concepts genuinely needing semantics (separate true semantic gaps from missing-rule gaps); embeddingsVerdict (concretely what embeddings recover here that lexical+structural cannot, quantified); recommendation for the codesearch project (ship lexical+structural only, or embeddings opt-in) weighed against cost (~GB vectors + Python MPS sidecar) and the north star (fast, low-token, daily-use, pure-.NET). IMPORTANT context: this repo lacks AutoMapper/Dapper/typed-axios, so explicitly assess whether the cheap stack's success depends on those (it can't here) and what carried the bridges instead. State confidence (1-100).`,
  { label: 'verdict:final', phase: 'Verdict', schema: VERDICT_SCHEMA })

return {
  repo: REPO,
  goldCount: gold.bridges.length,
  goldByDifficulty: ['exact', 'affix', 'semantic'].map(d => d + '=' + gold.bridges.filter(b => b.difficulty === d).length).join(' '),
  strategies: stratResults.map(s => ({ strategy: s.strategy.split('—')[0].trim(), recall: s.recallVsGold, precision: s.precisionEstimate, matched: s.matchedPairCount })),
  verdict,
  goldAuditTrust: audit ? audit.recommendation : 'audit failed',
}
