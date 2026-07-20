# P3 verification ledger — query-time hybrid retrieval

Scopes per `docs/plans/2026-07-20-p3-integration-plan.md` Verification Strategy. Fast suite =
`scripts/test.sh` (Category!=Scale, 30s wall ceiling); branch gate = `scripts/test.sh all` +
`dotnet build Miller.slnx -c Release`.

| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| affected-change (Batch A: F1+F2) | Combined F1 policy + F2 arm tree keeps the full fast suite green; off/shadow guarantees and converge guards undisturbed | `scripts/test.sh` | 682190c | PASS — 4070 passed, 0 failed, 2 skipped; 27s wall (ceiling 30s) | 2026-07-20 |
