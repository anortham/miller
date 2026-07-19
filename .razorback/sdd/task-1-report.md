# Task 1 Report — ADR-0003 + boundary reversal docs

**Status:** COMPLETE
**Commit SHA:** none - parallel-lead-commit
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`, branch `worktree-semantic-integration`, base commit `87f9b1d`

> Note: the previous contents of this path were a stale report from an unrelated run
> (*"Anti-flicker: idiomorph morph swaps + fragment ETag/304"*, worktree `.claude/worktrees/dashboard-ux-fixes`),
> read and confirmed unrelated before overwriting.

## What I implemented

### 1. `docs/adr/ADR-0003-semantic-retrieval-ownership.md` (new)

Follows the ADR-0001/ADR-0002 conventions: `# ADR-000N: <title>`, then **Status / Date /
Reverses**, a design-doc link block, then `## Context`, `## Decision`, `## Consequences`,
`## Applies To`, `## Future Agents` (the latter two sections mirror ADR-0001, the only existing ADR
that has them).

- **Context** cites the telemetry evidence from design §1: `search symbol` 0.7–2% empty (~1,100
  fleet calls, June–July 2026), `search auto` 1.3%, `context` ~0%, vs `search source` 42–60% /
  `search content` 26–46% / `search file` 50%; plus Julie's symbol-card-only embedding edge, and
  Eros's shifted direction / may-never-ship status. It also explicitly reconciles with the 2026-05
  `findings/embeddings.md` + `findings/architecture-decision.md` rejection (packaging objections
  answered by the sidecar model; the cross-language-bridge conclusion left intact) so a future agent
  reading those docs does not see a live, unexplained contradiction.
- **Decision** states the design §2.1 wording verbatim in bold: "Miller owns optional local semantic
  retrieval; Eros owns fleet-level semantics: cross-workspace ranking, guidance/confidence views,
  embeddings-as-a-service orchestration." Followed by the five operative consequences from design
  §2/§3 (off-guarantee, lexical parity, sidecar owns embedding generation, no new MCP tools,
  local-first no-egress).
- **Consequences** leads with the **Eros migration inventory** as a table (capability → 1.0 owner →
  owner after this ADR): local semantic retrieval and local-index embeddings move to Miller; fleet
  ranking, guidance/confidence views, embeddings-as-a-service, suppression persistence, and
  commercial orchestration stay Eros-reserved; extraction unchanged. Defines "reserved" as unbuilt
  and NOT absorbable by citing this ADR.
- **Applies To** names CLAUDE.md/AGENTS.md, the three README spots, the design doc, and later
  program phases.
- **Future Agents** names the **Julie-compatibility owner: the user (anortham)** and the
  shared-protocol rule (sidecar protocol / model fingerprint contract / on-disk vector format are
  shared property; breaking changes need a Julie compatibility check with that owner, additive ones
  do not), plus four guard rails: this is not "Miller owns semantics", `MILLER_SEMANTIC=off` is a
  guarantee not a tuning knob, lexical parity is a gate not an aspiration, and MCP stinginess /
  language parity / test split are unrelaxed.

### 2. `CLAUDE.md` — four minimal edits, no restructuring

- Intro (line 3–6): "does not own tree-sitter extraction or **embeddings**" → "or **embedding
  generation** … delegated to the pinned `julie-extract` binary **and embedding generation to the
  pinned `julie-semantic-sidecar` binary**". Kept as a delegation statement (still true), not a
  prohibition. Re-wrapped the following two lines to the file's ~100-col style.
- "1.0 replacement boundary" ownership sentence: Eros's list no longer contains "semantic/vector
  retrieval and embeddings"; Miller now owns "**optional local semantic retrieval**" and Eros owns
  "fleet-level semantics: cross-workspace/fleet ranking, guidance and confidence/evidence views,
  embeddings-as-a-service orchestration, suppression persistence, and commercial orchestration".
- The closing prohibition sentence: "Do not add Miller surfaces that need embeddings or semantic
  ranking" → a PERMIT sentence citing ADR-0003 + the design doc with the four operative constraints,
  followed by a narrowed prohibition ("surfaces that need **fleet-level semantics**"). The
  extraction-ownership prohibition is byte-identical.
- Search-sidecar bullet: added a two-line scope note that `search.db` stays lexical-only and the
  semantic arm lives in a separate `vectors.db` fused after ranking.

