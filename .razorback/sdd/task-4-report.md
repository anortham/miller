# Task 4 report — contract docs + boundary amendments (dead-code candidates)

**Branch:** `feat/dead-code-candidates` · **Start HEAD:** b9c7bdb · **Commit mode:** serial-worker-commit
**Status:** COMPLETE — all gates green.

## What changed (owned files)

1. **Created `docs/contracts/references-candidates-v1.md`** — full contract for `references candidates`:
   status experimental/evidence-gated (gate PASSED 2026-07-07, cites the findings doc); invocation/selectors;
   exit codes (0/2/3, v3 + v4-missing-resolution both exit 3); candidate rule summary; ALL ELEVEN suppression
   rules in table order with semantics + source; evidence-label section ("provenance, not certainty", the
   ≥10% query-time threshold, the partial-resolver caveat, and the write-only/comment-only "facts to check,
   not verdicts" stance); compact + `--json` envelope with a field table and a worked JSON example;
   capabilities keys; boundary.
2. **`docs/contracts/references-export-v1.md`** — replaced the stale "Eros owns candidate ranking,
   generated/framework suppression…" sentence (lines 5–8) per the 2026-07-06 consensus: Miller owns the
   deterministic candidate listing; persistence/history/dashboards/cross-workspace stay out.
3. **`docs/contracts/cli-eros-v1.md`** — added a `references candidates --json` row to the Stable JSON
   commands table; amended the bottom Boundary paragraph and the references-export feed description so no
   sentence assigns dead-code candidate listing to Eros.
4. **`docs/README.md`** — contracts-map entry for the new doc.
5. **`CLAUDE.md`** — 1.0-boundary sentence rewritten: dead-code candidates SHIPPED as an evidence-gated CLI
   surface, gate PASSED 2026-07-07 with julie-extract 2.10.0 variable_ref emission (392→5, zero
   confirmed-live), report/dashboard/MCP surfacing still needs explicit approval. `scripts/sync-agents.sh`
   ran; `cmp -s CLAUDE.md AGENTS.md` clean.

## Shapes verified against shipped code (file:line — read raw, index not trusted)

Copied verbatim from Task 3 code, not from memory:

- **Eleven suppression rule ids + table order** — `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:59-63`
  (`SuppressionRuleIds`): public_api, visibility_unknown, test_symbol, entry_point, override_member,
  live_member_container, framework_bound, annotated, generated_path, low_evidence_language,
  string_literal_match. Per-rule semantics from `FirstSuppressionRule` (`DeadCodeCandidates.cs:219-264`) and
  helpers (test-path 269-283, override 292-299, entry_point 301-308, generated 310-325, evidence label
  329-339).
- **Compact header string** — `CliDispatch.cs:772-774`: `candidates: N of M symbols examined · resolver:
  <status> — candidates are facts to check, not deletions to make.`
- **Compact candidate line / suppressed / literal_scan / coverage** — `CliDispatch.cs:776-808`
  (`unknown` for null visibility :781; `showing top K of N by path` :788-790; coverage ` resolved` vs
  ` — name-evidence only` at pct≥10 :807).
- **JSON envelope** — `CliDispatch.cs:813-896`: `schema_version` (:823, =1 at :899); `candidates[]` fields
  symbol_id/name/kind/language/path/start_line/visibility(null-safe)/evidence_label (:830-837) + nested
  `evidence{name_matches, resolved_inbound, pending_resolved_inbound, calls_inbound}` (:838-844);
  `suppressions{rule_id:count}` (:849-853); `literal_scan{files_scanned, files_skipped_stale}` (:855-859);
  `language_coverage[]{language, identifiers, resolved_pct}` (:861-871); `examined` (:873);
  `artifact{artifact_id(null-safe), revision(null-safe), reference_resolution_status,
  reference_resolution_version(null-safe)}` (:875-890).
- **resolved_pct convention** — 0–100, one decimal (`ResolvedPercent`, `DeadCodeCandidates.cs:102-108`).
- **Capabilities keys** — `optional_features.references_candidates: true` (`CliCapabilities.cs:186`, compact
  :129); `json_commands` has `references candidates --json` (:68); `json_contracts` `references_candidates`
  v1 → `docs/contracts/references-candidates-v1.md` (:103).
- **`--limit` bounds only the candidate list** — `CliDispatch.cs:757-764, 729-730`; default 50 (:755).
- **Exit-code mapping** — reader validation → `IncompatibleExtractException` → exit 3 (`CliDispatch.cs:726-728`).

## Miller MCP usage

Did not rely on the Miller index for shapes (index may lag the last commits, per brief) — all field names and
strings were verified by raw `Read`/`grep` on the source files cited above.

## Gates

- `scripts/test.sh` (fast suite): **Passed! Failed: 0, Passed: 2957, Skipped: 0** (incl. AgentInstructionsTests
  and the capabilities/doc gates). Wall 21s.
- `cmp -s CLAUDE.md AGENTS.md`: **clean** after `scripts/sync-agents.sh`.
- New contract doc: **no TBDs**.

## Judgment calls

- Plan text said "nine" suppression rules and the older CLAUDE wording; used the brief's correction + the code
  as single source → documented **eleven** in code order.
- Beyond the two required contract edits, also softened one adjacent references-export feed sentence in
  `cli-eros-v1.md` that implied Eros owns dead-code, to keep the doc internally consistent. Surgical; no
  scope creep — persistence/history/ranking/fleet remain Eros's in every amended sentence.
- Contract states the gate PASSED (per the findings FINAL VERDICT), and keeps the surface explicitly
  CLI-only with report/dashboard/MCP gated on user approval.
