# Index convergence and Windows Scale triage (2026-08-12)

Verified triage of three questions raised while standing up a second Windows dev machine:

1. Why do the Windows Scale-suite tests fail?
2. What is the accelerator-lease issue?
3. Why does the index take 5-10 minutes to build?

Method: six parallel code investigations, each candidate finding independently attacked by adversarial
verifiers instructed to default to *refuted*. **17 of 46 candidate findings survived**; the 29 that did not
are not repeated here except where the correction itself matters. Live measurements were taken on the
reporting machine (Windows 11, .NET 10.0.400, miller `1.18.3+13bebe201222`, pinned julie-extract 2.32.1,
226,104 symbols, 882 MB `store.db`).

**Bottom line.** Q1: 12 of 12 are test defects, four independent causes, no shipped behavior is wrong.
Q2: there is no accelerator-lease issue. Q3: build output is *not* indexed; the cold cost is dominated by an
upstream fixed per-file price, and the *repeatable* pain is a Miller-side defect chain that turns a committed
store import into a failure plus a duplicate.

---

## Corrections to earlier records

Three claims carried into this investigation from prior sessions are wrong, and each one had steered
analysis:

- `SemanticBrokerScaleTests` "expected 3, actual 0" — no assertion in that file expects 3. Both candidate
  asserts expect **1**; line 32 fails first.
- `VectorConvergePortScaleTests` "the fixture pins the literal version `1.18.3+3c68ba4ed4df`" — the assertion
  at `VectorConvergeServiceTests.cs:240` is `MillerVersion.Current`, dynamic, and was dynamic at pristine
  HEAD too. It is not a stale fixture.
- The two `StoreWorkspaceIndexProviderScaleTests` held-file pairings were **swapped** in the earlier note.
  Live: `ExplicitDowngradeOverride…` holds `workspaces.db` (:110); `MissingFamilyStoreRoot…` holds
  `base-*.db` (:173).

A fourth correction was made during this session's own measurement: an early reading suggesting `bin/`/`obj/`
writes moved the revision was **measurement contamination** — the Scale suite was churning revisions
concurrently. See the controlled table below.

---

## Q3 — index build time

### Build output is not indexed

The served view's `manifest_entries` holds **1,628 paths** (789 `.cs`, 495 `.md`, 166 `.json`) with **exactly
one** path under a `bin/` segment — the legitimately gitignore-negated `bin/miller-plugin-launcher.cjs` — and
**zero** `obj/` paths. `file_versions` agrees: 1,628 rows over 1,628 distinct paths.

Controlled measurement on a quiet machine (no build, no test run, no Scale suite):

| Action | Revision | Reading |
| --- | --- | --- |
| Idle, 12 s | 6661 -> 6661 | clean baseline |
| Write a `.cs` into `obj/` and into `bin/Release/net10.0/` | 6661 -> 6661 | build output is not watched |
| Full `dotnet build Miller.slnx -c Release` | 6661 -> 6661 | builds do not churn the index |
| Entire fast suite (6,491 tests) | 6661 -> 6661 | normal test runs do not churn it |
| Add one real source file under `src/` | 6433 -> 6439 | mechanism works; +6 per edit |

`WatchPathFilter.SkipSegments` (`WatchPathFilter.cs:41-56`, matched root-relative at `:96-115`) correctly
skips `bin`, `obj`, `.git`, `.miller`, `.claude/worktrees` and friends as whole path segments.

**Nuance worth recording:** the protection is *two* layers, not three. `WatchPathFilter` blocks the watcher
path; the scan path is protected by **`.gitignore` honored by julie-extract**, not by a hard extractor
exclusion — the surviving `bin/miller-plugin-launcher.cjs` exists precisely because a gitignore negation
re-included it, which proves the extractor honors the file rather than hard-excluding the directory. The
generated `invariant.julieignore` contains only `.miller/`, `.worktrees/`, `.claude/worktrees/` and is not a
bin/obj gate at all. On a workspace whose `.gitignore` lacks `bin/`/`obj/`, a full scan **would** enumerate
build output.

### Two different clocks

| Clock | Measured | Verdict |
| --- | --- | --- |
| Time to a *usable* index, cold | ~35 s | Fine. Bootstrap unblocks at the first manifest flip (L1 published), not at import completion — request `347ef3d6` flipped at 17:17:40.379 and Miller served from 17:17:41 while that same request ran until 17:20:27. Warm bootstraps on the identical 882 MB store: 196/207/319 ms. |
| Time to a *fully converged* cold index | ~7-9 min | **This is the "5-10 minutes."** Cold import 200.9 s (L1 31.4 s + L3 164.0 s) + first resolve 223.2 s + vector initial build 78 s + sidecar builds. |
| Time to quiescence on a churning machine | effectively never | 8 whole-repo imports ran in 37 minutes on an unchanged 1,628-file tree. |

### Irreducible today — dominant, and mostly upstream

| Contributor | Measured | Share of cold converge |
| --- | --- | --- |
| L3 store-import pass | 55-105 s per import; fixed ~40-60 ms/file, byte-independent | ~30% |
| First resolve (cold only) | 223.2 s; subsequent resolves 2.4-4.6 s | ~40% |
| Vector initial 15,825-card build | 78 s (203 cards/s), one-time | ~14% |
| L1 pass | 31.4 s cold; 2.5-5.8 s warm (23x collapse on reuse) | ~6% |

**The L3 cost is not parsing, and this was measured rather than inferred.** On a scratch store of 400
synthetic C# files of **149 bytes each**, warm L3 ran at 20.0-23.0 files/s — the same band as the real
store's 1,627 files averaging 15.4 KB (16-29 files/s), a 100x difference in bytes per file. Cold L3 on the
tiny files (17.9 files/s) was indistinguishable from warm. `--jobs` 1/4/8 gave 24.3/41.1/20.7 files/s — no
CPU scaling. It is a fixed per-file coordinator/store bookkeeping cost (203 chunk commits of 8 files each),
not re-extraction. `scan.progress` reporting `files_discovered:0, files_extracted:0, files_spooled:0` after
85 s is consistent with that: no extraction happened.

Consequence: **nothing here is recoverable from Miller's side.** See the upstream asks below.

### The Miller-side multiplier

| Defect | Cost in the observed window |
| --- | --- |
| **7 redundant whole-repo imports** on an unchanged tree — all `manifest_disposition:"reused"`, byte-identical `row_counts`, zero `version_level_completed` rows | ~546 s of genuine redundant work, plus 527 s of pure queue wait when three ran concurrently (the 317/327 s outliers are queue wait, not throughput contention — a single stall each, ending exactly when the preceding import commits) |
| **Revision cursor counts in-flight import progress chunks** | ~4,800 "revisions" at a constant 226,104 symbols; 722 index swaps logged, **709 of them landing on non-terminal `store_import_l*_chunk` rows** (`view_id` NULL, `version_id` NULL, `terminal` 0). Without the chunk clause the cursor would have moved ~7 times. |
| **Each swap is ~145 ms of real work**, in *every* Miller process | Measured against the live 882 MB store through production entry points: session Open 9-15 ms warm, `COUNT(*) FROM symbols WHERE name IS NOT NULL` 35-38 ms, `SELECT DISTINCT path …` 92-97 ms, total 130-156 ms, against an 8.9-12.1 ms probe. ~15% of a core per process, continuously, for zero useful work. |
| **Silent 35 s bootstrap wait with a 10-minute budget** | The lease loop (`IndexBootstrapService.cs:1856-1868`) emits **zero** log lines. A cold start landing mid-import blocks silently. |
| **Half the corpus is throwaway benchmark JSON** | 112,933 of 226,107 symbols come from `spike/` + `docs/findings/benchmarks/`; JSON is 136,928 symbols against C#'s 74,250, and **155,558 of 164,580 `structural_facts` rows (94.5%)**. |
| **`delete(C:\source\miller\)` -> `invalid_file_path`** | Cosmetic (2 occurrences, swallowed by `ExecuteIsolated`, prior index kept) but a real filter defect. |

#### Mechanism of the redundant imports

