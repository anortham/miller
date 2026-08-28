# Miller stale-view cleanup — 2026-08-28

This finding records the user-approved cleanup of nine missing-root views from Miller family
`a271f2bd-7368-4da6-b5aa-24ffad69fb1f`. The cleanup used the implementation committed at
`5e75ec23`; it did not run broad `workspace prune`.

## Before

- The producer store contained 12 views. Git reported two active worktrees: the main checkout and
  `tool-latency-health`.
- Nine producer views had missing roots and matched the approved inventory exactly.
- Seven were still registered in `~/.miller/workspaces.db`; `performance-recovery` and
  `release-1.18.3` were producer-only legacy views.
- Main (`7857a50b-4b5a-47ba-8c45-d4df703cc79e`), the task worktree
  (`26382897-e460-4b0a-a6a1-3e4f67201aea`), and `ct-dogfood-round2`
  (`eca382b2-ee00-4543-89d4-5cea01e6897b`) were present and explicitly excluded.

| missing root | view id | lifecycle path |
|---|---|---|
| `linux-dogfood-fixes-plan` | `72ea44ff-d05a-45bf-93dd-05514a6b2d36` | targeted workspace remove |
| `perf-ct-audit-2026-08-23` | `6d21a5ae-fa48-4cbd-90f9-6e79e98e049c` | targeted workspace remove |
| `performance-recovery` | `0be23b5f-20f3-4fc9-8ea1-19d3be06b630` | exact producer-only retirement |
| `qml-first-class-miller` | `77e1e6ac-1928-4e83-9f23-7e58e6cd75a1` | targeted workspace remove |
| `release-1.18.3` | `9b4d387d-9b15-4451-80be-5963a90eb880` | exact producer-only retirement |
| `release-1.21.1` | `5bf03b89-43ff-4cea-bfb0-878ba88b2673` | targeted workspace remove |
| `search-alias-canonicalization` | `8930c810-7207-487f-9da5-743841efb85f` | targeted workspace remove |
| `windows-release-hardening` | `18c8abd0-692f-4190-bd1e-3e85e43691b8` | targeted workspace remove |
| `windows-scale-contract-separation` | `55e78b4b-bdfc-4271-b78c-66b94da2dbf1` | targeted workspace remove |

## Execution

- Each exact family/view pair was rechecked against the live generation immediately before work.
- All nine dry-run reports returned `action=retire_view`, `mode=plan`, `disposition=planned`, and
  `counts.retired_views=1`. Preview elapsed time was 18.9–22.3 seconds.
- The seven registered targets ran through the rebuilt branch CLI. Each command previewed, applied,
  removed its registry row, and reclaimed two sidecar files. Elapsed time was approximately 65–90
  seconds per target.
- The two producer-only targets were applied directly by exact view id. Each returned
  `disposition=applied` and `counts.retired_views=1` in about 56 seconds. No matching per-view
  sidecar files existed.
- Registered cleanup reclaimed 14 sidecar files totaling 2,729,623,552 bytes.
- One final producer GC applied successfully: 741 manifests removed, 100 requests archived, 66
  request rows and 8,077 log rows pruned, and 100 versions demoted to L3.

## After

- The producer store contains exactly three views: main, `tool-latency-health`, and
  `ct-dogfood-round2`.
- All nine approved view ids are absent. All seven matching registry rows are absent.
- The two active Git worktrees are unchanged. All three excluded roots remain present.

## Impact timing

The same committed Task 2 git diff was replayed through the resident MCP process at depth 2 and
limit 200. Before cleanup, its graph phase took 12,061 ms; the call took 26,676 ms because a
14,413 ms workspace refresh was also requested. After cleanup, five warm calls took 8,466–8,607 ms
total, with graph phases of 8,284–8,418 ms. Removing stale views reduced this workload's graph time
by about 30%, but the 5,000 ms impact gate remains open. A clean-process branch CLI replay took
11.77 seconds including startup.

