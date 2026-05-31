# M4 ground truth — what julie 28/2 ACTUALLY captures (verified extract, 2026-05-30)

Produced by extracting **MyraNext** (the richest 3-repo study target: AutoMapper + Dapper + typed axios) with the
local **julie-server 7.13.0** binary (schema 28 / contract 2) and inspecting the real rows. This supersedes the
*assumed* shapes in the M4 design's first draft and corrects two overclaims in
[cross-language-bridge.md](cross-language-bridge.md). Every number below is from the real DB
(`/tmp/mn.sqlite`, 29,663 symbols, 24,830 identifiers).

**Why this doc exists:** the first M4 design was written against assumed column shapes and was caught by a codex
adversarial review + this extract. The rule it violated: *negative/▢shape claims need positive verification.* This
is that verification.

---

## Row counts (MyraNext)

| table | rows | note |
|---|---|---|
| symbols | 29,663 | |
| identifiers | 24,830 | |
| type_arguments | 1,797 | ~7% of identifiers (matches julie's measured bloat budget) |
| symbol_annotations | 1,339 | mostly test traits; the bridge-relevant ones are a minority |
| literals | 111 | 96 `url` + 15 `sql` |

## Verified table shapes (from `.schema`)

**`type_arguments`** — `id, identifier_id→identifiers(id), parent_arg_id→type_arguments(id) (nesting), ordinal,
type_name, target_symbol_id (NULL at extract), file_path, language, last_indexed`. **Keyed by `identifier_id`.**

**`literals`** — `id, literal_text (DECODED, interpolation folded to {}), kind (url|sql|route|other),
carrier (the verbatim callee), arg_position, language, file_path, start/end span, containing_symbol_id, confidence`.
**NO `identifier_id`** — only `containing_symbol_id` + span. (Codex finding confirmed.)

**`symbol_annotations`** — `id, symbol_id→symbols(id), ordinal, annotation, annotation_key (lowercased),
raw_text (the verbatim attribute incl. args), carrier, UNIQUE(symbol_id, ordinal)`. **Args live ONLY in
`raw_text`** (e.g. `HttpGet("name/{name}")`); no parsed-arg columns, no file/line columns. (Codex finding confirmed.)

---

## What each resolver leg ACTUALLY gets (the decisive part)

### ✅ Leg 2 — DTO ↔ Entity via `CreateMap<A,B>` — STRONG
Real rows, ordered + directional:
```
CreateMap [0] Core.Reporting.Data.Account      [1] ResponseObjects.Account
CreateMap [0] Permission                        [1] ResponseObjects.SecurityPermission
CreateMap [0] Core.Reporting.Data.Investigator  [1] ResponseObjects.InvestigatorRole
```
20 maps, ordinal 0 = source entity, ordinal 1 = dest DTO. **This leg works exactly as designed.** Join
`type_arguments` (where the use-site identifier name = `CreateMap`) → ordered `type_name`s.

### ✅ Leg 3 — Entity ↔ Table via `DbSet<Entity>` — STRONG (but NOT via `[Table]` or Dapper)
```
DbSet [0] ApplicationUser   DbSet [0] Preferences   DbSet [0] AppSetting   DbSet [0] ObjectCodeMapping ...
```
11 entities, clean. **Critical correction:** none of these entities carry a `[Table]` annotation (verified: 0
`table` keys in `symbol_annotations`). EF uses **convention** = the DbSet *property name* as the table. So the
table name comes from the **property symbol that owns the `DbSet<T>` use-site** (via `containing_symbol_id`), NOT
from a `[Table]` attribute and NOT from a pluralizer-on-the-entity-name. The pluralizer is at most a fallback.

### ✅ Leg 1 — TS call → C# endpoint via (verb, route) — STRONG, but via carrier+literal, NOT axios<T>
TS side (96 `url` literals, all `arg_position=0`):
```
/api/appsettings            carrier=axios.post   @createApplicationSetting
/api/messages/{}/dismiss    carrier=axios.patch  @dismissMessage
/api/objectcodemappings/{}  carrier=axios.delete @deleteObjectCodeMaping
```
C# side (`symbol_annotations` on controller methods):
```
GetByUsername  httpget  HttpGet("username/{username}")
Post           httppost HttpPost("award")
Delete         httpdelete HttpDelete("{id}")
```
**The carrier encodes the HTTP verb** (`axios.post` → POST). `literal_text` is the route with `{}` for params.
So `(verb, normalized-route)` is derivable on BOTH sides with **no type argument needed.** The route bridge is
fully intact.

---

## The corrections (assumptions that were WRONG)

### ✗ `axios.get<T>` response generic is NOT captured (codex finding — CONFIRMED)
`type_arguments` keyed by callee shows **zero** `get`/`post`/`put`/`delete` entries. julie's TS extractor records
generic args for `extends`, `new`, and type references — **not generic calls** (`typescript/identifiers.rs:57-99`).
**Workaround (turns this from blocking → non-issue):** the response DTO is the **C# controller method's return
type** (existing `symbols.signature`), reachable once the (verb,route) match identifies the endpoint. Resolve the
response shape from the backend, not the frontend. The TS-side generic was never needed for the link, only for the
shape — and the shape is on the C# side.

### ✗ Dapper `FROM`-table leg BARELY FIRES on real data (NOT in codex's review — found only by extracting)
This is the most important correction. Of 15 `sql` literals, **only 2 contain `FROM`** — and both are a
`ReadinessCheck` healthcheck (`SELECT TOP 1 Id FROM dbo.AppSettings`). The other 13 are stored-proc artifacts:
```
[AccountNumber, ProjectRole]  carrier=QueryAsync  @GetAwardProjectsFor   arg=2   <- Dapper splitOn: columns
[ProjectRole]                 carrier=QueryAsync  @GetNegotiationProjectsFor arg=2
[ActivityId]                  carrier=QueryAsync  @GetUnfundedAgreementsFor  arg=2
```
MyraNext's real data access uses **stored procedures** (`CommandType.StoredProcedure`), so there is **no inline
`SELECT…FROM`** for julie to capture — it captures the string *arguments* to `QueryAsync` (splitOn column lists,
param names), classified `sql` because the carrier is a SQL carrier. **The study's "Dapper FROM (17 tables)" was the
12-agent source probe reading raw source — NOT what julie's literal extraction yields.** There is a real gap between
"recoverable from source" and "present in the contract." **Consequence:** entity↔table must lean on EF `DbSet`;
the Dapper-FROM anchor is opportunistic bonus that fires only on repos with inline SQL, not stored-proc repos.

### ✗ `carrier`/`kind` were mislabeled in the design draft (codex finding — CONFIRMED)
Draft said `carrier ∈ {url,route}`, `carrier=sql`. Reality: **`carrier` = the callee** (`axios.post`, `QueryAsync`,
`GetAsync`); **`kind` = url|sql|route|other**. Filter/branch on `kind`; read the verb from `carrier`.

### ✗ `[FromBody]`, `[Table]`, `ToTable` not captured (codex findings — CONFIRMED, but low impact)
- Parameter attributes (`[FromBody]`) are not persisted (only decl-level attrs). The request-body DTO degrades to
  the controller method's parameter type in `signature`. Not needed for the call→endpoint link.
- `[Table]`/`ToTable` not captured → entity→table is DbSet-property-name + convention (see Leg 3). Fine for EF.

### ✗ `literals` cannot join to `type_arguments` by key (codex finding — CONFIRMED)
`type_arguments.identifier_id` vs `literals.containing_symbol_id`+span — no shared call-site key. Pairing a
`QueryAsync` literal with its generic `T` requires **span-proximity within the same containing symbol**, not a join.
Low impact given the Dapper leg is weak anyway, but the rule must be explicit where it's used.

---

## Net assessment

**All three legs resolve on real data** — the differentiator is intact — but:
- **Strong, contract-backed:** `CreateMap` (DTO↔entity), `DbSet` (entity↔table), carrier+literal+annotation
  (TS↔endpoint route). These are MyraNext's actual recall drivers.
- **Pivots that defuse 2 "blocking" findings:** verb-from-carrier; response-DTO-from-C#-return-type.
- **Genuinely degraded:** Dapper-FROM (stored-proc repos lose it), request-body typing, table-name overrides.
- **The honesty caveat stands harder than before:** recall numbers from the study were measured on *source*, and
  the *contract* captures a subset. Re-measure recall on the **contract** (this extract), not the source probe,
  before claiming a number to anyone.

## Reproduce
```
/Users/murphy/source/julie/target/release/julie-server extract --db /tmp/mn.sqlite --root ~/source/MyraNext --json scan
sqlite3 /tmp/mn.sqlite < /tmp/probe.sql   # (and probe2.sql)
```
