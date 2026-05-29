# julie + eros source audit (2026-05-29)

Grounding pass before Miller implementation. Four parallel agents read the actual code in
`~/source/julie` (Rust) and `~/source/eros` (Python). Every claim below is from source, file:line cited
in the agent transcripts. Two findings overturn prior assumptions (marked 🔄). Confidence ~88.

Names settled: **Miller** = the .NET personal/local tier (julie-replacement). **Eros** = the future
commercial/team tier (greenfield, not a port). See [[three-project-landscape]].

---

## 1. Extract contract — single-file incremental IS first-class ✅

The user's belief was correct, now verified. `julie-server extract` (module `src/external_extract/`) has
five sub-subcommands (`cli.rs:97-113`):

| subcommand | scope | behavior |
|---|---|---|
| `scan {--force}` | whole repo | delta by default (hashes every file, skips unchanged, deletes orphans); `--force` = full rebuild |
| `update --file X` | **single file** | hash-gated upsert of one file into existing DB |
| `delete --file X` | **single file** | remove one file's rows |
| `analyze` | — | post-process pass |
| `info` | — | report DB state |

- `update` is genuinely incremental: skips if file hash unchanged (`operations.rs:153`), converts to delete
  if the file became ignored, writes via `persist_single_file_replace` (delete-rows-by-path then insert).
- Writes are **upserts into the existing DB**, scoped per-file by delete-then-insert. Only `scan --force`
  recreates. Symbol insert uses `ON CONFLICT(id) DO UPDATE` (`helpers.rs:28-64`).
- Delete cascade is **manual** (`bulk/cleanup.rs:14-52`) in dependency order — FKs are turned OFF during
  writes (`atomic.rs:91`), so the schema's `ON DELETE` clauses are documentation, not the mechanism. A pure
  reader (Miller) doesn't care; only matters if Miller ever writes the DB itself.
- Data-loss guard: refuses a write if a previously-populated file now extracts zero symbols
  (`data_loss_guard.rs`) — parser-regression protection.

**Consequence for Miller's freshness design:** no full re-scan per edit. On a file change, shell
`extract update --file`; on delete, `extract delete --file`. Cheap, atomic, hash-gated.

## 2. Symbol IDs are span-derived, NOT stable across edits 🔄 (gotcha)

ID = `md5(relative_unix_path : name : start_line : start_col : end_line : end_col : start_byte : end_byte)`
(`crates/julie-extractors/src/base/types.rs:258-271`). Path is workspace-relative unix-style, so IDs are
stable across machines/abs-paths given a consistent `--root`.

- Re-extract of an **unchanged** file → identical IDs (deterministic, no timestamp/autoincrement).
- **Any edit that shifts byte offsets rewrites the IDs of every symbol below the edit point**, even textually
  identical ones, because byte/line offsets are in the hash. Span identity, not content identity.
- `identifiers.target_symbol_id` is written **NULL** by the extractor — resolution is explicitly left to the
  consumer ("will be resolved on-demand in C#", `creation_methods.rs:120`). That's our resolver's job, as planned.

**Consequence:** symbol IDs are valid only within a given extract revision. The cross-reference resolver must
**re-resolve after any file update**; do NOT persist resolved cross-file links (or memories) keyed on symbol ID
across edits without a re-resolve pass. Bake this into the resolver + any caching/memory design.

## 3. 🔄 The daemon exists for Tantivy, not concurrency — and Miller has no Tantivy

The single highest-leverage finding. julie's daemon is ~8,200 LOC (`src/daemon/`, 27 files) + ~3,000 (watcher)
+ ~1,200 (adapter), scarred with documented past bugs ("the 577-daemon cascade", "Fix A–F"). Topology:
MCP client → thin stdio `julie-adapter` → **localhost HTTP** → `julie-daemon` (port 7890, bearer token in
`~/.julie/daemon.token`). The adapter holds NO DB handle; **all reads forward to the daemon.** So the read path
is fully daemon-dependent.