`StoreWorkspaceCoordinator.RequireCommitted` (`:347-356`) tests `result.ExitCode != 0` **before** considering
state. All 8 imports in `coord.db` are `state='committed'` with populated `result_json` and no `error_json` —
yet a nonzero exit from julie-extract's post-commit lease-fencing check discards committed work.
`RequireCommittedAndCompleteJournal` (`:366-397`) then treats `Committed` as terminal and calls
`_requestJournal.Complete(...)` **on the throw path**, retiring the dedupe entry. Because `ImportFingerprint`
is a constant `import|{family}|{view}|Full|`, the next attempt mints a fresh request id and repeats the whole
import.

The replay branch (`Submit:302-318`) additionally fires a **second** fresh whole-repo import right after a
resumed one commits. Stack-offset evidence confirms it: the 22:20:52 failure is `Submit + 0x15d` (replay
branch, line 317) while every other failure that day is `Submit + 0x76` (line 305), and request `16099356`
was created 0.4 s after `347ef3d6` committed.

Miller's admission also bounds the wrong unit: `ScanGovernor` admission is released when `ops.Scan` **returns**
(`IndexerService.cs:902-908`, deliberately, per the 2026-08-06 P4 validation), but the coord.db *request*
outlives the subprocess. Request `16099356` kept emitting L3 chunks for **36 s after Miller logged it as
failed**, then blocked on the writer lease and committed five minutes later. That is how three imports were
simultaneously live.

### The convergence treadmill (live evidence)

The user-visible symptom is not slowness but a sidecar that can never catch up. Two samples, twenty minutes
apart, machine otherwise idle:

```
17:56  search_db:  STALE (built unknown, expected 6661)
       content_db: STALE rev 6439 (built 6439 < expected 6661)
       scan_governor: holding_elsewhere 931s  (holder pid 30904)

18:17  search_db:  STALE (built unknown, expected 6882)
       content_db: STALE rev 6661 (built 6661 < expected 6882)
       scan_governor: holding_elsewhere 1263s (holder pid 30904)
```

`content_db` converged to exactly **6661** — the value that had been "expected" twenty minutes earlier —
while the target had already advanced to 6882. It converges to the previous target every time. Independently,
the code analysis predicted that the corrected (chunk-excluding) cursor for this unchanged store would be
**6439**, which is the number `content_db` had built to in the prior sample. Two methods, arrived at
separately, agreeing on the digit.

Meanwhile `search_db` reports `built unknown` — a full rebuild rather than an incremental converge — and
`miller search` fails outright with *"Search sidecar for view … is missing or stale."* No `scan-failure.json`
exists, so no backoff is in play.

### Plain answer on "self-inflicted by indexing bin/obj"

**No.** Build output is not indexed. The self-inflicted share is real but a different mechanism: process and
leadership churn caused by the build loop (7 distinct Miller pids in 40 minutes, 2 of which became leader,
and **every leadership claim runs an unconditional whole-repo `Scan(IncrementalReconcile)`** —
`IndexerService.cs:479` -> `:900`), amplified by the request-journal defect above. Rough attribution of the
observed 37-minute window's store work: ~27% legitimate cold build, **~73% redundant re-imports and queue
wait**. Mechanism confidence high; the exact percentage medium — no controlled quiescent repro was run with
the live server stopped.

Also noted: **no `FileSystemWatcher buffer overflow` warning appeared in the original window** — but it DID
fire later the same day under heavier build/test activity (see the verification section below), so this is a
live problem on a repo this size, not a latent one. `IndexerWatcherSet.AttachCore` sets no `Filter` and
`IncludeSubdirectories=true`, so every bin/obj event still reaches the 64 KB buffer and is filtered in user
mode downstream of it — the filter cannot prevent an overflow.

---

## Q1 — the Windows Scale failures

Fresh run on this machine: **12 failed, 126 passed, 5 skipped, 6m49s.** All 11 previously on record
reproduce, plus `CliBinarySubprocessTests.BuiltBinary_WorkspaceOpen_BootstrapsFreshDir_ThenRemove`, which
shares the group (b) root cause. **All twelve are test defects. No production bug.**

Umbrella reason they survived: **no Windows CI job has ever run any of these classes.** `ci.yml:62-64` runs
the full Scale suite on `ubuntu-latest` only; `ci.yml:154-179` `windows-scale-smoke` is a hand-maintained
six-name allowlist (schedule/dispatch only) containing neither rebind class nor the governor class;
`windows-fast` and `build-test` both filter `Category!=Scale`.

### Group (b) — 5 governor tests + `CliBinarySubprocessTests`, one root cause

`Environment.GetFolderPath(SpecialFolder.UserProfile)` on Windows resolves through the known-folder API and
**ignores `USERPROFILE`/`HOME`**. The fixture's isolation (`ScanGovernorContentionScaleTests.cs:260-261`) is
therefore a no-op, and the class doc at `:19-22` states the false belief explicitly.

Measured, not inferred: `miller.exe workspace list --json` with both variables pointed at a scratch dir
returned the developer's **real** 28-workspace registry and wrote nothing to the scratch home. So the
fixture's governor is `<temp>/home/.miller/scan/scan-v1.lock` (`:179`) while every spawned child governs
`C:\Users\<user>\.miller\scan\scan-v1.lock` (`WorkspaceContext.cs:40-48` -> `CliDispatch.cs:4224-4238`).
**Two disjoint lock files — the parent's held lease can never refuse a child.**

That single fact predicts all five failures with no timing involved: `refusedAtLeastOnce == false`; both
`BlockedProcess_*` children acquire the free real-home lease and genuinely scan (the reported
`scan_duration_ms 2392` inside a 3-second budget is proof), so "Machine-wide scan admission is busy" never
prints; `ObserverProcess` renders governor state from the real home; `KilledHolder`'s `RequireLiveHolder`
polls a temp owner file no child ever writes.

`CliBinarySubprocessTests.cs:142-146` carries the identical false comment and the same defect.

**Collateral damage.** The suite has been polluting the real registry and stealing the machine-wide scan
lease from the live plugin server. The registry currently holds **27 dead rows** under
`%TEMP%\miller-scan-governor-<guid>\wt-a|wt-b|wt-large`, plus `miller-cli-e2e-*` rows carrying
`last_error: "Machine-wide scan admission is busy … owner is miller pid 7780 scanning 'C:\source\miller'"`.

**Consequence for triage:** these five tests are exercising two different lock files, so they say nothing
about whether `ScanGovernor` serializes correctly on Windows until the home fix lands. The failures are
deterministic for `ObserverProcess`/`KilledHolder`; the two `BlockedProcess_*` tests can intermittently pass
*for the wrong reason* when the real lock happens to be busy — wrong-lock, not a race, which is why this was
previously filed as "timing races".

### Group (a) — 2 rebind tests, stale assertions

