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

## Resolution (2026-08-20)

Implemented, with one change to the recommended rule.

The inversion is keyed on **coverage, not scope**. `ContinuousTestDaemonQueue.CoversEveryKnownCase`
compares the run's selection against the project's full known inventory as a SET, and only a run that
covers all of it is marked `WholeSuite`. A workspace-scope run whose already-fresh cases were dropped
by `DropCommittedFreshAt` can be down to a handful of ids, and running a whole assembly for three tests
is the same mistake in the other direction. The backfill lane is excluded by construction: it takes a
bounded batch on purpose.

`ContinuousTestCoordinatorRunRequest.WholeSuite` then makes the coordinator hand the provider an EMPTY
`TestCaseIds`, which is how every provider already says "run the whole assembly under the seeded trait
exclusions". Nothing in any provider changed, so constraint 2 is satisfied for cargo and pytest as well
— the inversion happens above the provider layer.

Constraint 1 holds: `TestCaseIds` still carries the full list into
`ContinuousTestStoreApplier.StartRun`, so the verdict rows still record every case the run covered and
freshness at the composite key stays honest. Tests assert both halves, plus that the queue actually sets
the flag — a flag nothing sets would compile and pass every test written about it.

### What an empty selection does to the failure paths

Checked, because an empty selection reaches code that used to be reached only with a case list:

- A chunk that produces NO TRX contributes nothing to `parsed`, and `parsed.Count == 0` then throws
  `ContinuousTestProviderException`. A whole-suite run that dies without an artifact therefore FAILS
  loudly. It cannot read as a pass.
- A TRX that parses to ZERO case results is the one behaviour that differs. With a case list,
  `runResult.CaseResults.Count == 0 && request.TestCaseIds.Count > 0` throws; with an empty selection
  that guard does not fire, and the provider returns an empty result set. The run is still not green —
  the applier recorded the full selected list, those cases stay unreported, and the verdict is partial
  rather than green — but nothing says WHY. Distinguishing "this assembly legitimately has no tests"
  from "something went wrong" needs the whole-suite intent to reach the provider, which this change
  deliberately avoids. Left as a follow-up; the safe direction already holds.

A test double that derives its results FROM the selection reports nothing for a whole-suite run, which
reads as a partial verdict for a run that passed everything. `DrainObservingProvider` was updated to
report what the assembly ran, the way a real provider parses its TRX.
