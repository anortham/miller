# Workspace Registry and Freshness Design

> Historical status: implemented/superseded design record. Current workspace behavior is summarized in
> [`../../README.md`](../../README.md), [`../../CLAUDE.md`](../../CLAUDE.md), and active CLI/MCP docs.

Status: **implemented historical design**. This spec records the agreed replacement direction for the pieces where Miller must do
better than Julie without recreating Julie's daemon and resource sink.

## Goal

Make Miller multi-workspace-aware without centralizing the heavy indices:

- keep each workspace's index local at `<workspace>/.miller/symbols.db`;
- add a small central registry at `~/.miller/workspaces.db`;
- let MCP read tools query another registered workspace by `workspace_id`;
- make explicit cross-workspace reads fresh by default;
- standardize file-content freshness on Julie's BLAKE3 hashes;
- keep dashboard support decoupled and read-only by default.

## Current Problems Verified

- **No central registry.** `workspace list` currently renders the current process workspace only, and the code says a
  multi-workspace registry is out of scope.
- **No cross-workspace reads.** Read tools use the process-local `IndexHolder` and do not accept `workspace_id`.
- **Startup can reuse stale DBs.** Bootstrap only scans when `.miller/symbols.db` is missing, then seeds
  `built_revision` from the existing DB. Files changed while Miller was down can be missed until a later watcher
  event or manual refresh.
- **Hash split-brain.** Julie stores BLAKE3 in `files.hash`; Miller's edit gate currently SHA-256 hashes indexed
  text and disk text itself. That gate is correct for its narrow comparison, but it bypasses the contract Julie
  already maintains.
- **Dashboard discovery is underspecified.** The MVP plan keeps a dashboard, but the current workspace model gives
  it no machine-wide list of local indices to query.

## Design Decisions

### D1: Registry is central metadata only

Add `~/.miller/workspaces.db` as a lightweight SQLite registry. It stores discovery and health metadata, not symbol
data.

Required row shape:

- `workspace_id`: stable full SHA-256 hex of the canonical root path.
- `display_id`: sanitized workspace name plus a short hash prefix, for humans only.
- `canonical_root`: symlink-resolved absolute root.
- `index_db_path`: expected local index path, normally `<canonical_root>/.miller/symbols.db`.
- `last_seen_at`: last time Miller registered or touched this workspace.
- `last_scan_at`: last successful Julie scan Miller initiated for this workspace.
- `last_revision`: latest observed `canonical_revisions` value for this workspace.
- `state`: `ready`, `missing`, `stale`, `refreshing`, or `error`.
- `last_error`: short diagnostic, nullable.

The full `workspace_id` is the durable key. `display_id` may collide; the registry must not.

### D2: Indices stay local

Do not move `symbols.db` into `~/.miller`. Local indices are smaller in Miller, cheap to rebuild, and naturally
travel with a worktree. The registry points at them.

This avoids Julie's old central-index gravity while still giving dashboards and MCP tools a discovery surface.

### D3: Registration happens on normal Miller operations

Miller updates the registry when:

- bootstrap starts for the current workspace;
- `workspace open(path)` primes another workspace;
- `workspace refresh` or `workspace full` succeeds;
- a cross-workspace `ensure_fresh` refresh succeeds or fails;
- `workspace remove(path)` removes a local index.

Registry writes are small and independent of symbol index writes.

### D4: Read tools accept `workspace_id`

Add optional `workspace_id` to read-only tools first:

- `search`
- `inspect`
- `context`
- `impact`
- `trace`

Default `workspace_id = null` means current process workspace. Non-null means resolve through the central registry.

`edit` stays current-workspace-only until cross-workspace mutation semantics are explicitly designed. Cross-workspace
editing would need target-file locking, stale-gate routing, write-through routing, and clearer user expectations.

### D5: Cross-workspace read routing uses a provider seam

Add a server-side resolver such as `IWorkspaceIndexProvider`.

Behavior:

- current workspace returns the live `IndexHolder` and resolver;
- another registered workspace loads its local `symbols.db` read-only;
- loaded cross-workspace indexes are cached by `(workspace_id, last_revision, index_db_path)`;
- cache entries are invalidated when `last_revision` changes or the DB disappears;
- telemetry rows are attributed to the target workspace, not the process workspace.

