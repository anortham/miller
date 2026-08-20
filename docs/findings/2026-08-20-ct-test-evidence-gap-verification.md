# CT test-role evidence gap — verified present (2026-08-20)

A `julie-extractors` session asked whether the current Windows CT branch already closes the
continuous-testing evidence gap. It does not. Every claim in that request is accurate against
`fix/ct-windows-hardening` (parent `9322cfee`). This note records the verification so the fix can be
scoped without re-checking.

## What the index carries

`IndexedSymbol` exposes the full evidence:

```csharp
// src/Miller.Indexing/IndexedSymbol.cs:30
public TestRoleEvidence TestEvidence =>
    new(IsTest, TestContainer, TestLifecycle, TestEvidenceStatus, TestEvidenceReason);
```

`TestEvidenceStatus` defaults to `TestRoleEvidence.UnknownStatus` and `TestEvidenceReason` to
`FileEvidenceUnavailableReason` (`IndexedSymbol.cs:26-27`), so "unknown" is a real, distinguishable
state and not merely an absent value. `ImpactTool` and `InspectTool` already render it.

## What CT receives

The CT fact boundary drops all of it except one boolean.

- `CtSymbolFact` (`src/Miller.Indexing/Testing/ICtFactSource.cs:23-34`) declares `bool IsTest` and no
  container, lifecycle, status, or reason field.
- `CtFactAdapter.ToSymbolFact` (`src/Miller.Indexing/Testing/CtFactAdapter.cs:249-260`) passes
  `symbol.IsTest` and never reads `symbol.TestEvidence`.
- `ContinuousTestImpactSelector.SymbolFact.FromMiller`
  (`src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs:932-944`) then collapses every
  positive into one shape:

```csharp
IsTest: symbol.IsTest,
TestRole: symbol.IsTest ? "testcase" : null,
FileRole: symbol.IsTest ? "test" : null);
```

So a test CONTAINER and a test LIFECYCLE hook both arrive at the selector as a directly runnable test
case, and a symbol whose evidence is unknown or stale is indistinguishable from one confirmed current.

## `ContinuousTestClassifier` is not wired in

`src/Miller.Testing/Parsing/ContinuousTestClassifier.cs:44` is a public static class whose only callers
in the repository are its own test file, `tests/Miller.Tests/Testing/Parsing/ContinuousTestClassifierTests.cs`.
No production path calls `ClassifyFileRole`, `ClassifySymbol`, or `TestCaseFromSymbol`.

It is a path-and-name classifier. The selector's live inventory comes from provider discovery instead.
Wiring the classifier in would put a second, weaker authority beside the provider inventory, so it
should stay out until the two are reconciled deliberately — which agrees with the request's own caution.

## Scope this fix separately

The gap is real and worth closing, but it is a different workstream from the Windows CT hardening on
this branch: it changes the fact contract (`CtSymbolFact`), the adapter, and the selector's tiering,
and it needs adapter-to-selector tests for case, container, lifecycle, and unknown evidence. None of
the Windows fixes touch those types.