julie-extract (Rust `std::fs::canonicalize`) emits the `\\?\` extended-length prefix; Miller's
`PathCanonicalizer.CanonicalizeRoot` deliberately strips it (`PathCanonicalizer.cs:45`, `:61-72`, with a doc
comment saying exactly this). `JulieExtractRunner.ParseRebindReport` (`:394-399`) carries julie's strings
verbatim by design. The tests compare a Miller-canonical (stripped) expectation against a raw julie string
with `Assert.Equal`.

**Production is prefix-immune**: `ArtifactRootIdentity.Matches` strips both operands (`:27-29`);
`IndexBootstrapService.cs:1583` delegates to it; `WorkspaceId.cs:13-15` strips before SHA-256, so both
spellings hash to one `workspace_id`. Reproduced against the pinned 2.32.1 binary on a scratch fixture:
`rebind --json` and `artifact_metadata.root_path`/`rebound_from_root` both carry the prefix. **`workspace_id`
is not at risk.** The only real-world effect is cosmetic: the raw prefixed root renders in the dashboard and
in `workspace status`/`health` via `RebindProvenanceReader`.

### Group (c) — 4 tests, three unrelated causes

1. **`SemanticBrokerScaleTests.EightSameModelProcesses_…`** — `SemanticBrokerEndpoint.Identity` hashes model
   id + model sha **only**; `millerHome` shapes the Unix socket and lock paths but **not**
   `WindowsPipeName = miller-semantic-{Identity}`, which is a machine-global namespace. The identity
   recomputed from `MillerSemanticContract.DefaultEncoder` is `90bf0aac063d5036` — byte-identical to the live
   server's endpoint, and `\\.\pipe\miller-semantic-90bf0aac063d5036` exists, owned by a sidecar whose
   `--lock` is under the **real** home. So `CountBrokerProcesses` (filtering on `<tempHome>\semantic`) returns
   0, no probe spawns, `OwnerCount` sums to 0. `EnsureStartedAsync` direct-connects before electing an owner,
   so the 8 probes attach to the live vulkan broker as non-owners. **The test is unpassable on any
   dogfooding Windows machine.** Prior art: `scripts/semantic-broker-soak.ps1:118-153` documents this exact
   hazard and hard-fails on a foreign broker.
2. **`VectorConvergePortScaleTests.Converge_ArtifactReplacementFromAnOlderMillerBuild_…`** — the test leaks a
   pooled handle. `VectorConvergeServiceTests.cs:206` opens `new SqliteConnection($"Data Source={vectorsPath}")`
   with Microsoft.Data.Sqlite's **default `Pooling=true`**, UPDATEs `vectors_meta` (materializing
   `vectors.db-wal`), and disposes it into the process-global pool with OS handles open. Windows SQLite holds
   `-wal` without `FILE_SHARE_DELETE`, so `MakeSelfContained(ActivePath)` throws. Production cannot hit this:
   **all 58** `new SqliteConnection` sites under `src/` build through `SqliteConnectionStringBuilder` with
   `Pooling = false`, and all four vectors.db openers are explicitly so (`VectorStore.cs:213,636`,
   `VectorGenerationManager.cs:528,550`). Decisive discriminator: two sibling Scale tests (`:109`, `:148`)
   drive the identical `ShadowRebuild -> Promote` path and pass, because their leaked pooled connections point
   at `symbols.db` instead.
3. **2x `StoreWorkspaceIndexProviderScaleTests`** — `using` **declarations** still in scope when the test
   deletes the temp tree. `ExplicitDowngradeOverride…`: `using var registry` at `:44` (method scope) against
   `Directory.Delete` at `:110`. `MissingFamilyStoreRoot…`: `using WorkspaceReadHandle session` at `:167` (try
   scope, closing at `:194`) against `Directory.Delete(pointer.StoreRoot)` at `:175` — the held file is
   `base-*.db` because `FamilyStoreReadSession.cs:1354-1357` ATTACHes it onto the same long-lived connection.
   Windows-only; POSIX unlink-while-open succeeds, which is why ubuntu is green. **Proven by experiment:**
   applying only the scoping change turned both green, with no product change and no added pool-clearing.
   (`SqliteConnection.ClearAllPools()` is already in both `finally` blocks and is inert here — do not add
   another.)

---

## Q2 — the accelerator lease

**There is no accelerator-lease issue.** Both the "state bug" and the "stranded lease" readings are refuted.

**1. `role: non_owner` together with `accelerator_lease: held` is coherent.** They describe different
subjects, by frozen contract. `role` is a fact about the *reporting process* — did it spawn and hold the
broker's stdin/Job lease (`WorkspaceRender.cs:211`, fed from
`SharedSemanticBrokerConnectionFactory.cs:423-431/518-523`, written only from that process's own
`_ownerProcess` handle). `accelerator_lease_held` is a fact about the *broker process*, copied out of its
`health` handshake (`SemanticEmbeddingSession.cs:899-902` -> `RecordHandshake` at `:353-369`).
`docs/contracts/semantic-broker-v1.md:88-93` and `:98` make this explicit and forbid a non-holder from
inferring ownership any other way.

Live proof on this machine, same broker, same instant: MCP `workspace health` reported
`"role":"owner","spawn_attempts":1` while CLI `workspace status` reported `"role":"non_owner","spawn_attempts":0`
with identical `endpoint_identity`, lease and backend. Two processes each correctly reporting their own
relationship — the same shape as two Millers reporting different `is_leader`.

**2. The lease is an OS file lock owned by the Rust sidecar. No TTL, no renewal, no reaper, and a crash
cannot strand it.** Miller never opens, reads, or deletes it: a repo-wide grep for `AcceleratorLockPath` finds
only `SemanticBrokerEndpoint.cs:20` (path computation) and `:24-31` (argv), plus docs and tests. Verified
empirically — opening `…\accelerator-v1.lock` with no sharing raises a sharing violation while the file is
**0 bytes with an mtime 18 minutes older than the live sidecar**. The file is residue; the lock is the open
handle, and the kernel releases it on any death.

**3. What the symptom actually is: the same machine-global-pipe test defect as group (c)-1.** The 3
fast-suite failures (`CliDispatchTests`, expecting `not_started`/`cpu`, getting `ready`/`vulkan`) happen
because those tests isolate only `millerHome`, use the default pin, and therefore compute the same pipe name
as the live server. The fake CPU broker host hardcodes `resolved_backend="cpu"`
(`Miller.SharedBrokerTestHost/Program.cs:204-217`), so an observed `vulkan` can only come from a real
accelerated broker. Under CLAUDE.md's testing rule this is a **fast-suite purity violation** (real I/O in the
fast suite), not a product bug.

### Three small caveats, none of them the reported symptom

- `health.AcceleratorLeaseHeld ?? health.Accelerated` (`SemanticEmbeddingSession.cs:215/:257`) means that
  against a sidecar omitting the field, the rendered lease is an *inference* from `accelerated`, not a
  reported lease fact.
- `SharedSemanticBrokerConnectionFactory.ClearExitedOwner` (`:565-570`) clears `IsOwner`/`OwnerProcessId` on
  owner death but leaves `State`/`Backend`/`AcceleratorLeaseHeld` pinned to the dead broker's last handshake.
  That process renders `non_owner / vulkan / held` for a broker that no longer exists, distinguishable by
  `retired_owners >= 1`. This is the one place with genuinely stale state content.
- Cross-platform asymmetry: on Unix two miller-homes with the same model get **separate** brokers
  (home-scoped socket); on Windows they share **one** (machine-global pipe). Documented, accepted, and
  contract-frozen — do not "fix" it.

---

## Fix list

### P0 — Miller product

1. **Stop counting non-terminal import chunks in the revision cursor.**
   `FamilyStoreReadSession.cs:993-1013` (`ReadStoreLogSequence`) **and** `RevisionDeltaReader.cs:348-366`
   (`StoreLogSequence`) carry byte-identical SQL; both must change or `StoreSidecarStamp.cs:282` sees
   disagreeing cursors and sidecar convergence breaks. Exclude `%\_chunk` event kinds. Do **not** use bare
   `terminal=1` — `store_import_l1_published`/`store_update_l1_published` are `terminal=0` and would be
   dropped. **Risk (real):** the corrected formula yields a strictly *lower* sequence for an unchanged store
   (6439 against 6661 here), so every persisted `store_log_sequence` in `search-*.db`/`content-*.db`/
   `vector-*.db` becomes ahead-of-expected and `SymbolSearchSidecar.cs:299-304` throws hard on mismatch —
   ship a one-time sidecar reconverge with the change. Rejected alternative: gating on
   `(ManifestGeneration, ManifestHash)` — `complete_l3` for generation 1 spans sequences 1647-5102, all
   *after* the manifest published at 1644/1645, so that gate freezes readers at symbols level indefinitely.
2. **Stop re-submitting into a live store request.** `StoreWorkspaceCoordinator.cs`: discriminate on
   `result.State` before `ExitCode` (`Committed`/`Acknowledged` is success regardless of exit code); treat a
   non-terminal (`Queued`/`Claimed`) result with nonzero exit as "still owned, work durable" — keep the latch
   armed, do not increment the failure streak, use `RecordDowngradedServe`-style pacing rather than no record
   at all (or the 250 ms drain re-submits every tick); do not `Complete(...)` the journal on the throw path;
   reconsider the replay branch (`Submit:302-318`); narrow `ImportFingerprint` (`:508`). **Risk medium-high** —
   this is the retry/backoff contract. Must not add a second retry timer (CLAUDE.md), and must not widen "any
   fencing text is retryable" (request `a4541e35` was a genuinely terminal-failed resolve with the identical
   message). Do **not** hold governor admission until COMMITTED — that contradicts the documented
   release-on-subprocess-return decision (`IndexerService.cs:904-907`, `1235-1238`) and has no bounded exit.
3. **Cheapen the freshness swap.** `FreshnessService.cs:233-250`, `WorkspaceIndexFactsReader.cs:57,:74` —
   carry `KnownExtensionsCount` forward when the manifest hash is unchanged. ~145 ms -> ~50 ms; fix 1 removes
   the swaps entirely on no-op imports. Prefer this over reading `_miller_visible_entries`: that is a
   *different set* (1,627 visible entries against 1,622 symbol-bearing paths), so the displayed `ext` count
   would flip on the first swap.
4. **Log the bootstrap wait.** `IndexBootstrapService.cs` — outcome at `:982-988`, first-failure line in the
   loop at `:1856-1868`. Do not rely on `DescribeBootstrapLockHolder`: it reads `leader.json`, which never
   exists when the blocker is a store importer. Do not implement "serve the last readable view immediately" —
   this was the first-ever import, and falling back to a stale legacy artifact is forbidden.
5. **Root-path watch guard.** `WatchPathFilter.ShouldProcess` should return false when the resolved
   root-relative path is `"."` or empty (`GetRelativePath(root, root)` -> `"."` -> splits to `["."]`, in no
   skip set). Also validate paths in `TryProcessFileConvergeRequests`, which enqueues with no
   `WatchPathFilter` call at all. The trigger is an **empty-name** `FileSystemWatcher` notification — a
   `RENAMED_NEW_NAME` whose `OLD_NAME` landed in the previous buffer read arrives as `Renamed` with a null old
   name, and `OnRenamed:1733` stats only the new path. Do **not** mirror the suppression in
   `HandleDirectoryChanged`: an empty-name event means information was *lost*, so keep the conservative
   `SignalRescan` there.

### P1 — corpus hygiene

6. **Exclude benchmark JSON** via the in-tree `.julieignore` (never `--ignore-file`, where a parse failure is
   a hard scan failure; `JulieIgnoreSeeder.EnsureSeeded` never overwrites an existing file). The obvious three
   patterns cover only 78%; needed: `spike/**/out*/`, `eval/**/out/`, `scripts/benchmarks/*.json`,
   `docs/findings/**/*results.json*`, `**/results.json`. **Stated tradeoff:**
   `ContentCorpusWriter.cs:780` enumerates the same `files` table, so excluded files also leave the content
   corpus (currently 210 JSON files / 1,198 chunks) — `search mode=content` over benchmark results stops
   working. **Magnitude honesty:** this removes ~half the symbol count and 95% of `structural_facts` but only
   ~29% of stored text; the largest block (`identifiers` + `reference_sites`, 183 MB combined) is 90%+ C# with
   zero JSON rows. It will **not** halve the 883 MB store or the 35 s cold bootstrap.

### P2 — tests

7. **`MILLER_HOME` override + fixture conversion** — the only fix here that adds production surface.
   `WorkspaceContext.cs:40-43`, `MillerServiceRegistration.cs:43-44`, `Program.cs:53-56`,
   `DashboardPaths.cs:12` must change together or a child splits its home across
   registry/telemetry/scan/semantic/stores. **Deliberate non-overrides, stated explicitly:**
   `WorkspaceRootSafety.cs:95` (the sensitive-root guard must keep resolving the real profile — making it
   steerable is a security regression, and CLAUDE.md marks the forbidden set load-bearing),
   `WorkspaceBindingResolver.cs:120` (plugin-install-root probing), `SemanticPrepareCli.cs:338` (`~/.cache`
   model cache, shared by design under ADR-0003). Then convert
   `ScanGovernorContentionScaleTests.cs:260-261` and `CliBinarySubprocessTests.cs:143-146` plus their false
   doc comments. Fixture self-check: assert the child's *reported* registry path is rooted under the temp
   home; do not assert `registered == 0`, which is vacuously true on an empty real registry. **Risk medium** —
   a partial conversion is worse than none.
8. **Rebind assertions -> identity relation.** `RebindVerbScaleTests.cs:43-44` and
   `RebindBootstrapScaleTests.cs:40` **and** `:41` should use `ArtifactRootIdentity.Matches`, which also
   absorbs the second Windows divergence a bare strip would miss (Rust reflects on-disk casing, Miller
   preserves as-launched casing). Leave `RebindVerbScaleTests.cs:48-49` alone — verbatim-against-verbatim is
   the correct pin. Do **not** strip the prefix in `ParseRebindReport`: julie's JSON *and* its
   `artifact_metadata` rows both carry `\\?\`, so stripping makes `:48-49` start failing.
9. **Handle scoping.** `StoreWorkspaceIndexProviderScaleTests.cs` `:44/:110` and `:167/:175` — scope the
   `using` declarations to close before the delete (note the naive "move `using var` into the try" does not
   compile; `registry` is consumed at `:45`/`:47`). `VectorConvergeServiceTests.cs:206` — add `Pooling = false`.
10. **CI coverage.** Add `RebindVerbScaleTests` to `ci.yml:179` after fix 8 (cheap, ~2 s to first assertion);
    add the heavier classes only after confirming headroom against `timeout-minutes: 30`. **Structural fix
    worth more than the entry:** the allowlist has no guard that a new Scale class gets added — prefer running
    Scale by *exclusion* (a named deny-list plus a convention test asserting every Scale class is listed or
    excused, same shape as `ScaleTraitConventionTests`). Note the job is `schedule`/`workflow_dispatch` only,
    so it is not a PR gate today.

---

## Upstream asks (julie-extractors) — Miller cannot fix these

1. **L3 re-walks every file on a reused manifest.** The store already knows the work is unnecessary
   (`manifest_disposition:"reused"`, zero `version_level_completed` rows) yet still pays ~40-60 ms/file. Ask:
   skip L3 for files whose content hash already has a completed version-level row, and/or accept a
   changed-paths argument on `store import`. **Highest-value upstream item** — worth 55-105 s per no-op import.
2. **A fencing-failed request keeps executing server-side.** Request `16099356` emitted chunks for 36 s after
   Miller was told it failed, then committed five minutes later. Since `JulieStoreClient.Submit` blocks on
   `WaitForExit` and the child is inside a `WindowsKillOnCloseJob`, the surviving executor either broke away
   from the job or is a separate process.
3. **Fencing reproduces on a brand-new, uncontended scratch store**, and in one run the CLI reported `failed`
   while `coord.db` recorded the same request `committed` with 1,627 file_versions written.
4. **Stale `claimed` coordinator rows with no live process** (`06c5e45b` resolve, `896c46c4` import). Given
   `uidx_coord_one_claimed_resolve`, one stranded claimed resolve blocks every future resolve in the family
   until reaped. **Analysed in depth 2026-08-13; an earlier note here proposing an *acquire-time* reaper was
   wrong.** Verified against the source:
   - **The requeue-on-takeover fix does not close it.** The resolve claim protocol deliberately holds no
     writer lease (`resolve.rs:235` claims before any `with_writer_lease`; `store_coordinator_contract.rs:251`
     and `:273` assert `lease().is_none()` while a resolve claim is live). Takeover requeue is keyed on an
     `old_holder_id` read from an existing `writer_lease` row, so with no lease row the `(None, None)` arm
     takes a plain INSERT and the requeue never runs.
   - **An acquire-time reaper would also miss it.** `execute_resolve` reaches `claim_until_deadline` without
     ever calling `try_acquire_or_takeover`. It blocks before it would ever acquire. **The correct hook is
     inside `claim_resolve`**, whose `other_claimed` EXISTS early-return (`coordinator.rs:716-726`) fires
     *before* the `prior_owner_dead` / staleness arms at `:727-737` — both of which are only ever evaluated
     for the row being claimed, never for the row doing the blocking.
   - **On Windows the existing dead-owner machinery is inert.** `coordinator.rs:2126-2129` is
     `#[cfg(not(unix))] -> PidStatus::Unknown`, so `prior_owner_dead` is hard-wired false on the very platform
     where this stranded. Any PID-based reaper needs a Windows liveness implementation first — and
     `unsafe_code = "forbid"` at the workspace root rules out direct FFI, so that means either a subprocess
     (`tasklist`, needing `CREATE_NO_WINDOW`) or a thin safe wrapper crate, which is the repo's own documented
     precedent (`getrandom`, `same-file` in `julie-extract-cli/Cargo.toml`, both added for exactly this reason).
   - **Adversarial review refuted the first design.** A second "reap at acquire" hook is unsound — it sits
     before the `existing` SELECT and `try_acquire_with_intent_policy` commits unconditionally, so a process
     *refused* the lease would still mutate the live holder's claimed rows. And a purely time-based
     hard-abandon arm reaps *live* work: `claim_heartbeat_at` is not refreshed during a quantum, so a healthy
     L3 import (71-85 s measured) looks stale the whole time.
   - **Disposition matters.** `reconcile`'s promote accepts `state IN ('queued','claimed','failed')`
     (`coordinator.rs:1758`), and `store gc` reclaims scratch only for `('failed','committed','acknowledged')`
     or a receipt (`maintenance.rs:2246-2255`) — so a resolve reaped to `queued` that nobody re-claims pins
     its `resolve-*.db` scratch forever and keeps `pending_request_ids` non-empty. For the **takeover** path
     the opposite holds and `queued` is correct: `claim_request` sets `claim_owner = holder_id`, so the rows
     takeover actually touches are drain-claimed kinds that `next_pending_request` re-claims, and marking
     those `failed` would silently discard merely-interrupted work.

   Net: this is a real upstream defect with a known correct hook, but it is a **larger change than a release
   patch** — it needs Windows liveness, a fenced CAS outside the write transaction, and a disposition split
   between the takeover and reaper paths.
