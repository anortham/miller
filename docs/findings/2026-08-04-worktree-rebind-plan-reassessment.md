# Worktree delta-rebind plan reassessment — 2026-08-04

The [rebind program plan](../plans/2026-08-02-worktree-delta-rebind-program.md) was written
2026-08-02 and then leapfrogged: fleet-safety landed (W1–W10 merged, pin now 2.25.0) and the plan's
own "candidate follow-on" — progressive indexing levels — was promoted, implemented, and shipped in
v1.16.0/v1.16.1 before rebind's P0 ever started. This document records the 2026-08-04 reassessment:
the lead's repo audit plus independent read-only reviews by Codex and Grok (both given the same
prompt; both investigated the repo themselves). **Both reviewers independently concluded: do not
execute the plan as written.** Raw outputs preserved in the session scratchpad
(`codex-rebind-review.txt`, `grok-rebind-review3.txt`); everything decision-relevant is restated
here.

## What changed since the plan was written

| Plan assumption (2026-08-02) | Reality (2026-08-04) |
|---|---|
| Start gate open: fleet-safety "in implementation" | **Gate met.** W1–W10 merged (`11247e91`); W7 adapter, W3 governor, W8 backoff, W10 fixture measurement all exist. |
| P0 real-repo baseline gated on four julie-extract 2.21.0 bugs | **Cleared.** 2.23.1 fixed them; clean dotnet/runtime baseline recorded ([v2.23.1 baseline](2026-08-03-dotnet-runtime-v2231-baseline.md)). |
| Full worktree cost "~25–40 min at 74k files" | Was already low (W10 measured 61.3 min on 2.21.0); now obsolete in the other direction: 2.24.0 cut dotnet/runtime full scan 76.3 → 23.7 min, and symbols-level first-open serves in **3.9 min** ([levels P3 validation](2026-08-03-progressive-levels-p3-validation.md)). Miller-sized repo: 9.7 s symbols vs 27.4 s full ([levels benchmark](2026-08-04-index-levels-indexing-benchmark.md)). |
| "Artifact write ~90% of small-repo cold start"; healthy dotnet projection 6–8 min | Both superseded by 2.24.0's write rework and the [reference-layer index audit](2026-08-03-reference-layer-index-audit.md) (three retired indexes, −2.43 GiB on the dotnet artifact). |
| Levels are a hypothetical L1–L4 follow-on | **Shipped**: two immutable levels (`symbols`/`full`), `MILLER_INDEX_LEVELS`, registry `level_policy`, derived `LevelUpgrade` latch, level-aware read guards ([levels design](../plans/2026-08-03-progressive-indexing-levels-design.md)). |
| W10 74k fixture reusable for P0/P4 | The fixture **harness was never committed** (W10 finding §9 — deliberate); only its recipe survives. |
| Rebind is "extraction-semantics-neutral", no levels work needed | **False now.** Artifacts carry an immutable `index_level`; the rebind contract must be level-aware throughout. |

## What survives untouched

The load-bearing invariants all still hold and none of the landed work conflicts with them:
promote-not-merge, never mutate the source artifact, no new MCP tools, `Miller.Core` I/O-free,
W8 backoff as the only retry timer, and the standing non-goal triggers (daemon / LanceDB / Eros).
The rebind idea itself — clone + rebind + delta as the same seam a future CI/team artifact pull
would use — is intact; what changed is the size of the problem it solves and the contract it must
honor.

## P0 measurement riders — actual remaining state

- **Already covered:** real-repo timed extract with phase split (2.23.1 baseline), W10-scale
  spool/WAL/RSS (on 2.21.0 — magnitudes obsolete), symbols-vs-full cost at scale and small-repo
  (levels P3 + benchmark).
- **Still genuinely unmeasured:** RSS per `--jobs` level; embedding/vector convergence stacking
  across N workspaces (every benchmark so far ran `MILLER_SEMANTIC=off`); Linux inotify watch
  consumption across N worktree workspaces; **artifact clone cost per platform** (APFS clonefile,
  Linux reflink vs full copy, Windows full copy) at both artifact levels (~5.5 GiB symbols /
  ~20.4 GiB full at dotnet scale).
- **Needs re-measurement on 2.25.0** before any target is set: any full-scan denominator. The
  P4 "≥10× vs full-scan baseline" bar is meaningless against the 2.21.0 numbers.

