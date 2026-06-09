# Miller — agent working notes

Miller is a read-only .NET 10 SQLite/MCP consumer of `julie-extract` output. It does not own
tree-sitter extraction or embeddings; parser-backed extraction is delegated to the pinned
`julie-extract` binary. Miller can re-read workspace source text for content corpus, source-region
snippets, and explicit text search using extractor hashes/spans as freshness guards. See
[README.md](README.md) for the current architecture and [docs/README.md](docs/README.md) for the
current-vs-historical documentation map.

## Language parity (load-bearing product rule)

A feature built on `julie-extract` data (a new table/column/extraction capability) is **not done until it
works for every language julie-extractors supports**, not just one. Verify per-language coverage on a real
extract before shipping or depending on it (`SELECT language, kind, COUNT(*) FROM <table> GROUP BY 1,2`); a
feature that silently covers only a subset but looks authoritative is a bug. When a capability needs new
extraction, add it across all supported languages in `julie-extractors`, not one at a time. (Why: the
`source_regions` table shipped in julie-extract 2.1.0 emitting only for JavaScript — Miller's consumer was
deferred to the 2.1.1 all-language emission rather than shipping a C#-empty feature.)

## Testing — read this before running tests

The suite is split into two categories. **Keep them separate; this is load-bearing.** julie's suite once
grew to 30+ minutes because slow integration tests ran on every change — Miller's split exists to prevent
that, and there are guards that will fail the build if the split erodes.

- **Default = fast suite.** A bare `dotnet test` runs ONLY `Category!=Scale` (pure logic + contract
  tests, no subprocess). This is enforced by `VSTestTestCaseFilter=Category!=Scale` in
  [`Miller.Tests.csproj`](tests/Miller.Tests/Miller.Tests.csproj) (the MSBuild default for `--filter`; a
  command-line `--filter` overrides it). Target <10s. **Run this on every change.** (A well-formed
  `.runsettings` `<TestCaseFilter>` works too; the csproj property is preferred because it needs no extra
  file and fails the build loudly on a typo instead of silently running everything.)
- **Scale suite is opt-in.** `Category=Scale` tests spawn the real `julie-extract` or build large
  fixtures. Run them with `scripts/test.sh scale` / `scripts/test.ps1 scale` (or `all`) before a
  commit/PR, or when you touch the indexing/extract path. They **skip** (not fail) if
  `.tools/julie-extract` is missing.

Use the wrapper, not raw `dotnet test`, unless you have a reason:

```bash
scripts/test.sh         # fast suite + a wall-clock budget tripwire (<30s)
scripts/test.sh scale   # scale suite only
scripts/test.sh all     # both
```

Windows PowerShell mirrors exist for cross-platform scripts:

```powershell
scripts/test.ps1
scripts/test.ps1 scale
scripts/test.ps1 all
```

### Rules when adding or changing tests

- **A test that spawns `julie-extract` MUST be tagged `[Trait("Category","Scale")]`** at the class level,
  and MUST obtain the binary via `ScaleTestSupport.RequireJulieServer()` (the single launch signal). Do
  not re-add a private `LocateJulieServer()`/`RepoRoot()` copy — those were deduplicated into
  [`ScaleTestSupport`](tests/Miller.Tests/ScaleTestSupport.cs) precisely so the guard has one signal to
  trust.
- The convention guard
  [`ScaleTraitConventionTests`](tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs) source-scans
  the tests and FAILS if any file referencing the launch signal is not tagged Scale. If it fails, you
  added a julie-spawning test without the trait — add the trait, don't weaken the guard.
- A test may be Scale for other reasons (e.g. a 50k-symbol fixture build with no julie). That's fine; the
  guard is one-directional (spawns julie ⟹ Scale), not the converse.
- Keep the fast suite genuinely fast and pure. If a "fast" test starts doing real I/O or heavy work, it
  belongs in Scale.

## Build

- Miller targets `net10.0`; source-checkout use and the checked-in `mcp-config.json` require the .NET 10
  SDK on `PATH`.
- `dotnet build Miller.slnx -c Release` — warnings are errors (`Directory.Build.props`,
  `TreatWarningsAsErrors`). The build must be 0 warnings / 0 errors; analyzer warnings (e.g. CA1416,
  xUnit1051) are build errors.
- Project seam: `Miller.Core` is pure logic with ZERO I/O deps. Keep it that way — it's why the core is
  unit-tested in milliseconds.
- The build COPIES `.tools/julie-extract` next to the output binary (`<out>/.tools/`, via a `Content`
  item in [`Miller.Server.csproj`](src/Miller.Server/Miller.Server.csproj)) because production locates it
  at `AppContext.BaseDirectory/.tools` (`WorkspaceContext.ToolsRoot`), NOT the repo. A no-restore machine
  still builds; the runtime then fails loudly with the restore-script message. To build from source, use
  `MILLER_JULIE_SOURCE=/path/to/julie-extractors scripts/restore-julie-extract.sh --from-source`
  or, on Windows, `$env:MILLER_JULIE_SOURCE='C:\path\to\julie-extractors';
  scripts/restore-julie-extract.ps1 -FromSource`.

## Release packaging

- The GitHub release workflow builds one self-contained package for each pinned `julie-extract` target:
  `aarch64-apple-darwin`, `x86_64-apple-darwin`, `x86_64-unknown-linux-gnu`, and
  `x86_64-pc-windows-msvc`. Keep this matrix in step with `scripts/julie-pins.json`.
- Release archives include `miller`, the matching `.tools/julie-extract` binary, the packaged dashboard
  executable under `dashboard/`, and `dashboard/wwwroot/dashboard.css`. The workflow smoke-runs
  `julie-extract --version` and `miller version`, then uploads a `.sha256` sidecar for each archive.
- Plugin support is first-class for Claude Code (`.claude-plugin/plugin.json`), Cursor
  (`.cursor-plugin/plugin.json`), and Codex (`.codex-plugin/plugin.json` plus `.mcp.json`). Keep those manifests
  and `miller-plugin.json` version-aligned on every release.
- Manual workflow dispatch defaults to package-only validation; set `publish: true` to create or update the
  GitHub release. Manual publish defaults to a prerelease. Tag pushes infer prerelease status from a
  hyphenated version such as `v0.2.1-beta.1`. The main `miller` binary is published with Native AOT; the
  dashboard executable remains self-contained/non-AOT because ASP.NET Razor Components do not currently support
  Native AOT.
- Do not publish, retag, delete, or overwrite a release without explicit user approval. README current-release
  metadata and release-evidence docs must come from live GitHub release facts, not guessed values.

## Public docs & onboarding

- `README.md` is the public first-use entry point, not only a developer architecture note. Keep the quickstart near
  the top and make the install paths clear for non-developers: Claude Code/Cursor/Codex plugin install, manual
  release archive install, manual MCP config, then source-checkout development.
- The public site is `https://anortham.github.io/miller/`; keep README linked to it.
- `docs/README.md` is the documentation map. Keep active contracts/current operating docs separate from historical
  design notes and dogfood evidence.
- Release-facing README facts must come from live GitHub release data. For `v0.3.2`, the live release has four
  platform archives plus four `.sha256` sidecars.
- When updating harness guidance, edit `CLAUDE.md` first, run `scripts/sync-agents.sh` or
  `scripts/sync-agents.ps1`, then confirm `cmp -s CLAUDE.md AGENTS.md`.

## Server host & startup

- **Host lifecycle gotcha (load-bearing).** The .NET Generic Host CONSTRUCTS every `IHostedService` up
  front, then calls `StartAsync` on each in registration order. Registration order orders `StartAsync`, NOT
  construction. So **no hosted-service constructor may read an `IndexBootstrapService` getter**
  (`Holder`/`Resolver`/`Workspace`/`Ledger`) — they throw until the bootstrap's `StartAsync` has run. The
  M3 services take only the bootstrap and read its getters lazily inside `ExecuteAsync`. The whole host
  graph is registered in one testable place,
  [`MillerServiceRegistration.AddMillerServices`](src/Miller.Server/Hosting/MillerServiceRegistration.cs);
  `HostStartupRegistrationTests` resolves the hosted-service set before bootstrap to guard this.
- **Sensitive-root guard.** [`WorkspaceRootSafety`](src/Miller.Server/Tools/WorkspaceRootSafety.cs) refuses
  to index the home dir, a filesystem/drive root, or a system dir. It runs at the very top of `Program.cs`
  (before any filesystem touch) and in `workspace open`. Ported from julie's `root_safety.rs` — keep the
  forbidden set in step with julie/eros.
- **CLI vs server (load-bearing branch).** The same `miller` binary is both the MCP stdio server and a one-shot
  CLI. `Program.cs` branches at the very top (before any filesystem touch / host build) on
  [`CliDispatch.IsCliInvocation`](src/Miller.Server/Cli/CliDispatch.cs): **no args OR `serve` → MCP host**
  (the historical default; stdio purity preserved), **any other verb → `CliDispatch.Run`** and exits. Read verbs
  load the current workspace's `symbols.db` through the same pure tool cores the server exposes (`SearchTool.Run`,
  `InspectTool.Run`, `WorkspaceRender`, …); lifecycle verbs such as `workspace open/remove` use the registry and
  refresh paths; `version`/`help` do not load an index. The CLI OWNS stdout — it does NOT start Serilog file
  logging or any background service. `mcp-config.json` launches the server with the explicit `-- serve`
  (cross-platform; no shell script). Build version is single-sourced in `Directory.Build.props` (`<Version>` + git
  short SHA → `MillerVersion.Current`), surfaced in MCP `ServerInfo.Version`, `miller version`, and the `workspace`
  status header.
