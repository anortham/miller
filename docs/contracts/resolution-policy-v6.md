# Resolution policy v6 — the reference-binding rules Miller computes at query time

Status: contract. This document vendors julie-extract's resolution policy (`RESOLUTION_VERSION = 6`,
`crates/julie-extract-cli/src/resolution.rs` at julie-extractors v2.33.7) so Miller's
query-time resolver implements the same decisions without reading the Rust. The spike
(branch `prototype/query-time-resolution`, `docs/findings/2026-08-18-query-time-resolution-spike.md`)
verified this text against the materialized graph: 475,377/475,377 identifiers at Miller scale,
2,152,928/2,152,935 at aspnetcore scale (the 7 divergences were the producer's bounded session
under-resolving, not policy differences).

## Inputs per edge

An edge is a pending relationship or a bare identifier.

| Field | Identifier source | Pending source |
|---|---|---|
| origin | `identifier` | `pending` |
| refKind | map of `identifiers.kind` (§ kinds) | map of `pending_relationships.kind` |
| language | `identifiers.language` | the file version's language |
| versionId | `identifiers.version_id` | `pending_relationships.version_id` |
| name | `identifiers.name` | `target_terminal_name` |
| receiver | `json_extract(metadata_json,'$.receiver')` | `target_receiver` |
| receiverQualifier | `json_extract(metadata_json,'$.receiver_qualifier')` | `target_namespace_json` array joined with `.` (empty/unparseable ⇒ none) |
| callerScopeSymbolId | `identifiers.containing_symbol_id` | `caller_scope_symbol_id` |
| sourceConfidence | `identifiers.confidence` | `pending_relationships.confidence` |

`import_context` exists in metadata but is read by no tier — ignore it.

Candidate symbols come from `symbols`: rows whose `kind` string is not a known `SymbolKind`
are dropped entirely. `isStatic` is `json_extract(metadata_json,'$.isStatic')`: JSON bool, or the
strings `"true"`/`"false"`; anything else ⇒ unknown. Every candidate lookup is restricted to
manifest-visible versions (`manifest_entries` at the pinned (view, generation), status
`indexed` or `failed_preserved`).

Imports are `symbols` rows with `kind='import'` (there is no imports table). ImportRecord:

```
local_name    = metadata.alias ?? metadata.local_name ?? symbols.name
imported_name = metadata.imported_name ?? metadata.imported ?? metadata.importedName
                ?? (local_name != symbols.name ? symbols.name : none)
source        = metadata.source (non-empty only)
is_type_only  = metadata.isTypeOnly  || metadata.is_type_only
is_default    = metadata.isDefault   || metadata.is_default
is_namespace  = metadata.isNamespace || metadata.is_namespace
```

`module_version` = the first existing manifest path among the module candidates whose language
equals the importing file's language. Module candidates: only for relative specifiers
(`./`, `../`); normalize against the importing file's directory (`..` pops; popping past root ⇒
none); if the last segment contains `.`, the single candidate is the path itself; otherwise try
extensions in order — typescript: `ts,tsx,js,jsx`; javascript: `js,jsx,ts,tsx`; other languages:
none — first as `<path>.<ext>` for all, then `<path>/index.<ext>` for all.

## Kinds

Reference kinds: identifier `call→Call`, `type_usage→TypeUsage`, `member_access→MemberAccess`,
`variable_ref→VariableRef`, anything else ⇒ no chain (`no_context`). Relationship kinds:
`calls→Call`, `instantiates→Instantiates`, `uses|extends|implements→TypeUsage`, anything else ⇒
skip (pending rows only resolve for those kinds).

`TYPE_LIKE = {class, interface, struct, enum, type, trait, union, delegate}`

Tier 1–3 compatible symbol kinds:

| RefKind | kinds |
|---|---|
| Call | function, method, constructor |
| Instantiates | class, struct, constructor |
| TypeUsage | TYPE_LIKE |
| MemberAccess | property, field, method, constant, enum_member |
| VariableRef | variable, constant, field, property |

Tier 4 compatible kinds: Call → {function, constructor} (methods excluded); Instantiates →
{class, struct, constructor}; TypeUsage → TYPE_LIKE; MemberAccess and VariableRef → empty
(tier 4 disabled).

