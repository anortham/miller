# Exact Reference Consumers v1

Status: active for `trace`, `inspect`, `context`, CLI equivalents, and `rename_symbol`.

## Evidence tiers

Miller resolves the requested symbol once and queries reference evidence by its exact symbol ID.

- `exact`: target-proven identifier, resolution, relationship, or pending-resolution evidence.
- `fallback`: unresolved same-name evidence. It is never mixed into exact results.
- `suppressed_ambiguous_name`: fallback is withheld because multiple definitions share the target name.

Every rendered evidence row carries resolution status, provenance, numeric confidence, and source position when
available. Exact evidence carries the target symbol ID; unresolved fallback renders `target_symbol_id=null`.

## Inspect

For `depth=overview|full`, inspect JSON exposes:

- `refs`: exact inbound evidence.
- `reference_fallback`: unresolved inbound evidence.
- `reference_coverage`: exact/fallback available, returned, truncated, and fallback-status fields.
- `callers`: distinct containing symbols from exact `call` and `instantiation` evidence only.
- `referenced_by`: distinct containing symbols from other exact inbound evidence.
- `callees`: exact outgoing call evidence with `target_symbol_id`, definition location, site location, provenance,
  confidence, and resolution tier.
- `callee_fallback`: unresolved outgoing call evidence.
- `callee_coverage`: exact/fallback available, returned, and truncated fields.

Compact output uses the same tiers and labels fallback as unresolved.
Caller and `referenced_by` membership comes from the full exact evidence set, independent of the bounded `refs`
display page.

## Context

`reference_mode=usage` adds exact inbound items with reason `reference`. Exact outgoing calls use `callee`; other
exact outgoing evidence uses `dependency`. Unresolved inbound items use `possible_reference`; unresolved outgoing
calls use `unresolved_callee`, and other unresolved outgoing evidence uses `unresolved_dependency`.

Reference bundle items may add:

- `target_symbol_id`
- `resolution_status`
- `provenance`
- `evidence_confidence`

Fallback items cannot displace exact items with the same identity.

## Trace

The trace reference contract is specified in [Miller trace JSON v1](trace-json-v1.md). Reference pages reuse the
stateless [Tool Continuation Contract v1](tool-continuation-v1.md).

## Rename

`rename_symbol` defaults to `rename_mode=exact`. Exact mode requires target-proven reference spans plus the exact
definition token. It refuses incomplete or unusable exact coverage rather than silently widening by name.

`rename_mode=include_fallback` is an explicit opt-in. Fallback sites remain separately labeled in preview and JSON
because they may include homonyms. Atomic multi-file apply and rollback cover the combined approved plan.

No agent-facing path may call the legacy name-only `ReadReferences(dbPath, name)` reader.
