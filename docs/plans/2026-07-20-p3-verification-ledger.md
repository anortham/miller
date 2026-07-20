# P3 verification ledger — query-time hybrid retrieval

Scopes per `docs/plans/2026-07-20-p3-integration-plan.md` Verification Strategy. Fast suite =
`scripts/test.sh` (Category!=Scale, 30s wall ceiling); branch gate = `scripts/test.sh all` +
`dotnet build Miller.slnx -c Release`.

| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| affected-change (Batch A: F1+F2) | Combined F1 policy + F2 arm tree keeps the full fast suite green; off/shadow guarantees and converge guards undisturbed | `scripts/test.sh` | 682190c | PASS — 4070 passed, 0 failed, 2 skipped; 27s wall (ceiling 30s) | 2026-07-20 |
| branch-gate (fast) | Full P3 branch (F1–F5 + G1–G4 + lead fixes) keeps every fast invariant: golden parity byte-identical, off/shadow zero-work, determinism per arm, guard conventions | `scripts/test.sh` | 06067a3 | PASS — 4153 passed, 0 failed, 2 skipped; 26s wall. First `all` attempt flaked the pre-characterized `IndexerServiceScanTests.StartAsync_AsLeader_RecordsLeaderIdentity_AndRemovesItOnStop` (isolated rerun 29/29 green) and tripped the 30s wall under `all`-mode load (38s); clean rerun under ceiling | 2026-07-20 |
| branch-gate (scale) | Real julie-extract (2.16.0) and real julie-semantic-sidecar (0.1.0-rc.1) paths green, incl. the RC promotion gate (handshake fingerprint) and real-vec0 round trip | `scripts/test.sh scale` | 06067a3 | PASS — 83 passed, 0 failed ×3 consecutive runs. One unidentified transient failure on the first scale invocation (no test name captured, xUnit reflection frame only; 0 recurrences in 3 reruns) — recorded, not suppressed | 2026-07-20 |
| branch-gate (build) | 0 warnings / 0 errors Release build (TreatWarningsAsErrors) | `dotnet build Miller.slnx -c Release` | 06067a3 | PASS — 0 Warning(s), 0 Error(s) | 2026-07-20 |
