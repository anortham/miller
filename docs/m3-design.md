# M3 — Freshness: single-writer indexer + file watcher + mutation gate

Implementation spec for M3. Every contract below is **verified against the live pinned `julie-server` v7.12.2
(schema 26 / contract 1)** by running `extract scan|update|delete` and probing the DB — not read from julie
source (which is ahead at schema 27). Grounded; no placeholders. Corpus: `docs/findings/julie-eros-audit.md`
§2–§3, `julie-contract-verified.md`, `miller-toolbox.md`, the M3 row of `miller-mvp-plan.md`, plus a 6-agent
research sweep over julie + eros.

## Goal
The index stays fresh automatically as files change, and edits are safe — across an **unknown number of
concurrent Miller reader processes** (it's a product, not a personal tool). External edit → the index converges;
missed-event/burst self-heals; a stale-target gate primitive is ready for M6 `edit`; N readers + 1 writer share
the WAL DB without corruption. **Exit: fresh-index correctness the editing tools can rely on.**

## The seam
- **Miller.Core** gains the **pure** freshness logic (event-coalescing queue, event→op routing, staleness check)
  — unit-tested with zero FileSystemWatcher / SQLite / subprocess.
- **Miller.Indexing** gains the infra: `extract update/delete`, the `IndexHolder` (atomic swap), the
  `FreshnessReader` (revision poll), path canonicalization, the cross-process single-writer lock.
- **Miller.Server** gains the hosted services (the leader-gated watcher; the freshness poller that rebuilds +
  swaps), repoints the tools at the holder, and populates `index_fresh`.
- Default test suite stays **< 10s**; anything needing the live binary or real FS watching is `[Trait("Category","Scale")]`.

---

## Verified facts (live probe, pinned v7.12.2 / schema 26)
1. **`canonical_revisions` EXISTS in schema 26.** Cols: `revision INTEGER PK`, `workspace_id TEXT`, `kind TEXT`
   (`fresh|incremental`), `*_count`, `created_at`. The freshness cursor is real on the pinned binary.
2. **`extract update --file` (changed) → `status=changed`, `files_updated=1`, `revision` bumps** (1→2). A **no-op
   update** (content hash unchanged) → `status=unchanged`, `files_updated=0`, **revision does NOT bump.** So a
   reader only rebuilds when data actually changed.
3. **`extract delete --file` (file removed, canonical paths) → `status=deleted`, `files_deleted=1`, revision
   bumps, the file's symbols are gone.** A second delete → `status=not_found`, `files_deleted=0` (idempotent).
4. **PATH GOTCHA (verified):** `delete` lexically normalizes `--file` (no symlink resolve) but the root is
   canonicalized. On macOS (`/var` → `/private/var`) a non-canonical `--file` under a symlinked root fails with
   `external_extract_error: "... is outside external extract root ..."` (exit 1). **Fix: Miller passes
   symlink-resolved canonical absolute paths for BOTH `--root` and `--file`, always.** Watch the canonical root
   so FileSystemWatcher events are already canonical.
5. **`revision_file_changes(revision, change_kind, file_path)` exists** — `change_kind ∈ {added, modified,
   deleted}`, one row per changed file per revision. The exact changed-file delta (gold for incremental + M4).
6. **`external_extract_metadata`** carries `workspace_id`, `updated_at`, `analyzed_revision`, `root_path` — the
   `workspace_id` for the poll's WHERE clause, read once at startup.
7. **Report JSON** returns `revision`, `files_updated`, `files_deleted`, `workspace_id`, `status` — the fields
   Miller's `ExtractReport` DTO must gain to branch on outcomes.
8. **WAL = 1 writer + N readers is safe** (authoritative SQLite docs; julie sets `journal_mode=WAL`,
   `busy_timeout=5000`, `synchronous=NORMAL`, `wal_autocheckpoint=2000`, TRUNCATE-on-close). A long-lived
   `Mode=ReadOnly` reader with **no lingering transaction** sees the writer's commits on its next command — poll,
   never reopen. Same host + local FS only (the `-shm` wal-index is shared memory).

---

## Decision log (resolved — do not re-litigate)
1. **Topology = leader-elected in-process watcher.** Each `miller` tries to acquire a cross-process writer lock
   for the workspace; the holder (the *leader*) runs the watcher and shells `extract`; all other instances are
   pure readers. Every instance (leader included) polls `canonical_revisions` and hot-swaps its own in-memory
   index. The **actual DB writer is the `julie-server` subprocess** (separate, optional process — its absence
   degrades freshness, not reads); the leader merely orchestrates it. Leader death → another instance acquires
   the lock and takes over. Chosen over (a) a separate `miller-indexer` binary (extra binary + lifecycle to ship)
   and (b) uncoordinated per-server watchers (N windows = N redundant watchers thrashing the flock). **User
   decision, 2026-05-29.**
2. **Freshness propagation = poll `canonical_revisions`.** Reader holds ONE long-lived `Mode=ReadOnly`
   connection with no open explicit transaction; on an interval (and after its own writes) runs
   `SELECT MAX(revision) FROM canonical_revisions WHERE workspace_id=@id`. When it exceeds the holder's
   `BuiltRevision`, rebuild + swap. No IPC, no daemon in the read path, no reopen.
3. **Miller never computes file hashes — julie owns hashing.** Watcher events → `extract update/delete --file`
   (julie blake3-checks and no-ops if unchanged). Overflow / `.git/HEAD` change / startup → `extract scan`
   (julie's own whole-repo hash-delta reconcile). Miller's only self-computed signal is cheap `mtime` as a "maybe
   changed" pre-filter. This sidesteps the blake3-parity risk (line-ending / normalization) entirely.
4. **Reconcile-on-overflow is DELEGATED to `extract scan`, not ported.** `extract scan` *is* julie's
   hash-reconcile (adds + modifies + orphan deletes in one revision). Miller ports only the **watcher +
   coalesce + overflow-detection + event→op routing**, which are pure logic in Core.
5. **Refresh = full rebuild + atomic swap behind `IndexHolder`** (a `volatile` reference to the immutable
   `MillerRepositoryIndex`). A reference swap of a frozen index is lock-free and torn-state-free; in-flight reads
   keep their snapshot. This **structurally satisfies the symbol-ID-churn rule** (§2): the whole resolved index
   is replaced, so no stale link keyed on a churned ID survives. **Incremental update is deferred as a
   measured-latency-gated optimization** — a Scale test measures rebuild time on a large fixture; if a coalesced
   single-save rebuild exceeds budget, the decision is escalated *with numbers* (incremental would break the
   frozen/dense-DocId invariant, so it is not taken speculatively). `revision_file_changes` is the delta it would
   consume if/when justified.
6. **Mutation-gate primitive (M3 scope) = pure `StalenessCheck` (Core) + cross-process `SingleWriterLock`
   (Indexing).** The staleness check compares content-hash + exact-text (per eros; `mtime` is only a cheap
   pre-filter, never the authority). The full edit-apply machinery (TOCTOU re-check under lock, atomic
   temp→replace, reverse-order rollback, `preview_id`) is **M6 `edit`** — M3 ships only the primitive M6 calls.
7. **`.git/HEAD` watch = a Miller addition** (neither julie nor eros do it). On `.git/HEAD` change, force one
   `extract scan` reconcile instead of drowning in the per-file event storm a checkout produces.
8. **`index_fresh` = coarse boolean** `BuiltRevision == latestRevision && queue empty`. Cheap, no hot-path I/O.
   It populates the column/field/scope/binding that already exist (left null in M2).
9. **Single-writer is double-guarded.** Miller's leader lock prevents N watchers; julie's
   `<db>.julie-extract.lock` 30s flock is the cross-process backstop even if two writers ever race. A flock
   timeout surfaces as "another writer in progress — coalesce/retry", never fatal.
10. **Error handling is outcome-aware.** `extract` exit 1 carries `errors[].code` + message. Distinguish:
    data-loss guard (empty re-parse of a previously-populated file) and flock timeout → **keep the prior index,
    flag a repair, retry later**; usage / outside-root / operator errors → surface loudly. Never treat a
    transient parser failure as "wipe the file."

---

## Components

### 1. Pure freshness logic (Miller.Core — zero I/O, the unit-test target)
- **`WatchEvent`** — `record WatchEvent(string Path, WatchEventKind Kind)` where `Kind ∈ {Created, Modified,
  Deleted, Renamed}` (renamed carries an old path too).
- **`WatchEventQueue`** — per-path coalescing queue with julie's merge state machine: `Created+Modified→Created`,
  `Deleted+Created→Modified`, `Created+Deleted→Deleted`, `Modified+Modified→Modified`, etc. Bounded (`MaxQueue`,
  default 1000); on overflow drain to `OverflowTarget` (750) and set `NeedsRescan`. Pure — feed events, assert
  the coalesced drain. The merge function is a pure static.
- **`WatchEventRouter`** — given the drained events + an injected `Func<string,bool> exists` (stat) + a
  `NeedsRescan`/HEAD-changed flag, produce an ordered `IReadOnlyList<ExtractOp>` where
  `ExtractOp ∈ {Update(path), Delete(path), Scan}`. Routing: `NeedsRescan` or HEAD → a single `Scan` (drop the
  rest); else `Created/Modified` with `exists==true` → `Update`; `Deleted` or `exists==false` → `Delete`;
  `Renamed` → `Delete(old)` + `Update(new)`. Pure (stat injected).
- **`StalenessCheck`** — the mutation-gate primitive: `Check(Snapshot indexed, Probe current) → Fresh | Stale`
  where the snapshot is `(indexedHash, indexedExactText?)` and the probe is `(currentHash, currentText?)`. Stale
  iff `currentHash != indexedHash` OR (exact text supplied and differs). `mtime` is NOT consulted here (a caller
  may use mtime upstream to decide whether to even read the file). Pure, no FS.

### 2. Read-layer / infra extensions (Miller.Indexing)
- **`JulieExtractRunner`** += `BuildUpdateArgs(absDb, absRoot, absFile)` →
  `extract --db <db> --root <root> --json update --file <file>` and `BuildDeleteArgs(...)` (same with `delete`),
  plus `Update(root, db, file)` / `Delete(root, db, file)` routing through the existing `Run()` →
  `Interpret()` → `VerifyReport()` (exit 0/1/2 contract is identical to `scan`; no new exception types). The two
  arg-builders are static seams, contract-tested with no spawn. **All paths passed are canonical** (see §4).
- **`ExtractReport`** += `Revision`, `FilesUpdated`, `FilesDeleted`, `WorkspaceId` (already in the JSON). The
  runner branches on `status`/counters: `changed`/`scanned` → swap-pending; `unchanged`/`not_found` → no-op;
  `failed` → outcome-aware error (decision-10).
- **`PathCanonicalizer`** — resolves an absolute, **symlink-resolved** path (root + per-file) so `delete` never
  trips the outside-root trap (verified-fact 4). The workspace root is canonicalized once at startup; file paths
  are canonicalized (or composed under the canonical root) before every `extract` call.
- **`FreshnessReader`** — owns the long-lived `Mode=ReadOnly` connection; `LatestRevision(workspaceId)` =
  `SELECT MAX(revision) FROM canonical_revisions WHERE workspace_id=@id` (returns 0/none when absent);
  `ChangedSince(rev, workspaceId)` reads `revision_file_changes` (for future incremental / M4). Hard rule: never
  leave an explicit transaction or undisposed reader open between polls (it would pin a stale snapshot).
- **`IndexHolder`** — `volatile MillerRepositoryIndex _current` + `long BuiltRevision`; `Current` (per-call read),
  `Swap(next, revision)` (atomic publish). The single seam tools depend on so the index can be replaced behind
  live readers.
- **`SingleWriterLock`** — cross-process exclusive lock on `<.miller>/indexer.lock` (a `FileStream` with
  `FileShare.None`, retried). `TryAcquire()` → leadership; released on dispose/process exit. This is the leader
  election; julie's own flock remains the lower-level backstop.

### 3. Hosted services + wiring (Miller.Server)
- **`IndexerService : BackgroundService`** (leader-gated). On start: `SingleWriterLock.TryAcquire()`. If leader:
  attach a `FileSystemWatcher` on the **canonical** workspace root (`IncludeSubdirectories=true`, filtered like
  julie — skip `.git`, build dirs, etc.) + a watch on `.git/HEAD`; on the FSW `Error` event (InternalBuffer
  overflow) set `NeedsRescan`. Events feed the `WatchEventQueue`; a debounce timer (~1s, julie's tick) drains →
  `WatchEventRouter` → for each `ExtractOp` call `JulieExtractRunner.Update/Delete/Scan` (canonical paths,
  serialized — one in-flight subprocess). If not leader: idle (a periodic re-`TryAcquire` enables failover).
- **`FreshnessService : BackgroundService`** (ALL instances). Polls `FreshnessReader.LatestRevision` on an
  interval (and is poked right after the leader's own successful `extract`); when it exceeds
  `holder.BuiltRevision`, rebuild `MillerRepositoryIndex` from a fresh `SqliteSymbolReader.Read` and
  `holder.Swap(next, latest)`. This is how reader instances pick up the leader's writes.
- **Tools repoint to the holder.** `SearchTool`, `InspectTool`, `SmartTargetResolver` depend on `IndexHolder` and
  read `holder.Current` per call instead of capturing a fixed index. (DI: register `IndexHolder` as the
  singleton; the bootstrap sets its initial value.)
- **`index_fresh` population.** The telemetry filter (or a tool with a touched-file context) sets
  `TelemetryScope.IndexFresh = holder.BuiltRevision == freshness.LatestRevision(wsId) && queueEmpty`. Cheap.
- **`WorkspaceContext`** += canonical root + `WorkspaceId` (read from `external_extract_metadata` after the
  initial scan). `IndexBootstrapService` sets `holder.BuiltRevision` to the scan's revision.

---

## Test strategy
**Default suite (< 10s, no julie-server binary, no real watcher):**
- `WatchEventQueueTests`: every merge transition (Created+Modified→Created, Deleted+Created→Modified,
  Created+Deleted→Deleted, idempotent Modified), bounded cap, overflow drains to target + sets `NeedsRescan`.
- `WatchEventRouterTests`: Created/Modified+exists→Update; Deleted/!exists→Delete; Renamed→Delete+Update;
  `NeedsRescan`→single Scan (others dropped); HEAD-changed→Scan. `exists` injected (pure).
- `StalenessCheckTests`: equal hash→Fresh; differing hash→Stale; equal hash but differing exact text→Stale;
  hash-only mode (no text)→hash decides; the contract that mtime is NOT consulted here.
- `IndexHolderTests`: `Current` returns the swapped instance after `Swap`; `BuiltRevision` tracked; a reader
  holding an old `Current` reference still sees a consistent (old) snapshot after a swap (no torn state).
- `JulieExtractRunnerUpdateDeleteTests`: `BuildUpdateArgs`/`BuildDeleteArgs` argv pinned exactly (static, no
  spawn); `Interpret` reused for update/delete (0→report, 1→failed-with-errors, 2→usage). Canonical-path
  assertion (the builder receives already-canonical paths).
- `ExtractReportParsingTests`: `revision`/`files_updated`/`files_deleted`/`workspace_id` parsed from a
  `changed`/`unchanged`/`deleted`/`not_found`/`failed` report JSON; outcome mapping.
- `PathCanonicalizerTests` (POSIX): a temp dir reached via a symlink resolves to its real path for both root and
  a child file; idempotent on an already-canonical path. (Pins the verified-fact-4 fix.)
- `FreshnessReaderTests` (synthesized DB with `canonical_revisions` + `revision_file_changes` rows):
  `LatestRevision` = MAX by workspace_id; unknown/absent workspace_id → none; `ChangedSince` returns the delta;
  the "no lingering transaction → next poll sees a second connection's committed insert" contract (simulate the
  writer with a second connection on a throwaway WAL DB).
- `SingleWriterLockTests`: first `TryAcquire` wins; a second (same process, different handle) is refused while
  held; released on dispose → re-acquirable. (Cross-process variant is Scale.)
- `IndexFreshTests`: the boolean = built==latest && queue empty across the truth table.

**Scale suite (`[Trait("Category","Scale")]`, excluded by default):**
- `LiveFreshnessTests`: restore julie-server → scan a tiny throwaway repo → build + hold index → modify a file →
  drain/route → `extract update` → revision bumps → `FreshnessService` rebuilds + swaps → `search` sees the new
  symbol; delete a file → its symbol disappears; touch `.git/HEAD` → a `scan` reconcile runs. End-to-end.
- `MultiProcessWalTests`: one writer doing `extract update` while N `Mode=ReadOnly` reader connections poll +
  read concurrently → no corruption, readers observe the bump (validates the spec's WAL requirement).
- `RebuildLatencyTests`: measure `MillerRepositoryIndex.Build` on a large fixture (record ms); asserts a budget
  and **prints the number** so decision-5 (incremental?) is data-driven, not guessed.

Banned-test discipline (CLAUDE.md): assert on values, cover overflow/rename/NULL/failed/idempotent paths,
parameterize, no smoke-only/tautological tests, keep the existing 200 tests green, default suite < 10s.

## Implementation order (strict TDD)
1. `PathCanonicalizer` (the gotcha first) → red→green.
2. `ExtractReport` DTO fields + `JulieExtractRunner.Update/Delete` (+ arg-builders) → red→green.
3. `WatchEvent` + `WatchEventQueue` (coalesce/overflow) — Core → red→green.
4. `WatchEventRouter` (routing) — Core → red→green.
5. `FreshnessReader` (revision poll + changed-since) → red→green.
6. `IndexHolder` (atomic swap) → red→green.
7. `StalenessCheck` (mutation-gate primitive) — Core → red→green.
8. `SingleWriterLock` (leader election) → red→green.
9. `IndexerService` (leader-gated watcher) + `FreshnessService` (poll→rebuild→swap).
10. Repoint `SearchTool`/`InspectTool`/`SmartTargetResolver` to `IndexHolder`; populate `index_fresh`.
11. `WorkspaceContext`/`IndexBootstrapService`/`Program.cs` DI wiring.
12. Scale tests (live freshness + multi-process WAL + rebuild latency).

**Verify:** `dotnet build Miller.slnx -c Debug` → 0/0 (warnings-as-errors). `dotnet test --filter "Category!=Scale"`
→ all green (existing 200 + new), < 10s. Then the live Scale path.

**Exit:** an external edit converges the index automatically; every reader instance picks it up via the revision
poll; a missed-event/branch-switch burst self-heals through `extract scan`; the `StalenessCheck` + `SingleWriterLock`
primitives are ready for M6; N readers + 1 writer share the WAL DB without corruption.
