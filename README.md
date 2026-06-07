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
- stale or corrupt search sidecars self-heal to correct in-memory search instead of silently lying;
- cross-language bridge evidence stays structural and provider-scoped, not embedding-driven.

> **Status: source-checkout beta candidate.** M0-M8 are implemented, the source-checkout beta gates are closed
> from Miller's side, and the remaining decisions are product/release decisions. See
> [docs/plans/2026-06-05-beta-readiness-checklist.md](docs/plans/2026-06-05-beta-readiness-checklist.md).

## Requirements

- **.NET 10 SDK** installed and on `PATH` for source-checkout use, including the checked-in
  [`mcp-config.json`](mcp-config.json). `dotnet --version` should report a `10.x` SDK.
- A restored pinned `julie-extract` binary in `.tools/` for indexing and scale tests. The restore scripts
  download the platform-matched binary and verify its pinned SHA-256 digest.
- Release archives are built from the same .NET 10 projects and are self-contained per platform. The main
  `miller` binary publishes with Native AOT, so it runs without a .NET SDK; only the packaged dashboard
  helper stays self-contained/non-AOT because ASP.NET Razor Components do not yet support Native AOT.

## How it works

Miller does **not** parse source code itself and does **not** use embeddings. Extraction is delegated to a
prebuilt `julie-extract` binary (Rust + tree-sitter) that writes symbols, identifiers, files, and
relationships into a SQLite database. Miller is the pure-.NET host on top:

```
┌───────────────────────────┐     stdio / MCP
│  Claude Code / MCP client  │◀──────────────────┐
└───────────────────────────┘                    │
                                       ┌──────────────────────┐
                                       │   Miller.Server       │  MCP host + 7 tools + telemetry ledger
                                       └──────────────────────┘
                                                  │
                          ┌───────────────────────┼───────────────────────┐
                          ▼                                                 ▼
              ┌──────────────────────┐                        ┌──────────────────────┐
              │     Miller.Core       │                        │   Miller.Indexing     │
              │  (pure logic, no I/O) │                        │  (infrastructure)     │
              │  • in-memory index    │                        │  • julie-extract      │
              │    + BM25 ranking     │◀──── populated from ───│    subprocess         │
              │  • cross-lang resolver│                        │  • SQLite (WAL) read  │
              └──────────────────────┘                        │  • watcher / indexer  │
                                                               └──────────────────────┘
                                                                          │
                                                              ┌──────────────────────┐
                                                              │  SQLite extract DB    │
                                                              │  (from julie-extract) │
                                                              └──────────────────────┘
```

Design choices that follow from this:
- **No embeddings in the default path** — search is BM25 over an in-memory inverted index rebuilt from SQLite at
  startup. Indexes are small and rebuild in seconds.
- **No custom daemon** — SQLite WAL is the read-concurrency primitive, so many reader instances (agent teams, git
  worktrees, the dashboard) share one index. The writer/indexer is a separate, optional process whose death
  degrades freshness, not reads.
- **Hard logic↔infrastructure seam** — `Miller.Core` has zero I/O dependencies, so the ranking and the resolver
  are unit-tested in milliseconds with no live DB, subprocess, or transport. This keeps the default test suite fast.

## Project structure

```
src/
  Miller.Core/       pure logic, ZERO I/O deps: contract record types, in-memory index + BM25, the resolver
  Miller.Indexing/   infrastructure: julie-extract subprocess, SQLite (WAL) read layer, watcher/indexer
  Miller.Server/     MCP stdio host, the 7 tools, the telemetry interceptor + ledger
  Miller.Dashboard/  narrow loopback ops dashboard reading the registry + telemetry DB
tests/
  Miller.Tests/      unit (Core, fast) + contract (against a committed extract-DB fixture) + tagged scale set
docs/
  miller-mvp-plan.md           milestone history
  plans/                       beta/readiness/design routing docs
  findings/                    dogfood evidence and investigation notes
```

Miller keeps only the local operational dashboard: registered workspaces, freshness, telemetry, sidecar
health, and refresh/troubleshooting actions. Eros owns richer product UX such as next-action guidance,
confidence/evidence views, semantic/vector retrieval, and commercial workflows.

## The tool surface

Seven tools, each with smart defaults so the common path is the simplest call: `search`, `inspect`, `context`,
`trace`, `impact`, `edit`, `workspace`. Read tools accept a `workspace_id` selector: display ID, unique prefix,
full ID, `current`, or `primary`. Explicit `workspace_id` defaults `ensure_fresh=true`. Targets are smart strings, not JSON objects. See
[docs/findings/miller-toolbox.md](docs/findings/miller-toolbox.md).

## Running Miller