- **Eros-facing CLI/export contracts.** Keep Eros on public process/artifact contracts, not Miller private .NET
  internals. Current documented surfaces live in [`docs/contracts/cli-eros-v1.md`](docs/contracts/cli-eros-v1.md):
  `capabilities --json`, `refresh --json --wait`, `workspace status --json`, `workspace health --json`,
  `content export`, `telemetry export --jsonl`, and stable read-command JSON such as `impact --json`,
  `trace --json`, and `patterns --json`. Add or harden new surfaces only when a concrete Eros workflow needs
  facts the documented contracts do not cover.
- **Logging.** All processes append to ONE shared daily pair (`.miller/logs/miller-<YYYYMMDD>.log` +
  `.jsonl`, Serilog `shared:true`); `pid`/`role`/`cid` are line properties, not file-name segments. There
  is no per-pid file and no startup reaper (both removed 2026-05-31; see the superseded D1/D6 notes in
  [`docs/m8-design.md`](docs/m8-design.md)).
- **Workspace registry.** Index DBs stay local at `<workspace>/.miller/symbols.db`; the central discovery
  surface is `~/.miller/workspaces.db`. Read tools accept `workspace_id` selectors: display ID, unique prefix,
  full ID, registered root path, `current`, or `primary`; explicit `workspace_id` defaults to refresh-first.
  When a user asks from workspace A to inspect workspace B, keep the session in A, run `workspace list`, and pass
  B's selector to `search`/`inspect`/`context`/`impact`/`trace`/`patterns`. If B is not registered, run
  `workspace open` with its root path first. `workspace_id=all` is only for `content search` text audits, not
  symbol/code read tools. The dashboard reads the registry, shared telemetry DB, and read-only aggregate facts
  from workspace artifacts. It must not hydrate full indexes just to render list/detail views.
