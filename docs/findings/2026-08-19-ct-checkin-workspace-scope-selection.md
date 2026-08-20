# CT check-in — workspace-scope runs should not enumerate methods (2026-08-19)

An independent check-in session reviewed the CT hardening work on 2026-08-19. Verdict: the
approach is sound, the fixes are measured and test-first, and the defect classes match the
expected Windows-port list (file locks, argv cap, `.cmd` shims, process trees). One design
finding needs a decision; it extends open finding 8 in
[`2026-08-19-ct-dogfood-findings.md`](2026-08-19-ct-dogfood-findings.md).

## Finding: a full-suite run pays per-method selection it does not need

- `ContinuousTestImpactSelector.Select`
  (`src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs:97-107`) returns EVERY known
  test-case id when `request.WorkspaceScope` is true.
- The .NET provider then emits one `-method <FQN>` pair per id. `CtArgvChunking` splits Miller's
  ~6,000 methods into ~50 processes. Each process pays startup plus discovery.
- Finding 8 measured the cost of per-method selection: 25 seconds under `dotnet test` versus
  6+ minutes under CT for the same fast subset.
- The fast path already exists: `DotnetTestProvider.BuildRunCommands`
  (`src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs:342-343`) runs the whole assembly
  with only the seeded trait exclusions when the selection is empty.

## Recommendation

A workspace-scope run should send an EMPTY selection to the provider and rely on the seeded
trait exclusions. Method enumeration should serve only small deltas — the normal CT case. This
removes the chunked slow path from every full run. Two constraints:

1. Verdict rows must still record the full-assembly run as covering all known cases, so
   freshness at the composite `(index_identity, revision)` key stays honest.
2. The same inversion applies to the other providers when their full-suite selection would
   otherwise enumerate cases (cargo filter chunks, pytest node ids).
