Task 1: complete (parallel-lead-commit, Lead inline review clean, lead commit 700cc50)
Task 3: complete (parallel-lead-commit, Lead inline review clean, lead commit 747d887)
Task 5: complete (parallel-lead-commit, Lead inline review clean, lead commit 557bdd6)
Task 2: complete (parallel-lead-commit, Lead inline review clean, lead commit 9ec1ce3)
Task 4: complete (parallel-lead-commit, Lead inline review clean, lead commit 6a2bffc)
Task 6: complete (parallel-lead-commit, Lead inline review clean, lead commit 132c911)
Task 7: complete (serial-worker-commit, worker commit 367a7f2, Lead inline review clean — pin: Qwen3-Embedding-0.6B f16 1024d int8; fallback bge-small-en-v1.5 f32 384d int8)

## Verification ledger — branch gate @ ffcf896
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| branch-gate (build) | Release build clean, warnings-as-errors | dotnet build Miller.slnx -c Release | ffcf896 | PASS 0W/0E | 2026-07-19 17:39 |
| branch-gate (fast) | Full fast suite green | scripts/test.sh | ffcf896 | PASS 3617/0 (x2); wall tripwire 31-32s>30s under external load (rustc, loadavg ~25); 27s under ceiling on quiet machine same day | 2026-07-19 17:45 |
| branch-gate (scale) | Scale suite green incl. julie-extract spawn paths | scripts/test.sh scale | ffcf896 | PASS 54/54 | 2026-07-19 17:42 |
| branch-gate (carry) | 338e665 differs from ffcf896 only by .memories/ checkpoint markdown (no code/test delta) — ffcf896 evidence reused | git diff --stat ffcf896..338e665 | 338e665 | CARRIED | 2026-07-19 17:45 |

## Verification ledger — post-fix branch gate @ 2e26dba
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| branch-gate (build) | Release build clean, warnings-as-errors | dotnet build Miller.slnx -c Release | 2e26dba | PASS 0W/0E | 2026-07-19 18:11 |
| branch-gate (fast) | Full fast suite green incl. new exception-path edit test | scripts/test.sh | 2e26dba | PASS 3618/0; wall tripwire 59s>30s under sustained external load (loadavg ~23); suite duration inflation is environmental — same suite 27s wall on quiet machine earlier today | 2026-07-19 18:12 |
| branch-gate (scale) | Scale suite green | scripts/test.sh scale | 2e26dba | PASS 54/54 | 2026-07-19 18:13 |
| worker (eval harness) | Cluster-unit weight invariance + report shape | dotnet test eval/retrieval-eval/tests -c Release | 7703e5f | PASS 31/31 (lead re-ran) | 2026-07-19 18:05 |

## P1 execution (plan docs/plans/2026-07-19-p1-freeze-and-conformance-plan.md)
Task 1: complete (parallel-lead-commit, Lead inline review clean, lead commit 9c4bbfe — incl. D1 error-vocabulary correction to plan+design)
Task 2: complete (parallel-lead-commit, Lead inline review clean + lead division-of-labor addition, lead commit 15dd864)
Task 3: complete (parallel-lead-commit, Lead inline review clean + lead --verify re-run 78/78, lead commit 2c81b71)
Task 4: complete (serial-worker-commit, worker commit b464721, Lead inline review clean — F5 encoder_fingerprint derivation fixed by lead in canary contract)

## Verification ledger — P1 branch gate @ cd26381
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| branch-gate (build) | Release build clean | dotnet build Miller.slnx -c Release | cd26381 | PASS 0W/0E | 2026-07-19 19:20 |
| branch-gate (fast) | Fast suite green | scripts/test.sh | cd26381 | PASS 3618/0; wall tripwire 54s>30s environmental (loadavg ~32) | 2026-07-19 19:21 |
| branch-gate (scale) | Scale suite green | scripts/test.sh scale | cd26381 | PASS 54/54 | 2026-07-19 19:23 |
| branch-gate (conformance) | Golden fixtures reproducible within frozen tolerance | python3 eval/sidecar-conformance/generate.py --verify | cd26381 | PASS 78/78 (lead re-run) | 2026-07-19 19:23 |

