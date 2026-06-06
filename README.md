# Miller

A fast, token-thrifty, local code-intelligence MCP server for AI coding assistants, built in .NET 10.

Miller indexes a codebase and answers structural questions about it (find, inspect, trace references, assess
change impact) over the Model Context Protocol, so an agent spends tokens on reasoning instead of grepping and
re-reading files. Its differentiator is a **deterministic cross-language structural resolver** that links code
across language boundaries (e.g. a C# entity to its EF table, a TypeScript call to the C# route that serves it)
without embeddings.

> **Status: WIP replacement for Julie.** The core MCP tool surface, freshness path, workspace registry, and
> registry-backed dashboard are implemented on the active development branch. See
> [docs/miller-mvp-plan.md](docs/miller-mvp-plan.md) for the milestone line and remaining hardening work.

## Requirements

- **.NET 10 SDK** installed and on `PATH` for source-checkout use, including the checked-in
  [`mcp-config.json`](mcp-config.json). `dotnet --version` should report a `10.x` SDK.
- A restored pinned `julie-extract` binary in `.tools/` for indexing and scale tests. The restore scripts
  download the platform-matched binary and verify its pinned SHA-256 digest.
- Release archives are built from the same .NET 10 projects and are self-contained per platform, but Native
  AOT is still deferred beta hardening work.

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
  miller-mvp-plan.md           milestones M0–M7
  findings/                    the investigation this design was mined from
```

Miller keeps only the local operational dashboard: registered workspaces, freshness, telemetry, sidecar
health, and refresh/troubleshooting actions. Eros owns richer product UX such as next-action guidance,
confidence/evidence views, semantic/vector retrieval, and commercial workflows.

## The tool surface (target)

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
  and exits — for shells, CI, and integration tests. The CLI reuses the *same* tool cores the server exposes,
  so output matches a tool call.

  ```bash
  dotnet run --project src/Miller.Server -c Release -- search "WorkspaceIndexProvider" --limit 5
  dotnet run --project src/Miller.Server -c Release -- search "source-checkout beta" --mode content --limit 5
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
`workspace status` — so a session can always confirm *which* build it is talking to.

**Dashboard.** The local dashboard binds to loopback and reads only the workspace registry plus telemetry DB.
Use the CLI launcher so multiple Miller sessions reuse one machine-global dashboard process while opening the
current workspace selector. `--port` selects the launch port only when no healthy dashboard is already running:

```bash
miller dashboard
miller dashboard --port 4977
```

Open the printed URL to view registered workspaces and scoped per-tool telemetry. Set `MILLER_REGISTRY_DB`,
`MILLER_TELEMETRY_DB`, or `MILLER_DASHBOARD_WEBROOT` only when testing non-default paths.

## Release archives

The release workflow is configured for one archive per pinned `julie-extract` platform:

- `aarch64-apple-darwin`
- `x86_64-apple-darwin`
- `x86_64-unknown-linux-gnu`
- `x86_64-pc-windows-msvc`

Each archive contains `miller`, the matching `.tools/julie-extract` binary, the loopback dashboard binary
under `dashboard/`, and `dashboard/wwwroot/dashboard.css`. The workflow also uploads a `.sha256` sidecar for
each archive and smoke-runs both `julie-extract --version` and `miller version` before packaging.

## CLI output expectations

For beta, text output is a compact human-facing contract and JSON output is the integration contract.

- Exit code `0` means success, `2` means usage/argument error, and `3` means no usable index, refused
  workspace operation, missing restore, or another operational failure a script should not ignore.
- `--json` is supported by `search`, `inspect`, `context`, `impact`, and `workspace` operations. `trace`
  is text-only for now.
- Text headings and ordering are intended to be stable enough for humans and logs, not for strict parsers.
  Use `--json` when a caller needs fields.
- Search result kinds are deliberately separate: symbol search ranks `name + signature`, `--mode content`
  searches docs-like file content, and `--regions` searches explicit source regions when region indexing
  is enabled.

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
- Full `inspect` can still be expensive on very large repositories; summary `inspect`, `search`,
  `context`, and workspace status/list are the fast dogfood paths.
- Native AOT is release-readiness work, not a beta blocker.
- A rebuilt MCP server is picked up only after the MCP client restarts the Miller subprocess.

## Troubleshooting

- `no Miller index`: run `miller workspace full`, or open the folder in the Miller MCP server so the
  index can be created.
- Missing `julie-extract`: run the restore script for your platform, then rerun the scale or refresh path.
- Unsure which server is live: run `miller version` or `miller workspace status` and compare the git SHA
  suffix with the build you expect.

## License

MIT