Why it must be: the daemon owns two single-writer-process resources —
1. **Tantivy** search index (exclusive directory writer lock, `src/search/index.rs:521`), and
2. per-workspace SQLite pools + in-memory state.

The smoking gun (`src/daemon/legacy_migration.rs:5-7`): *"Both legacy and new daemons read/write the same
workspace SQLite + Tantivy index files; if both run concurrently, those indexes corrupt silently."*

**But SQLite is already WAL + 5s busy_timeout** (`src/database/mod.rs:122-145`) = concurrent readers + single
writer, no corruption. So **SQLite alone already permits direct multi-process reads.** The ONLY thing forcing
everything through one daemon is Tantivy's process-exclusive writer lock.

Miller's architecture already dropped Tantivy (pure-.NET lexical: in-memory inverted index rebuilt from SQLite
<1s, or FTS5 — see [[architecture-decision]]). **Therefore the entire daemon apparatus evaporates:** the three
lock files (`daemon.singleton.lock`, `daemon.lock`, `daemon-startup.lock`), the 682-line PID-file state machine
(`pid.rs`, Alive/Dead/Indeterminate + NTP-skew + PID-reuse defense), the 343-line spawn cascade
(`launcher.rs`), the hand-rolled Windows named-event shutdown (no SIGTERM on Windows), the stale-binary
detection that can't fire on Windows (image-section lock) — all of it is Tantivy-workaround scaffolding.

### Windows pain catalogued (all avoidable in .NET)
- No graceful stop signal → hand-rolled Win32 named event (`shutdown_event.rs`).
- Aggressive PID reuse → creation-time field in pid + discovery files, raw `OpenProcess`/`GetProcessTimes` FFI.
- `ACCESS_DENIED` on recycled privileged PIDs → whole `Indeterminate` branch.
- In-place rebuild impossible while running (image-section lock).
- Token-file ACL = ~110 lines of `windows-sys` SID/DACL FFI vs one POSIX `0600`.
- NTFS case-insensitivity, OneDrive mass-download footgun, watcher-handle drop ordering.

### Refined Miller process model (evidence-backed)
```
┌─ Indexer service  (ONE per machine; the only writer; NOT in the read path)
│    • file watcher → shell `julie-server extract update/delete`
│    • optional: dashboard host (Kestrel), warm embed model if semantic ever added
│    • if it dies → reads still work against last-indexed SQLite (graceful degrade)
│
├─ MCP server instances  (one per agent/harness/worktree)
│    • open SQLite read-only (WAL) + build own in-memory inverted index (<1s, ~35MB)
│    • self-sufficient for reads; no IPC, no named pipes, no daemon dependency
│    • write-through on its own edits via extract update (+ mutation gate, §6)
│
└─ Concurrency primitive = SQLite WAL (multi-reader + single writer). NOT a custom daemon.
```
Worktrees: julie keys workspace identity = SHA256(canonical path) → separate index dir per worktree
(`registry.rs:308`). Same approach works; each worktree = its own SQLite, no collision.

### KEEP from julie's watcher (the good part)
`notify` crate, 1s tick, per-path coalescing state machine, queue cap 1000. Crucially it **reconciles against
truth, not the event stream**: on overflow/missed-events it runs a full rescan that hashes every file and
compares to stored hashes (`runtime.rs:726-934`) — this is why julie survives `git checkout`. Port this idea:
trust events on the fast path, reconcile by hash on overflow/startup. Per-platform rename handling
(inotify Both / Windows split From-To / macOS FSEvents existence-probe) is also worth copying.

## 4. Test suite — root cause confirmed: no seam, no CI gate

~4,811 test functions across ~687 files (4,294 `#[test]` + 517 `#[tokio::test]`). All inline `#[cfg(test)]`;
the conventional `tests/` dir is empty. Two populations:

