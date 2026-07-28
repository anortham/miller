# Task 3 report — connection-factory semantic sessions

**Status:** implementation and worker verification complete; left unstaged and uncommitted for the lead.

## Result

- Added async, transport-neutral `ISemanticSidecarConnectionFactory` and `ISemanticSidecarConnection`.
- `SemanticEmbeddingSession` now connects asynchronously, aborts only a failed connection, reconnects through
  the factory, and closes every session connection.
- Session disposal leaves borrowed factories alive, disposes explicitly owned factories, and never sends an
  implicit protocol `shutdown`.
- `StdioSemanticSidecarConnectionFactory` preserves child-process stdio behavior. The old
  `ProcessSemanticSidecarLauncher` name is a narrow compatibility adapter for existing callers outside Task 3.
- Evaluation sessions own injected factories by default but can explicitly borrow them; package-smoke sessions
  own their stdio factories. Shared-broker production wiring remains deferred to Task 7.

## TDD evidence

- RED:
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~SemanticEmbeddingSessionTests`
  failed with six `CS0246` errors because `ISemanticSidecarConnectionFactory` and
  `ISemanticSidecarConnection` did not exist.
- GREEN, focused session class: 26 passed, 1 intentional skip, 0 failed.
- Grok fix-round RED:
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~SemanticEvaluationAdapterTests`
  failed with `CS1739` because the injected-factory overload could not express borrowed ownership.
- Grok fix-round GREEN: the evaluation adapter class passed 25 tests with 1 intentional skip. The default-owned
  test proves existing behavior; the borrowed test proves `ownsConnectionFactory: false` survives session
  disposal.
- Final required three-class gate:
  `FullyQualifiedName~SemanticEmbeddingSessionTests|FullyQualifiedName~SemanticEmbeddingSessionBrokerTests|FullyQualifiedName~SemanticEvaluationAdapterTests`
  — 55 passed, 2 intentional skips, 0 failed.
- Final fast gate: `scripts/test.sh` — 5,223 passed, 2 intentional skips, 0 failed; 23s wall time under the
  30s ceiling.
- `git diff --check` passed.

## Changed files

- `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs`
- `src/Miller.Indexing/Semantic/SemanticEvaluationAdapter.cs`
- `scripts/Miller.PackageSemanticSmoke/PackageSemanticSmoke.cs`
- `tests/Miller.Tests/Support/FakeSemanticSidecar.cs`
- `tests/Miller.Tests/Indexing/SemanticEmbeddingSessionTests.cs`
- `tests/Miller.Tests/Indexing/SemanticEmbeddingSessionBrokerTests.cs`
- `tests/Miller.Tests/Indexing/SemanticEvaluationAdapterTests.cs`
- `.razorback/sdd/task-3-report.md`

## Miller evidence

- `workspace onboarding` and `workspace health` established a fresh index; semantic vectors were unavailable
  but irrelevant to this source refactor.
- `context` on the session/launcher/channel/evaluation seam identified the process-shaped boundary and its
  existing retry, handshake, and adapter callers.
- `impact target=SemanticEmbeddingSession` found 98 dependents and selected the session, broker, evaluation,
  search-arm, converge, CLI, and package-smoke test surfaces.
- Full inspection covered `SemanticEmbeddingSession`, `StartIfNeededAsync`, `ResetChild`,
  `ExchangeAsync`, both old interfaces, `ProcessSemanticSidecarLauncher`, and `SemanticEvaluationAdapter`.
- After the Grok fix round, `workspace refresh` reached revision 12. Full inspection proved the exact
  factory/connection
  signatures plus `CloseConnectionAsync` and ownership-aware `SemanticEmbeddingSession.DisposeAsync`.
- Post-edit impact over the seven product/test paths found 99 impacted symbols; the full fast suite covered the
  broader callers beyond the three focused classes.
- Focused impact over the evaluation adapter fix exhausted at 10 symbols and identified the evaluation graph and
  convergence tests; the full fast suite covered them.

## Architecture Quality

**Affected modules:** semantic session transport seam, stdio adapter, evaluation adapter, package smoke, fakes.

**Caller-facing interface:** one async factory method and one connection with input/output, closed state,
abort, and async close. Callers do not know whether the transport is a child process or shared IPC.

**Depth/locality check:** retry, handshake, circuit, timeout, and protocol policy remain inside
`SemanticEmbeddingSession`; transport lifecycle stays inside connection implementations.

**Test surface:** tests exercise the same factory/connection interface callers use, including abort/reconnect,
borrowed disposal, owned disposal, and absence of implicit shutdown.

**Seams/adapters:** the second planned IPC adapter earns the factory seam. The compatibility adapter is
deliberately shallow because Task 3 cannot edit current `SemanticSearchArm` and scale-test callers.

**Rejected shortcuts:** no transport branching in the session, no static/global factory, no factory disposal by
borrowed sessions, no process-wide shutdown on session disposal, and no synchronous launch-only interface.

**Architecture risk:** medium because the session is widely referenced; focused and full fast gates are green.

## Judgment calls

- Session factory ownership defaults to borrowed. Evaluation adapter injection defaults to owned for compatibility
  but exposes `ownsConnectionFactory: false`; Task 7 will wire server/CLI lifetime ownership.
- Normal close uses `DisposeAsync`; transport faults call `Abort` first, then `DisposeAsync`.
- The stdio connection may kill only its own child while closing. A future shared-IPC connection will close only
  its client stream, so neither path implies broker-wide shutdown.

## Final state

- Worktree: `/Users/murphy/source/miller/.worktrees/shared-semantic-broker-plan`
- Branch: `codex/shared-semantic-broker-plan`
- HEAD: `c163e129c7cdc972e67337d55b3bd4a31d6bdd64`
- Task 3 fix files are unstaged. `.razorback/sdd/task-2-report.md` and the implementation plan are concurrent
  lead/sibling changes and were not touched.
