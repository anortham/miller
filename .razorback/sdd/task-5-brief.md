### Task 5: Canary telemetry contract (frozen)

**Files:**
- Create: `docs/contracts/canary-telemetry-v1.md`
- Note: the `docs/README.md` map line is handed to the lead as text (Task 1 owns that file)

**Interfaces:**
- Consumes: design §9.1 (canary requirements), Task 2's `miller_version` column (referenced, not implemented here).
- Produces: the frozen field/semantics contract P2b implements and P5 gates on. Field list is exact and exhaustive — implementers may not add fields without a v2.

**Contract inputs:** Telemetry privacy constraint. Existing telemetry vocabulary (`tool_telemetry` columns, `metadata_json` conventions — verify names with Miller before writing).

**File ownership:** Create: `docs/contracts/canary-telemetry-v1.md`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch (docs/README.md line goes through the lead to avoid conflict with Task 1).

**What to build:** The complete contract: assignment unit (stable per workspace+day+query-class bucket, hash-derived — define the exact derivation so assignment is deterministic and balanced); `experiment_id`/`arm` enums; `query_class` enum (mirrors SemanticQueryPolicy classes, enumerated now: `identifier`, `path`, `short_token`, `prose`, `docs_like`, `mixed`); opaque result identifiers (existing target-hash mechanism, named explicitly) with a follow-up attribution window (definition: a subsequent `inspect`/`content read` whose target hash matches a result served within the window; window length stated); the success event definition; per-row fields (arm, eligibility, per-arm result counts, rescue/fallback reason enum, backend enum, cold/warm flag, latency bucket enum with exact bucket edges); shadow-population semantics for identifier non-inferiority (shadow-execute, compare offline, never affects served results); retention and aggregation-export shapes (enums/counters only). State explicitly which fields land in columns vs `metadata_json`.

**Approach:** Follow `docs/contracts/` house style (e.g. `references-candidates-v1.md`, `metrics-history-v1.md`). Every field gets: name, type, enum values, when written, privacy note.

**Acceptance criteria:**
- [ ] Contract is implementable without further design decisions (a P2b worker could build it from this doc alone)
- [ ] No field can carry query text or paths; each field's privacy note says why
- [ ] Assignment determinism + attribution window + success event are exactly defined
- [ ] Worker-scope verification (doc self-check: no TBDs, all enums enumerated); diff handed to lead (parallel-lead-commit)

