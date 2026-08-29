# Task 1 report: direction-aware graph scratch

## Status

Implemented and verified. This packet changes only graph scratch construction and its tests.
The serial worker commit is reported in the handoff after commit; no Task 2 measurement was run.

## Change

- `ResolveGraphQuery(SqliteConnection, IReadOnlyList<string>, Direction, GraphReadKind)` now passes
  `Direction` into a private `ResolveQuery` overload.
- The existing two-argument `ResolveQuery` remains the full-read path and delegates with
  `Direction.Both`; the four non-graph exact/fallback readers therefore retain bidirectional reads.
- Forward graph reads run identifier-within and pending-by-source arms only.
- Reverse graph reads run identifier-named and pending-by-name arms only.
- `Direction.Both` runs all four arms as before.
- A skipped arm records `new GraphResolutionMeasurement(TimeSpan.Zero, 0, 0)`.
- Candidate lookup, identifier details, identifier/pending resolution, relationships, scratch reuse,
  query-time resolver policy, ordering, and public graph records are unchanged.

## Miller evidence

Miller was used for orientation, symbol inspection, reference tracing, and impact analysis before and
after the edit.

- `context` identified `QueryTimeResolutionReader` as the implementation pivot and `Direction` as
  the existing graph direction enum.
- `inspect` proved `ResolveGraphQuery` is private with signature
  `ResolveGraphQuery(SqliteConnection connection, IReadOnlyList<string> candidateIds,
  Direction direction, GraphReadKind kind)` and that its callers are exactly
  `ReadResolutionEdges` and `ReadUnresolvedNameEdges`.
- `trace` proved `ResolveQuery` has four non-graph callers (`ReadInboundExact`,
  `ReadInboundFallback`, `ReadOutgoingExact`, `ReadOutgoingFallback`) plus the graph caller.
- `inspect` proved `PendingScratch` already carries `Direction` and `GraphReadKind`, so no cache key
  or interface change was needed.
- `inspect` proved `GraphResolutionMeasurement` is the existing internal record
  `(TimeSpan Elapsed, int Rows, int Operations)`.
- Pre-edit `impact target=ResolveQuery` reported the graph and four exact/fallback consumers plus
  their likely tests. Post-edit `impact changed_paths` showed the expected graph/read-session,
  evidence, and test dependents; no public interface was added.

## TDD evidence

Tests were added before production changes.

- RED command:

  `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~QueryTimeResolutionReaderTests" --no-restore`

  Result: 2 failed, 22 passed, 0 skipped. The forward test observed named rows `4` instead of `0`;
  the reverse test observed within rows `5` instead of `0`.

- GREEN command (after implementation and after restoring each mutation): same command.

  Result: 24 passed, 0 failed, 0 skipped.

- Controlled mutation 1 changed the identifier-within branch to run for reverse. The focused class
  failed 4 tests, including forward within expected `5`/actual `0` and reverse within expected
  `0`/actual `5`.

- Controlled mutation 2 omitted the identifier-named branch. The focused class failed 2 tests,
  including reverse named expected `4`/actual `0`.

- Direction-aware scratch reuse test exercises unresolved-first and resolution-first call order,
  asserts one resolve pass, and compares literal serialized forward edges.

## Assigned worker gates

- Focused union:

  `dotnet test --filter "FullyQualifiedName~QueryTimeResolutionReaderTests|FullyQualifiedName~SqliteSymbolGraphIndexTests|FullyQualifiedName~FamilyStoreReadSessionTests|FullyQualifiedName~BoundedRevisionFactCacheTests" --no-restore`

  Result: 137 passed, 0 failed, 0 skipped. This covers graph parity, homonym, pending override,
  QML, ordering, bounded fact-cache, and non-graph family-session paths.

- `dotnet build src/Miller.Indexing/Miller.Indexing.csproj -c Release --no-restore`

  Result: 0 warnings, 0 errors.

- `dotnet build tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore`

  Result: 0 warnings, 0 errors.

- `git diff --check`: passed.

## Concerns and handoff

- The direction branch is intentionally inside `QueryTimeResolutionReader`; no storage, cache,
  schema, `GraphReadKind`, public signature, or resolver-policy change was introduced.
- `Both` behavior is covered by the existing complete-subphase observation test and existing graph
  parity tests.
- Performance and byte-level CLI parity are Task 2 responsibilities and remain unmeasured here.

## Worktree

- Path: `/home/murphy/source/miller/.worktrees/tool-latency-health`
- Branch: `fix/tool-latency-health`
- Starting HEAD: `9b971aa4`
- Current state before commit: owned source/test/report changes plus the pending Goldfish checkpoint;
  no unrelated edits were observed.

## Round 1 coverage correction

The lead review identified two gaps in the first packet. Both are closed without changing
production behavior:

- `DirectionAwareGraphFrontierReusesScratchInEitherConsumerOrder` now runs both `Direction.Forward`
  and `Direction.Reverse`, with unresolved-name-first and resolution-first order for each direction.
  Each case asserts one resolve pass and compares hand-derived literal serialized edges. The reverse
  literals cover the named identifier, named pending, unresolved member-access, and variable-reference
  edges, so loss of a reverse arm or call-order dependence fails the test.
- Forward and reverse observation tests now assert exact `TimeSpan.Zero` in addition to zero rows and
  operations for every skipped identifier/pending arm.

This was a coverage expansion, not a defect repair: the new reverse parity cases passed against the
existing implementation, so no RED run was invented. The narrow reader class remained 24/24 and the
focused four-class union remained 137/137. `git diff --check` passed.