- **Hash split.** Stable `workspace_id` is SHA-256 of the canonical root. File freshness uses
  `files.content_hash` (`blake3:<hex>`, normalized before comparison) and is guarded by
  `artifact_metadata.hash_algorithm=blake3`.
- **Search sidecar.** Symbol search is served from a Miller-owned, on-disk FTS5 artifact
  `<workspace>/.miller/search.db` (a revision-keyed derived index, same pattern as `telemetry.db`) built by the
  lock-holding writer (`IndexerService` leader / `CrossWorkspaceRefreshService`) and opened read-only by
  `WorkspaceIndexProvider`. **On by default — opt out with `MILLER_SEARCH_SIDECAR=0`** (`SymbolSearchSidecar.FromEnvironment`).
  The writer converges it incrementally from `revision_file_changes` after single-file updates; missing, stale, or
  corrupt sidecars fail visibly in search/status rather than silently allocating an in-memory fallback. The explicit
  opt-out path uses the in-memory BM25 index.
  Ranking stays in C# (`Miller.Core.Search.Bm25`, shared by both backends); FTS5 is recall-only (a word arm plus a
  collapsed-trigram arm for interior substrings). See
  [`docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md`](docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md).
- **Content corpus and text search.** File/content text search is served from the Miller-owned content corpus
  sidecar `<workspace>/.miller/content.db`, plus explicit external/web imports. Keep symbol search narrow
  (`name + signature`) unless real dogfood shows the explicit text modes fail an agent task. Route by intent:
  `mode=content` for docs/config prose, `mode=source` for workspace source-body text,
  `mode=external|web|all-text` for imported or full corpus text, and
  `regions=comment|doc_comment|string_literal` for source-region text. Do not add doc comments, literals, or broad
  source text directly to symbol ranking just because an old TODO predates content corpus FTS.
- **Patterns and structural facts.** The `patterns` MCP/CLI surface reads `structural_facts` emitted by
  `julie-extractors`. Miller may list, group, filter, and render generic `pattern_id` facts, but it must not own
  parser recognition or raw AST query execution. Current extractor examples include ASP.NET minimal API routes,
  htmx attributes, and Alpine directives. When a new fact shape needs extractor support, add it across all
  supported languages in `julie-extractors` first, then consume the stable artifact contract from Miller/Eros.
- **Web research.** Miller has a mirrored `miller-web-research` skill. Web fetching stays outside Miller in the
  skill layer via `browser39`; Miller imports fetched markdown as `web` content and supports bounded
  search/read through the content corpus.
- **Agent instructions.** The MCP server-level guidance is
  [`MILLER_AGENT_INSTRUCTIONS.md`](src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md), embedded in the binary
  and set as `ServerInstructions`. Edit the markdown; `AgentInstructionsTests` guards that every tool stays
  documented.

## AGENTS.md is generated

`AGENTS.md` is a byte-for-byte mirror of THIS file. Edit `CLAUDE.md` only, then run
`scripts/sync-agents.sh` or `scripts/sync-agents.ps1` to regenerate `AGENTS.md`. A pre-commit hook
(installed via `scripts/install-hooks.sh` or `scripts/install-hooks.ps1`, which sets
`core.hooksPath=.githooks`) fails the commit if they diverge.
