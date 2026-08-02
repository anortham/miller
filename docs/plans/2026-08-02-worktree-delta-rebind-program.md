# Worktree Delta Rebind — Successor Program Plan

> **For agentic workers:** this is a two-repo program plan, not a single-session execution plan.
> Each phase below becomes its own razorback implementation plan (razorback:writing-plans →
> razorback:subagent-driven-development) in its owning repo when picked up. Do not begin
> implementation from this document without explicit user approval.

**Goal:** a fresh linked worktree of an already-indexed repo becomes ready in seconds-to-low-minutes
by rebinding the main checkout's artifact and delta-scanning only changed files, instead of paying a
full extraction per worktree.

**Provenance:** strategic follow-up to the 2026-08-01 field report
([`docs/findings/2026-08-01-multi-worktree-fleet-triage.md`](../findings/2026-08-01-multi-worktree-fleet-triage.md)).
That report exposed two distinct problems. The
[fleet-safety plan](2026-08-01-multi-worktree-fleet-safety-plan.md) (in implementation) fixes the
first: crashes — OOM cascades, spool leaks, ignore-rule loss, crash loops. This program fixes the
second: **cost** — every worktree builds its own full index (~25–40 min at 74k files with a bounded
jobs cap), serialized one-at-a-time by the new scan governor, so an 8-worktree fleet converges in
hours. Worktree-per-task agent workflows pay this on every task. The safety plan makes fleets *not
die*; this program makes them *usable*. The consensus round deliberately deferred this capability
("shared-artifact sibling-worktree bootstrap … future shape is a julie-extractors-owned artifact
contract"); this plan is that future shape, promoted to the committed next program (2026-08-02
architecture discussion, user-approved).

**Why zero-regret:** "bootstrap from an existing artifact + delta to my checkout" is the same
contract as "pull a CI-built artifact and delta to my checkout." Sibling worktrees are the local
proof; a future Eros artifact service is the same seam at team scale. Building this forecloses
nothing and founds both.

**Status:** approved as the successor program. Gated on the fleet-safety plan landing; each phase
still requires explicit user approval to begin implementation.

## Start gate

- Fleet-safety plan landed in both repos. Hard dependencies: W7 (git-worktree metadata adapter),
  W3 (machine scan governor), W8 (persisted scan-failure policy). Soft dependency: the W10
  74k-file fixture (reused for P0 measurement and P4 validation).
- julie-extractors release window approved for the rebind contract (P2 pin bump).

## Global constraints (carried forward)

- Miller stays a read-only consumer of julie-extract output; **Miller never rewrites extractor
  metadata privately** — rebind's root/identity rewrite is executed by julie-extract itself.
- No new MCP tools; new state surfaces through existing `workspace status`/`health` JSON, CLI
  verbs, or the dashboard.
- `Miller.Core` remains I/O-free; new I/O lives in `Miller.Indexing`/`Miller.Server`.
- Lexical-only search output stays byte-identical; nothing here touches ranking or fusion.
- Fast suite stays fast: policy/lineage/decision logic gets pure unit tests; anything spawning
  julie-extract is `[Trait("Category","Scale")]` via `ScaleTestSupport.RequireJulieServer()`.
- julie-extract additions are opt-in with unchanged defaults, adopted behind a
  `scripts/julie-pins.json` bump. No release/publish/pin-bump without explicit user approval.
- Rebind is extraction-semantics-neutral (it reuses existing extraction), so the language-parity
  rule needs no per-language work — but P4 must validate on a real multi-language artifact.

## Phases

### P0 — Measurement riders on W10. ~0.5 agent session.

Extend the W10 fixture runs with the measurements the 2026-08-02 architecture discussion found
missing; these numbers drive the P1 design choices.

- RSS per `--jobs` level at 74k-file scale — the data for future memory-aware admission (the
  governor currently bounds *count*, not memory).
- Post-scan embedding/vector convergence load — it runs **outside** the scan governor (woken async
  per workspace, `src/Miller.Server/Hosting/IndexerService.cs:672–676`); measure whether N
  workspaces stack CPU-side convergence after their scans.
- Linux inotify watch consumption across N worktree workspaces (per-directory watches × N; silent
  exhaustion would regenerate the overflow-rescan storm class W9 fixed for branch switches).
- Artifact clone cost per platform at multi-GB size: APFS `clonefile` (`cp -c`), Linux reflink
  (btrfs/XFS) vs full copy, Windows full copy.
