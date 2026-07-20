# P2 Miller lanes — verification ledger

Plan: [2026-07-20-p2-miller-lanes-plan.md](2026-07-20-p2-miller-lanes-plan.md). Scope labels per the plan's
Verification Strategy. Worker-scope evidence lives in the per-task reports under `.razorback/sdd/`.

| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| affected-change | Batch A (B1+C1+E1+D1) integrated: no regression across the fast suite incl. golden search parity, off-guarantee, clones byte-stability, edit failure stamping | `scripts/test.sh` | f2dcb63 | PASS — 3733 passed, 0 failed, 1 skipped (25s test phase). Wall tripwire fired at 33s (>30s ceiling): B2 worker building in parallel; workers measured 22–28s warm. Re-measure quiet at branch gate before treating as a leak. | 2026-07-20 |
| branch-gate | All 11 P2 tasks integrated: full fast + scale suites green, Release build 0 warnings | `scripts/test.sh all` (SPIKE_CACHE_DIR set; real sqlite-vec + julie-extract Scale legs ran, not skipped) | fd05737 | PASS — fast 4004 passed / 0 failed / 2 skipped, 26s wall (under 30s ceiling); scale 75 passed / 0 failed. One prior gate attempt hit the pre-characterized IndexerServiceLeadership load flake (passes 23/23 isolated; B4 proved it at clean baseline); the recorded run is the clean re-run. | 2026-07-20 |
| branch-gate (post-review-fixes) | Codex review fixes (ce791aa) integrated: full fast + scale suites green, Release build 0 warnings | `scripts/test.sh all` (SPIKE_CACHE_DIR set) | ce791aa | PASS — fast 4014 passed / 0 failed / 2 skipped, 27s wall; scale 78 passed / 0 failed. First attempt had 1 non-reproducing failure (two consecutive clean runs followed; consistent with the pre-characterized IndexerServiceLeadership load flake). | 2026-07-20 |
