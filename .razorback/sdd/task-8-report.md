# Task 8 report — ADR-0001, CLAUDE.md bullet, evidence loop

**Status:** complete
**Worktree:** `/Users/murphy/source/miller/.worktrees/guidance-delivery` (branch `guidance-delivery`)

## What changed

- **Created `docs/adr/ADR-0001-guidance-delivery-channels.md`.** `docs/adr/` did not exist, so this is
  the first ADR. Shape: Context / Decision / Consequences / Applies To / Future Agents. Captures the
  2026-07-02 measurement (11,856-char doc cut at char 2,047), issue #43474, Tool Search deferral of
  descriptions, and the telemetry baseline (two surviving tools took ~73% of 5,249 calls; seven cut
  tools nearly unused). Documents the three-channel decision (core ≤1,900 discovery / descriptions
  ≤900·1,500·1,100 usage / NextStepHint nudges) plus tail relocation and the accepted non-plugin loss.
- **Modified `CLAUDE.md`** — one load-bearing bullet appended to the end of "## Server host & startup"
  (after the Agent instructions bullet), matching neighbour style: descriptions = usage contract
  (≤900 default / documented overrides), core = discovery contract (≤1,900; ~2KB truncation inside a
  shared ~4KB block), do not grow either or re-invent the 12k budget without reading ADR-0001; gates
  live in `AgentInstructionsTests`.
- **Ran `scripts/sync-agents.sh`** — regenerated `AGENTS.md`; `cmp -s CLAUDE.md AGENTS.md` → SYNCED.
  AGENTS.md not hand-edited.
- **Modified `docs/plans/2026-07-02-guidance-delivery-design.md`** — appended one line to "### 5.
  Evidence loop (report-only)": follow-up checkpoint due ~2026-07-23, re-read tool mix with
  `miller workspace onboarding --json` and append the before/after comparison to the doc.

## Verification

- `cmp -s CLAUDE.md AGENTS.md && echo SYNCED` → **SYNCED** (harness docs are byte-identical mirrors).
- `dotnet build Miller.slnx -c Release` → **Build succeeded, 0 Warning(s), 0 Error(s)**.
- Docs-only task; no test scope owned (per plan Task 8 — no worker filter).

## Concerns

None. Commit scope limited to the four owned files (ADR, CLAUDE.md, AGENTS.md, design doc) by explicit
path; this report is intentionally left uncommitted (not in the owned set). Did not push.
