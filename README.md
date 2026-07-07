# Miller

Miller is a local code-intelligence server for coding agents. It keeps a current SQLite-backed view of a
workspace, then answers structural questions through MCP and a matching CLI: find symbols, inspect files,
build focused context, trace relationships, assess change impact, and check workspace freshness without asking
an agent to grep and reread the repo by hand.

Miller is the free local core in the Miller/Eros product split. It stays deterministic, lexical/structural,
daemon-light, and embedding-free. Eros sits above it for higher-level guidance, semantic/vector workflows,
confidence/evidence views, and commercial orchestration.

The practical difference from a one-time graph dump is that Miller is built for active agent work:

- workspace state, freshness, refresh, and selectors are first-class;
- CLI and MCP calls share the same read cores, so examples can be dogfooded in a shell and used by agents;
- the registry, telemetry, and dashboard show what Miller knows right now;
- telemetry-derived onboarding can summarize how agents have used this indexed repo without storing raw queries;
- stale or corrupt search sidecars fail visibly instead of silently lying; refresh the workspace or explicitly
  opt out with `MILLER_SEARCH_SIDECAR=0` when debugging the in-memory fallback;
- cross-language bridge evidence stays structural and provider-scoped, not embedding-driven.

> **Current release: v1.4.5.** Miller ships as agent plugins, self-contained per-platform release archives,
> and a source-checkout workflow. Plugin and release-archive installs include the pinned `julie-extract`
> binary; users do not install it separately.
>
> Website: [anortham.github.io/miller](https://anortham.github.io/miller/) · Release:
> [v1.4.5](https://github.com/anortham/miller/releases/tag/v1.4.5)

## Quickstart

Most Claude Code and Codex users should start with the agent plugin. The plugin launcher downloads the matching
Miller release archive, verifies its `.sha256` sidecar, caches it under `~/.miller/plugin-cache/`, and starts
`miller serve` as an MCP server. That archive includes Miller's pinned `.tools/julie-extract` binary, so plugin
users do not install `julie-extract` or the .NET SDK separately.

Claude Code:

```bash
/plugin marketplace add anortham/miller
/plugin install miller@miller
```

Codex:

```bash
codex plugin marketplace add anortham/miller
codex
# then open /plugins and install Miller from the miller marketplace
```

Cursor: install Miller from the Cursor plugin marketplace, or add a user-global `~/.cursor/mcp.json`
entry with an absolute path to `miller serve`. Miller binds its workspace from MCP client roots on the first
tool call, so one global install works per editor window without `${workspaceFolder}` placeholders.

After extracting a release archive (see [Manual Binary Install](#manual-binary-install)), add to
`~/.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "miller": {
      "type": "stdio",
      "command": "/absolute/path/to/miller-1.4.5-aarch64-apple-darwin/miller",
      "args": ["serve"]
    }
  }
}
```

On Windows, use the full path to `miller.exe` as `command`. Optional override for clients without MCP roots:
set `"env": { "MILLER_WORKSPACE_ROOT": "/absolute/path/to/project" }` on the server entry.

For Miller development in this checkout, run `scripts/install-cursor-local-dev.sh` (or `.ps1`) to write
`~/.cursor/mcp.json` with your Release build path and retire legacy plugin-cache installs.

Reload Cursor after adding the entry, then ask your agent to search, inspect, build context, trace, or check impact
with Miller. Miller writes its local index under that workspace's `.miller/` directory.

### Common Agent Workflow

Use Miller's structural tools before broad file reads:

1. `search "NameOrConcept"` to find candidate symbols or files.
2. `inspect NameOrSymbol --depth overview` for the first symbol read. `overview` gives bounded refs, callers,
   callees, complexity, and a body preview without dumping the complete body.
3. `trace NameOrSymbol --mode refs` or `trace From --mode path --to To` when the question is about usages or
   graph paths.
4. `impact --git` or `impact SymbolName` before refactors and risky edits.
5. `edit replace_text` for localized existing-file edits when `query`, `anchor`, or `line` can avoid a full-file
   read. Preview shows match mode, source, line range, disk verification, and the diff before `apply=true`.
6. `inspect NameOrSymbol --depth full` only when you need the complete body or complete relation lists.

For model or harness switches, the plugin also ships `handoff-out` and `handoff-in` skills. They use existing
Miller tools plus local git state to write and validate markdown packets under `.miller/handoffs/`; they are not
new MCP tools or CLI commands, and the packets stay local unless you explicitly share them.

Miller's embedded MCP server instructions are kept to a ~1,900-character discovery core on purpose (Claude Code
truncates merged server instructions at roughly 2KB). The full workflow catalog, the subagent-dispatch primer, and
per-tool parameter detail live in [`docs/agent-guidance.md`](docs/agent-guidance.md); plugin users also get the
same depth through the `miller-*` skills.

### Manual Binary Install

Use this path when your MCP client does not use Miller's plugin package.

1. Download the archive for your platform from the
   [v1.4.5 release](https://github.com/anortham/miller/releases/tag/v1.4.5), plus the matching `.sha256`
   sidecar:

   - `miller-1.4.5-aarch64-apple-darwin.tar.gz`
   - `miller-1.4.5-x86_64-apple-darwin.tar.gz`
   - `miller-1.4.5-x86_64-unknown-linux-gnu.tar.gz`
   - `miller-1.4.5-x86_64-pc-windows-msvc.zip`

2. Verify and extract it:

   ```bash
   shasum -a 256 -c miller-1.4.5-aarch64-apple-darwin.tar.gz.sha256
   tar -xzf miller-1.4.5-aarch64-apple-darwin.tar.gz
   cd miller-1.4.5-aarch64-apple-darwin
   ./miller version
   ```

   ```powershell
   (Get-FileHash .\miller-1.4.5-x86_64-pc-windows-msvc.zip -Algorithm SHA256).Hash
   # compare against miller-1.4.5-x86_64-pc-windows-msvc.zip.sha256, then extract
   Expand-Archive .\miller-1.4.5-x86_64-pc-windows-msvc.zip -DestinationPath .
   .\miller-1.4.5-x86_64-pc-windows-msvc\miller.exe version
   ```

   Keep the extracted directory together. The native library files beside `miller`/`miller.exe`, the `.tools/`
   directory, and `dashboard/` are part of the runtime layout. The `.tools/` directory contains the matching
   pinned `julie-extract` binary; do not move it out or replace it with a separately installed copy.

3. Point your MCP client at the extracted binary. Use an absolute path inside the versioned directory and the
   explicit `serve` argument:

   ```json
   {
      "mcpServers": {
        "miller": {
         "command": "/absolute/path/to/miller-1.4.5-aarch64-apple-darwin/miller",
          "args": ["serve"]
        }
      }
   }
   ```

   On Windows, use the full path to `miller.exe` as `command`. For Cursor, put this in `~/.cursor/mcp.json` or
   install the Miller plugin — Miller resolves the open project via MCP `roots/list` on the first tool call.

### Source Checkout

Use this path for Miller development or for trying unreleased local changes. It requires the .NET 10 SDK on
`PATH`, and the restore script downloads the pinned `julie-extract` binary into this checkout's `.tools/`
directory.

```bash
bash scripts/restore-julie-extract.sh
dotnet build Miller.slnx -c Release
dotnet run --project src/Miller.Server -c Release -- workspace open --path /path/to/repo --full
dotnet run --project src/Miller.Server -c Release -- search "WorkspaceIndexProvider" --limit 5
```

On Windows:

```powershell
scripts/restore-julie-extract.ps1
dotnet build Miller.slnx -c Release
dotnet run --project src/Miller.Server -c Release -- workspace open --path C:\source\repo --full
dotnet run --project src/Miller.Server -c Release -- search "WorkspaceIndexProvider" --limit 5
```

## Requirements By Install Path

- **Plugin or release archive:** no .NET SDK is required to run the main `miller` binary. The release archive
  includes the matching pinned `.tools/julie-extract` binary and dashboard helper.
- **Source checkout:** the .NET 10 SDK must be installed and on `PATH`; `dotnet --version` should report a
  `10.x` SDK. Run the restore script once to download the pinned `julie-extract` binary into `.tools/`.
- **Dashboard:** the packaged dashboard helper is self-contained/non-AOT because ASP.NET Razor Components do
  not currently support Native AOT.

## How it works

Miller does not own tree-sitter extraction and does **not** use embeddings. Parser-backed extraction is
delegated to a prebuilt `julie-extract` binary (Rust + tree-sitter) that writes symbols, identifiers, files,
and relationships into a SQLite database. Plugin and release-archive installs bundle the matching extractor
under `.tools/`; source checkouts restore it with `scripts/restore-julie-extract.*`. Miller is the pure-.NET
host on top; it can re-read workspace source text for explicit content/source search and source-region
snippets, guarded by extractor hashes and spans:

```
┌───────────────────────────┐     stdio / MCP
│ Claude Code / Cursor / MCP │◀──────────────────┐
└───────────────────────────┘                    │
                                       ┌──────────────────────┐
                                       │   Miller.Server       │  MCP host + CLI + telemetry
                                       └──────────────────────┘
                                                  │
                          ┌───────────────────────┼───────────────────────┐
                          ▼                                                 ▼
              ┌──────────────────────┐                        ┌──────────────────────┐
              │     Miller.Core       │                        │   Miller.Indexing     │
              │  (pure logic, no I/O) │                        │  (infrastructure)     │
              │  • BM25 ranking       │                        │  • julie-extract      │
              │  • resolver + graph   │                        │    subprocess         │
              │  • result contracts   │                        │  • SQLite readers     │
              └──────────────────────┘                        │  • sidecar writers    │
                                                               └──────────────────────┘
                                                                          │
                          ┌───────────────────────────────────────────────┼───────────────────────────────────────────────┐
                          ▼                                               ▼                                               ▼
              ┌──────────────────────┐                        ┌──────────────────────┐                        ┌──────────────────────┐
              │ .miller/symbols.db    │                        │ .miller/search.db     │                        │ .miller/content.db    │
              │ julie-extract output  │                        │ symbol FTS recall     │                        │ source/docs/web text  │
              └──────────────────────┘                        └──────────────────────┘                        └──────────────────────┘
```

Design choices that follow from this:
- **No embeddings in the default path** — ranking stays deterministic in C#. Symbol search uses Miller's BM25
  over candidates recalled from the on-disk symbol sidecar, with an in-memory fallback. Explicit file/text search
  uses the content corpus sidecar.
- **No standalone daemon to manage** — SQLite WAL is the read-concurrency primitive, so many reader instances
  (agent teams, git worktrees, the dashboard) share local artifacts. Refresh and sidecar writes are explicit
  Miller operations; if no writer is active, reads still work but freshness does not advance.
- **Hard logic↔infrastructure seam** — `Miller.Core` has zero I/O dependencies, so the ranking and the resolver
  are unit-tested in milliseconds with no live DB, subprocess, or transport. This keeps the default test suite fast.

## Replacing Julie

The 1.0 replacement story is the product family, not Miller alone:

- **Miller** replaces Julie's deterministic local agent-tool core: search, inspect, context, references, trace/path,
  impact, editing, workspace lifecycle, content/web/text import, structural facts, marker audits, telemetry,
  JSON/JSONL feeds, and deterministic local analysis reports (`metrics churn|clones|complexity|risk` and the
  composed `miller report` repo-quality rollup).
- **`julie-extractors` / `julie-extract`** owns parser-backed extraction. Miller consumes its artifacts and ships a
  pinned extractor for its own indexing workflow; standalone extraction workflows belong in `julie-extractors`.
- **Eros** owns what requires semantics or fleet state: semantic/vector retrieval and embeddings, guidance,
  confidence/evidence views, cross-workspace ranking, suppression persistence, and commercial orchestration.

That boundary is deliberate. Miller should stay predictable and local; the product layer above it decides what the
facts mean and what to do next.

## Project structure

```
src/
  Miller.Core/       pure logic, ZERO I/O deps: ranking, resolver, graph, and result contracts
  Miller.Indexing/   infrastructure: julie-extract subprocess, SQLite readers, search/content sidecar writers
  Miller.Server/     MCP stdio host, the tool surface, the telemetry interceptor + ledger
  Miller.Dashboard/  narrow loopback ops dashboard reading registry, telemetry, and workspace artifact facts
tests/
  Miller.Tests/      unit (Core, fast) + contract (against a committed extract-DB fixture) + tagged scale set
docs/
  README.md                    current-vs-historical documentation map
  contracts/                   active integration contracts
  plans/                       design and implementation records
  findings/                    dogfood evidence and investigation notes
```

Miller keeps only the local operational dashboard: registered workspaces, freshness, read-only aggregate facts
from workspace artifacts, telemetry, sidecar health, and refresh/troubleshooting actions. Miller tools may return
immediate recovery hints for the next useful local call; Eros owns richer product UX such as workflow guidance,
confidence/evidence views, semantic/vector retrieval, and commercial workflows.

## The tool surface

Nine MCP tools, each with smart defaults so the common path is the simplest call: `search`, `inspect`,
`context`, `trace`, `impact`, `edit`, `content`, `patterns`, and `workspace`. Read tools accept a `workspace_id`
selector: display ID, unique prefix, full ID, registered root path, `current`, or `primary`. Explicit `workspace_id` defaults
`ensure_fresh=true`. Targets are smart strings, not JSON objects. See
[docs/findings/miller-toolbox.md](docs/findings/miller-toolbox.md).

`trace` is the graph workflow tool: `mode=refs` lists name-based identifier references, `mode=path` shows the
shortest extracted graph path to `to`, and `mode=bridge` follows provider-scoped bridge evidence. Current providers
are `dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`, `react`, and `backend-http`; they link route
references to file routes/definitions, client requests to Next.js/Nuxt route handlers, and client requests
(fetch/axios/`requests`/`httpx`/`net/http`/`Net::HTTP`/`HttpClient`/Ktor/Guzzle/Req/reqwest) to
Express/Fastify/FastAPI/Flask/Django/Spring/Go/gin/echo/Rails/NestJS/Laravel/Phoenix/axum/actix route templates —
not every framework route shape.
No-path, unsupported bridge, and ambiguous-target results include bounded next actions; JSON callers get the same
guidance in additive `next_actions` rows.

`content` is the large-text workflow: import or search the right content kind, keep the `source_id` from each
hit, then read a bounded line window. Empty searches and failed reads include recovery guidance, diagnostic codes
in JSON, and reminders to pass `workspace_id` when a hit came from a cross-workspace search.

For cross-workspace code reading, stay in the current session and run `workspace list`. If the target repo is
registered, pass its display ID, unique prefix, full ID, or root path as `workspace_id` to `search`, `inspect`,
`context`, `impact`, `trace`, or `patterns`. If it is not registered yet, run
`workspace operation=open path=/absolute/repo` from MCP or `miller workspace open --path /absolute/repo --full`
from the CLI, then retry the read tool. The CLI-only `miller metrics` commands accept the same single-workspace
selectors. The `workspace_id=all` selector is only for `content search` text audits across registered workspace
content DBs.

`patterns` is the structural-facts surface. It lists and searches known code-shape facts emitted by
the pinned `julie-extract` catalog — 130+ pattern ids across ~36 languages, spanning framework facts
(ASP.NET minimal API routes, htmx attributes, Alpine directives), language facts (async/await, unsafe blocks,
decorators, goroutines), SQL DDL shapes, and JSON/YAML/TOML/Markdown document structure. It is intentionally not
a raw AST query language:

```text
patterns()
patterns(operation="search", pattern_id="aspnet.minimal_api.route.v1", where="verb=GET", path="Program.cs")
patterns(operation="search", pattern_id="htmx.attribute.v1", where="attribute_name=hx-get", path="Views/**")
patterns(operation="search", pattern_id="alpine.directive.v1", where="directive=x-data", path="Views/**")
patterns(operation="search", query="route")
```

Start with `patterns()`/`patterns(operation="list")`; list and no-match output now includes concrete next
actions, and search misses can suggest near pattern IDs or explain when active filters removed all rows. Use
`query` when you remember the kind of fact but not the exact `pattern_id`.

The CLI-only `miller metrics` command reports deterministic local facts: recent git churn mapped to current
symbols, identical body-hash clone groups, and complexity hotspots with transparent thresholds. It is not semantic
ranking or cleanup advice:

```bash
miller metrics churn --range HEAD~20..HEAD --limit 25
miller metrics clones --min-count 2
miller metrics complexity --min-severity moderate --exclude-tests
```

## Using Miller

The single `miller` binary runs two ways:

- **MCP server (default).** With no arguments — or the explicit `serve` verb — Miller speaks the MCP protocol
  over stdio. This is how an MCP client (Claude Code, Cursor, Codex, etc.) connects; see
  [`mcp-config.json`](mcp-config.json):

  ```bash
  dotnet run --project src/Miller.Server -c Release -- serve
  ```

  This source-checkout config uses `dotnet run`, so the MCP client machine needs the .NET 10 SDK installed.

- **CLI (one-shot).** Any other verb runs a single command over the current directory's `.miller/symbols.db`
  and exits — for shells, CI, and integration tests. Read verbs also accept `--workspace-id <selector>`
  (`--workspace <path>` is a path alias) for registered workspaces, so dogfood and CI calls can target another
  indexed repo without changing directories. The CLI reuses the *same* tool cores the server exposes, so output
  matches a tool call.

  ```bash
  dotnet run --project src/Miller.Server -c Release -- search "WorkspaceIndexProvider" --limit 5
  dotnet run --project src/Miller.Server -c Release -- search "WorkspaceIndexProvider" --workspace-id miller
  dotnet run --project src/Miller.Server -c Release -- workspace open --path /path/to/other/repo --full
  dotnet run --project src/Miller.Server -c Release -- search "routing bug" --workspace-id /path/to/other/repo
  dotnet run --project src/Miller.Server -c Release -- search "release archive" --mode content --limit 5
  dotnet run --project src/Miller.Server -c Release -- content add-markdown /tmp/page.md --url https://example.com/page --display-path "Example page" --json
  dotnet run --project src/Miller.Server -c Release -- content search "important phrase" --kind web --limit 5
  dotnet run --project src/Miller.Server -c Release -- patterns --json
  dotnet run --project src/Miller.Server -c Release -- patterns search --pattern htmx.attribute.v1 --where attribute_name=hx-get --path "Views/**" --json
  dotnet run --project src/Miller.Server -c Release -- metrics churn --range HEAD~20..HEAD --json
  dotnet run --project src/Miller.Server -c Release -- metrics clones --min-count 2 --json
  dotnet run --project src/Miller.Server -c Release -- metrics complexity --min-severity moderate --exclude-tests --json
  dotnet run --project src/Miller.Server -c Release -- search "TODO,FIXME" --mode markers --file-pattern "src/**"
  dotnet run --project src/Miller.Server -c Release -- inspect src/Miller.Server/AgentInstructions.cs --depth full
  dotnet run --project src/Miller.Server -c Release -- context "CLI workspace routing" --token-budget 2000
  dotnet run --project src/Miller.Server -c Release -- trace AgentInstructions --depth 2
  dotnet run --project src/Miller.Server -c Release -- trace AgentInstructions --mode refs --reference-kind call --limit 20 --json
  dotnet run --project src/Miller.Server -c Release -- impact AgentInstructions --max-depth 2
  dotnet run --project src/Miller.Server -c Release -- impact --git --base origin/main --max-depth 1
  dotnet run --project src/Miller.Server -c Release -- workspace status
  dotnet run --project src/Miller.Server -c Release -- workspace health --json
  dotnet run --project src/Miller.Server -c Release -- workspace onboarding --json
  dotnet run --project src/Miller.Server -c Release -- workspace leader --json
  dotnet run --project src/Miller.Server -c Release -- workspace list
  dotnet run --project src/Miller.Server -c Release -- version
  ```

  Build once and run the binary directly (`src/Miller.Server/bin/Release/net10.0/miller <verb>`) to skip the
  `dotnet run` up-to-date check. `miller help` lists every verb: `capabilities`, `search`, `todos`, `content`,
  `patterns`, `metrics`, `telemetry`, `symbols`, `references`, `complexity`, `refresh`, `inspect`, `context`, `impact`,
  `trace`, `dashboard`, `workspace`, `version`, `help`, and `serve`.

**Dogfooding the server.** Because MCP runs over stdio, a new build takes effect only after the MCP client
restarts the subprocess. A build made inside the repo carries its git short SHA — `miller version` prints
`<version>+<sha>` (just `<version>` for a build with no `.git`), and the same string heads the `# workspace` block of
`workspace status`. The status header also includes the process id (`pid <n>`), which is the quickest way to
confirm a restarted MCP client is talking to a new Miller subprocess when you rebuilt uncommitted changes and
the SHA suffix stayed the same.

**Dashboard.** The local dashboard binds to loopback and reads the workspace registry, shared telemetry DB, and
read-only aggregate facts from each workspace's Miller artifacts. It does not hydrate full indexes. Use the CLI
launcher so multiple Miller sessions reuse one machine-global dashboard process while opening the current
workspace selector. From an MCP session, use the `workspace` tool's dashboard operation to start or reuse the
same dashboard without leaving the session. `--port` selects the launch port only when no healthy dashboard is
already running:

```bash
miller dashboard
miller dashboard --port 4977
```

```text
workspace(operation="dashboard")
workspace(operation="dashboard", port=4977)
```

Open the printed URL to view registered workspaces and scoped per-tool telemetry. Set `MILLER_REGISTRY_DB`,
`MILLER_TELEMETRY_DB`, or `MILLER_DASHBOARD_WEBROOT` only when testing non-default paths.
The selected-workspace detail view also surfaces local complexity hotspots and body-hash clone groups from the
artifact. Git churn stays in the CLI-only `miller metrics churn` path because it reads a revision range from git.

## Agent Plugin Details

Miller's first plugin distribution path lives in this repository, not a separate `miller-plugin` repo:

- `.claude-plugin/plugin.json` exposes Miller to Claude Code.
- `.cursor-plugin/plugin.json` exposes Miller to Cursor (plugin marketplace install).
- `.codex-plugin/plugin.json` and `.mcp.json` expose Miller to Codex.
- `skills/` is generated from `.agents/skills/` by `scripts/sync-plugin-skills.sh`.
- `bin/miller-plugin-launcher.cjs` downloads the configured GitHub release archive, verifies the `.sha256`
  sidecar, caches it under `~/.miller/plugin-cache/`, and runs `miller serve`. Workspace binding is handled
  by Miller via MCP client roots; set `MILLER_WORKSPACE_ROOT` only when your client lacks roots support.

The plugin launcher consumes the release version in `miller-plugin.json`. Use
`MILLER_BINARY=/absolute/path/to/miller` when testing an unreleased local build.

Claude Code local-checkout install:

```bash
claude plugin install /path/to/miller
```

Cursor MCP install from a local checkout:

```bash
scripts/install-cursor-local-dev.sh
```

```powershell
scripts/install-cursor-local-dev.ps1
```

That writes `~/.cursor/mcp.json` with your Release build path and retires legacy `~/.cursor/plugins/local/miller`
copies.

Then reload Cursor and confirm Miller appears under Settings > Tools & MCP. The plugin launcher no longer guesses
workspace cwd — Miller binds from MCP roots per open window. Use `MILLER_WORKSPACE_ROOT` in the MCP env block only
for clients without roots support (for example Codex per-project config).

For the GitHub-hosted plugin source, use:

```bash
/plugin marketplace add anortham/miller
/plugin install miller@miller
```

For local development against this checkout:

```bash
dotnet build Miller.slnx -c Release
export MILLER_BINARY="$PWD/src/Miller.Server/bin/Release/net10.0/miller"
claude
```

## Local proof commands

These commands are intentionally real Miller-checkout examples. Run them after the restore/build step above
from this repo, or replace `dotnet run --project src/Miller.Server -c Release --` with an installed `miller`
binary:

```bash
dotnet run --project src/Miller.Server -c Release -- workspace status
dotnet run --project src/Miller.Server -c Release -- workspace health --json
dotnet run --project src/Miller.Server -c Release -- workspace onboarding --json
dotnet run --project src/Miller.Server -c Release -- workspace leader --json
dotnet run --project src/Miller.Server -c Release -- workspace list
dotnet run --project src/Miller.Server -c Release -- search "WorkspaceIndexProvider" --limit 5
dotnet run --project src/Miller.Server -c Release -- search "release archive" --mode content --limit 5
dotnet run --project src/Miller.Server -c Release -- patterns --json
dotnet run --project src/Miller.Server -c Release -- metrics complexity --json --limit 10
dotnet run --project src/Miller.Server -c Release -- inspect src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md
dotnet run --project src/Miller.Server -c Release -- context "dashboard telemetry and workspace registry" --token-budget 1200
dotnet run --project src/Miller.Server -c Release -- impact src/Miller.Server/Tools/WorkspaceTool.cs --max-depth 1 --limit 10
dotnet run --project src/Miller.Server -c Release -- impact --git --max-depth 1 --limit 20
```

What these prove:

- `workspace status`, `workspace health`, `workspace onboarding`, and `workspace list` avoid source-file reads
  and full graph hydration; onboarding adds a read-only telemetry summary plus current-index target recovery.
- `workspace leader` reports leader identity/liveness and can request graceful handoff without killing processes.
- `workspace onboarding` turns local telemetry into starter commands, hot current-index targets, common misses,
  and friction signals. Telemetry stores target hashes, not raw queries or raw target text.
- Symbol search stays narrow and structural (`name + signature`). Docs/config use `--mode content`; source
  bodies and imported text use the explicit content corpus modes.
- `patterns --json` discovers extractor-recognized code-shape facts across the full pattern catalog (framework
  routes, language constructs, SQL DDL, data-document structure) without private SQLite reads or raw AST queries.
- The dashboard surfaces local complexity and clone facts from workspace artifacts; `miller metrics` also exposes
  local churn without semantic ranking or cleanup workflow ownership.
- `inspect`, `context`, and `impact` use the same projection-specific read paths exposed to MCP tools.
- The dashboard is operational evidence, not a separate product UI: it shows registered workspaces, index facts,
  telemetry, latency/failure signals, and scoped JSON endpoints from the same local state.

## Release archives

The release workflow builds one archive per pinned `julie-extract` platform:

- `aarch64-apple-darwin`
- `x86_64-apple-darwin`
- `x86_64-unknown-linux-gnu`
- `x86_64-pc-windows-msvc`

Each archive extracts to a versioned top-level directory such as
`miller-<version>-aarch64-apple-darwin/`. Keep that directory together: it contains `miller`, native runtime
libraries such as SQLite and BLAKE3 bindings, the matching `.tools/julie-extract` binary, the loopback dashboard
under `dashboard/`, `LICENSE`, and `THIRD-PARTY-NOTICES.md`. The workflow also uploads a `.sha256` sidecar for
each archive and smoke-runs both `julie-extract --version` and `miller version` before packaging.

Maintainers should use the two-step validation/promote flow in
[`docs/release-process.md`](docs/release-process.md) so publishing reuses already validated artifacts instead
of rebuilding the platform matrix.

Release archives are self-contained: the main `miller` binary is built with Native AOT, so a release machine
does **not** need the .NET SDK to run it. The manual install steps are in the Quickstart above.

## CLI output expectations

Text output is a compact human-facing contract and JSON output is the integration contract.

- Exit code `0` means success, `2` means usage/argument error, and `3` means no usable index, refused
  workspace operation, missing restore, or another operational failure a script should not ignore.
- `capabilities --json` reports the Miller build, `julie-extract` contract versions, optional feature flags,
  supported JSON commands, and export feeds for Eros/local integrations.
- `--json` is supported by `search`, `todos` (CLI alias for marker audits), `inspect`, `context`, `impact`, `trace`, `patterns`, `dashboard`,
  `content` operations, and `workspace` operations.
- `workspace onboarding --json [--workspace-id SELECTOR|--workspace DIR]` is a read-only, privacy-safe startup
  view for an indexed repo. It summarizes tool mix, successful flows, repeated current-index targets, common
  misses, and friction from the shared telemetry ledger.
- `refresh --json --wait [--workspace-id SELECTOR|--workspace DIR] [--full]` is the Eros-friendly top-level
  convergence command. It wraps the existing registered-workspace refresh path and includes sidecar facts.
- `search`, `inspect`, `context`, `impact`, `trace`, and `patterns` accept `--workspace-id <selector>`; selectors are the
  same registry IDs/display IDs/path selectors used by MCP `workspace_id`: display ID, unique prefix, full ID,
  registered root path, `current`, or `primary`.
- `search --mode file --json` intentionally returns symbol rows from matching files (`name`, `kind`, `file`,
  `line`, `symbol_id`) for compatibility with the normal search JSON contract. Use compact text when an
  interactive caller wants the file-first rendering, or `mode=content|source|all-text` when the caller needs
  path/line/snippet text hits.
- Text headings and ordering are intended to be stable enough for humans and logs, not for strict parsers.
  Use `--json` when a caller needs fields.
- Search result kinds are deliberately separate: symbol search ranks `name + signature`, `--mode markers`
  audits TODO/FIXME/HACK/XXX comment markers, `--mode content`
  searches docs-like file content, `--mode source|external|web|all-text` searches explicit content-corpus text,
  and `--regions` searches explicit source regions when region indexing is enabled.
- `trace --mode refs` returns name-based identifier references for a resolved target symbol. Use
  `--reference-kind call|variable_ref|type_usage|member_access|import` to narrow the result and `--no-definition`
  when only reference rows are needed. `trace --mode path` no-path and `trace --mode bridge` unsupported results
  include next local calls to try; JSON includes them as `next_actions`.
- `todos --json` is a CLI compatibility alias over marker search for Eros/scripts. For agents and normal
  interactive use, prefer `search "TODO,FIXME,HACK,XXX" --mode markers`; it returns marker, file:line,
  snippet, and containing symbol when available, with `--file-pattern` and `--language` for scope.
- The `content` CLI stores non-workspace text in `.miller/content.db`. Use `content import` for logs/reports
  and `content add-markdown <path> --url <url>` for browser-fetched pages. Search web imports with
  `content search "<phrase>" --kind web`, then read bounded windows with `content read --source-id <id>`.
- `content search "<term>" --workspace-id all --kind source|docs|config|external_file|web` searches registered
  workspace content DBs and reports workspace/display IDs on every hit for audits. Pass the hit's workspace ID back
  to `content read --workspace-id <id>` for external/web hits from another workspace.
- `content export [--kind KIND] [--content-workspace-id ID]` writes deterministic JSONL chunk rows for Eros
  and other local consumers. It includes raw chunk text; use it as an integration feed, not as an interactive
  reading shortcut.
- `telemetry export --jsonl [--workspace-id ID|all]` writes machine-global telemetry rows as JSONL for local
  dashboard/history consumers. It exports stored target hashes, not raw queries.
- `symbols export --jsonl`, `references export --jsonl`, and `complexity export --jsonl` write deterministic
  artifact fact feeds for fleet rollups and Eros workflows. `references export` is a usage-fact feed, not a
  dead-code ranking tool.
- Eros-facing CLI contracts live in `docs/contracts/cli-eros-v1.md`; workspace onboarding JSON fields live in
  `docs/contracts/workspace-onboarding-v1.md`; trace JSON fields live in `docs/contracts/trace-json-v1.md`;
  content export fields live in `docs/contracts/content-corpus-v1.md`; and references export fields live in
  `docs/contracts/references-export-v1.md`.
- Search defaults to 6 results. Compact symbol rows include name, kind, file, line, and signature when available;
  use `--limit N` when you need a wider page.

## Build & test

```bash
dotnet build Miller.slnx -c Release
dotnet test  Miller.slnx -c Release           # fast suite only — Scale tests excluded by default
```

The test suite is split in two so the dev loop stays fast (the lesson from julie, whose suite grew to
30+ minutes once slow integration tests ran on every change):

- **fast** (`Category!=Scale`) — pure logic + contract tests, no `julie-extract` subprocess. Target <10s.
  This is the default: a bare `dotnet test` runs only this suite (the test project sets
  `VSTestTestCaseFilter=Category!=Scale`, the MSBuild default for `--filter`; a command-line `--filter`
  overrides it).
- **scale** (`Category=Scale`) — live tests that spawn the real pinned `julie-extract` or build large
  fixtures. Run before a commit/PR. They **skip** (not fail) if `.tools/julie-extract` is absent.

The friendly wrapper sets a wall-clock budget tripwire on the fast suite and handles the filters:

```bash
scripts/test.sh            # fast suite (default), with a <30s budget tripwire
scripts/test.sh scale      # scale suite only (needs .tools/julie-extract — see restore script)
scripts/test.sh all        # both suites
```

Windows PowerShell mirrors are available for cross-platform scripts:

```powershell
scripts/test.ps1
scripts/test.ps1 scale
scripts/test.ps1 all
```

Two guards keep the split honest: a convention test
([`ScaleTraitConventionTests`](tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs)) fails the
build if any julie-spawning test is missing `[Trait("Category","Scale")]`, and CI time-budgets the fast
suite. A second convention guard requires Windows PowerShell mirrors for critical scripts. To enable
the scale suite locally:

```bash
bash scripts/restore-julie-extract.sh   # downloads the pinned julie-extract into .tools/
MILLER_JULIE_SOURCE=~/source/julie-extractors bash scripts/restore-julie-extract.sh --from-source
```

```powershell
scripts/restore-julie-extract.ps1       # downloads the pinned julie-extract.exe into .tools\
$env:MILLER_JULIE_SOURCE='C:\source\julie-extractors'; scripts/restore-julie-extract.ps1 -FromSource
```

Warnings are errors (`Directory.Build.props`).

## Known limits

- No embeddings or semantic/vector retrieval in Miller. If that is needed, Eros owns the projection.
- Region search is explicit at query time and indexed by default: call
  `search --regions comment|doc_comment|string_literal`. Set `MILLER_REGION_INDEX=0` to opt out, or
  `MILLER_REGION_MAX_BYTES=<n>` to lower or raise the per-region byte cap for very large comment/string-literal
  corpora.
- Ambiguous targets may need a file path, a more specific symbol, or a symbol ID. The CLI reports ambiguity
  instead of guessing.
- Bridge trace (`trace mode=bridge`) is provider-scoped, not a general all-language/all-framework feature. Current
  providers are `dotnet-web` (ASP.NET controllers, TypeScript/JS client URL calls, AutoMapper, Entity Framework),
  `nextjs`/`nextjs-api` (route references to file routes, client requests to Next.js route handlers),
  `nuxt`/`nuxt-api` (NuxtLink route references to Nuxt file routes, client requests to Nuxt server routes), `vue`
  (Vue route references to route definitions), `react` (React route references to route definitions), and
  `backend-http` (client requests to Express/Fastify/FastAPI/Flask/Django/Spring/Go/gin/echo/Rails/
  NestJS/Laravel/Phoenix/axum/actix route templates).
  API handlers, server actions, middleware rewrites, redirects, and runtime route rules need extractor facts before bridge can claim them. The mode intentionally uses the full bridge graph
  for provider-scoped evidence. Normal `search`, `inspect`, graph-only `context`, `impact`, non-bridge `trace`,
  and workspace status/list stay on projection-specific read paths.
- The main `miller` release binary publishes with Native AOT (no .NET SDK required to run it). The packaged
  dashboard helper stays self-contained/non-AOT because ASP.NET Razor Components do not yet support Native AOT.
- A rebuilt MCP server is picked up only after the MCP client restarts the Miller subprocess. Use
  `workspace status` and compare the `pid` in the header to confirm the restart actually loaded a new process.

## Troubleshooting

- `no Miller index`: run `miller workspace full`, or open the folder in the Miller MCP server so the
  index can be created. If the missing target is another repo, run `miller workspace open --path /absolute/repo --full`
  or MCP `workspace operation=open path=/absolute/repo`, then pass that repo's selector as `workspace_id`.
- Cursor shows duplicate or stale Miller rows (`user-miller`, `plugin-miller-miller`): remove extra `miller`
  entries from `~/.cursor/mcp.json`, move aside `~/.cursor/plugins/local/miller` if it exists, and reload Cursor.
- Cursor Miller fails with `Could not determine a Miller workspace root`: open a project folder (Miller binds via
  MCP `roots/list` on the first tool call), or set `MILLER_WORKSPACE_ROOT` to an absolute project path in the MCP
  server env. Do not use `${workspaceFolder}` in user-global config — it often stays unresolved.
- Cursor search results come from the wrong repo: reload the window or run `workspace status` and confirm the
  header root matches the open project. Pass an explicit `workspace_id` for another registered workspace when needed.
- Missing `julie-extract` from a plugin or release-archive install: reinstall or re-extract the full Miller
  archive and keep its `.tools/` directory beside the `miller` binary.
- Missing `julie-extract` from a source checkout: run the restore script for your platform, then rerun the scale
  test or refresh path.
- Unsure which server is live: run `miller version` or `miller workspace status`; compare the git SHA suffix
  with the build you expect, and compare `workspace status`'s `pid` before/after a restart.

## License

MIT