**Untouched:** MCP-stinginess paragraph, language-parity section, testing/test-split section, build,
release packaging, public docs, server host & startup, guidance-delivery-channels bullet (ADR-0001),
AGENTS.md-is-generated section. Verified by reading the full `git diff CLAUDE.md` — only the four
hunks above appear.

### 3. `README.md` — three spots (all located via Miller)

- Line ~8 product-split summary: "embedding-free" → "local-first, with **optional local semantic
  retrieval** that is off-switchable and leaves lexical-only results byte-identical"; Eros reframed
  to fleet-level semantics.
- Line ~317 1.0-replacement-story bullet: Eros bullet drops "semantic/vector retrieval and
  embeddings", gains "embeddings-as-a-service orchestration", and points at ADR-0003 for local
  semantic retrieval.
- Line ~728 "Known limits": rewritten from a permanent prohibition to an honest current-state limit —
  Miller **owns** local semantic retrieval but has **not shipped it yet**; links ADR-0003 + the design
  doc; restates the Eros-reserved fleet scope.

Left intact deliberately: line ~20 "cross-language bridge evidence stays structural and
provider-scoped, not embedding-driven" — ADR-0003 explicitly preserves the bridge conclusion, so this
line is still true.

### 4. `docs/README.md` — four map lines under "Current docs"

- `adr/ADR-0003-semantic-retrieval-ownership.md`
- `contracts/canary-telemetry-v1.md` with the exact wording supplied by the lead on behalf of Task 5
  (file created concurrently by Task 5; the line was added regardless, as instructed)
- `plans/2026-07-19-miller-semantic-integration-design.md`
- `plans/2026-07-19-p0-governance-and-gates-plan.md`

### 5. `AGENTS.md` — regenerated via `scripts/sync-agents.sh` (never hand-edited)

## Verification