5. **`coordinator writer pragma configuration failed: attempt to write a readonly database`** on a coord.db
   that is writable with 1.6 TB free.
6. **Store schema key width.** Measured by full-DB simulation: converting every `*_id TEXT` (32-53 chars, some
   with 21-24-char ASCII prefixes such as `reference_site_spanless-`) to 16-byte BLOBs and `path TEXT` ->
   INTEGER FK yields **499.5 MB against 873.8 MB compacted — a 374 MB / 43% saving**. Also worth reviewing the
   index set: 327 MB of secondary indexes on 25 MB of source, the largest single being
   `idx_gc_source_regions_export_order` at 41.7 MB. Breaking contract change; needs a schema-version bump and
   a coordinated Miller reader update. Note the store is **not** bloated by history (1,628 file_versions over
   1,628 distinct paths, freelist 0, 5.6% slack) and holds no file text.
7. **`scan.progress` reports `files_discovered:0` during the `store_import` phase** — a reporting gap,
   cosmetic (Miller's stall detection uses byte-length stamps and never parses counters). Miller-side: add
   `store_import`/`store_update` to the phase list in `ScanProgressRecord`'s doc comment.

### Three more, found 2026-08-13 while preparing julie-extract 2.33.0 (all pre-existing at HEAD)

> **Status 2026-08-13: all three are FIXED in julie-extractors and fold into the unreleased 2.33.0.**
> Each fix carries a regression test that fails at pristine HEAD on Windows and passes after. Serialized,
> `store_resolution_contract` improved from 6 failures to 4 — the two that started passing are the
> crash-resume pair, which is exactly what a working liveness probe unblocks. That suite is parallel-unstable
> on Windows at HEAD (5, 10, and 6 failures across three pristine parallel runs), so only the serialized run
> is evidence. The measured import repair: a lease released after 500 ms went from a 30 s timeout failure to
> committing in 1.1 s.
>
> One thing the fix had to buy back: `tasklist` costs **108 ms per call** on this host (20 calls, 2,160 ms),
> and the lease paths probe from retry loops that tick every 10 ms — so a naive port would have spawned a
> process per tick on Windows. The probe reuses each result for 250 ms, which bounds the spawn rate and
> costs at most 250 ms of takeover delay against a 5 s lease.

Found by running the full workspace suite and the `contract` tier on Windows and comparing against an
isolated pristine-HEAD worktree. **17 tests fail at HEAD on Windows** — 11 in the workspace suite and 6 more
in `store_resolution_contract`, which `cargo test --workspace` never even runs because it is gated behind
`--features test-store-resolution-contract`. Adversarial verification separated genuine product bugs from
test defects.

8. **A failed resolve leaks its scratch database on Windows, and the "leaves no artifact" guarantee is
   violated.** `ResolutionBaseWriter` (`resolution.rs:3222`) and `ResolutionScratchWriter`
   (`resolution_diff.rs:317`) each own a `rusqlite::Connection` **as a field** and unlink their file inside
   the `Drop::drop` **body** — which Rust runs BEFORE dropping fields, so the SQLite handle is still open.
   SQLite's `winOpen` requests `FILE_SHARE_READ | FILE_SHARE_WRITE` with **no `FILE_SHARE_DELETE`**, so
   `DeleteFileW` returns `ERROR_SHARING_VIOLATION` and the `let _ =` discards it. POSIX unlink-while-open
   succeeds, which is why Linux CI is green. Consequence on Windows: orphaned
   `resolve-exact-*.db`/`resolve-delta-*.db` accumulate after every failed resolve — the post-run cleanup is
   skipped by the early `?` return — and a partially written `completed=0` file survives. It is not a hard
   wedge only because every call site defensively pre-cleans before opening. **Fix:** swap the connection for
   an in-memory placeholder before the unlink loop, the idiom already used 30 lines above in the same file
   (`resolution.rs:3421-3422`). Isolating control: `ResolutionScratchDelta::abort()` asserts the same
   `!exists()` and PASSES on Windows — it holds no `Connection`.
9. **`store import` never retries `drain`, so a crash-resume burns the entire request budget.**
   `import.rs:484` calls `drain` once; on `LeaseUnavailable` it falls into a passive observer loop
   (`:488-541`) that only waits for someone else to finish, then reports `failure_class: request_timeout`
   and exits 1. Measured: a lease that was free after **4.95 s** still cost a **119-second** retry (the
   first process was given `--request-timeout-seconds 120`), which proves the loop never re-attempts rather
   than merely waiting out the 5 s TTL. **Not Windows-only** — on Unix the same stall occurs whenever
   liveness returns anything but `Dead`: an unreaped zombie, a recycled pid, or a container without
   `ps`/`kill`. Windows just hits it every time because `process_status` is `Unknown` there. `store
   update`/`delete`/`from-artifact` already do the right thing via `drain_when_available`
   (`import.rs:954-975`). **Subtlety that must not be missed in any fix:** a retry inherits the ORIGINAL
   `requester_deadline` (`coordinator.rs:621-628` returns the existing request), so the retry's budget is
   whatever remains of the crashed process's window, not a fresh 30 s.
10. **A Windows `process_status` already exists in this repo and is simply not used by the lease path.**
    `export.rs:1288-1301` ships `#[cfg(windows)] fn process_is_alive` using
    `tasklist /FI "PID eq {pid}" /FO CSV /NH`, while `coordinator.rs`'s and `watchdog.rs`'s copies return
    `PidStatus::Unknown` on all non-Unix targets. So this is an inconsistency across three call sites, not a
    deliberate platform limit, and porting it needs no new dependency. (`unsafe_code = "forbid"` is real, but
    the CLI crate already pulls `same-file` and `getrandom` precisely to reach OS APIs behind that lint.)

The remaining Windows failures are test defects, not product bugs, and are listed here so they are not
re-triaged: a test shells out to `sh -c` for `$$` (`sh` is not on a stock Windows PATH); a test asserts the
documented Unix-only `--parent-pid` watchdog fires; a test backdates a file through a read-only `File::open`
handle, which cannot satisfy `SetFileTime`'s `FILE_WRITE_ATTRIBUTES`; and one diagnostic builds a path with
`Path::join` on an already-`/`-separated relative, emitting a cosmetically mixed `\`/`/` string
(`reports.rs:137`) — the only Miller-visible one, and Miller does not consume that field.

**Verified good news:** the spool reaper is CORRECT on Windows. Its test fails only in setup. A standalone
replica proved a live scan's spool is never reaped (Windows `LockFileEx` is per-handle, giving the same
`Held` verdict `flock` gives) and a dead scan's spool IS reaped. The 130 GB-orphan field incident's inverse
failure mode is not present.

**Correction to this document's earlier claim that CI is ubuntu-only:** `ci.yml:100-119` defines a
`windows-capacity-store` job on `windows-latest`. It runs exactly two things — `capacity_tests --lib` and the
single test `public_store_import_reaches_the_production_executor`. So Windows CI exists but is a
two-item allowlist, the same structural gap as Miller's own `windows-scale-smoke` allowlist: a new Windows
failure is invisible unless somebody adds it by hand.

---

## Still unknown, and the exact next measurement

| Unknown | Next measurement |
| --- | --- |
| Controlled cost of the redundant-import chain — every number above comes from a contended window with a live server and a build loop | Stop the plugin server, `miller workspace refresh` twice on a quiescent tree, count `coord.db` requests and `store_log` chunk spans per request. Expected if fix 2 is right: 1 request, not 2+. |
| Does the surviving store executor break away from `WindowsKillOnCloseJob`, or is it a separate process? | During a reproduced fencing failure, snapshot the process tree and job-object membership of the `julie-extract` pid recorded in `coord.db.requester_id`. Fencing reproduces on a fresh scratch store, so this is repeatable without the live workspace. |
| Why did pid 7780 escalate the vector shadow build six times without promoting (17:22-17:34)? | Read `symbol_last_error` in `vectors.db` meta; log `plan.Trigger` alongside `(CompletedRevision, TargetRevision, Candidates.Count, ReEmbed.Count, DeltaHistoryComplete, FullPass)` at `VectorConvergeService.cs:882`. |
| Does `ScanGovernor` genuinely fail to serialize on Windows *independent* of the home defect? | Land fix 7, re-run the 5 governor tests. Do not treat the currently-red tests as evidence about the governor until then. |
| Is `windows-scale-smoke` currently red? | `gh run list --workflow ci.yml --json conclusion,createdAt`, or dispatch it. Two `StoreWorkspaceIndexProviderScaleTests` are already in its filter and fail deterministically on Windows, so it should be red. |
| Does WAL/checkpoint churn on an 882 MB store contribute? **Not cleared** — WAL file size is not a rate, and `wal_autocheckpoint` is 1000 pages (~4 MB), so any sample below that is uninformative (the same file measured 20 KB and 404 KB minutes apart). | Sample `PRAGMA wal_checkpoint` counters or WAL size/mtime delta over a fixed window *during* churn. |
| ~~Does the `FileSystemWatcher` 64 KB buffer ever overflow?~~ **ANSWERED: yes**, on this repo, under build + test load. | Now a design question, not a measurement: the overflow forces a whole-repo rescan. Do **not** "fix" it with `FileSystemWatcher.Filter` — that is a single glob and cannot express the 12-entry skip set. Raising `InternalBufferSize` is the usual lever. |
| Why does a brand-new scratch workspace open as `failure=corrupt` while `coord.db` shows import AND resolve `committed`? Bisected clear of P0-1. | Surface the inner exception that `FamilyStoreReadSession`'s outer `catch (… or SqliteException or FormatException)` currently swallows into a generic message, then re-run `workspace open` on a 40-file scratch repo. |

---

## Implementation status (same session)

All P0 items landed, plus every test defect. Build stayed 0 warnings / 0 errors throughout.

| Item | Status | What changed |
| --- | --- | --- |
| P0-1 cursor | **done** | New `Miller.Indexing.Reads.StoreLogCursor` holds the ONE definition of the cursor SQL; `FamilyStoreReadSession.ReadStoreLogSequence` and `RevisionDeltaReader.StoreLogSequence` both consume it, so the two can no longer drift. Predicate excludes `%\_chunk` kinds with `terminal = 1` tested first. 3 regression tests write chunk rows the way the producer actually does (view_id NULL, version_id NULL, terminal 0) — the pre-existing fixtures all used a non-null view_id, which is why nothing caught this. Verified against the live store: 1,971 chunk rows against 20 real events, 0 terminal chunks. |
| P0-2 committed-is-not-failed | **done** | `RequireCommitted` discriminates on `result.State` first; `Committed`/`Acknowledged` returns regardless of exit code. The journal double-retire disappears as a consequence (a committed request no longer takes the throw path). 4 regression tests; `RecordingStoreClient` gained a `stateOverride` so committed-with-nonzero-exit is representable at all — it previously derived state FROM the exit code, which is why the defect shipped untested. |
| P0-3 swap cost | **done** | `FreshnessService.ReadFactsReusingExtensions` memoizes the streaming distinct-path scan (92-97 ms) on (manifest hash, symbol-bearing file count); the single counts statement (35-38 ms) still runs every swap. Keyed on the file count too, because a progressive level upgrade inside one manifest can add symbol-bearing paths. New public `WorkspaceIndexFactsReader.ReadKnownExtensionsCount(session)`. |
| P0-4 bootstrap wait | **done** | Instrumented through the `sleep` delegate at the call site, so the pure `AcquireBootstrapScanLease` helper is untouched: one Information line naming what is awaited on the first failed acquire, one on completion with outcome, elapsed ms and poll count. |
| P0-5 root guard | **done** | `WatchPathFilter.IsWorkspaceRootItself` rejects a root-relative `"."`/empty path; 4 tests. `TryProcessFileConvergeRequests` now runs the filter over drained requests (it enqueued with none), resolving a relative path against the root for the decision only. |
| P1-6 corpus | **done** | Five patterns added to the in-tree `.julieignore` with the content-corpus tradeoff and the "this will not halve store.db" magnitude note recorded in the file itself. |
| P2-7 MILLER_HOME | **done** | New `Miller.Indexing.MillerHome` (`MILLER_HOME`, else the user profile). Converted together: `WorkspaceContext.Create`, `MillerServiceRegistration` (governor), `Program` (deferred log dir), `DashboardPaths`, `DashboardEndpoints` fallback. Deliberately NOT converted: `WorkspaceRootSafety` (security), plugin-root probing, the `~/.cache` model dir. Both fixtures now set `MILLER_HOME` and their false doc comments are corrected. 6 fast tests, including one asserting `HOME`/`USERPROFILE` are NOT the switch. |
| P2-8/9 test defects | **done** | Rebind assertions use `ArtifactRootIdentity.Matches` (both files, 4 assertions); the two leaked `using` declarations are scoped ahead of their deletes; the pooled connection gets `Pooling = false`. |
| P2-10 CI | **partial** | The three cheap fixed classes were added to the `windows-scale-smoke` allowlist with a comment explaining that a class missing from it is a class no Windows CI has ever run. The run-Scale-by-exclusion restructure plus convention guard was **not** done: the excuse list (which classes are too slow for a hosted runner under `timeout-minutes: 30`) is a design call, not something to invent. |
| P2-11 broker isolation | **skip-guard only** | `SemanticBrokerScaleTests` now skips with an explanatory message when a foreign broker already owns the machine-global pipe, instead of failing. The preferred injectable-snapshot seam for `CliDispatch.CliSemanticBrokerFacts` (the MCP path already has one) was not built; the 3 `CliDispatchTests` broker failures therefore remain. |

### Verified after restarting the wedged server

Both suites re-run on a quiet machine (no server holding scan admission):

| Suite | Before | After |
| --- | --- | --- |
| Scale (`Category=Scale`) | **12 failed**, 126 passed, 5 skipped | **0 failed, 137 passed, 6 skipped** (5m44s) |
| Fast (`Category!=Scale`) | 6,466 passed, 3 failed | **6,483 passed, 3 failed** (+17 tests, same 3) |

The 3 remaining fast failures are the known live-broker environmental ones (P2-11). While the wedged server
was still running, two consecutive fast runs produced *different* incidental failures
(`BootstrapAdmissionRetryTests`, `StoreSidecarConvergerTests`, `WorkspaceBindingServiceTests`,
`SharedSemanticBrokerConnectionFactoryTests`, `SemanticEmbeddingSessionTests`) that each passed in isolation;
all of them are green on the quiet machine, confirming contention rather than regression.

**P1-6 measured on the live workspace:** symbols fell **226,104 -> 100,905** after the `.julieignore` patterns
took effect — in line with the predicted 112,933, and the store re-imported at generation 3.

**P2-7 measured:** `workspace open` with `MILLER_HOME` set to a scratch directory put the registry AND the
family store under that directory. This is precisely what setting only `USERPROFILE`/`HOME` failed to do.

### Running the fixed build on the live workspace: P0-1 confirmed, and the real remaining blocker found

With the MCP server switched to the source build (`MILLER_BINARY` in gitignored
`.claude/settings.local.json`, which the plugin launcher honours before any cache logic), the live workspace
was polled for 10 minutes:

- **P0-1 works.** `expected` held **constant at 7438** for the whole window. Before the fix the same field
  climbed continuously (6661 -> 6882 -> 7389 -> 7406 within minutes). The moving target is gone.
- **P0-2 behaves correctly.** `coord.db` holds 11 `committed` imports and 4 `committed` updates that the new
  state-first check passes through; it threw only on a request that genuinely never committed (stack line is
  the `Failed || ExitCode != 0` branch, reached only when state is not Committed/Acknowledged).
- **But the sidecars still do not converge**, for a different reason.

**The blocker is stranded coordinator claims — upstream ask 4, now reproduced with owners named:**

| kind | state | request | claim_owner | owner alive? |
| --- | --- | --- | --- | --- |
| resolve | `claimed` | `06c5e45b` | `cli-36084` | **no** |
| import | `claimed` | `a0331411` | `cli-35676` | **no** |
| resolve | `queued` | `5749eb8b` | — | — |
| resolve | `queued` | `b8fed8fe` | — | — |
| resolve | `failed` | `a4541e35` | — | — |

Both claim owners are **`cli-*` one-shot CLI processes that have exited**, and neither has a heartbeat. Given
`uidx_coord_one_claimed_resolve`, the single stranded claimed resolve blocks every future resolve in the
family — which is exactly why the store sits at `level=symbols` with `resolution=unbound` and a full-level
upgrade owed, and why the derived sidecars cannot converge. The startup delta scan then fails on the writer
fence and the persisted backoff reaches 4 consecutive failures (30-minute suppression).

**This raises the severity of upstream ask 4.** It is not only that stale rows exist: an ordinary
`miller <verb>` CLI invocation that exits or is killed mid-request can strand a claim, and one stranded
resolve wedges the whole family with no reaper. Miller cannot fix this from its side — `coord.db` is
producer-owned and Miller must never write it (CLAUDE.md), so recovery today means rebuilding the family
store.

### Two findings from the post-fix verification

1. **The `FileSystemWatcher` buffer overflow DOES fire on this repo.** The earlier triage recorded zero
   overflow warnings; under the heavier test/build activity of this session the log shows
   `System.IO.InternalBufferOverflowException: Too many changes at once in directory:C:\source\miller` followed
   by `forcing a rescan`. The latent risk described above is real on a repo this size, and `WatchPathFilter`
   runs downstream of the buffer so it cannot prevent it.
2. **A brand-new scratch workspace fails to open, and it is NOT caused by these changes.** `workspace open` on
   a fresh 40-file git repo ended `store: state=failed failure=corrupt` /
   "The family store could not be opened as a validated read session", even though `coord.db` showed the
   import AND resolve both `committed` with `result_json` and no `error_json`. Bisected directly: the identical
   failure reproduces with the ORIGINAL cursor predicate on the same store, so P0-1 is not implicated. A second
   scratch run stranded its import at state `claimed` with `store-writer lease fencing check failed` — the same
   producer defects already filed as upstream asks 2-4, now reproduced on an uncontended store. **This wants
   its own investigation**; the outer catch in `FamilyStoreReadSession` swallows the inner exception, so the
   first step is surfacing it.

## Round 2 (same evening): the actual convergence blocker, and three corrections

A second adversarially-verified investigation (39 agents, 12 findings surviving) plus live measurement
overturned several claims made earlier in this document. **Read these corrections before acting on anything
above.**

### Corrections

1. **The search sidecar stamp was never unreadable.** `StoreSidecarStamp.TryRead` reads it cleanly in every
   artifact opened, including the quarantined one. `built unknown` is a **rendering bug**:
   `SymbolSearchSidecar.cs:161-162` calls `StoreSidecarCatalog.IsCurrent`, discards the stamp it just read,
   and hard-codes `null` for the revision on the stale branch. `ContentCorpusSidecar.InspectStore`
   (`:210-227`) does not take that shortcut, which is why content honestly reported `STALE rev 7134` for the
   identical state. `IsCurrent` (`StoreSidecarStamp.cs:251-252`) is whole-record equality over 13 fields, so
   the sidecar was stale on four at once (manifest `a28142a9`→`2c0996f3`, generation 3→8, level
   symbols→full, sequence 7134→7905) — not on sequence alone.
2. **The leader never parked, and there is no in-memory backoff.** `PersistedScanFailurePolicy.Read()`
   (`ScanFailurePolicyStore.cs:113,145`) re-reads the journal on every `Evaluate`, so deleting
   `scan-failure.json` DOES clear the streak on the next 250 ms tick. The retry timer fired on schedule
   (20:52:57, 21:03:20). The apparent 48-minute silence is an OBSERVABILITY hole: `IndexerCore.ExecuteIsolated`
   logs only after `_ops.Scan` returns, `TryAcquireScanAdmission` logs only refusals, and
   `StoreWorkspaceCoordinator` contains **zero logging calls of any kind**. Store-sidecar convergence likewise
   logs nothing on success (`IndexerSidecarConverger.ConvergeStoreSidecar:148-160` discards the `bool`), and
   the "Converged … at revision" lines at `:186-187`/`:218-219` are on the LEGACY path, unreachable in store
   mode.
3. **Search recovered on its own.** Quarantining the file only made the rebuild happen sooner; the drain's
   full-rebuild arm (`SymbolSearchSidecar.cs:331`) would have run anyway. Verified live afterwards:
   `search_db: current rev 7905`, `content_db: current rev 7905`, and `miller search` returning ranked
   results.

### The live blocker: batch work is killed at 4 s and rolled back

`julie-extract-artifact/src/store/coordinator.rs`:

```rust
const DEFAULT_MAX_QUANTUM_MS: i64 = 4_000;
const DEFAULT_LEASE_DURATION_MS: i64 = 5_000;
fn permits_renewable_quantum(self) -> bool { matches!(self, Self::FromArtifact) }
```

An `Import`/`Resolve` quantum that exceeds 4 s is rolled back (`drop(transaction)`) and requeued, and the
caller receives `LeaseLost` — whose display string is literally *"store-writer lease fencing check failed"*
(`coordinator.rs:467`). The work runs IN FULL first. Since the L3 store-import phase alone measures 71-85 s,
**a repository whose scan exceeds 4 s can never converge.**

Measured on this workspace: a CLI refresh reporting `scan_duration_ms: 559330` (9 min 20 s) ending
`status: failed` / fencing; and a 41 s startup delta scan failing deterministically on every restart.

**Two distinct sites fire**, distinguishable by spelling in the log: `store-writer …` (hyphen) is
`CoordinatorError::LeaseLost`; `store writer …` (no hyphen) is `StoreConnectionError::WriterLeaseLost`. Both
appear, so both the quantum cap AND lease loss are real.

### The second mechanism: one transient error discards a whole scan

`LeaseHeartbeatGuard::start` (`coordinator.rs:2504-2542`) already runs a renewal thread for the whole drain,
ticking every `lease_duration_ms / 3` ≈ 1.67 s. But its handler was:

```rust
Ok(false) | Err(_) => { worker_current.store(false, …); return; }
```

A single transient `SQLITE_BUSY`, or one tick arriving late because the renewal thread was starved behind the
extractor's own rayon pool, permanently marked the lease lost and killed the thread. Nothing renewed
afterwards, the lease lapsed, and the commit's `validate_writer_lease` rejected everything the executor had
done. It bites hardest under load — exactly when scans are slow.

### What was changed in julie-extractors

| Change | Status |
| --- | --- |
| Takeover requeues a dead holder's claims instead of adopting them | **verified** (60/60 crate tests when the machine was quiet) |
| `LeaseHeartbeatGuard` retries transient failures (`HEARTBEAT_RENEWAL_ATTEMPTS = 3`) and, on a lapsed row, re-extends via `reclaim_lapsed_lease_at` ONLY when the row still carries our fencing token (proving no takeover) | **unverified** — defensible by inspection; see the test caveat |
| `permits_renewable_quantum` widened to `Import \| Resolve` | **unverified** |
| `step_incremental_vacuum` drains the pragma instead of stepping it once | **verified** — 23.70 s → 0.55 s, harness green (see below) |
| Heartbeat retry backoff capped at `interval / 2` so the ladder cannot outlast the lease it defends | **verified** — 60/60 serialized; at the 120 ms lease the contract tests use, the flat 100 ms delay meant one tick's ladder (200 ms) exceeded the whole lease |

**Test status.** `store_coordinator_contract` passes **60/60 with `--test-threads=1`** — which is how the
formal `contract` gate runs it (`xtask/src/test_tiers.rs:300-311`). Under the **parallel** `default` tier
(`cargo test -p julie-extract-artifact`) three tests in this harness are wall-clock sensitive and fail on a
loaded Windows machine at **pristine HEAD**:

| Test | Sensitive assertion |
| --- | --- |
| `batch_progresses_under_a_sustained_interactive_producer` | scheduling under load |
| `drain_caps_the_interactive_burst_at_32_before_a_batch_quantum` | scheduling under load |
| `long_running_quantum_heartbeats_writer_lease_and_commits_once` | `recv_timeout(2 s)` waiting for the worker thread to be scheduled, *before* any lease logic runs |

The third was verified pre-existing by stashing every local change and reproducing at HEAD: identical
failure, identical 59-passed/1-failed count, same assertion (line 2314 at HEAD = line 2327 with the local
13-line test edit). **They are therefore a flaky `default` gate on a slow/loaded host, not a signal about
these changes** — and worth making deterministic separately, since `cargo xtask test default` runs them in
parallel.

### The red HEAD: `PRAGMA incremental_vacuum(N)` was reclaiming exactly one page per call

`store_maintenance_contract`'s `gc_steps_incremental_vacuum_until_the_freelist_is_empty` failed with
`Connection(WriterLeaseLost)` at pristine HEAD, so julie-extractors main was already red — and
`store_maintenance_contract` is in the formal `cargo xtask test contract` gate
(`xtask/src/test_tiers.rs:296`), so this was gate-blocking, not a stray test.

The name misleads. Instrumented per-step timing put **11,308 ms of an 11,477 ms apply inside
`step_incremental_vacuum`** (`maintenance.rs:1708`), against the 5,000 ms writer lease minted in
`acquire_for_action`. The lease lapsed mid-vacuum; the next `open_writer` — reached through
`finish()` → `restore_serving_source_floor_and_clear_coord` — failed `validate_writer_lease`
(`connection.rs:278-302`). Both earlier `open_writer` calls succeeded at +54 ms, and
`transaction_with_behavior` does **not** re-validate the lease, which is why the metadata transaction at
+11.5 s committed happily against a lease that had died 6.5 s earlier.

Root cause: `PRAGMA incremental_vacuum(N)` compiles to a VDBE loop yielding a row after **each** page it
reclaims, and `Connection::execute_batch` steps a statement exactly once and discards the row. The `N`
argument was therefore inert — one call reclaimed ONE page — and the surrounding `loop` re-drove it once per
page: 4,608 iterations for 4,608 pages, each a separate implicit write transaction under
`synchronous = FULL` (one fsync per page), plus two `PRAGMA freelist_count` round-trips each.

**Not a debug-speed artifact:** release fails identically (9,908 ms vacuum). It is 4,608 serialized fsyncs,
which optimization cannot remove.

Fixed by draining the pragma's rows (`prepare` + `query` + step to completion) so one call reclaims the whole
requested budget in one transaction. **Measured: 23.70 s → 0.55 s, test green; the full 19-test harness runs
in 7.29 s.** (The harness has 20 `#[test]`s; the 20th is `#[cfg(unix)]`, which is why Windows shows 19 and the
last-green Linux gate recorded 20.)

