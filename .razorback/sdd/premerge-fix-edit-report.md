# Pre-merge fix: EditTool unhandled-path telemetry metadata

**Worktree:** /Users/murphy/source/miller/.claude/worktrees/semantic-integration
**Branch:** worktree-semantic-integration
**Dirty state at start:** clean
**Commit SHA:** none - parallel-lead-commit

## Finding
`EditTool.Edit` stamped all request-derived telemetry (Op, SetTarget, and the eight request-fact
SetMetadata calls) only after `EditService.Execute(request)` returned. An exception escaping the
pipeline produced a row classified `unhandled_<Type>` but with no operation, no target hash, and no
request flags — unusable for operation-level diagnosis.

## Miller orientation
- `inspect target=EditTool.Edit depth=overview` — confirmed the single try/catch shape, the
  post-Execute stamping block, and 14 dependents (all in EditToolTests).
- `inspect target=TelemetryScope depth=overview` — confirmed `Op` (settable property) and
  `TargetHash` (SHA256 hex, set via `SetTarget`), plus `MetadataJson` as the assertion surface.
- Test-file structure read directly (grep + targeted reads): existing telemetry tests capture the
  scope by `ledger.Measure("edit", op: null)` on a real `TelemetryLedger` over a temp
  `telemetry.db`, then assert against `telemetry.MetadataJson` via the `StampedFailureBucket`
  helper. No new capture mechanism was invented.

## Change
`src/Miller.Server/Tools/EditTool.cs`
- Moved `telemetry.Op`, `telemetry.SetTarget(target)`, and the `format`/`apply`/`allow_stale`/
  `has_scope`/`match_mode`/`has_query`/`has_anchor`/`has_line` SetMetadata calls into a new
  `if (telemetry is not null)` block placed BEFORE the `EditService` construction and `Execute`.
- Result-derived stamps (`ResultCount`, `IndexFresh`, `Outcome`, failure-reason from result,
  `edit_noop` empty reason) stay after Execute, unchanged.
- Catch block untouched; it now inherits the pre-stamped request facts.
- Values are byte-identical to what was stamped before — all derive from the method arguments and
  are privacy-safe (hashes and booleans, no raw text).

`tests/Miller.Tests/Server/EditToolTests.cs`
- Added `Edit_UnhandledException_RetainsRequestDerivedDiagnosisMetadata`, using the existing
  `ThrowingWriteThrough` fake and ledger/scope pattern. Asserts on the exception path:
  `Outcome=Error`, `Op == "replace_symbol_body"`, a non-empty `TargetHash`, and the request
  metadata keys `match_mode=exact`, `apply=true`, `allow_stale=false`, `has_scope=true`,
  `has_line=true`, `has_query=false`, `has_anchor=false`, `format=compact`.
- Existing `Edit_UnhandledException_StampsExceptionTypeNameBucketWithoutMessage` left intact.
- Zero comments per repo rules.

## Verification
`dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~EditToolTests"`
→ **Passed! Failed: 0, Passed: 70, Skipped: 0, Duration: 1s** (Release build, 0 warnings/0 errors).

**Invariant proved:** an edit call whose pipeline throws still records a telemetry row carrying the
request-derived diagnosis facts — operation, target hash, and the request flag metadata — alongside
the `unhandled_<Type>` failure bucket. Under the old ordering these were all absent, so the new test
is red before the source change and green after.

## Concerns
- The finding text mentioned an `operation` metadata key; there is no such metadata key — operation
  is the scope-level `Op` column. The test asserts `telemetry.Op` accordingly.
- No other tool shell was audited for the same ordering bug; if the P0 telemetry task cares
  repo-wide, sibling tools (search/inspect/trace/impact) should be checked separately. Out of scope
  here per file ownership.