- **Invariant:** boundary docs updated without weakening any machine-guarded guidance.
- **Scope:** assigned worker scope only (fast suite subset). No broader scope run.
- **Command:** `scripts/sync-agents.sh && cmp -s CLAUDE.md AGENTS.md && dotnet test --filter "FullyQualifiedName~AgentInstructionsTests"`
- **Result:** `AGENTS.md regenerated from CLAUDE.md.` → `cmp` exit 0 → **Passed! Failed: 0, Passed: 52, Skipped: 0, Total: 52, Duration: 35 ms**
- **Timestamp:** 2026-07-19, run in `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
- Ran twice: once after the initial edits (52/52 green, 36 ms), once after a line-wrap fix (52/52
  green, 35 ms).

## Files changed

| File | Change |
| --- | --- |
| `docs/adr/ADR-0003-semantic-retrieval-ownership.md` | created |
| `CLAUDE.md` | 4 hunks (intro, boundary ownership sentence, boundary prohibition sentence, search-sidecar scope note) |
| `README.md` | 3 hunks (product-split summary, 1.0-story Eros bullet, Known limits) |
| `docs/README.md` | 2 hunks, 4 added lines |
| `AGENTS.md` | regenerated |

Unrelated changes present in the worktree from sibling tasks (NOT touched by me):
`src/Miller.Server/Telemetry/TelemetryLedger.cs`, `tests/Miller.Tests/Server/EditToolTests.cs`,
`tests/Miller.Tests/Server/TelemetryLedgerTests.cs`.

## Miller calls used

| Call | What it confirmed |
| --- | --- |
| `workspace operation=status` | Worktree indexed and fresh (rev 1, 41,099 symbols); `content_db` current with 928 sources / 2,117 chunks. Needed because the first `search mode=content` failed with "content.db not found" — status showed the corpus had since converged. |
| `search query="semantic" mode=content` | Located `docs/plans/2026-07-19-miller-semantic-integration-design.md` and the P0 plan; established the benchmark-JSON noise floor that made file-scoping necessary. |
| `search query="Eros" mode=content` | Surfaced `TODO.md`'s Eros-boundary bullets (checked: complexity/dead-code/CLI-contract splits, no semantic assignment → out of scope, correctly untouched). |
| `search query="semantic vector retrieval embeddings Eros owns" mode=content file_pattern={README.md,CLAUDE.md,AGENTS.md,docs/README.md}` | Pinpointed the single CLAUDE.md/AGENTS.md ownership sentence at line 17. |
| `search query="Eros semantic" mode=content file_pattern=README.md` | Found **all three** README spots (lines 9, 317, 728) — the 1.0-story bullet alone would have been an incomplete edit. |
| `search query="embeddings" mode=content file_pattern={README.md,docs/README.md}` | Confirmed no further embedding assignments in those two files, and surfaced `docs/findings/embeddings.md` + `architecture-decision.md` as the historical rejection record ADR-0003 needed to reconcile with. |

Raw file regions were read only after Miller located them (`CLAUDE.md` 1–35 + 242–256, `README.md`
1–20 / 308–335 / 724–741, `docs/README.md` 1–45, the two existing ADRs, design doc §1–3 and §12).

## API-shape evidence

Docs-only task; no code APIs consumed. Shape evidence used:

- ADR structure taken from the two live files, not from memory: `ADR-0001` (has `Applies To` +
  `Future Agents`), `ADR-0002` (has a `Reverses:` header + `What stays forbidden`). ADR-0003 uses
  ADR-0001's fuller section set plus ADR-0002's `Reverses:` header, since this ADR reverses a prior
  decision.
- `docs/README.md` line format copied exactly: `` - [`path`](path) - description. `` (hyphen, not
  em-dash, in the separator position).
- Decision sentence checked character-for-character against design §2.1 / the lead's contract input.

## Judgment calls

- `docs/adr/ADR-0003-semantic-retrieval-ownership.md:26` — added a paragraph reconciling with the
  2026-05 `findings/embeddings.md` / `findings/architecture-decision.md` rejection, which the brief
  did not require. Chose this over silence because a future agent reading those findings would
  otherwise see a live, unexplained contradiction of ADR-0003; ADR-0001's precedent is to name and
  dispose of superseded reasoning explicitly.
- `docs/adr/ADR-0003-semantic-retrieval-ownership.md:63` — rendered the Eros migration inventory as a
  table rather than prose bullets. Chose a table because the brief specifies a two-column mapping
  (what Eros was slated to own → where it goes now) and a table makes "reserved" vs "moved"
  unmissable at a glance.
- `CLAUDE.md:3` — changed "does not own tree-sitter extraction or embeddings" to "or embedding
  generation", naming the sidecar. Chose to edit over leaving it because the original phrasing reads
  as a capability prohibition once the boundary flips; the edited form is a delegation statement that
  stays true (Miller consumes sidecar output exactly as it consumes extractor output) and matches the
  sentence's existing structure. Rejected alternative: delete the clause, which would lose the
  still-correct fact that Miller does not generate embeddings itself.
- `CLAUDE.md:259` — the search-sidecar "scope note" the brief named turned out to contain no semantic
  prohibition, so there was nothing to reverse. Chose to ADD a two-line note (`search.db` stays
  lexical-only; semantic arm lives in `vectors.db`, fused after ranking) rather than edit nothing,
  because later-phase workers reading that bullet need to know the semantic arm is a separate
  artifact and not an extension of `search.db`.
- `README.md:728` — rewrote "Known limits" as a not-yet-shipped limit rather than deleting the
  bullet. Chose this over deletion because semantic retrieval is genuinely not shipped today, and
  README is the public entry point; deleting the line would imply a capability users cannot use.
- `README.md:20` — left "cross-language bridge evidence stays structural … not embedding-driven"
  unchanged. ADR-0003 explicitly preserves the bridge conclusion, so the line is still accurate.
- `docs/README.md` — did NOT backfill the missing `ADR-0002` map line, despite noticing it is absent.
  Outside this task's minimal-edit intent; flagged below instead.

## Concerns

- **`ADR-0002` is missing from the `docs/README.md` map.** Pre-existing gap, noticed while adding
  ADR-0003. Not fixed — one line, trivially added by the lead if wanted.
- **`docs/contracts/canary-telemetry-v1.md` may not exist yet.** The map line was added as instructed
  on behalf of Task 5; if Task 5 does not land, that link dangles. No automated link checker exists
  in the fast suite, so nothing will catch it — worth a lead-side confirmation at integration.
- **`TODO.md` still contains Eros-boundary bullets** (complexity workflows, dead-code split, Eros
  CLI/export contracts). None assign semantics to Eros, so none contradict ADR-0003, but `TODO.md`
  was outside my ownership and I did not audit it line-by-line beyond the Miller hits.
- **Verification scope is narrow by design.** `AgentInstructionsTests` proves the guidance budgets
  and golden clauses still pass; it cannot check that the prose *means* what the ADR says. The
  semantic correctness of the boundary reversal rests on human review of the diff.
