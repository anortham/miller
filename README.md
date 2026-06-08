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
- stale or corrupt search sidecars fail visibly instead of silently lying; refresh the workspace or explicitly
  opt out with `MILLER_SEARCH_SIDECAR=0` when debugging the in-memory fallback;
- cross-language bridge evidence stays structural and provider-scoped, not embedding-driven.

> **Current release: v0.3.2.** Miller ships as agent plugins, self-contained per-platform release archives,
> and a source-checkout workflow.
>
> Website: [anortham.github.io/miller](https://anortham.github.io/miller/) · Release:
> [v0.3.2](https://github.com/anortham/miller/releases/tag/v0.3.2)

## Quickstart

Most users should start with the agent plugin. Miller's plugin package supports Claude Code, Cursor, and Codex.
The plugin launcher downloads the matching Miller release archive, verifies its `.sha256` sidecar, caches it
under `~/.miller/plugin-cache/`, and starts `miller serve` as an MCP server.

Claude Code:

```bash
/plugin marketplace add anortham/miller
/plugin install miller@miller
```

Cursor local plugin install:

```bash
mkdir -p ~/.cursor/plugins/local
ln -s /path/to/miller ~/.cursor/plugins/local/miller
```

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE\.cursor\plugins\local"
New-Item -ItemType Junction -Path "$env:USERPROFILE\.cursor\plugins\local\miller" -Target "C:\path\to\miller"
```

Reload Cursor after installing. Cursor expects `.cursor-plugin/plugin.json` at the plugin root; Miller's Cursor
manifest points at the same release launcher used by the Claude Code and Codex plugin paths. If Cursor previously
auto-discovered Miller from an older Claude plugin cache, reinstall from a package that contains
`.cursor-plugin/plugin.json`.

After installing, open a code workspace and ask your agent to search, inspect, build context, trace, or check
impact with Miller. Miller writes its local index under that workspace's `.miller/` directory.

### Manual Binary Install

Use this path when your MCP client does not use Miller's plugin package.

1. Download the archive for your platform from the
   [v0.3.2 release](https://github.com/anortham/miller/releases/tag/v0.3.2), plus the matching `.sha256`
   sidecar:

   - `miller-0.3.2-aarch64-apple-darwin.tar.gz`
   - `miller-0.3.2-x86_64-apple-darwin.tar.gz`
   - `miller-0.3.2-x86_64-unknown-linux-gnu.tar.gz`
   - `miller-0.3.2-x86_64-pc-windows-msvc.zip`

2. Verify and extract it:

   ```bash
   shasum -a 256 -c miller-0.3.2-aarch64-apple-darwin.tar.gz.sha256
   tar -xzf miller-0.3.2-aarch64-apple-darwin.tar.gz
   cd miller-0.3.2-aarch64-apple-darwin
   ./miller version
   ```

   ```powershell
   (Get-FileHash .\miller-0.3.2-x86_64-pc-windows-msvc.zip -Algorithm SHA256).Hash
   # compare against miller-0.3.2-x86_64-pc-windows-msvc.zip.sha256, then extract
   Expand-Archive .\miller-0.3.2-x86_64-pc-windows-msvc.zip -DestinationPath .
   .\miller-0.3.2-x86_64-pc-windows-msvc\miller.exe version
   ```

   Keep the extracted directory together. The native library files beside `miller`/`miller.exe`, the `.tools/`
   directory, and `dashboard/` are part of the runtime layout.

3. Point your MCP client at the extracted binary. Use an absolute path inside the versioned directory and the
   explicit `serve` argument:

   ```json
   {
      "mcpServers": {
        "miller": {
          "command": "/absolute/path/to/miller-0.3.2-aarch64-apple-darwin/miller",
          "args": ["serve"]
        }
      }
   }
   ```

   On Windows, use the full path to `miller.exe` as `command`.

### Source Checkout

Use this path for Miller development or for trying unreleased local changes. It requires the .NET 10 SDK on
`PATH`.

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

Miller does **not** parse source code itself and does **not** use embeddings. Extraction is delegated to a
prebuilt `julie-extract` binary (Rust + tree-sitter) that writes symbols, identifiers, files, and
relationships into a SQLite database. Miller is the pure-.NET host on top:

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
from workspace artifacts, telemetry, sidecar health, and refresh/troubleshooting actions. Eros owns richer product
UX such as next-action guidance, confidence/evidence views, semantic/vector retrieval, and commercial workflows.

## The tool surface

Eight MCP tools, each with smart defaults so the common path is the simplest call: `search`, `inspect`,
`context`, `trace`, `impact`, `edit`, `content`, and `workspace`. Read tools accept a `workspace_id`
selector: display ID, unique prefix, full ID, registered root path, `current`, or `primary`. Explicit `workspace_id` defaults
`ensure_fresh=true`. Targets are smart strings, not JSON objects. See
[docs/findings/miller-toolbox.md](docs/findings/miller-toolbox.md).

For cross-workspace code reading, stay in the current session and run `workspace list`. If the target repo is
registered, pass its display ID, unique prefix, full ID, or root path as `workspace_id` to `search`, `inspect`,
`context`, `impact`, or `trace`. If it is not registered yet, run `workspace operation=open path=/absolute/repo`
from MCP or `miller workspace open --path /absolute/repo --full` from the CLI, then retry the read tool. The
`workspace_id=all` selector is only for `content search` text audits across registered workspace content DBs.

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
  dotnet run --project src/Miller.Server -c Release -- inspect src/Miller.Server/AgentInstructions.cs --depth full
  dotnet run --project src/Miller.Server -c Release -- context "CLI workspace routing" --token-budget 2000
  dotnet run --project src/Miller.Server -c Release -- trace AgentInstructions --depth 2
  dotnet run --project src/Miller.Server -c Release -- impact AgentInstructions --max-depth 2
  dotnet run --project src/Miller.Server -c Release -- workspace status
  dotnet run --project src/Miller.Server -c Release -- workspace list
  dotnet run --project src/Miller.Server -c Release -- version
  ```

  Build once and run the binary directly (`src/Miller.Server/bin/Release/net10.0/miller <verb>`) to skip the
  `dotnet run` up-to-date check. `miller help` lists every verb: `search`, `inspect`, `context`, `impact`,
  `trace`, `content`, `workspace`, `refresh`, `capabilities`, `telemetry`, `dashboard`, `version`, `serve`.

**Dogfooding the server.** Because MCP runs over stdio, a new build takes effect only after the MCP client
restarts the subprocess. A build made inside the repo carries its git short SHA — `miller version` prints
`0.3.2+<sha>` (just `0.3.2` for a build with no `.git`), and the same string heads the `# workspace` block of
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

## Agent Plugin Details

Miller's first plugin distribution path lives in this repository, not a separate `miller-plugin` repo:

- `.claude-plugin/plugin.json` exposes Miller to Claude Code.
- `.cursor-plugin/plugin.json` exposes Miller to Cursor.
- `.codex-plugin/plugin.json` and `.mcp.json` expose Miller to Codex.
- `skills/` is generated from `.agents/skills/` by `scripts/sync-plugin-skills.sh`.
- `bin/miller-plugin-launcher.cjs` downloads the configured GitHub release archive, verifies the `.sha256`
  sidecar, caches it under `~/.miller/plugin-cache/`, and runs `miller serve`.

The plugin launcher consumes the release version in `miller-plugin.json`. Use
`MILLER_BINARY=/absolute/path/to/miller` when testing an unreleased local build.

Claude Code local-checkout install:

```bash
claude plugin install /path/to/miller
```

Cursor local-checkout install:

```bash
mkdir -p ~/.cursor/plugins/local
ln -s /path/to/miller ~/.cursor/plugins/local/miller
```

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE\.cursor\plugins\local"
New-Item -ItemType Junction -Path "$env:USERPROFILE\.cursor\plugins\local\miller" -Target "C:\path\to\miller"
```

Then reload Cursor and confirm Miller appears under Settings > Plugins. The Cursor manifest uses
`node ./bin/miller-plugin-launcher.cjs` with `cwd: "."` so it does not depend on Claude-specific
`${CLAUDE_PLUGIN_ROOT}` expansion.

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
dotnet run --project src/Miller.Server -c Release -- workspace list
dotnet run --project src/Miller.Server -c Release -- search "WorkspaceIndexProvider" --limit 5
dotnet run --project src/Miller.Server -c Release -- search "release archive" --mode content --limit 5
dotnet run --project src/Miller.Server -c Release -- inspect src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md
dotnet run --project src/Miller.Server -c Release -- context "dashboard telemetry and workspace registry" --token-budget 1200
dotnet run --project src/Miller.Server -c Release -- impact src/Miller.Server/Tools/WorkspaceTool.cs --max-depth 1 --limit 10
```

What these prove:

- `workspace status` and `workspace list` read cheap registry/freshness metadata instead of hydrating the full
  graph.
- Symbol search stays narrow and structural (`name + signature`). Docs/config use `--mode content`; source
  bodies and imported text use the explicit content corpus modes.
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
- `--json` is supported by `search`, `inspect`, `context`, `impact`, `dashboard`, `content` operations, and
  `workspace` operations. `trace` is text-only for now.
- `refresh --json --wait [--workspace-id SELECTOR|--workspace DIR] [--full]` is the Eros-friendly top-level
  convergence command. It wraps the existing registered-workspace refresh path and includes sidecar facts.
- `search`, `inspect`, `context`, `impact`, and `trace` accept `--workspace-id <selector>`; selectors are the
  same registry IDs/display IDs/path selectors used by MCP `workspace_id`: display ID, unique prefix, full ID,
  registered root path, `current`, or `primary`.
- `search --mode file --json` intentionally returns symbol rows from matching files (`name`, `kind`, `file`,
  `line`, `symbol_id`) for compatibility with the normal search JSON contract. Use compact text when an
  interactive caller wants the file-first rendering, or `mode=content|source|all-text` when the caller needs
  path/line/snippet text hits.
- Text headings and ordering are intended to be stable enough for humans and logs, not for strict parsers.
  Use `--json` when a caller needs fields.
- Search result kinds are deliberately separate: symbol search ranks `name + signature`, `--mode content`
  searches docs-like file content, `--mode source|external|web|all-text` searches explicit content-corpus text,
  and `--regions` searches explicit source regions when region indexing is enabled.
- The `content` CLI stores non-workspace text in `.miller/content.db`. Use `content import` for logs/reports
  and `content add-markdown <path> --url <url>` for browser-fetched pages. Search web imports with
  `content search "<phrase>" --kind web`, then read bounded windows with `content read --source-id <id>`.
- `content search "<term>" --workspace-id all --kind source|docs|config|external_file|web` searches registered
  workspace content DBs and reports workspace/display IDs on every hit for audits.
- `content export [--kind KIND] [--content-workspace-id ID]` writes deterministic JSONL chunk rows for Eros
  and other local consumers. It includes raw chunk text; use it as an integration feed, not as an interactive
  reading shortcut.
- `telemetry export --jsonl [--workspace-id ID|all]` writes machine-global telemetry rows as JSONL for local
  dashboard/history consumers. It exports stored target hashes, not raw queries.
- Eros-facing CLI contracts live in `docs/contracts/cli-eros-v1.md`; content export fields live in
  `docs/contracts/content-corpus-v1.md`.
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
- Region search is explicit and opt-in: set `MILLER_REGION_INDEX=1`, refresh the workspace, then
  call `search --regions comment|doc_comment|string_literal`. Set `MILLER_REGION_MAX_BYTES=<n>` to lower
  or raise the per-region byte cap for very large comment/string-literal corpora.
- Ambiguous targets may need a file path, a more specific symbol, or a symbol ID. The CLI reports ambiguity
  instead of guessing.
- Bridge trace (`trace mode=bridge`) is provider-scoped, not a general all-language feature. The current
  provider is the `dotnet-web` stack (ASP.NET controllers ↔ TypeScript/JS client URL calls ↔ AutoMapper ↔
  Entity Framework), so do not expect cross-language bridge results on another stack. It intentionally uses
  the full bridge graph for that provider-scoped evidence. Normal `search`, `inspect`, graph-only `context`,
  `impact`, non-bridge `trace`, and workspace status/list stay on projection-specific read paths.
- The main `miller` release binary publishes with Native AOT (no .NET SDK required to run it). The packaged
  dashboard helper stays self-contained/non-AOT because ASP.NET Razor Components do not yet support Native AOT.
- A rebuilt MCP server is picked up only after the MCP client restarts the Miller subprocess. Use
  `workspace status` and compare the `pid` in the header to confirm the restart actually loaded a new process.

## Troubleshooting

- `no Miller index`: run `miller workspace full`, or open the folder in the Miller MCP server so the
  index can be created. If the missing target is another repo, run `miller workspace open --path /absolute/repo --full`
  or MCP `workspace operation=open path=/absolute/repo`, then pass that repo's selector as `workspace_id`.
- Missing `julie-extract`: run the restore script for your platform, then rerun the scale or refresh path.
- Unsure which server is live: run `miller version` or `miller workspace status`; compare the git SHA suffix
  with the build you expect, and compare `workspace status`'s `pid` before/after a restart.

## License

MIT