## Verification ledger — P1 post-fix branch gate @ a739027
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| branch-gate (build) | Release build clean | dotnet build Miller.slnx -c Release | a739027 | PASS 0W/0E | 2026-07-19 19:47 |
| branch-gate (fast) | Fast suite green | scripts/test.sh | a739027 | PASS 3618/0, 25s wall (tripwire clean) | 2026-07-19 19:49 |
| branch-gate (scale) | Scale suite green | scripts/test.sh scale | a739027 | PASS 54/54 | 2026-07-19 19:47 |
| branch-gate (conformance) | Goldens reproducible under frozen truncation | generate.py --verify | a739027 | PASS 78/78 (lead re-run) | 2026-07-19 19:46 |

## P1 pre-merge codex review
7 findings (3 high vectors-v1, 2 high fixtures, 1 med vectors-v1... full record .razorback/sdd/premerge-*-report.md + scratchpad p1-codex-review.json), ALL verified real by lead, ALL fixed: 505445b (D2 lead), fac8157 (vectors x4), 231360e+a739027 (fixtures x2 + frozen truncation 20fbb72). Zero dismissed. Zero false positives.
## P2 execution (plan docs/plans/2026-07-20-p2-miller-lanes-plan.md)
Task B1: complete (parallel-lead-commit, Lead inline review clean, lead commit da63f84)
Task C1: complete (parallel-lead-commit, Lead inline review clean, lead commit 32e1491)
Task E1: complete (parallel-lead-commit, Lead inline review clean + lead contract-doc addition, lead commit e7765a5)
Task D1: complete (parallel-lead-commit, Lead inline review clean, lead commit f2dcb63)