- **Real-repo tier (added 2026-08-02, first results already in):** synthetic fixtures cannot
  contain real-world pathologies — proven immediately. The standing real-repo target is
  **dotnet/runtime @ `a2f953fe266`** (58,500 files, .NET wedge). The first baseline run
  ([`docs/findings/2026-08-02-dotnet-runtime-scale-baseline.md`](../findings/2026-08-02-dotnet-runtime-scale-baseline.md))
  found four julie-extract 2.21.0 bugs: a **stack-overflow crash** at default thread stacks, **~68×
  worst-case spool amplification** (982 KB source → 66.6 MB spool entry; ~10× aggregate), the
  blocker — a non-recoverable **`reference_site identity conflict`** on duplicated generated code
  that aborts the whole import (the repo cannot be indexed at any stack size) — and a bare
  "failed" non-JSON error path. Healthy-run projection ~6–8 min end-to-end at jobs=4 (~190
  files/s extraction); **artifact write is ~90% of small-repo cold start** (39.6 s of 44.2 s on the
  1.5k-file Miller repo — the top first-impression fix for typical repos). All four bugs plus the
  write-throughput target need julie-extractors work — bundle into the same release as the
  fleet-safety W4–W6 flags. P0's
  real-repo baseline is **gated on that fix**; the ladder for later tiers: dotnet/roslyn (~15k),
  openjdk/jdk (~70k), linux (~85k), llvm-project (~150k, stress).
- Acceptance:
  - [ ] Each measurement recorded in a findings doc alongside the W10 spool/RSS/WAL numbers.
  - [ ] A clean, crash-free timed extract of dotnet/runtime @ pinned commit with final DB/WAL
        sizes and phase timings (extract vs artifact write).

### P1 — Rebind contract design doc. ~1 agent session.

julie-extractors-owned contract: given a source artifact + a new root, julie-extract rewrites the
recorded root/identity metadata itself and runs an incremental delta scan keyed on the existing
blake3 `files.content_hash`.

- Decide v1 shape: **copy-and-rebind (recommended)** — clone/copy the artifact, rebind, delta-scan
  — vs **base+overlay** — SQLite ATTACH read-through to the main artifact plus a per-worktree
  overlay DB of changed files. Evaluate overlay on paper with P0 clone-cost data; keep it
  documented as the possible future shape (near-zero disk, instant open; harder consistency:
  cross-boundary references, base artifact changing underneath).
- Decide identity approach. Lean: keep `WorkspaceId.FromCanonicalRoot`
  (`src/Miller.Indexing/WorkspaceId.cs:10`) unchanged and add registry **lineage columns** (git
  common dir + admin-dir generation, resolved via the W7 adapter) for sibling-artifact lookup and
  path-reuse detection — rather than a breaking workspace_id re-derivation. Final call in the
  design doc.
- Define failure semantics: a failed/interrupted rebind must leave the source artifact untouched
  and fall back cleanly to a full scan under the W8 backoff policy.
- Cross-model review gate (Codex + Grok) before the contract freezes, per repo convention.
- Acceptance:
  - [ ] Design doc in `docs/plans/` with the v1 shape, identity call, and failure semantics;
        cross-model review recorded.

### P2 — julie-extractors implementation. ~1–2 agent sessions + release/pin-bump approval.

- Implement the rebind verb/flags + delta scan per the frozen P1 contract, with crate tests
  (`cargo test -p julie-extract-cli` / `-p julie-extract-artifact`) covering rebind identity
  rewrite, delta detection via content hashes, and interrupted-rebind recovery.
- Ship a release; bump `scripts/julie-pins.json` in Miller (user approval required).
- Acceptance:
  - [ ] Rebound artifact is indistinguishable from a fresh scan of the same tree (row-level
        equivalence on a multi-language fixture).

### P3 — Miller wiring. ~1.5–2 agent sessions.