ES-module languages: `javascript, jsx, typescript, tsx`. Tier-2 languages: `typescript,
javascript` exactly.

## Tier chains per (origin, refKind, hasReceiver)

| origin, kind | condition | chain |
|---|---|---|
| pending, Call | receiver present | Receiver, StaticType |
| pending, Call/Instantiates/TypeUsage | otherwise | Import, Receiver, StaticType, Global |
| pending, MemberAccess | receiver present | Receiver, StaticType |
| pending, MemberAccess | otherwise | Import, Receiver, StaticType |
| pending, VariableRef | — | (empty) |
| identifier, Call | receiver present | Receiver, StaticType |
| identifier, Call/TypeUsage | otherwise | Import, StaticType, Global |
| identifier, MemberAccess | receiver present | Receiver, StaticType |
| identifier, MemberAccess | otherwise | (empty) |
| identifier, VariableRef | — | Local |
| identifier, Instantiates | — | (empty) |

Gotchas: identifier TypeUsage WITH a receiver still runs [Import, StaticType, Global] (the
receiver guard exists only on the Call and MemberAccess arms); identifiers run Receiver only for
Call/MemberAccess with a receiver; Local runs only for identifier variable_ref.

## Driver

```
empty name           -> no_context
no applicable tiers  -> no_context
for tier in chain:
    Import tier is SKIPPED (not attempted) unless language ∈ {typescript, javascript}
    attempted = true
    summary = tier candidates (distinct symbols, deduped keeping max confidence)
    count == 1 -> Resolved(target, tier number, min(tier confidence, sourceConfidence), method)
    count > 1  -> remember FIRST ambiguous summary; KEEP GOING
outcome = first-ambiguous ? Ambiguous(count) : attempted ? Missing : NoContext
```

Ambiguity never stops the chain; a later exactly-one tier wins over an earlier ambiguous one.
There is no best-guess selection anywhere: a wrong edge is worse than a missing one. Evidence
ordering (for the ≤2 recorded candidates) is `(version_id, symbol_id ordinal)`.

Tier numbers/methods/confidences: 1 `tier1_local` 0.95 · 2 `tier2_import` 0.85 ·
3 `tier3_receiver` 0.75 (declared type fact) or 0.65 (`is_inferred`) · 3 `tier3_static_type`
0.70 · 4 `tier4_global` 0.55. Stored confidence is always `min(tier value, sourceConfidence)`.

## Scope walk (shared)

From `callerScopeSymbolId` upward via `parent_symbol_id` (same version): at each level take the
scope's children matching the filter; STOP at the first level with ≥1 match — the filter includes
the kind set for tier-1 walks, and name+language only for receiver lookup. If the chain
exhausts, fall back to the file's top-level symbols (`parent_symbol_id IS NULL`), same filter.

## Tiers

**Tier 1 Local (identifier variable_ref only):** scope walk with filter name+language+kinds
(VariableRef set), confidence 0.95.

**Tier 2 Import:** for each import of the file, in symbol order: skip type-only, namespace, and
default imports; skip if `local_name != name`. `target = imported_name ?? local_name`. If the
import has a `source` but no resolved `module_version`, skip the import. Candidates: symbols
named `target`, same language, tier-1-3 kinds, and (when `module_version` is set) in that
version. No module-wide branch: a named import authorizes only its own binding.

**Tier 3 Receiver:** requires a receiver. Find receiver symbols by scope walk (name+language
filter, no kinds). For each receiver symbol, for each of its `type_facts`: find the UNIQUE
symbol with `name == resolved_type` (verbatim — no namespace or generic stripping), same
language, kind ∈ TYPE_LIKE; 0 or ≥2 ⇒ the fact contributes nothing. Candidates: the type's
DIRECT children (no base-class walk) named `name`, same language, tier-1-3 kinds. Confidence
0.65 when the contributing fact `is_inferred`, else 0.75; dedupe keeps the max.

**Tier 3 StaticType:** requires a receiver; runs for every language. Ordered refusals:
1. Scope binds the receiver name ⇒ refuse. Walk the scope chain; STOP and pass at the first
   type-like scope; refuse if a scope has a child named receiver with kind `variable`, or the
   scope's `signature` declares a parameter named receiver (contents of the first top-level
   `(…)`, split on top-level commas over bracket depth `<([{`, last identifier token before any
   `=` default).