## Lead decisions (2026-07-20, post-Batch A)
- B1 `disabled` status vocabulary: plan Global Constraints WIN over a literal vectors-v1 §Status reading — off mode assembles the fact but renders nothing (compact + JSON byte-identical). B6 must keep this; if vectors-v1 needs an errata note, B6 adds it additively.
- Fast-suite tripwire (58-76s observations): adjudicated as parallel-build contention, not a leaked slow test — warm quiet runs are 22-28s, lane test additions total <2s. No Scale re-tagging now; re-measure on a quiet machine at branch gate (ledger entry f2dcb63).
- C1 P3 blast radius: fusion at SearchRouteExecutor.CollectSymbolCandidates ONLY (search tool route) is the intended scope — other SearchTool.Run callers (context/impact/trace/CLI) stay lexical. Matches ADR-0003/design; P3 confirms at its own gate.
Task D2: complete (parallel-lead-commit, Lead inline review clean, lead commit 42968e2)
Task B2: complete (serial-worker-commit, worker commit 5f6511c, Lead inline review clean)
- D2 lead rulings: (1) evidence-based narrowing of design §7.4 ACCEPTED — premises were false (fuzzy has successes; no replayable corpus exists by privacy design); only the cost-neutral cap change ships. (2) KnownCeilingGap precision defect ACCEPTED as documented + corpus-pinned; any ceiling change is a future evidence-backed task, out of P2 scope. (3) matched_mode telemetry enum recorded as the recommended post-P2 follow-up (unblocks the next fuzzy evaluation); not silently added to P2. (4) wait-in-EditService + poll-success-condition divergence ACCEPTED (content.db can lag symbols.db).
Task E2: complete (parallel-lead-commit, Lead inline review clean + lead contract-doc additions, lead commit 6dff82b)
Task B3: complete (serial-worker-commit, worker commit f906a86, Lead inline review clean)
Task B4: complete (serial-worker-commit, worker commit 9c9690c, Lead inline review clean; plan mismatch accepted: SqliteVectorConvergePort owns atomic-commit SQL until B5 folds CommitBatch into VectorStore)
Task B5: complete (serial-worker-commit, worker commit 8ecabc1, Lead inline review clean; fold-in sanction honored, net -165 lines in VectorConvergeService). Follow-up dispatched to impl-b5: wire promote execution + corruption-recovery trigger in drain loop (sanctioned VectorConvergeService.cs extension) before B6.
Task B5 follow-up: complete (serial-worker-commit, worker commit 1e8ceef, Lead inline review clean; shadow rebuild executed from drain, corruption recovery wired)
Task B6: complete (serial-worker-commit, worker commit f4f44cc, Lead inline review clean; VectorSidecar.cs deviation accepted — sole facts producer/off-guarantee seam; TagsWithLiveReaders decision recorded: P2 posture = soak-window-only GC protection, registration lands with the P4 GC scheduler)
## P2 execution COMPLETE — all 11 tasks landed. P4 follow-ups recorded in task-B6-report.md concerns (top: converge_pause_state producer; disk preflight; downloading producer; GC scheduler + live-reader registry).
P2 pre-merge review (codex): 4 findings — 3 real (all fixed in ce791aa, lead-reviewed), 1 dismissed (canary unwired = stated P2 posture, wiring is P3/P5). Post-fix branch gate PASS at ce791aa.
Task F1: complete (parallel-lead-commit, Lead inline review clean, lead commit 5a25e5c)
Follow-up (recorded, not P3 scope): lift QueryShapeFor + shape predicates from SearchTool.cs into a Core QueryShape helper after F3/F4 land; SemanticQueryPolicy.HasCodeSyntax is deliberately byte-identical to LooksLikeSourceCodeQuery until then (F1 judgment call 2).
Task F2: complete (parallel-lead-commit, Lead inline review clean, lead commit 682190c)
Task G1 (Track 1): complete (serial-worker-commit c4c3270, Lead inline review clean; fast-suite tripwire waived on isolation proof — compile failure owned by F3 in-flight TDD)
Task F3: complete (commits a88f8ee, serial-worker-commit, Lead inline review clean — SemanticMode.On gate verified, blast radius confirmed via FusionArm-only reachability)
Task G2 (Track 1): complete (serial-worker-commit ccdc20c, Lead inline review clean — prerelease-aware guard regex + sidecar-anchored pin regex both verified as load-bearing divergences)
Task F4: complete (commits 26d3a7a, serial-worker-commit, Lead inline review clean — ctor-composition over registered services ACCEPTED (WorkspaceContext pre-bind throw verified; activation pinned by test); DI-lift into MillerServiceRegistration recorded as candidate follow-up, path-level chunk join recorded as eval datapoint)
Task F5: complete (commits d601eb2, serial-worker-commit, Lead inline review clean — ForcedHybridFusionArm duplication accepted as deliberate trade, flagged for codex focus; flagless-path production-arm composition accepted per brief with off-default byte-identity pinned)
Lead fix: f68dad8 — ModelRevision pin corrected to 'main' (both encoders) after G3's live RC handshake caught the mismatch; fingerprint change accepted pre-ship.
Task G3 (Track 1): complete (serial-worker-commit ee0e3f3, Lead inline review clean — RC promotion gate PASSES live; caught the ModelRevision pin defect that motivated lead fix f68dad8)
Lead fix: VectorConvergePortScaleTests.TryOpen_WithoutThePinnedExtension premise repaired (parks the packaged vec0 during the test) after G2's csproj copy transitively landed .tools/ in test output — emergent G1+G2 interaction, surfaced by G3.
Task G4 (Track 1): complete (serial-worker-commit f97ec01, Lead inline review clean — pin-driven workflow legs fail loud, publish carry proven on osx-arm64; workflow NOT dispatched, first live validation at next package-only run)
Codex pre-merge review (P3): 5 findings, ALL verified real (0 false positives), ALL fixed — (1) cross-workspace fusion root c632649; (2) SqliteException containment 45d1254; (3) forced-hybrid loud-fail + (4) --arm semantic filters ee5833a; (5) third-party notices 57c6f7c. 0 dismissed, 0 flagged. Post-fix branch gate PASS at ee5833a (fast 4159/0 serial 27s; scale 86/86 x2 serial; Release 0/0). Known non-compiling intermediate: c632649 (CliDispatch hunk interleaved with cx3's in-flight work; healed by ee5833a).

## P4 shadow rollout — plan docs/plans/2026-07-20-semantic-p4-shadow-rollout.md (approved, codex review) — worktree semantic-p4 off a921bae
Note: cross-lane concurrent dispatch (T1+T3) uses parallel-lead-commit per SDD commit-mode contract; within-lane and single tasks stay serial-worker-commit as planned.
Task 6: complete (serial-worker-commit, sidecar repo commits 76923d2 + 1010fac, Lead inline review: 1 finding (gate item 3 misdescribed packaged smoke) fixed round 1; bench measured 82.8 units/s PASS at floor 40, negative path exit 2)
Task 1: complete (parallel-lead-commit, Lead inline review clean, lead commit 3351d6b; 4 new pause tests, 33/33 VectorConvergeServiceTests; IndexerServiceScanTests flake confirmed load-induced — passes isolated 91ms, owned by Task 8)
Task 3: complete (parallel-lead-commit, Lead inline review clean, lead commit 7732efe; 16 new SemanticPrepareCliTests, fast 4188/2/0 at 28s, Release 0W/0E diagnostic; marker contract recorded for Task 4; DefaultPreflight swap deferred to Task 4 as planned)
Task 2: complete (parallel-lead-commit, Lead inline review clean, lead commit caefb9e; 14 new tests (10 DiskPreflight + 4 drain), worker fast run 4209/2/0; 104s wall = ambient load under parallel workers, Task 8 owns)
Task 4: complete (parallel-lead-commit, Lead inline review clean, lead commit 6fc6216; downloading consumer+producer, DIM probe extension accepted, DiskPreflight swap landed; worker fast run 4209/2/0)
Task 5: complete (parallel-lead-commit, Lead inline review clean, lead commit db47d45; registry+GC wiring, 78/78 worker red-green, fast 4223/2/0 at 28s; per-query reader window mismatch accepted per B6)
Lead ledger: scale suite (escalation trigger, converge path) PASS 86/86 at db47d45 after restoring pinned .tools binaries into the worktree (julie-extract 2.16.0, sidecar 0.1.0-rc.2).
Task 8: complete (serial-worker-commit 45ae5e4, Lead inline review clean; fsync-bound JulieDbFixture batched into one txn + synchronous=OFF throwaway DBs, IndexerServiceScanTests waits 5s→30s; worker 3x runs 18/19/18s, lead run 22s green 4223/2/0, scale 86/86)
Task 7: complete (lead-executed; Q8_0 = manifest-gap record (no Q8 pin; cost = pin+goldens+eval re-run); measured f16 82.9 u/s @ 1.27GiB RSS vs bge-small 743.7 u/s @ 196MiB on fixed Metal sidecar; sidecar bench gained --model+RSS, commit 34866ba sidecar-local; Miller finding docs/findings/2026-07-20-q8-footprint-benchmark.md)
Task 9: complete (lead-executed; goldfish 40s/2.2MiB clean + rebuild promote 1048 cards + debris reclaim, eros fault campaign circuit-open 16s self-report + clean recovery + chunk-starvation finding (medium, pre-P5 fix), julie 244s/9.4MiB zero errors; RSS steady 1.33GiB peak ~4.8GiB; findings docs/findings/2026-07-20-p4-shadow-dogfood.md)
Lead fix: 642fb86 — IndexerServiceLeadershipTests 5s waits -> 30s ScanSignalTimeoutMs (same class as T8 de-flake; tripped once under branch-gate load, passes isolated 3/3).
Branch gate: PASS at 642fb86 — fast 4223/2/0 (20s wall), scale 86/86, Release 0W/0E.
Lead fix: complete (commit 412033d, vec0 park-race — serialized VectorStoreTests/VectorGenerationManagerScaleTests/SemanticSidecarScaleTests on SqliteVecEnvironment)
Branch gate (final, post-review): PASS at 412033d — scripts/test.sh all (fast 4227 passed/2 skips, scale 86/86, exit 0; one transient fast failure in an intermediate run did not reproduce across two clean full runs) + Release build 0W/0E
Task 1: complete (parallel-lead-commit, Lead inline review clean, lead commit fffe9d8) — P5 plan docs/plans/2026-07-21-semantic-p5-canary-plan.md
Task 2: complete (parallel-lead-commit, Lead inline review clean — contract judgment calls verified against frozen doc, lead commit f6bd105)
Task 4: complete (parallel-lead-commit, Lead inline review clean; plan mismatch accepted: no in-flight JSON rebuild field exists — hint keys on ShadowRebuildPendingMarker; follow-up: vectors.db.rebuild disk probe for the single-wake window, lead commit c3bd69e)
Task 3: complete (parallel-lead-commit, Lead inline review — 1 finding (EmbedTimeout unproducible; typed EndedByTimeout existed at transport layer) fixed round 1, lead commit 5b7b946)

## Verification ledger — P5 Batch 1+2 gate @ 5b7b946
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| affected-change (fast) | Fast suite green after T1-T4 | scripts/test.sh | 5b7b946 | PASS 4305/0 (2 skip), 19s wall | 2026-07-21 |
| affected-change (scale, escalation: VectorConvergeService) | Converge/vec0/sidecar paths green | scripts/test.sh scale | 5b7b946 | PASS 86/86 | 2026-07-21 |
| affected-change (build) | Release 0W/0E | dotnet build Miller.slnx -c Release | 5b7b946 | PASS | 2026-07-21 |
Task 5: complete (serial-worker-commit 4f4797e + fix-round commit 2c4ce8a, Lead inline review — 1 finding (mirrored symbol pipeline) fixed round 1 via RunSymbolsCore unification; CanaryTelemetryTests 'until P5' criteria revision accepted; lead gate fast 4351/0 + Release 0W/0E at 2c4ce8a)
Task 6: complete (serial-worker-commit 44f3b36, Lead inline review clean — path-only stamping without CanaryTelemetry.cs edit accepted (T7 owns that file; absent-vs-zero honored); content semantic_contribution_count = path-membership analogue noted for analysis layer; lead gate fast 4359/0 + Release 0W/0E)
Task 7: complete (serial-worker-commit 39333db, Lead inline review clean — ShadowSymbolArm policy-gate bypass ACCEPTED (production arm abstains on identifiers; degenerate rows would invalidate the experiment; contract §Shadow step 3 requires embed+fuse); semantic_result_count omission on shadow rows ACCEPTED per 'records ONLY'; both flagged for codex focus; lead gate fast 4390/0 + Release 0W/0E)
Task 8: complete (serial-worker-commit 4495cea, Lead inline review clean — README 'not shipped yet'→'off by default' correction accepted; runbook operational-only, 23 spellings code-verified)

## Verification ledger — P5 branch gate @ 049fa9d
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| branch-gate (fast) | Full fast suite green, T1-T8 landed | scripts/test.sh all (fast leg) | 049fa9d | PASS 4390/0 (2 skip), 22s wall | 2026-07-21 |
| branch-gate (scale) | Scale suite green incl. converge/vec0/sidecar | scripts/test.sh all (scale leg) | 049fa9d | PASS 86/86 | 2026-07-21 |
| branch-gate (build) | Release 0W/0E | dotnet build Miller.slnx -c Release | 049fa9d | PASS | 2026-07-21 |
Codex fix F5+F8: complete (parallel-lead-commit, lead commit f538962)
Codex fix F2+F3+F4+F6+F7: complete (parallel-lead-commit, lead commit 6a8ea37; TelemetryScope/TelemetryRecord ownership expansion accepted for the single-instant ts fix)

## Verification ledger — P5 post-fix branch gate @ 6a8ea37
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| branch-gate (fast) | Fast suite green incl. 7 codex-fix test additions | scripts/test.sh all (fast leg) | 6a8ea37 | PASS 4401/0 (2 skip) | 2026-07-21 |
| branch-gate (scale) | Scale suite green | scripts/test.sh all (scale leg) | 6a8ea37 | PASS 86/86 | 2026-07-21 |
| branch-gate (build) | Release 0W/0E | dotnet build Miller.slnx -c Release | 6a8ea37 | PASS | 2026-07-21 |

## fusion-v2 execution — plan docs/plans/2026-07-21-encoder-comparison-fusion-v2-plan.md (approved, codex pre-merge) — worktree fusion-v2-eval off 59c2c79, design+plan committed 067c1f7
Batch A dispatched (T1 spike / T2 corpus freeze / T3 adapter, parallel-lead-commit).
Task 2: complete (parallel-lead-commit, Lead inline review clean — graded-doc/exclusion overlap verified NONE, lead commit 566da2d)
Task 3: complete (parallel-lead-commit, Lead inline review clean + lead out/-gitignore addition; parity-smoke criterion deferred to Task 4, lead commit c3f7e58)
Task 4 (lead-driven) parity smoke: PASS 5/5 exact at pool-closure depth (limit 500 both sides; root cause of first FAIL = limit-dependent lexical overFetch pool min(4L+10,500) — dumps must capture the full pool. Sweep dumps stay at plan depths, recorded as offline-arm shape). Artifacts: eval/fusion-arm/out/parity-smoke/.
Task 4 lead judgment: ownership extension — bench.py cmd_rank gains --symbol-dump/--symbol-k (per-query symbol-level semantic rankings; the adapter join needs symbol_id which bench's doc-collapsed results discard). Lexical dumps 82/82 done (frozen workspaces, --limit 50). Embed lanes deliberately HELD until Task 1 retry resolves the llama.cpp pin (uniform-runtime rule).
Task 1: complete (parallel-lead-commit, FINAL DROP drop-reason=converter — no released llama.cpp guards bert.py:372 non-MoE lookup through b10076; retry harness staged in .cache/parity/; lead commit 3a2c306)
Lead correction: Batch B (Task 6 ∥ Task 4) was file-safe but NOT wall-clock-safe — bench embed lanes contend with Task 6's timed builds. Task 6 ordered to HOLD timed runs until bench lanes finish; overlapped runs marked contaminated-discarded; loadavg<4 guard added to the run protocol.
Task 6 ack: HOLD honored — qwen3-run1/run2 marked contaminated-discarded, loadavg guard live, non-timed evidence done (10,063 cards + 792 doc chunks; qwen3 vectors.db 10.27 MiB; model sizes qwen3 1.198 GB vs bge 133.6 MB). Worker found+fixed 2 harness safety bugs: (1) kill pattern matched ANY `net10.0/miller serve` (could kill live main-checkout sessions — now scoped to fusion-v2-eval; likely cause of the session-start MCP disconnect); (2) rw sqlite read created phantom empty vectors.db breaking leader store init — now existence-guarded.
Task 4: bench embed lanes COMPLETE exit 0 (task bfw7m08jv) — all 3 candidates corpus+query vectors at eval/model-bench/.cache/runs/vecs/. Doc-level semantic-only leaders: qwen3-512d-int8 nDCG@10 0.6347 / bge-small 0.5983 / arctic 0.5682 / bm25 controls ~0.49. Sweep driver vecdir corrected (.cache/vectors → .cache/runs/vecs) and launched (task be1expf39): lexical-control + per-candidate semantic-only/12 fused profiles/forced-hybrid, all scored. Task 6 timed window opens AFTER sweep (sweep outputs are deterministic quality metrics, load-insensitive; t6 messaged, still holding for exact "machine quiet — proceed").
Task 4: complete (lead-driven, lead commit 9a16bdb — 43/43 arms scored, parity PASS, meta.json 43/43; scorer `units` extension re-scored all arms with headline metrics unchanged; retrieval-eval tests 32/32).
Task 5: complete (lead-driven, lead commit 9a16bdb — winner bar NOT MET: k20-r2 LOUO-modal 44/48, bge CI [+0.0007,+0.0139] excludes 0 but qwen3 CI [-0.0201,+0.0080] does not; FUSION-V1 STANDS; Task 7 skipped by precondition. Pin rule → bge-small-en-v1.5-f32 takes the pin; pin-change implementation is a follow-on scoped change per design acceptance criteria, deferred until impl-t6's timed window closes to avoid CPU contention).
Verification ledger addendum: | worker-red-green (scoring) | units block reconciles with overall; per-unit pairing possible | dotnet test eval/retrieval-eval/tests | 9a16bdb | PASS 32/32 | 2026-07-21 |
Pin-change checklist (scouted, deferred until impl-t6 window closes — CPU discipline):
1. src/Miller.Indexing/Semantic/MillerSemanticContract.cs — swap DefaultEncoder/FallbackEncoder bodies (bge-small becomes default; both pins already fully defined; swap classifies ShadowRebuild via PinnedIdentity, old generation retained for rollback).
2. Tests hard-coding qwen3 as default (verify each): MillerSemanticContractTests, SemanticEncoderSelectionTests, VectorSidecarClassificationTests, SemanticEmbeddingSessionTests, WorkspaceVectorFactsRenderTests, SemanticPrepareCliTests. Symbolic MillerSemanticContract.DefaultEncoder references self-adjust.
3. Docs: README.md, docs/contracts/vectors-v1.md, docs/contracts/semantic-sidecar-protocol-v1.md, CLAUDE.md default/fallback line + scripts/sync-agents.sh + cmp CLAUDE.md AGENTS.md.
4. eval/sidecar-conformance: goldens exist for BOTH models (golden-bge-small-f32.jsonl, golden-qwen3-0.6b-f16.jsonl) — verify which one the default path exercises; likely no regeneration.
5. Prove ShadowRebuild classification for the swap on a qwen3-stamped artifact (VectorSidecarClassificationTests pattern).
6. scripts/test.sh + dotnet build Miller.slnx -c Release (0W/0E). Distinct commit.
Lead correction #2 (Task 6 gate): loadavg<4 was mis-calibrated — ambient loadavg on this 24-core machine is 6–10 while 77% CPU-idle (macOS loadavg counts waking threads; Zoom + live sessions inflate it), so the gate could never clear. Replaced with the intent-faithful criterion: (a) no benchmark workloads live at run start (ps check for bench.py/llama-server/dotnet build|test), (b) CPU idle ≥60% via top -l 2 second sample, (c) loadavg + top-5 consumers recorded raw for transparency. The two bench-overlapped qwen3 runs remain discarded (real contention). Amendment to be recorded verbatim in findings §Task 6 by impl-t6.
Upstream filing (user-approved): llama.cpp converter bug filed as https://github.com/ggml-org/llama.cpp/issues/25970 (unguarded num_experts lookup in conversion/bert.py:372 crashes all non-MoE NomicBert conversions, b10068–b10076 + master). Duplicate search first: zero hits for NomicBertModel/num_local_experts. TODO: add the issue URL to findings §Task 4 CodeRankEmbed paragraph + bench-pins rejected_candidates note once impl-t6 releases the findings file.
Load-bearing bug (impl-t6 discovery, user-approved lead fix, commit bf58afd): serve/CLI sidecar launches passed NO argv, so MILLER_SEMANTIC_MODEL never reached the child — any non-default encoder wedged the vector build at commit time (512-dim emissions into a 384-dim lane), and MatchEncoder validated the sidecar against itself so the handshake let it through. Fixed: ForServe launcher factory (serve --model <id>), MatchEncoder expected-pin overload (stated refusal at handshake), both ProcessSession sites pass Active. Fast 4406/0, Release 0W/0E. ESCALATION TRIGGER FIRED (serving-path files beyond Task 7's two) → scale suite REQUIRED at branch gate. impl-t6's qwen3 numbers remain valid (default lane unaffected).
Task 6: complete (serial-worker-commit b6b81e6, Lead inline review clean — diff confined to findings §Task 6 + regenerated brief/report; gate amendment recorded verbatim; discarded runs preserved raw; bge↔bf58afd dependency stated). Cost verdict: bge-small 7.7× faster E2E build (40.4s vs 312.7s), 27× lighter sidecar RSS (470MiB vs 12.34GiB), 5× faster warm query (802ms vs 4048ms), 9× smaller download. Lead filled the harness-not-engine row (723.0/186.4/207.5s) + llama.cpp issue link in §Task 4.
Pin-change implementation: complete (lead commit 0153adc — DefaultEncoder=bge-small/Fallback=qwen3 swap, TDD re-pin, 7 test files' dim collateral fixed, docs README/vectors-v1/sidecar-protocol/conformance-README updated, ShadowRebuild migration test added; test.sh all fast 4407/0 + scale 86/86 [live sidecar serve --model bge under strict handshake], Release 0W/0E).
Task 7: SKIPPED by precondition (winner bar not met — fusion-v1 stands); recorded in plan.
Task 8: freeze record + pre-registered sealed thresholds written (findings §Task 8); frozen arm 0153adc; sealed request handed to user in the session's final report.

## Verification ledger — fusion-v2-eval branch gate @ 0f07ef1
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| branch-gate (fast) | Full fast suite green incl. pin-flip + forwarding-fix tests | scripts/test.sh all (fast leg) | 0f07ef1 | PASS 4407/0 (2 skip) | 2026-07-21 |
| branch-gate (scale) | Scale suite green incl. LIVE sidecar serve --model bge + strict handshake (escalation-trigger requirement) | scripts/test.sh all (scale leg) | 0f07ef1 | PASS 86/86 | 2026-07-21 |
| branch-gate (build) | Release 0W/0E | dotnet build Miller.slnx -c Release | 0f07ef1 | PASS | 2026-07-21 |
| branch-gate (eval) | Scorer suite green incl. units-block reconciliation | dotnet test eval/retrieval-eval/tests | 0f07ef1 | PASS 32/32 | 2026-07-21 |
Codex pre-merge review @ 0f07ef1: needs-attention, 4 findings, all verified REAL by lead.
- F2 (high) semantic prepare w/o --model prepares sidecar-manifest default (qwen3) not Miller's bge default → real-bug, dispatched fix-f2 worker (SemanticPrepareCli + tests + runbook; parallel-lead-commit).
- F3 (medium) vectors-v1.md DDL/meta still declared 512 default → real-bug (doc contract), fixed inline (DDL lane, meta example, parameterization sentence).
- F4 (medium) winner-vs-v1 CI is post-selection inference → real-improvement, fixed inline: analyze.py max-statistic selection-adjusted bootstrap. Result STRENGTHENS verdict: bge sel-adj-p=0.475 (naive CI excl-zero was winner's curse), qwen3 p=1.0. Findings updated; adjusted procedure recorded for future gate runs.
- F1 (high) offline arm shape ≠ production request shape → real limitation, pre-registered (plan depth 50); explicit limitation note added to findings §Task 4; FULL regeneration at production shape FLAGGED for user judgment (encoder comparison arm-internal-valid; sealed event runs the real arm).
Codex fix round: complete (lead commit db5a3c6 — F2 worker fix reviewed+staged, F3/F4/F1 lead-inline). Branch gate RE-RUN at db5a3c6: fast 4407/0, scale 86/86, Release 0W/0E. Freeze record's frozen-arm commit updated 0153adc → db5a3c6 (serving path identical; prepare fix required for bge adopter path).

## Verification ledger — post-codex-fix branch gate @ db5a3c6 (+ final freeze-pointer commit)
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| branch-gate (fast) | Fast suite green incl. F2 prepare-default tests | scripts/test.sh all (fast leg) | db5a3c6 | PASS 4407/0 (2 skip) | 2026-07-21 |
| branch-gate (scale) | Scale suite green (live sidecar, bge default) | scripts/test.sh all (scale leg) | db5a3c6 | PASS 86/86 | 2026-07-21 |
| branch-gate (build) | Release 0W/0E | dotnet build Miller.slnx -c Release | db5a3c6 | PASS | 2026-07-21 |

## Semantic production-readiness repair
Task 3: complete (parallel-lead-commit, lead inline review clean, lead commit e392ede — `.claude/worktrees/**` excluded from watcher and fresh extractor scan; watcher 61/61, live full-repo scale 1/1).
Task 2: complete (parallel-lead-commit, lead inline review clean, lead commit 9cfd632 — typed pre/post-embed freshness, one process-local broker, query priority, rebind-safe GC; focused 147 passed/1 skip).
Task 1: complete (parallel-lead-commit, lead inline review clean, lead commit 6a19fe1 — control/shadow lexical serving, full-pipeline policy, semantic-only content union; focused 79/79; Task 4 owns shadow-arm telemetry labeling).

## Verification ledger — semantic repair Batch A @ 6a19fe1
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| affected-change (build) | Release clean, warnings-as-errors | `dotnet build Miller.slnx -c Release` | 6a19fe1 | PASS 0W/0E | 2026-07-21 |
| affected-change (focused) | Search/canary/content/vector/session/converge/DI/watcher | focused `dotnet test ... -c Release --no-build` | 6a19fe1 | PASS 294/0, 1 skip | 2026-07-21 |

Task 4: complete (parallel-lead-commit, lead inline review clean, lead commit 34001ed — exact identity export strata, identifier clause gates promotion, shadow hybrid stamping suppressed, resolved content-read attribution, typed fallback preservation).
Task 5: complete (parallel-lead-commit, lead inline review clean + lead content semantic-off parity test, lead commit a1d3c2d — current vector facts/health, truthful CLI refresh guidance, normal CLI symbol/content production and canary parity).
Task 6: complete (parallel-lead-commit, lead inline review clean, lead commit ddd2ce7 — exact staged sidecar/encoder/embed/sqlite-vec/KNN smoke before every archive leg; Darwin arm64 local staged smoke passed).

## Verification ledger — semantic repair Batch B @ 34001ed
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| affected-change (build) | Release clean, warnings-as-errors | `dotnet build Miller.slnx -c Release` | 34001ed | PASS 0W/0E | 2026-07-21 |
| affected-change (focused) | Canary/export/gate/content/CLI/workspace/package/workflow contracts | focused `dotnet test ... -c Release --no-build` | 34001ed | PASS 506/0 | 2026-07-21 |
| fast suite | All non-Scale assertions and timing tripwire | `scripts/test.sh` | pre-commit Batch B tree | PASS 4477/0, 2 skip, 26s | 2026-07-21 |

## Semantic maturity decision execution
Canary Task 1: complete (serial-worker-commit, lead inline review clean with CLI v3 shadow forwarding assigned to Task 2, commit bb26d4cd — decision activation, contract-3 stamping, 100% identifier shadow sampling; focused 117/117; Release server build 0W/0E).
Evaluator Task 1: complete (parallel-lead-commit, lead inline review clean, commit 4e9cd3f — pure paired task scorer, Wilson lift gate, identifier/path safety, privacy-safe subgroup suppression; focused 23/23, worker evaluator suite 59/59, Release evaluator build 0W/0E).
Evaluator Task 2: complete (parallel-lead-commit, lead inline review clean — frozen per-query search modes, correct content routing, report mode counts; evaluator 59/59, Python runner 4/4, prior production metrics unchanged).
Evaluator Task 3: complete (serial-worker-commit f0e346e0, lead inline privacy review found and closed path-like language subgroup leakage — aggregate-only task-score CLI, strict schema adapters, blinded protocol; worker evaluator 65/65, Python runner 4/4).
Canary Task 2: complete (serial-worker-commit f5a818de, lead inline review clean — v2/v3 export and gate selection, strict source identity, warm bucket export, CLI decision shadow forwarding; worker owned tests 230/230, impacted 7/7, Release build 0W/0E).