## Levels × rebind: what P1 must now decide

1. **Level propagation.** Rebound artifact inherits the source's immutable `index_level`; the
   rebind verb must not pass `--level` (julie 2.25 rejects level conflicts on existing artifacts,
   and `update` already extracts at the recorded level — the delta side is handled).
2. **Eligibility matrix.** symbols→symbols and full→full rebind; a symbols source can never yield
   a full target (the reference layer cannot be delta'd into existence). If the target policy
   wants full and only a symbols sibling exists: rebind symbols + let the derived `LevelUpgrade`
   latch re-arm (it derives from artifact metadata + policy, so this works by construction — but
   P3 must verify rebind never masks it).
3. **Lineage columns + level.** The registry has `level_policy` but no lineage columns yet; the
   P1 lineage lookup (git common dir + admin-dir generation via the W7 adapter) must also carry
   source level, extractor/schema/hash compatibility, and artifact readability — not just "main
   checkout has a DB".
4. **A distinct scan intent/operation.** Do NOT reuse `ScanIntent.RootRebind` — it already means
   foreign-root *repair* (own-intent-only, never downgradable, mapped to a symbols rebuild under
   progressive policy). Sibling-bootstrap rebind is a different operation with different failure
   semantics and needs its own name in the intent/outcome space. (Codex finding, verified in
   `ScanIntent.cs` / `IndexLevels.cs`.)
5. **Provenance lifecycle.** "Rebound from <source>" must survive — or explicitly evolve through —
   a later full-level upgrade promote; record bootstrap origin separately from current-artifact
   origin or the upgrade either erases provenance or falsely retains it.
6. **Failure semantics vs W8.** Resolve the tension between "falls back cleanly to a full scan"
   and "recorded, backed off, no immediate re-force": a failed rebind (a failed *optimization*)
   must not poison `scan-failure.json` in a way that delays the correctness fallback, and must
   never touch the source artifact.
7. **Per-level equivalence.** "Rebound artifact indistinguishable from a fresh scan" must be
   defined as level-matched semantic row equivalence excluding expected deltas (root, artifact_id,
   revision history, timestamps, provenance) — literal indistinguishability is impossible.

## Value case — both reviewers, converged

Levels + 2.24/2.25 already bought most of the first-impression win the plan was chartered for.
What rebind still uniquely buys:

- **Skipping the full/reference-layer rebuild** — still ~22 min and ~20 GiB per worktree at dotnet
  scale, and levels only defer it; a fleet whose agents need `trace`/`impact`/rename-safety pays
  it N times, serialized by the governor (hours for 8 worktrees).
- **Removing N duplicate extractions** — levels shrink each governor ticket; rebind removes the
  tickets.
- **Disk × N — only if the clone is CoW.** A blind 20 GiB full copy × 8 worktrees on ext4/Windows
  could make copy-and-rebind **net-negative**; clone economics is the single go/no-go measurement.
- The CI/Eros artifact-pull seam remains real but non-urgent (Eros archived); "zero-regret" was
  overclaimed — a remote artifact service adds trust/integrity/transport contracts local rebind
  never exercises.

**Converged recommendation:** narrow, don't execute as written. v1 = **full-source → full-target
CoW clone + rebind + delta** (the symbols-source path is marginal against a 3.9-min cold symbols
open; base+overlay stays paper-only). Run a thin P0 measurement gate on 2.25.0 first — clone cost
per platform at both artifact sizes plus a re-based full-scan denominator — and proceed only if
clone+delta clears a material bar (Codex suggested ≥3× and ≥10 min saved per large worktree) or
telemetry shows governor-wait / full-readiness pain in real fleets. Estimated narrowed program:
~3–5 agent sessions instead of 5–7. The alternative — defer with that same telemetry trigger
recorded — is honest if current progressive-default fleets are good enough.

## Decision needed (user)

1. **Narrow + proceed:** amend the plan per this reassessment, then start the thin P0 measurement
   gate (clone economics + 2.25.0 re-baseline) under the plan's existing per-phase approval rule.
2. **Defer with trigger:** amend the plan to deferred status with the telemetry trigger
   (sustained governor wait / repeated full-level opens), revisit when it fires.
3. **Amend only:** update the plan document to be truthful today (gate met, stale numbers, levels
   interaction) without committing to either path.