2. Resolve the receiver as a type: the unique symbol named receiver, same language, kind ∈
   (ES-module languages: {class, enum}; else TYPE_LIKE). ES-module fallback: for each import with
   `local_name == receiver`, not type-only/namespace, whose `imported_name` differs from the
   receiver, the unique type named `imported_name`; a set `module_version` must equal the type's
   version; two distinct matches ⇒ refuse.
3. Reachability: refuse if the type's parent is type-like (nested types never bind); namespace/
   module parents are fine. If `receiverQualifier` is set, it must suffix-match the declared
   namespace path segment-for-segment — declared path = names of namespace/module ancestors
   root-first, EACH SPLIT ON `.` (C# emits dotted namespace names as one symbol); `global` and
   empty segments dropped from the qualifier; empty qualifier always matches. Same file ⇒ OK;
   cross-file requires the type's `visibility == "public"` exactly.
4. Import corroboration: same file ⇒ pass; non-ES-module language ⇒ pass; else some import must
   bind the type (not type-only/namespace/default, `local_name == receiver`,
   `module_version == type's version`, `imported_name ?? local_name == type.name`).
5. Candidates: the type's direct children named `name`, same language, tier-1-3 kinds, statically
   reachable, and (cross-file) member-visible. Statically reachable: enum_member/constant/enum ⇒
   yes; `isStatic` true ⇒ yes, false ⇒ no; unknown ⇒ the signature's modifier prefix contains a
   standalone `static` word (strip `[...]` attributes first; the prefix ends at the first `(`,
   `<`, `=`, `{`, or `"`). Member cross-file visibility: null/public/open/internal ⇒ yes;
   private/protected/fileprivate ⇒ no; any other string ⇒ yes. Confidence 0.70.

**Tier 4 Global:** kinds from the tier-4 table (empty ⇒ 0 candidates). Candidates: symbols named
`name`, same language, kind in set — and for ES-module languages, same version only. Exactly one
distinct symbol ⇒ resolved at 0.55.

## Propagation (runs conceptually "before" the identifier chain)

The final answer for an identifier follows the producer's pass order — pending propagation
writes first, relationship propagation overwrites (last-write-wins), the chain fills the rest:

1. **Pending propagation:** resolve each pending row with its chain (above). On Resolved, locate
   the co-located identifier and give it the pending's target/tier/confidence/method.
2. **Relationship propagation:** for each `relationships` row with kind ∈ {calls, instantiates,
   uses, extends, implements}: name = the TARGET symbol's name; locate the co-located identifier
   in the relationship's span; outcome Resolved, target `to_symbol_id`, tier 1,
   `min(confidence, 0.95)`, method `tier1_local`.
3. **Identifier chain** for every identifier not covered above.

`locate_identifier(version, name, start_byte, end_byte, start_line)`: when both bytes are
present, identifiers named `name` with `start_byte ∈ [start, end]` and `end_byte ≤ end`;
otherwise same `start_line`. The match counts only when EXACTLY ONE identifier matches.

## Outcome totality

Every visible identifier has exactly one outcome ∈ {resolved, ambiguous, missing, no_context}.
`member_access` without a receiver, any identifier `instantiates`, unmapped kinds, and empty
names are `no_context`. There is no keyword list and no ubiquitous-name suppression in the
policy. Pending rows are sparse: only Resolved pendings produce an edge.

## How outcomes become reference edges (Miller read semantics, unchanged)

- identifier Resolved ⇒ edge `containing_symbol → target`, kind = `identifiers.kind`,
  confidence = the outcome's, reason `identifier_target`.
- pending Resolved ⇒ edge `from_symbol → target`, kind = `pending_relationships.kind`,
  confidence `min(pending.confidence, outcome confidence)`, reason `pending_resolution`.
- `relationships` rows remain their own edge source (reason `relationship`), as today.
- The unresolved-name fallback (globally unique name, confidence × 0.5, reason
  `identifier_name`) applies only to identifiers whose outcome is not Resolved.