Archaeology verdict: **always-latent, newly exposed** — not a regression. `maintenance.rs` and the test file
are byte-identical to the last recorded green gate (`3d2d560b`); the lease has been wall-clock since
`eef16758`, a day *before* the test was written. The budget stayed 5,000 ms while work inside it grew.

**Still open (deliberately not fixed here).** `docs/plans/2026-08-10-store-concurrent-fencing-hardening.md:32`
is normative — *"Long work must heartbeat or re-acquire"* — and `apply_with_policy` heartbeats nowhere;
`heartbeat_generation_build` renews only `maintenance_intent`, never the `writer_lease` row that
`validate_writer_lease` actually checks, and is called only from the M3 generation path. So a GC whose delete
transaction alone outruns the lease on a multi-GB store still fails this way. A background renewal thread is
**the wrong fix**: `MaintenanceInspector::inspect` uses `PRAGMA data_version` on `coord.db` as a race guard
(`maintenance.rs:907`, `:980-984`), so any concurrent coord.db writer converts `WriterLeaseLost` into
`InspectionRaced` on exactly the long runs it was meant to rescue. The crate's own compatible idiom is
synchronous between-phase renewal (`resolution.rs:1661-1662`: *"Never heartbeat mid-transaction"*). That needs
its own design pass.

