# Tool latency branch Claude review

Date: 2026-08-29. Branch: `fix/tool-latency-health`. Review range: `058199ca..31aac1fe`.

No external-model policy was declared. The full branch diff was sent read-only to Anthropic at the
user's explicit request. Claude ran one general adversarial pass and one security pass. The CLI had
`Read`, `Grep`, and `Glob` only, strict MCP configuration, and no write-capable shell.

## Result

The two passes returned three findings each. After deduplication, the lead verified four medium
findings with Miller and the focused tests.

- **Fixed: edit misses symbols absent from a lagging search sidecar.** Edit now requests complete
  current-workspace recall from the live session projection. Ordinary inspect, context, impact, and
  trace reads keep the fast sidecar route. Commit: `2091700d`.
- **Fixed: global prune can retire a view for a temporarily unavailable root.** Store-member prune
  now requires positive persisted linked-worktree removal evidence: linked lineage, an accessible
  Git admin parent, and an absent exact Git admin entry. Otherwise the row stays and the result names
  exact `workspace remove`. Commit: `acf81bd9`.
- **Fixed: global prune can start producer work for every stale store member in one call.** Dry-run
  and apply now attempt one confirmed producer target per call and keep later targets with rerun
  guidance. Exact targeted remove remains unchanged. Commit: `acf81bd9`.
- **Dismissed: removal should proceed without the pinned extractor.** The approved retirement design
  deliberately keeps the registry row when the producer cannot retire the captured view. Removing
  it anyway would recreate the stale, untraceable producer-view problem this branch fixes.

The reviewer also suggested a new lease/background job system for cleanup. The producer operation is
already lock-holding, identity-validated, and idempotent. The one-target prune bound removes the
unbounded-call issue without adding another lifecycle system.

## Verification

- Integrated review-fix union: 579 passed, 0 failed.
- Final fast suite: 9,231 passed, 9 skipped, 0 failed.
- Final Scale suite: 203 passed, 18 skipped, 0 failed.
- Release build: 0 warnings, 0 errors.
- Gitleaks: 2,099 commits scanned, no leaks.
- NuGet vulnerability audit: no vulnerable packages.

Claude used 1,630 input tokens and 56,740 output tokens across both passes, with a reported cost of
`$17.94`. Cache accounting is available in the raw private result envelopes; those files were not
stored in the repository.

## Campaign status

```text
REVIEW CAMPAIGN STATUS
state: clean
evidence: external-reviewed
round: 2/2
external_invocations: 2/2
open_critical_high: 0
open_medium_low: 0
open_above_floor: 0
campaign_closed: yes
```
