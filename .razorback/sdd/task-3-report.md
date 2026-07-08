# Task 3 Report: Registry test isolation + convention guard

**Status:** DONE
**implementation commit SHA:** 152d155

## Summary

Stopped bootstrap failure paths from writing registry error rows to the real `~/.miller/workspaces.db` by routing all `IndexBootstrapService` workspace-context creation through a single test-overridable helper, wiring every direct test construction (and DI-resolved failure-capable paths) to set `TestHomeDirectoryOverride`, cleaning up leaked temp dirs, and adding a source-scanning convention guard.

## Miller orientation (required)

| Call | Result |
|------|--------|
| `context(query='IndexBootstrapService MarkBootstrapFailed WorkspaceContext Create TestRunBootstrapOverride')` | Confirmed root cause: `MarkBootstrapFailed` and `RunBootstrap` both used 2-arg `WorkspaceContext.Create` → real `~`; existing `TestRunBootstrapOverride` hooks in `WorkspaceBindingServiceTests` |
| `inspect IndexBootstrapService depth=full` | Located `TestRunBootstrapOverride`, `RunBootstrap`, `MarkBootstrapFailed`, `WorkspaceContext.Create` 3-arg overload contract |
| `search query='new IndexBootstrapService(' file_pattern=tests/**` | 11 test files with direct construction |
| `inspect ScaleTraitConventionTests depth=full` | Model for comment-stripped source scan + exempt-file list + vacuous-pass sanity checks |

## API shape (production)

**File:** `src/Miller.Server/Hosting/IndexBootstrapService.cs`

```csharp
internal string? TestHomeDirectoryOverride { get; set; }

private WorkspaceContext CreateWorkspaceContext(string canonicalRoot) =>
    WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory, TestHomeDirectoryOverride);
```

- `RunBootstrap` uses `CreateWorkspaceContext(canonicalRoot)` (was 2-arg `Create`)
- `MarkBootstrapFailed` uses `CreateWorkspaceContext(canonicalRoot) with { ... }` (was 2-arg `Create`)
- No env var, no production behavior change when override is null

## Deliverables

### Created
- `tests/Miller.Tests/Conventions/RegistryIsolationConventionTests.cs` — fails if any `*.cs` under `Miller.Tests` contains `new IndexBootstrapService(` without also referencing `TestHomeDirectoryOverride` (comment-stripped scan, modeled on `ScaleTraitConventionTests`)

### Modified (production)
- `src/Miller.Server/Hosting/IndexBootstrapService.cs`

### Modified (tests — all set `TestHomeDirectoryOverride`)
- `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs` — `NewBootstrap(tempHome)` helper, `DeleteTempDir` cleanup, new direct regression test, updated failure-path registry assertions to temp home
- `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs` — override on DI-resolved bootstrap (both rebind tests)
- `tests/Miller.Tests/Server/IndexerServiceLeadershipTests.cs`
- `tests/Miller.Tests/Server/IndexerServiceScanTests.cs`
- `tests/Miller.Tests/Server/IndexerWatcherExtensionGateTests.cs`
- `tests/Miller.Tests/Server/LeaderWriteThroughTests.cs`
- `tests/Miller.Tests/Server/LiveWorkspaceTests.cs`
- `tests/Miller.Tests/Server/WorkspaceToolTests.cs`
- `tests/Miller.Tests/Server/FreshnessServicePollNowTests.cs`
- `tests/Miller.Tests/Server/VersionAwareLeadershipScaleTests.cs`

## New direct regression test

`MarkBootstrapFailed_WritesRegistryErrorUnderTestHomeOverride_NotRealHome` in `WorkspaceBindingServiceTests`:

1. Constructs bootstrap with `TestHomeDirectoryOverride = tempHome`
2. Drives `TestRunBootstrapOverride` throw path
3. Opens `Path.Combine(tempHome, ".miller", "workspaces.db")` and asserts `WorkspaceRegistryState.Error` row
4. Asserts registry path is under temp home, not user profile

## Temp dir cleanup

`WorkspaceBindingServiceTests` now uses labeled `CreateTempDir(label)` + `DeleteTempDir` in `try/finally` on every test that allocates temp roots/homes (fixes prior `miller-bindsvc-*` leakage).

## Judgment calls

- **Per-file helpers** (`NewBootstrap` in binding tests; inline override elsewhere) rather than a shared test fixture class — matches existing file-local patterns.
- **DI path** (`HostStartupRegistrationTests`): sets override on `GetRequiredService<IndexBootstrapService>()` even though convention guard only scans `new IndexBootstrapService(` — task brief explicitly required it for failure-capable paths.
- **Seeded workspace home alignment**: tests that already create `WorkspaceContext` with explicit `home` derive override from that home so bootstrap and seeded workspace stay consistent.

## Verification (final re-run)

```
BEFORE ~/.miller/workspaces.db rows: 294

dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter \
  "FullyQualifiedName~RegistryIsolationConventionTests|FullyQualifiedName~WorkspaceBindingServiceTests" -c Release
→ Passed: 14, Failed: 0, Skipped: 0

AFTER ~/.miller/workspaces.db rows: 294
DELTA: 0
```

## Acceptance criteria

| Criterion | Status |
|-----------|--------|
| New direct test proves failed bootstrap writes under override home | ✅ |
| Convention guard fails on un-isolated construction (passes current tree) | ✅ |
| All modified bootstrap/binding/leadership/tool tests pass with overrides | ✅ |
| Temp dirs cleaned up (`DeleteTempDir` / existing `IDisposable` fixtures) | ✅ |
| Zero rows added to real `~/.miller/workspaces.db` | ✅ (294→294) |

## Notes

- Convention guard covers direct `new IndexBootstrapService(` construction. DI-resolved bootstraps are covered by explicit override setup in `HostStartupRegistrationTests`.
- Full branch gates are recorded in the Task 5/Task 6 closeout evidence and final lead verification.
