# Workspace Correctness, Safety, And Bounds Evidence

**Date:** 2026-07-25  
**Status:** implementation complete; final Claude re-review clean

## Outcome

Miller keeps its decisive workspace advantage over Julie: fixed process binding,
cross-process leadership, atomic rebuild promotion, registry lifecycle, health,
onboarding, dashboard launch, typed diagnostics, and stable CLI contracts. The
implementation also ports Julie's useful root-existence signal and registered-only
removal rule without adopting Julie's mutable session switch or list-time cleanup.

## Accepted audit findings

| Finding | Disposition |
|---|---|
| Removal could derive a delete target from a corrupt registry path or machine-global directory | One shared core validates `<root>/.miller/symbols.db`, rejects sensitive/global targets, and deletes only registered workspaces |
| MCP health could return exhaustive JSON/markdown | MCP exposes bounded compact/summary JSON only; exhaustive JSON/markdown is CLI-only |
| Onboarding loaded and grouped the full telemetry window, then silently truncated | SQL aggregates run in one read transaction and carry exact section totals/omissions |
| List queried the registry twice and always reported success/current registry count | One registry snapshot drives rows, totals, result count, and typed empty outcomes |
| A missing registered root produced generic recovery | `workspace_root_missing` offers prune preview and registered removal actions |
| Cross-workspace status omitted leader/version facts | MCP and CLI status gather the target's leader identity and extractor/artifact versions |
| Compact health rendered unavailable extraction sections as healthy-looking zeroes | Every unavailable aggregate is labeled and counted separately |
| List did not stat roots | Every row exposes `root_missing` plus exact registered/matched/returned missing totals |
| Invalid parameters and failures were silently coerced or inferred from output text | Operations validate their parameter sets and return explicit typed diagnostics |
| A corrupt registration could use a symlinked root or `.miller` directory to escape the registered delete target | Removal verifies both paths remain symlink-resolved canonical paths before acquiring leases or deleting |
| Current status, leader, and onboarding could report typed success when their required index or telemetry source was unavailable | Missing sources now return explicit unavailable diagnostics and error outcomes |
| Refused oversized operations were persisted verbatim as telemetry operation keys | The telemetry operation is assigned only after request validation succeeds |

## Additional corrections

- Every MCP workspace operation is subject to a 12 KiB serialized ceiling.
- JSON and compact list output use the same default limit of 20 and allow only 1–100.
- List and prune byte fitting preserve exact totals and omission counts.
- Health grouped reads share one SQLite snapshot.
- Onboarding never truncates its privacy explanation.
- Onboarding compact output now includes friction rather than silently omitting it.
- Missing or unreadable target health returns an error outcome with a recovery action.
- Refresh/full statuses map to precise success, refusal, or unavailable diagnostics.
- Non-search reads remain independent of search-sidecar readiness.
- Removed unsafe behavior has no deprecated compatibility path.
- Exact registry-wide missing-root totals intentionally perform one synchronous existence probe per row; the
  CLI contract documents that cost because exact coverage was retained over a cheaper partial count.

## Julie comparison

Ported:

- root existence in workspace inventory;
- registered-target-only deletion;
- clear typed blocked/missing outcomes.

Intentionally not ported:

- mutable session-wide workspace switching;
- list-time registry mutation;
- unbounded workspace inventory;
- destructive suggestions attached to every lifecycle response.

## Claude review

The fresh read-only Workspace pass used Claude Opus with high effort and JSON
output. It reported nine material findings; all nine were accepted after local
source and behavior validation and are recorded above. Later full-diff passes
found eight additional issues, followed by two low contract/coverage gaps. Each
claim was checked locally; the exact registry-wide root probe was retained as an
explicit product choice, and every confirmed defect was fixed. After the latest
exact-state pass found two low guidance/evidence gaps, both were repaired and the
final fresh follow-up returned `verdict=clean`.

## Verification

- Focused Workspace, renderer, removal, onboarding, health-reader, prune, and CLI
  gate: 537 passed, 0 failed, 0 skipped.
- Fast gate: 4,968 passed, 2 platform/environment skips, 0 failed.
- Scale gate: 91 passed, 3 optional semantic/platform skips, 0 failed. The live
  removal fixture now registers its canonical non-live target before exercising
  the registered-only deletion contract.
- Release build: 0 warnings, 0 errors.
- `git diff --check`: passed.
- Public contracts: `workspace-health-v1`, `workspace-onboarding-v1`,
  `workspace-status-v1`, and `cli-eros-v1`.