The provider is the single path all read tools use, so `workspace_id` support is consistent.

### D6: `ensure_fresh` defaults to true for explicit `workspace_id`

Tool behavior:

- current workspace: use the live freshness model already attached to the process;
- explicit `workspace_id`: default `ensure_fresh = true`;
- caller may pass `ensure_fresh = false` for fast, best-effort stale reads;
- compact output must report when results came from an unconfirmed or stale index.

Freshness is part of the read contract. A cross-workspace query should not silently explore an old index.

### D7: Cross-workspace refresh is lock-based, not daemon-based

When `ensure_fresh = true` targets another workspace:

1. Resolve the registry row.
2. Verify the root still exists and is not a sensitive root.
3. Try to acquire the target workspace's `.miller` writer lock.
4. If acquired, run Julie `extract scan` with `force: false` against the target local DB.
5. Update registry metadata from the scan report.
6. Load or reload the target index.
7. Execute the read.

If the lock is busy, another Miller process owns that workspace. Miller waits briefly, then reads the latest DB it can
see. The result must include a freshness warning if it cannot confirm a refresh. It must not run a second scan against
the same DB.

The default busy-lock wait is 2 seconds, polling every 100 ms for a visible revision change. After that, read tools may
serve the latest readable DB only with an explicit `freshness=unconfirmed_lock_busy` note. Mutating/admin refresh
operations return a lock-busy result instead of claiming success.

This gives project A the ability to refresh project B when no project-B Miller owns it, without rebuilding Julie's
always-on daemon model.

### D8: Startup runs a Julie delta scan when this process becomes leader

Bootstrap may still load an existing DB quickly, but the leader must run a startup delta scan before claiming the
workspace is fresh.

Accepted behavior:

- missing DB: scan before first load, as today;
- existing DB: load it for fast startup, then leader runs Julie `extract scan(force: false)`;
- after scan, `FreshnessService` or an explicit reload swaps in the updated index;
- status should distinguish `loaded_existing` from `fresh_after_startup_scan`.

Julie owns hash-delta reconciliation. Miller should not pre-filter changed files by mtime.

### D9: BLAKE3 is the file-content freshness hash

Use Julie's `files.hash` as the file-content authority. Julie already computes BLAKE3 over raw file bytes and stores
that digest in the extract DB.

Miller rules:

- use BLAKE3 for any high-volume or DB-facing file freshness check;
- read current disk bytes when hashing, not decoded text, to avoid encoding drift;
- compare against `files.hash`;
- treat the hash algorithm as explicit contract metadata;
- keep SHA-256 for low-volume privacy hashes and stable workspace IDs.

The existing SHA-256 edit gate can remain until the BLAKE3 contract and dependency are wired, but the target design is
to compare current BLAKE3 bytes to Julie's stored BLAKE3.

### D10: Julie contract improvements are allowed

Because Julie is owned by this project, Miller should not contort around awkward extract gaps.

Julie should add or guarantee:

- `external_extract_metadata.hash_algorithm = blake3`;
- documentation that `files.hash` is BLAKE3 over raw file bytes;
- scan report fields sufficient for Miller registry updates: `workspace_id`, `revision`, changed counts, deleted
  counts, root, DB path, and status;
- stable no-op semantics: unchanged delta scans do not pretend a revision changed;
- enough error detail for Miller to store a useful `last_error` without scraping human text.

No Julie daemon IPC is required for this design.

### D11: Dashboard is a registry reader

The dashboard reads:

- `~/.miller/workspaces.db` for workspace discovery and status;
- `~/.miller/telemetry.db` for usage and latency;
- each local `<workspace>/.miller/symbols.db` read-only only when it needs index facts.

It should not own refresh loops in v1. Refresh buttons can call the same lock-based refresh path as the MCP
`workspace` tool.

### D12: Search stays hybrid, not embedding/Tantivy-first

Keep in-memory BM25 as the primary symbol search engine. It is already deterministic, small, and tested.

Add SQLite FTS5 only where symbol BM25 is the wrong data model:

- file/content search over `files.content`;
- large prose/log/ad-hoc content under the separate M9 file-tool design;
- snippets/body text where symbol names and signatures are not enough.

Do not replace the existing symbol index with FTS5. Do not add embeddings or Tantivy to Miller's default path.