- **Fast (the model to copy):** the extractor crate ~1,955 fns are pure tree-sitter parse over **inline source
  strings**, no DB. Only 4 files in the whole crate touch SQLite.
- **Slow (~60%):** `src/tests` ~2,300 fns run against live components — 103 files spin a real
  `JulieServerHandler`, 89 drive the full `call_tool` MCP path, 150 create real SQLite, 239 use on-disk
  tempdirs, 113 reference embeddings. The canonical fixture (`InProcessDaemon`) **binds a real 127.0.0.1 TCP
  listener + writes token files per test.** One dogfood test **indexes the entire julie repo (~164s)** to
  assert one body-inclusion detail; search-quality suite ~151-180s.

Their own `xtask/test_tiers.toml`: 45 buckets, **~26 min expected sequential / ~75 min timeout budget**, 77
`#[serial]` markers (shared global state defeats parallelism), 43 `#[ignore]`. **There is no test CI** —
`.github/workflows` has only release + pages. A suite too slow to gate merges, so it rots. That is the
project-threatening mechanism, confirmed.

**Anti-patterns Miller must NOT inherit:**
1. Real-stack-to-assert-a-unit-fact (164s repo-index for a body check). → in-memory unit test over tiny fixture.
2. Real TCP daemon per fixture → ASP.NET `WebApplicationFactory`/`TestServer` (in-memory transport).
3. On-disk SQLite per test → `:memory:` or store-behind-interface + fake.
4. 77 serialized tests from shared global state → per-test DI scope, everything parallel.
5. 106 MB fixtures + repo self-indexing → tiny purpose-built fixtures; full-corpus runs in a separate opt-in bench.
6. No CI gate → keep default `dotnet test` (unit+contract) < ~10s and FAIL CI on regression.

The decisive design rule: **a hard seam between logic (extractor output → resolver → result) and
infrastructure (DB, watcher, transport)**, so the differentiator is unit-testable with no live components.
This architecture is inherently more testable than julie's, which is what keeps TDD — and the project — alive.

## 5. MCP tool surface — richer than our memory recorded (12 tools, incl. editing)