**A dead end worth recording:** a second heartbeat (`QuantumLeaseRenewal`) scoped to the quantum was written
and then removed — `LeaseHeartbeatGuard` already covers the whole drain, so it was pure duplication. Check for
the existing guard before adding renewal machinery.

### Ranked cost budget for the fixed ~40-60 ms/file (all julie-extractors)

| Item | Measured |
| --- | --- |
| Per-file spool disk round-trip, ×2 passes, inside a process-global mutex (`executor.rs:619-655`, `:40`) | 13-15 ms |
| L3 `l1_projection_matches_in_transaction`: fresh 106-statement in-memory schema per file + 18 projection queries | 6-8 ms |
| ~7-10 fresh `open_coordinator`/`open_writer` per quantum, each a full pragma configure + verify | ~5 ms |
| Per-quantum store.db close-checkpoint ÷ 8 files | ~3.75 ms |
| L1 capability snapshot rebuild + 415-row write + verify per file | 1.5-2.5 ms |

Two cautions from the verification pass: the non-store extraction path uses ONE spool for a whole scan and
runs at 274 files/s, so the per-file spool is the biggest single item — but fixing it does **not** restore
`--jobs` scaling, because only the tree-sitter parse is parallel and the entire store-write loop from
`executor.rs:1657` is sequential. And do **not** measure `--jobs` scaling on the 149-byte synthetic files:
the parallel region is ~0 there, so files/s stays flat whether or not the fix worked. Also note `open_writer`
(`connection.rs:186`) never sets a prepared-statement cache capacity while `rows.rs` has 29 distinct
`prepare_cached` strings against rusqlite's default of 16 — a one-line fix.

## Machine provenance

Second Windows dev machine, brought up the same day. Setup gaps found and closed before triage: a
credential-less `nuget.telerik.com` source in the user NuGet config that 401'd every restore; no .NET 10 SDK
(6/8/9 plus an 11.0 preview — the preview also **silently ignored `--filter "Category!=Scale"`**, running
Scale tests inside the fast suite, which is exactly the split erosion CLAUDE.md calls load-bearing); no
embedding model (`miller semantic prepare` never run); git hooks not installed. After closing those, the fast
suite matched the reference machine test-for-test: 6,466 passed / 3 failed / 22 skipped.