- Bootstrap path: when opening a linked worktree with no artifact whose main checkout has one
  (registry lineage lookup from P1's columns), run rebind + delta instead of a full scan — still
  under the W1 bootstrap lock and W3 governor.
- Build into `symbols.db.rebuild` and promote via the existing
  [`FullRebuildPromotion`](../../src/Miller.Indexing/FullRebuildPromotion.cs) promote-not-merge
  machinery; never touch the source (main checkout) artifact.
- Failure falls back to a full scan under the W8 failure policy (recorded, backed off — no
  immediate re-force).
- Surface provenance in `workspace status`/`health` JSON and the dashboard ("rebound from
  <source workspace>"). No new MCP tools.
- Sidecars (search/content/vectors) converge from the rebound artifact through the existing
  revision-keyed paths; no sidecar copying in v1.
- Acceptance:
  - [ ] Fresh linked-worktree open with an eligible sibling artifact runs rebind, not full scan.
  - [ ] Rebind failure leaves the workspace on the W8 backoff path with the source artifact intact.
  - [ ] Provenance visible in `workspace status --json`.

### P4 — Scale validation. ~1 agent session.

- On the W10 74k-file fixture: fresh worktree open completes in seconds-to-low-minutes (target:
  ≥10× faster than the full-scan baseline measured in W10).
- 8-worktree fleet from one indexed main checkout converges in minutes, not hours.
- SIGKILL mid-rebind leaves recoverable state: source artifact intact, `.rebuild` debris cleaned by
  the next attempt, no spool leak beyond the safety plan's guarantees.
- Validate on a real multi-language artifact (language-parity check: per-language row counts match
  a fresh scan).

## Candidate follow-on — progressive indexing levels (discussion, NOT committed scope)

Raised 2026-08-02 (user): cold start could serve basic functionality fast and deepen in the
background — "level 1" minimal index immediately, richer levels converging behind it. Recorded here
so it isn't lost; requires its own design + explicit approval, and is deliberately sequenced
**after** the P0 extractor fixes and re-measurement, because the spool autopsy suggests the bugs
may erase much of the need.

- **Natural levels already exist in the artifact:** L1 = files + hashes + symbols/signatures
  (serves `search`, `inspect`, `context` — most first-minutes agent value); L2 = reference
  sites/relationships (serves `refs`, `trace`, `impact` — and per the spool autopsy, the dominant
  extraction cost); L3 = source_regions + structural_facts (serves `patterns`, region search);
  L4 = embeddings (**already progressive today** — lexical serves immediately, vectors converge in
  the background, fail-open). The semantic arm is the in-tree proof of the pattern: default-on,
  honest degradation, background convergence.
- **Ownership boundary:** levels must be a julie-extract contract feature (e.g. a scan depth flag
  plus a "deepen" pass that upgrades an existing artifact in place of a rescan), uniform across all
  supported languages; Miller orchestrates scheduling and surfaces per-capability readiness
  ("search ready; references converging") in status/health and tool not-ready results. Deepening
  writes follow promote-not-merge or revision-keyed converge — never a bulk in-place merge on a
  served artifact.
- **Second axis — priority, not just depth:** index src-first/recently-changed-first and serve
  partial results early. At the measured ~190 files/s, priority ordering alone puts the files an
  agent will actually ask about into the index within seconds; possibly a cheaper UX win than
  depth levels, and the two compose.
- **Escalation policy if built:** automatic by default (L1 ready → L2+ kick off in the background),
  env/config cap for CI or huge repos — mirroring the `MILLER_SEMANTIC` default-on/off-switch
  precedent.

## Non-goals with standing triggers

Documented so they stop being open questions; revisit only when the trigger fires.

- **Central machine daemon** — trigger: sustained 20+ heavy-churn worktrees with governor-wait pain
  visible in status/telemetry. Escalation path is governor lease → small elected broker process
  (the semantic-broker precedent), not a rewrite. Rebind pushes this trigger further out by making
  the governed operation cheap.
- **LanceDB / storage-engine swap** — trigger: ~100× local embedding growth. The artifact-as-
  copyable-file property is load-bearing for this entire program (and any Eros future); a server
  database would destroy it to solve a write-contention problem Miller does not have.
- **Eros artifact service** — trigger: concrete demand for team-shared or CI-built artifacts. This
  program's rebind contract is deliberately its foundation: a central service producing artifacts
  that local Millers rebind is the same contract as a main checkout serving its worktrees. Runtime
  (Miller, agent-facing MCP surface) vs supply chain (Eros) is the boundary — not addon vs
  replacement.

## Sequencing

| Order | Item | Depends on |
|---|---|---|
| 1 | P0 measurement riders | fleet-safety W10 fixture |
| 2 | P1 contract design doc | P0 data; W7 adapter shape |
| 3 | P2 julie-extractors rebind + release + pin bump | P1 freeze; user approval |
| 4 | P3 Miller wiring | P2 pin bump; W1/W3/W8 landed |
| 5 | P4 scale validation | P3 |

Estimated total: **~5–7 agent sessions** across both repos, plus two human approval points (the
julie-extract release/pin bump, and the Miller release that ships rebind).

## Verification strategy

**Project source of truth:** Miller `CLAUDE.md` (testing split + build guards); julie-extractors
workspace `cargo test` conventions.

**Worker red/green scope:** Miller — `scripts/test.sh` (fast suite) for every change; pure unit
seams for lineage/decision/fallback logic. julie-extractors — targeted crate tests.

**Lead affected-change scope:** `scripts/test.sh scale` for the bootstrap/rebind path;
julie-extractors full `cargo test` + `xtask dogfood`.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all` before
any PR; julie-extractors release checklist before the pin bump.

**Program success criteria:**
- [ ] Fresh linked-worktree open on an already-indexed repo: ≥10× faster than full-scan baseline.
- [ ] 8-worktree fleet convergence measured in minutes on the W10 fixture.
- [ ] Rebound artifact row-equivalent to a fresh scan across all languages present.
- [ ] No failure mode can corrupt or mutate the source (main checkout) artifact.
