# julie-extract 2.1.0 compatibility bump

**Date:** 2026-06-03
**Scope:** Make Miller read julie-extract **v2.1.0** extracts (schema/contract/report **2**).
**Out of scope:** consuming the new `source_regions` data — that is a separate "consume next" piece.

## Why

julie-extractors v2.1.0 adds a `source_regions` row domain (spans for comments, doc comments,
string literals, and embedded-language regions). The release is a **contract break**: SQLite
schema `1→2`, `extract_contract_version` `1→2`, JSONL `1→2`, report schema `1→2`. CLI flags and
exit codes are unchanged.

Miller's `JulieSchemaGate` is exact-equality on version `1`, so a v2.1.0 extract DB is **rejected
today**. This bump moves the pin forward.

## Key facts that shape the design

- The **only** SQLite change v1→v2 is the *addition* of the `source_regions` table + its 3 indexes.
  Every table Miller reads (`files`, `symbols`, `identifiers`, `relationships`,
  `pending_relationships`, `type_facts`, `literals`, `artifact_metadata`, `extraction_revisions`, …)
  is unchanged. So Miller's readers work as-is on a v2 DB; only the version gate rejects it.
- julie-extract's **incremental `scan` hard-fails on a schema-mismatched DB** (no auto-rebuild).
  Only `scan --force` removes + rebuilds. So a leftover v1 DB can only be upgraded by a force
  rebuild (`workspace full`), never by `workspace refresh`.
- Miller reads SQLite + the scan **report**; it does not consume JSONL, so the JSONL schema bump is
  irrelevant.
- The restore script verifies the **archive** sha256 from `julie-pins.json`; the v2.1.0 archive
  digests come from julie-extractors' release evidence and are confirmed by running the script.

## Decision: hard-cut to v2 (not a dual-accept window)

Reject anything that is not schema/contract/report 2. Rationale: readers need no changes; a
dual-accept window would let an old v1 DB keep *reading* while `workspace refresh` *fails* (julie's
incremental scan can't open a mismatched DB) — a confusing half-state. Hard-cut gives one clean,
loud rebuild path and matches Miller's single-pinned-contract design (D7). Re-indexing is cheap and
routine, so forcing it is acceptable.

## Changes

1. `src/Miller.Indexing/MillerExtractContract.cs` — four `Expected*` constants `1→2`;
   `PinnedJulieExtractVersion` `"2.0.3"→"2.1.0"`; refresh docstrings (keep the D7 orthogonality
   note).
2. `scripts/julie-pins.json` — `version` `→2.1.0` + the four v2.1.0 archive sha256s.
3. `src/Miller.Indexing/ExtractVersionMismatch.cs` — the "older" remedy in `BuildMessage` now leads
   with **`workspace full`** (force rebuild) and notes that an incremental refresh cannot upgrade a
   schema-mismatched DB; keeps the "restore" hint as secondary. Docstrings v1→v2.
4. `src/Miller.Indexing/JulieSchemaGate.cs` — docstring v1→v2 (logic reads the constants).
5. Tests — versions cascade via `JulieDbFixture.PinnedSchema/PinnedContract` and
   `MillerExtractContract.Expected*`. Update the literal-value test
   (`MillerExtractContractTests`) to `2` / `"2.1.0"`, and add `workspace full` assertions to the two
   older-path gate tests in `JulieSchemaGateTests`.
6. Local/dogfood — `scripts/restore-julie-extract.sh` to install v2.1.0; rebuild; `scripts/test.sh
   all`; re-index repos with `workspace full` after restarting the MCP server on the new binary.

## Acceptance criteria

- [ ] `dotnet build Miller.slnx -c Release` → 0 warnings / 0 errors.
- [ ] `scripts/test.sh` (fast) green; `scripts/test.sh scale` green using the v2.1.0 binary.
- [ ] `restore-julie-extract.sh` downloads v2.1.0 and passes sha256 verification on this machine.
- [ ] A fresh `scan --force` on a real repo produces a **schema-2** DB Miller reads
      (search/inspect/trace).
- [ ] A leftover **v1** DB is rejected with a message that names `workspace full`; `workspace full`
      upgrades it cleanly.
- [ ] `source_regions` is untouched (out of scope); readers ignore the new table.

## Deferred to "consume next"

- `JulieDbFixture` learning to create a `source_regions` table; renaming `JulieDbFixtureV1SchemaTests`.
- Surfacing `source_regions` in search/inspect/trace (comments, string literals, embedded languages).