From a source checkout, restore the extractor once, build, then open or refresh a workspace:

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

The single `miller` binary runs two ways:

- **MCP server (default).** With no arguments — or the explicit `serve` verb — Miller speaks the MCP protocol
  over stdio. This is how an MCP client (Claude Code, etc.) connects; see [`mcp-config.json`](mcp-config.json):

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
  dotnet run --project src/Miller.Server -c Release -- search "source-checkout beta" --mode content --limit 5
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
  `trace`, `workspace`, `dashboard`, `version`, `serve`.

**Dogfooding the server.** Because MCP runs over stdio, a new build takes effect only after the MCP client
restarts the subprocess. A build made inside the repo carries its git short SHA — `miller version` prints
`0.1.0+<sha>` (just `0.1.0` for a build with no `.git`), and the same string heads the `# workspace` block of
`workspace status`. The status header also includes the process id (`pid <n>`), which is the quickest way to
confirm a restarted MCP client is talking to a new Miller subprocess when you rebuilt uncommitted changes and
the SHA suffix stayed the same.

**Dashboard.** The local dashboard binds to loopback and reads only the workspace registry plus telemetry DB.
Use the CLI launcher so multiple Miller sessions reuse one machine-global dashboard process while opening the
current workspace selector. From an MCP session, use the `workspace` tool's dashboard operation to start or
reuse the same dashboard without leaving the session. `--port` selects the launch port only when no healthy
dashboard is already running:

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

## Agent plugins

Miller's first plugin distribution path lives in this repository, not a separate `miller-plugin` repo:

- `.claude-plugin/plugin.json` exposes Miller to Claude Code.
- `.codex-plugin/plugin.json` and `.mcp.json` expose Miller to Codex.
- `skills/` is generated from `.agents/skills/` by `scripts/sync-plugin-skills.sh`.
- `bin/miller-plugin-launcher.cjs` downloads the configured GitHub release archive, verifies the `.sha256`
  sidecar, caches it under `~/.miller/plugin-cache/`, and runs `miller serve`.

The plugin launcher consumes the release version in `miller-plugin.json`. Until that GitHub release exists with
matching archives, use `MILLER_BINARY=/absolute/path/to/miller` to skip the download and run a local build.

Claude Code local-checkout install:

```bash
claude plugin marketplace add /path/to/miller
claude plugin install miller@miller
```

Codex local-checkout install:

```bash
codex plugin marketplace add /path/to/miller
codex plugin add miller@miller
```

After the marketplace is published from GitHub, use the repo source instead:

```bash
claude plugin marketplace add anortham/miller
claude plugin install miller@miller

codex plugin marketplace add anortham/miller
codex plugin add miller@miller
```

For local development against this checkout:

```bash
dotnet build Miller.slnx -c Release
export MILLER_BINARY="$PWD/src/Miller.Server/bin/Release/net10.0/miller"
claude
```

Cursor plugin support is intentionally deferred until Miller has an npm launcher or another reliable way for Cursor
to locate the installed plugin root. See
[docs/plans/2026-06-06-plugin-distribution-design.md](docs/plans/2026-06-06-plugin-distribution-design.md).

## Source-checkout beta proof

These commands are intentionally real Miller-checkout examples. Run them after the restore/build step above
from this repo, or replace `dotnet run --project src/Miller.Server -c Release --` with an installed `miller`
binary:

```bash
dotnet run --project src/Miller.Server -c Release -- workspace status
dotnet run --project src/Miller.Server -c Release -- workspace list
dotnet run --project src/Miller.Server -c Release -- search "WorkspaceIndexProvider" --limit 5
dotnet run --project src/Miller.Server -c Release -- search "source-checkout beta" --mode content --limit 5
dotnet run --project src/Miller.Server -c Release -- inspect src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md
dotnet run --project src/Miller.Server -c Release -- context "dashboard telemetry and workspace registry" --token-budget 1200
dotnet run --project src/Miller.Server -c Release -- impact src/Miller.Server/Tools/WorkspaceTool.cs --max-depth 1 --limit 10
```

What these prove:

- `workspace status` and `workspace list` read cheap registry/freshness metadata instead of hydrating the full
  graph.
- Symbol search stays narrow and structural (`name + signature`); prose uses `--mode content`.
- `inspect`, `context`, and `impact` use the same projection-specific read paths exposed to MCP tools.
- The dashboard is operational evidence, not a separate product UI: it shows registered workspaces, index facts,
  telemetry, latency/failure signals, and scoped JSON endpoints from the same local state.

## Release archives

The current beta path is source-checkout first. The release workflow is already configured for one archive per
pinned `julie-extract` platform when a packaged prerelease is approved:

- `aarch64-apple-darwin`
- `x86_64-apple-darwin`
- `x86_64-unknown-linux-gnu`
- `x86_64-pc-windows-msvc`

Each archive contains `miller`, the matching `.tools/julie-extract` binary, the loopback dashboard binary
under `dashboard/`, `dashboard/wwwroot/dashboard.css`, `LICENSE`, and `THIRD-PARTY-NOTICES.md`. The workflow
also uploads a `.sha256` sidecar for each archive and smoke-runs both `julie-extract --version` and
`miller version` before packaging.

### Install from a release archive

Source checkout remains the primary documented path, but release archives are self-contained: the main
`miller` binary is built with Native AOT, so a release machine does **not** need the .NET SDK to run it.

1. Download the archive for your platform from the GitHub release, plus its matching `.sha256` sidecar
   (for example `miller-<version>-aarch64-apple-darwin.tar.gz` and `…​.tar.gz.sha256`).
2. Verify the checksum, then extract:

   ```bash
   # macOS / Linux
   shasum -a 256 -c miller-<version>-aarch64-apple-darwin.tar.gz.sha256
   tar -xzf miller-<version>-aarch64-apple-darwin.tar.gz
   ```

   ```powershell
   # Windows
   (Get-FileHash .\miller-<version>-x86_64-pc-windows-msvc.zip -Algorithm SHA256).Hash
   # compare against the .sha256 sidecar, then extract
   Expand-Archive .\miller-<version>-x86_64-pc-windows-msvc.zip -DestinationPath .\miller
   ```

3. The extracted layout puts the `miller` binary next to its tooling and dashboard:

   ```text
   miller                 # the AOT binary
   .tools/julie-extract   # the matching pinned extractor
   dashboard/             # the packaged loopback dashboard helper (+ wwwroot/dashboard.css)
   LICENSE
   THIRD-PARTY-NOTICES.md
   ```

4. Point your MCP client at the **absolute path** of the extracted `miller` binary with the `serve` arg.
   No `dotnet run` and no .NET SDK are required for the AOT binary:

   ```json
   {
     "mcpServers": {
       "miller": {
         "command": "/absolute/path/to/extracted/miller",
         "args": ["serve"]
       }
     }
   }
   ```

   On Windows, use the full path to `miller.exe` as `command`.

## CLI output expectations

For beta, text output is a compact human-facing contract and JSON output is the integration contract.

- Exit code `0` means success, `2` means usage/argument error, and `3` means no usable index, refused
  workspace operation, missing restore, or another operational failure a script should not ignore.
- `--json` is supported by `search`, `inspect`, `context`, `impact`, and `workspace` operations. `trace`
  is text-only for now.
- `search`, `inspect`, `context`, `impact`, and `trace` accept `--workspace-id <selector>`; selectors are the
  same registry IDs/display IDs/path selectors used by MCP `workspace_id`.
- Text headings and ordering are intended to be stable enough for humans and logs, not for strict parsers.
  Use `--json` when a caller needs fields.
- Search result kinds are deliberately separate: symbol search ranks `name + signature`, `--mode content`
  searches docs-like file content, and `--regions` searches explicit source regions when region indexing
  is enabled.
- The `content` CLI stores non-workspace text in `.miller/content.db`. Use `content import` for logs/reports
  and `content add-markdown <path> --url <url>` for browser-fetched pages. Search web imports with
  `content search "<phrase>" --kind web`, then read bounded windows with `content read --source-id <id>`.
- `content export [--kind KIND] [--content-workspace-id ID]` writes deterministic JSONL chunk rows for Eros
  and other local consumers. It includes raw chunk text; use it as an integration feed, not as an interactive
  reading shortcut.
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

Windows PowerShell mirrors are available for the beta-critical scripts:

```powershell
scripts/test.ps1
scripts/test.ps1 scale
scripts/test.ps1 all
```

Two guards keep the split honest: a convention test
([`ScaleTraitConventionTests`](tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs)) fails the
build if any julie-spawning test is missing `[Trait("Category","Scale")]`, and CI time-budgets the fast
suite. A second convention guard requires Windows PowerShell mirrors for beta-critical scripts. To enable
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

## Known beta limits

- No embeddings or semantic/vector retrieval in Miller. If that is needed, Eros owns the projection.
- Region search is explicit and opt-in for beta: set `MILLER_REGION_INDEX=1`, refresh the workspace, then
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
  index can be created.
- Missing `julie-extract`: run the restore script for your platform, then rerun the scale or refresh path.
- Unsure which server is live: run `miller version` or `miller workspace status`; compare the git SHA suffix
  with the build you expect, and compare `workspace status`'s `pid` before/after a restart.

## License

MIT
