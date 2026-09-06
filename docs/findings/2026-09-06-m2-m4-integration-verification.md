# M2 sidecar convergence and M4 Context-boundary integration verification

## Status

M2 and M4 are implemented and locally qualified on `feature/remaining-architecture-plans`. The accepted verification packet is commit `7e746f28`. It includes the published-producer and all-language tests; the M2 implementation commits precede it, and the M4 refactor is integrated on the same branch.

This is local integration evidence. Nothing in this finding records a push, Miller release, deployment, semantic-model efficacy result, or new S1 runtime qualification.

## Runtime and source identity

- Verification base before the accepted test packet: `f72570eca20503de7886d91dae342baecf239aed`.
- Accepted verification packet: `7e746f28` (`test: verify published cursor and all-language context contracts`).
- .NET SDK: `10.0.111`.
- Published `.tools/julie-extract`: `2.40.4`, SHA-256 `acfb332fe2795b4b60283c178fab9835ceaa5a650317bc387b623cf6818a5bc3`.
- Raw local command records: `/home/murphy/.codex/sessions/2026/09/06/rollout-2026-09-06T00-16-33-01a07525-9800-7642-a723-6114e18fc54c.jsonl`.

## M2 ordering, recovery, and cleanup

The real-producer Scale fixture uses disposable source and family-store directories. It verifies both Content and Search through the actual `StoreConsumerCursorRunner` and cursor-aware convergence path.

- A baseline cursor advance completes before the delta read. Sidecar rows and the matching stamp commit before the final advance.
- A real `gen-001` to `gen-002` promotion publishes the new same-kind sidecar before releasing that kind's obsolete cursor. Content completion does not release Search history early.
- A producer-accepted final advance whose reply is treated as lost leaves durable owed work. A fresh session retries the same identity and sequence while the sidecar is current and performs no rebuild.
- Exact Content release preserves the Search cursor. Exact Search release and captured-view retirement complete without discovering targets by enumeration.
- Producer maintenance ran with `reader_registrations = 0`, and the delta from the protected cursor baseline remained complete afterward. This observation demonstrates cursor interoperability without an M1 reader registration. It does not prove the cursor was the producer's exclusive reason for retaining those rows, because default producer retention may also preserve recent history.

The producer transcript projection is in [the published cursor evidence](sidecar-convergence/2026-09-06-published-cursor.json). Its consumer IDs, generation names, and sequences come from the real 2.40.4 report and pass Miller's strict report matching. Fast injected-boundary tests separately cover corrupt journals, incomplete history, mismatched generations, advance/release failures, and fresh-process recovery.

## M2 cost and parity evidence

The [five-run SQLite synthetic record](sidecar-convergence/2026-09-06-sqlite-synthetic.json) uses a fresh process and fresh fixture per run with uncontrolled OS cache. Build time is outside the measurement. Process wall time and process peak RSS include VSTest, fixture construction, operations, and validation, so they are report-only and are not sidecar-only latency or memory promises.

Hard gates compare complete stamps and canonical logical rows between incremental and independent full builds for Content and Search. The fixture includes additions, modifications, deletions, aliases, empty deltas, incomplete-history fallback, and preserved explicit Content imports. Search parity follows the actual `symbol_id` joins and validates metadata `doc_id` uniqueness; SQLite row order and FTS rowid are not artifact contracts.

Across all five runs, each incremental path inspected four delta rows, changed two paths, and deleted two paths. Full paths rebuilt three files and three logical documents. Content incremental operations reported one inserted, two updated, and two deleted logical rows. Search reported one inserted, two updated, and one deleted logical row. Elapsed time varies by run and is excluded from deterministic counter equality.

## All-language read and projection gate

The [all-language evidence](sidecar-convergence/2026-09-06-all-languages.json) records SHA-256 provenance for 40 parser-backed source fixtures copied from julie-extractors commit `3b3e5b6f03b724448df9012bb75224e99ca68f5d`. The fixture set exactly equals the real pinned producer's `languages --json` inventory.

The exact `SELECT language,kind,COUNT(*) FROM symbols GROUP BY language,kind ORDER BY language,kind` result contains 237 non-empty language/kind groups. After syntax-safe trailing-whitespace updates to every source, both cursor-aware incremental sidecars equal independent full builds at the logical-row boundary and carry complete stamps. For every language, the public Context JSON response has no error or diagnostic and selects the representative exact symbol ID in a `bundle` record with `item_type=symbol` and `role=pivot`.

This is a read/projection and convergence gate for the supported-language inventory. It is not exhaustive proof of every parser construct or fact kind in every language.

## M4 public behavior

M4 splits Context into the public adapter, shared query service, bundle builder, renderer, and internal model without adding an MCP field or alternate execution path. MCP, CLI, and the semantic evaluator use the shared route.

The immutable public fixture `tests/Miller.Tests/Fixtures/ContextCharacterization/public-boundary-v1.tsv` contains 16 compact/JSON cases and remains SHA-256 `308c7a27acf32fc7c2be2d94ad2ec98d015c6372f1de4a8fb2f7f208eb3b3f2c`; it proves exact output-byte parity. Separate public characterization tests preserve cancellation phases, source reads, semantic-off zero work, and retrieval counts. The accepted focused M4 gate passed 468 tests with one platform skip. The joint fast gate reran the public characterization on the combined branch.

## Joint verification ledger

Every .NET command used `flock --close /tmp/miller-remaining-plans-dotnet.lock`.

| Scope | Result | Wall time |
| --- | --- | ---: |
| `dotnet build Miller.slnx -c Release` | 0 warnings, 0 errors | 13.35 s |
| `dotnet test` | 10,002 passed, 9 skipped, 0 failed | 55.25 s |
| `scripts/test.sh scale` | 220 passed, 24 skipped, 0 failed | 98.90 s |
| Final `StoreSidecarCursorScaleTests` after Context assertion review | 2 passed, 0 skipped, 0 failed | 17.90 s |

The first fast run exposed a scheduling-only assertion in `BackgroundWarmKeepsTheSamePinUntilItsConnectionCloses`: it required a background task to start within three seconds even though pin lifetime has no three-second startup contract. The accepted correction uses one 30-second xUnit test timeout, propagates early warm failure, rejects impossible successful completion before entry, and retains the original coalescing, close, release, and event-order assertions. The final parallel fast run passed.

Scale skips were 5 platform-only gates, 8 optional CT-provider toolchain gates, 7 recorded-input gates, 3 evaluator/runtime prerequisite gates, and the retired producer `store resolve` capability gate. No skipped test is counted as a pass.

## Remaining limits

- M2's wall-time and RSS records are baselines for one synthetic fixture, not performance budgets or production-volume forecasts.
- M1 reader registrations and M2 consumer cursors remain separate. The cursor qualification does not add native forced-death, PID-reuse, or unknown-kernel-identity evidence to M1.
- M4 is a behavior-preserving responsibility split. It makes no claim that Context retrieval quality or task success improved.
- Semantic runtime qualification remains governed by S1, and agent outcome efficacy remains governed by M5. Neither follows from these gates.