This registry/freshness spec does not require implementing FTS5. It records the search architecture boundary so the
workspace work does not accidentally degrade symbol search while adding cross-workspace routing.

## API Shape

### Read tools

Add parameters:

- `workspace_id?: string`
- `ensure_fresh?: bool`

Defaulting:

- if `workspace_id` is null: `ensure_fresh` defaults to false or ignored, because current-workspace freshness is
  managed by the live services;
- if `workspace_id` is non-null: `ensure_fresh` defaults to true.

Compact output should include:

- target workspace display/root when cross-workspace;
- freshness state if not confirmed fresh;
- clear missing-workspace or missing-index messages.

### Workspace tool

Update operations:

- `list`: list registry rows, marking current workspace;
- `status`: current workspace by default, optional target by `workspace_id` or `path`;
- `open(path)`: prime and register another workspace;
- `refresh`: current workspace by default, optional target by `workspace_id` or `path`;
- `full`: current workspace by default, optional target by `workspace_id` or `path`;
- `remove`: unregister and optionally remove local index, refusing unsafe paths and live in-use deletion.

`refresh` and `full` must respect the same writer-lock rule as `ensure_fresh`.

## Error Handling

- Missing registry row: return a typed "unknown workspace" message and suggest `workspace open(path)`.
- Missing root: mark registry row `missing`; do not delete automatically.
- Missing DB: run delta scan if lock acquired; otherwise report unconfirmed/missing index.
- Busy lock: wait briefly, then read latest DB with a warning if allowed by the operation; never double-write.
- Julie scan failure: keep prior index, update registry `state=error`, surface the short error.
- Schema/contract mismatch: refuse to load and point at the pinned Julie restore/update path.

## Testing Strategy

Fast suite:

- stable workspace ID generation from canonical roots;
- registry insert/update/list/remove behavior;
- provider routing for current vs registered workspace;
- `ensure_fresh` defaulting rules;
- busy-lock decision behavior;
- freshness render warnings;
- telemetry target attribution;
- BLAKE3-vs-stored-hash freshness checks with tiny byte fixtures;
- search mode routing keeps symbol BM25 ordering unchanged.

Scale suite:

- live Julie scan registers a workspace;
- startup with changed-on-disk file runs delta scan and converges;
- project A refreshes project B when B is not locked;
- project A does not scan B when B's lock is held;
- cross-workspace `search`/`inspect` read another real extracted repo;
- Julie contract includes `hash_algorithm = blake3`.

Verification commands follow the repo rules:

- `scripts/test.sh` for the fast suite;
- `scripts/test.sh scale` when touching Julie extract, startup freshness, or live cross-workspace refresh paths;
- `dotnet build Miller.slnx -c Release` before handoff.

## Acceptance Criteria

- [ ] `workspace list` shows every registered workspace from `~/.miller/workspaces.db`.
- [ ] Each registered workspace keeps its symbols DB under its own root.
- [ ] `search`, `inspect`, `context`, `impact`, and `trace` accept `workspace_id`.
- [ ] Explicit cross-workspace reads default to `ensure_fresh=true`.
- [ ] Project A can refresh project B when B is not locked.
- [ ] Project A does not double-write project B when B is locked.
- [ ] Startup detects files changed while Miller was down by running a Julie delta scan.
- [ ] File freshness uses Julie BLAKE3 hashes for DB-facing checks.
- [ ] SHA-256 remains only for workspace IDs, telemetry target privacy hashes, and release asset checksums.
- [ ] Dashboard can discover workspaces without scanning the filesystem.
- [ ] Symbol search quality does not regress from the current in-memory BM25 tests.
- [ ] This workspace/freshness work does not add FTS5 to symbol search.
- [ ] Any later FTS5 work is scoped to content search and does not replace symbol BM25.

## Explicit Non-Goals

- Recreate Julie's daemon.
- Centralize symbol indices under `~/.miller`.
- Add embeddings or Tantivy to the default path.
- Cross-workspace editing.
- Dashboard-owned background refresh loops.
- Live IPC between Miller processes in v1.

## Confidence

Confidence: **90/100**.

The main remaining risk is operational polish around lock-busy freshness, but the v1 behavior is now concrete:
2-second wait, 100 ms polling, warning for reads, lock-busy result for admin refresh.
