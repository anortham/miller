## Task 2 Report: Miller Structural Fact Bridge Input

Status: verified; commit pending.

## Summary of Changes

- Added `StructuralFactRecord` as a Core raw bridge contract for selected `structural_facts` rows.
- Added `BridgeData.StructuralFacts` and `BridgeProviderContext.StructuralFacts`.
- Extended `SqliteBridgeReader` to read only:
  - `aspnet.minimal_api.route.v1`
  - `htmx.attribute.v1`
  - `vue.route_reference.v1`
- Preserved raw metadata JSON, line/column fields, byte span, confidence, path, language, and containing symbol id.
- Wired `RepositoryIndexLoader.Load` through `BridgeGraphBuilder.Build`.
- Added tests for SQLite row selection, unrelated pattern exclusion, provider-context pass-through, and loader pass-through without graphing unrelated facts.

## Miller Calls Used

- `workspace status` for `/Users/murphy/source/miller/.worktrees/web-stack-structural-facts-bridge`: confirmed the requested worktree was registered, fresh, and in reader mode; also showed missing search/content sidecars before refresh.
- `workspace refresh`: rebuilt current search/content sidecars before code navigation and refreshed again after edits; final refresh reported revision `4`.
- `search mode=file` for `RAZORBACK.md` and `task-2-brief`: confirmed no indexed repo-root `RAZORBACK.md` and that the hidden task brief was outside indexed content, so it was read directly from the user-specified path.
- `context` for the Task 2 bridge seam: identified `BridgeGraphBuilder`, `BridgeProviderContext`, `SqliteBridgeReader`, `RepositoryIndexLoader`, and the bridge loader tests as the relevant entry points.
- `inspect` on `IBridgeProvider.cs`, `BridgeGraphBuilder.Build`, `SqliteBridgeReader`, `SqliteBridgeReader.Read`, `BridgeData`, `RepositoryIndexLoader.Load`, and the two allowed test files: confirmed current signatures, call flow, and test fixture shape before edits.
- `trace mode=refs` for `BridgeProviderContext` and `BridgeData`: confirmed the context is constructed in `BridgeGraphBuilder` and `BridgeData` is owned by `SqliteBridgeReader`.
- `search mode=source` / `inspect` for `PatternFactsReader`, `PatternMatchRow`, and structural-fact SQL: confirmed the `structural_facts` column set and deterministic ordering used by the pattern reader.
- `impact target=BridgeProviderContext`, `impact target=SqliteBridgeReader.Read`, and `impact git=true`: checked the planned/public seam impact and post-edit changed surface.
- Final `inspect` on `StructuralFactRecord`, `BridgeProviderContext`, and `SqliteBridgeReader.ReadStructuralFacts`: confirmed the indexed final shape includes the new raw contract, context field, and selected-pattern SQL.

## Verification Ledger

| Scope | Invariant | Command | Commit SHA | Result | Timestamp |
| --- | --- | --- | --- | --- | --- |
| TDD red check | New tests fail before implementation because `StructuralFactRecord` / `BridgeData.StructuralFacts` do not exist | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~BridgeGraphBuilderTests|FullyQualifiedName~SqliteBridgeReaderTests|FullyQualifiedName~RepositoryIndexLoaderBridgeTests"` | `5fe6377a397afd6ab879c15dd15fccf9584fc48f` + test-only working tree | Failed as expected with CS0246 / CS1061 missing structural-fact seam errors | 2026-06-30 session |
| Worker scope | Selected structural facts are read from SQLite, carried through `BridgeData` and `BridgeProviderContext`, and passed through `RepositoryIndexLoader` without graphing unrelated pattern ids | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~BridgeGraphBuilderTests|FullyQualifiedName~SqliteBridgeReaderTests|FullyQualifiedName~RepositoryIndexLoaderBridgeTests"` | `5fe6377a397afd6ab879c15dd15fccf9584fc48f` + Task 2 working tree | Pass: 37 passed, 0 failed, 0 skipped | 2026-06-30T14:56:28Z |
| Fast suite | Public bridge context changes do not break the fast test suite | `scripts/test.sh` | `5fe6377a397afd6ab879c15dd15fccf9584fc48f` + Task 2 working tree | Pass: 2502 passed, 0 failed, 0 skipped; wall time 20s under 30s ceiling | 2026-06-30T14:56:28Z |
| Diff hygiene | Task 2 diff has no whitespace errors | `git diff --check` | `5fe6377a397afd6ab879c15dd15fccf9584fc48f` + Task 2 working tree | Pass, exit 0 | 2026-06-30T14:56:28Z |

## Acceptance Criteria Checklist

- [x] `SqliteBridgeReader.Read` returns selected structural facts with metadata JSON and span intact.
- [x] Missing selected pattern ids and unrelated pattern ids are ignored by the bridge reader.
- [x] `RepositoryIndexLoader.Load` passes structural facts into `BridgeGraphBuilder.Build`.
- [x] `BridgeGraphBuilder.Build` passes structural facts into `BridgeProviderContext`.
- [x] Existing bridge tests compile without requiring callers to pass structural facts manually.
- [x] Worker-scope verification passes.

## Concerns or Plan Mismatches

- No Task 2 architecture mismatch found.
- `BridgeGraphBuilder` records a neutral `bridge.structuralFacts` evidence count so the loader pass-through can be proven without adding htmx/Vue reduction or graph nodes.
- `.razorback/sdd/task-1-report.md` is modified by unrelated/concurrent work in this worktree. It was not touched for Task 2 and must remain unstaged for the Task 2 commit.

## Commit

- Commit SHA: reported in the final response after commit creation.