Memory said "6 tools (search/index/memory/navigate/relationships/impact)". julie now exposes **12**
(`src/handler/tools/`): `fast_search`, `fast_refs`, `get_symbols`, `get_context` (token-budgeted subgraph),
`deep_dive` (one-symbol progressive depth), `call_path` (shortest call-graph path A→B), `blast_radius`
(deterministic impact), `spillover_get` (paging), `edit_file` (fuzzy edit, no read-first), `rename_symbol`
(index-aware workspace rename), `rewrite_symbol`, `manage_workspace`. Note the **index-aware editing tools** —
a real capability surface, not just retrieval. eros's surface is a cleaner 7: `workspace, search_code,
inspect_code, build_context, trace_code, assess_change, modify_code`.

## 6. eros is NOT a commercial product — it's a richer LOCAL tool 🔄 (reframes the split)

The prior framing ("eros = the commercial attempt") was about *intent*; the *code* is explicitly
single-developer, single-machine, **local-only**. `pyproject.toml:8`: "Local developer-machine code
intelligence hub." Verified absent: multi-tenancy (no tenant/org/user columns), auth (the token only guards
`/shutdown` + dashboard CSRF; all functional routes unauthenticated, security = the 127.0.0.1 bind), hosted
mode (no `0.0.0.0`/CORS), teams, billing, admin/user endpoints. So **there is no commercial code to split off —
the hosted tier is entirely greenfield.** This is consistent with the user's history: he has built the *local*
engine three times (coa-codesearch, julie, eros) and the commercial layer never materialized ("rudderless
ship"). Commercial is genuinely new territory.

Architecture: FastAPI hub on `127.0.0.1:8765` (autostart via file-locked launcher) + thin stdio MCP forwarder
+ Typer CLI — same adapter/hub shape as julie. Extractors are julie's, via PyO3 (71 MB `.so`). Embeddings
**in-process** (not a sidecar like julie), default `LANCEDB_HYBRID_CODERANK` (CodeRank 768d) but **opt-in** —
torch/sentence-transformers/lancedb are optional extras; the shipped baseline falls back to **SQLite FTS**
(`retrieval/sqlite_fallback.py`). Confirms the lexical-first / semantic-opt-in instinct, and re-confirms the
Python pain: heavy ML extras (108 resolved packages), upper-bound version juggling on every dep, a maturin/PyO3
Rust+Python dual toolchain, cpython 3.12/3.13/3.14 churn, and a 353-line `autostart.py` for cross-platform
file-locking/pid/discovery.

### eros = the LOCAL feature spec for Miller (net-new over julie, all local, worth inheriting)
- **Mutation gate / freshness** (`freshness/*`): **blocks edits when the index is stale.** This is the clean
  answer to "editing tools rely on a fresh index" — correctness via a gate, not by trusting the watcher. Steal it.
- **Token-budgeted `build_context`** (`context/planner.py`) with explicit dropped-item provenance.
- **Test-confidence pipeline** (`testing/*`, 12 modules): import JUnit + lcov coverage, link tests↔symbols,
  surface pre-edit confidence in `assess_change`. High-value, agent-facing.
- **Dependency intelligence** (`dependencies/*`): import OSV/CVE advisories, match to a dep inventory.
- **Security findings / context packs** (`security/*`).
- **Telemetry ledger + performance budgets with gates** (`store/central.py`, `telemetry/*`).
- Local web dashboard (htmx-style).

### Scope-creep flags (Miller MVP should defer or drop)
- The dashboard's **agent-runner control plane** that launches claude/codex/gemini subprocesses
  (`dashboard/agents.py`) — large surface, not core code-intel.
- Test-confidence + dependency/CVE intel are valuable but are **phase-2**, not MVP.

### The one seam to preserve for the future Eros tier
Both julie and eros already speak **localhost HTTP at a tool boundary** (`POST /tools/{name}`, today zero-auth,
loopback-bound). That boundary is the natural commercial insertion point: swap loopback+no-auth for an
authenticated remote endpoint + tenancy key on the workspace registry. Keep Miller's read path behind one
interface so "local SQLite" vs "remote service" is swappable. Build nothing else commercial until Miller is
indispensable.

## 7. Memory tool — greenfield (no working reference in either)

julie has no `memory` MCP tool. It has dead `memory_vectors` scaffolding (a `vec0` table, zero callers, no
companion content table). eros dropped even that — its "memory" is read-only rendering of the Goldfish
`.memories/` dir in the dashboard. The top-level `.memories/` is the *agent's* Goldfish system, unrelated to
either product's DB. **If Miller wants a memory tool, design it from scratch:** a content/metadata table plus
(optionally) a search index over it; scope per-workspace; and remember §2 — don't key memories to symbol IDs
that churn on edit.

---

## Decisions surfaced (for the user)

1. **Daemon: don't build one for the read path.** SQLite WAL + per-process in-memory index. A minimal single
   writer (indexer + watcher + optional dashboard) that, if down, only degrades freshness, not reads. ~12K LOC
   of julie's fragility deleted by construction because Miller has no Tantivy.
2. **Split reframe:** eros is the *local feature spec*, not commercial code. Miller inherits the best of
   julie+eros locally; the Eros tier is greenfield over the preserved HTTP tool seam. Confirm this is the model.
3. **Edit-freshness = mutation gate** (eros's pattern) + write-through extract, NOT watcher-trust.
4. **MVP feature line:** core = search/refs/symbols/context/trace/impact + index-aware edit + mutation-gate
   freshness. Phase-2 = test-confidence, dependency/CVE intel, dashboard. Drop/defer the agent-runner plane.
5. **Test architecture is a first-class design constraint**, not an afterthought: enforce the logic↔infra seam,
   keep the default suite <10s, gate it in CI from day one.
