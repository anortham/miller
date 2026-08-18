# Miller CLI reference

The single `miller` binary runs two ways:

- **MCP server (default).** With no arguments, or the explicit `serve` verb, Miller speaks the MCP
  protocol over stdio. This is how an MCP client (Claude Code, Cursor, Codex, etc.) connects; see
  [`mcp-config.json`](../mcp-config.json) for the source-checkout config.
- **CLI (one-shot).** Any other verb runs a single command over the current directory's
  `.miller/symbols.db` and exits, which suits shells, CI, and integration tests. The CLI reuses the
  *same* tool cores the server exposes, so output matches a tool call.

Read verbs accept `--workspace-id <selector>` (`--workspace <path>` is a path alias) for registered
workspaces, so dogfood and CI calls can target another indexed repo without changing directories.
Selectors are the same registry IDs used by MCP `workspace_id`: display ID, unique prefix, full ID,
registered root path, `current`, or `primary`.

`miller help` lists every verb: `capabilities`, `search`, `todos`, `content`, `patterns`, `metrics`,
`telemetry`, `symbols`, `references`, `complexity`, `refresh`, `inspect`, `context`, `impact`, `trace`,
`dashboard`, `workspace`, `version`, `help`, and `serve`.

## Command examples

These run from a source checkout with `dotnet run`; with an installed binary, replace
`dotnet run --project src/Miller.Server -c Release --` with `miller`. Build once and run the binary
directly (`src/Miller.Server/bin/Release/net10.0/miller <verb>`) to skip the `dotnet run` up-to-date
check.

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

## Tool surface details

`trace` is the graph workflow tool: `mode=refs` lists name-based identifier references, `mode=path`
shows the shortest extracted graph path to `to`, and `mode=bridge` follows provider-scoped bridge
evidence. Current providers are `dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`,
`react`, `blazor`, and `backend-http`; they link route references to file routes/definitions, Blazor
navigation references to Razor page routes, client requests to Next.js/Nuxt route handlers, and client
requests (fetch/axios/`requests`/`httpx`/`net/http`/`Net::HTTP`/`HttpClient`/Ktor/Guzzle/Req/reqwest)
to Express/Fastify/FastAPI/Flask/Django/Spring/Go/gin/echo/Rails/NestJS/Laravel/Phoenix/axum/actix/
Symfony/Ktor route templates. It does not cover every framework route shape. No-path, unsupported
bridge, and ambiguous-target results include bounded next actions; JSON callers get the same guidance
in additive `next_actions` rows.

`content` is the large-text workflow: import or search the right content kind, keep the `source_id`
from each hit, then read a bounded line window. Empty searches and failed reads include recovery
guidance, diagnostic codes in JSON, and reminders to pass `workspace_id` when a hit came from a
cross-workspace search.

For cross-workspace code reading, stay in the current session and run `workspace list`. If the target
repo is registered, pass its selector as `workspace_id` to `search`, `inspect`, `context`, `impact`,
`trace`, or `patterns`. If it is not registered yet, run `workspace operation=open path=/absolute/repo`
from MCP or `miller workspace open --path /absolute/repo --full` from the CLI, then retry the read
tool. The CLI-only `miller metrics` commands accept the same single-workspace selectors. The
`workspace_id=all` selector is only for `content search` text audits across registered workspace
content DBs.

`patterns` is the structural-facts surface. It lists and searches known code-shape facts emitted by the
pinned `julie-extract` catalog, spanning framework facts (ASP.NET minimal API routes, htmx attributes,
Alpine directives), language facts (async/await, unsafe blocks, decorators, goroutines), SQL DDL/DML
shapes, and JSON/YAML/TOML/Markdown document structure. It is intentionally not a raw AST query
language:

```text
patterns()
patterns(operation="search", pattern_id="aspnet.minimal_api.route.v1", where="verb=GET", path="Program.cs")
patterns(operation="search", pattern_id="htmx.attribute.v1", where="attribute_name=hx-get", path="Views/**")
patterns(operation="search", pattern_id="alpine.directive.v1", where="directive=x-data", path="Views/**")
patterns(operation="search", query="route")
```

Start with `patterns()`/`patterns(operation="list")`; list and no-match output includes concrete next
actions, and search misses can suggest near pattern IDs or explain when active filters removed all
rows. Use `query` when you remember the kind of fact but not the exact `pattern_id`.

The CLI-only `miller metrics` command reports deterministic local facts: recent git churn mapped to
current symbols, identical body-hash clone groups, and complexity hotspots with transparent thresholds.
It is not semantic ranking or cleanup advice:

```bash
miller metrics churn --range HEAD~20..HEAD --limit 25
miller metrics clones --min-count 2
miller metrics complexity --min-severity moderate --exclude-tests
```

`miller metrics history` reads a per-workspace record of how those signals move over time (symbol
count, complexity p90, clone groups, and markers), recorded automatically
after each index converge and when the heavy commands run. It only reads the recorded trend
(append-only `.miller/history.db`); the same trends render as sparklines on the dashboard workspace
detail view, and `workspace health` reports the history sidecar's status and size. The `--json`
envelope is a stable contract ([`contracts/metrics-history-v1.md`](contracts/metrics-history-v1.md)):

```bash
miller metrics history
miller metrics history --metric complexity_p90,near_duplicate_group_count --limit 30 --json
```

## Dogfooding the server

Because MCP runs over stdio, a new build takes effect only after the MCP client restarts the
subprocess. A build made inside the repo carries its git short SHA: `miller version` prints
`<version>+<sha>` (just `<version>` for a build with no `.git`), and the same string heads the
`# workspace` block of `workspace status`. The status header also includes the process id (`pid <n>`),
which is the quickest way to confirm a restarted MCP client is talking to a new Miller subprocess when
you rebuilt uncommitted changes and the SHA suffix stayed the same.

## Dashboard

The local dashboard binds to loopback and reads the workspace registry, shared telemetry DB, and
read-only aggregate facts from each workspace's Miller artifacts. It does not hydrate full indexes. Use
the CLI launcher so multiple Miller sessions reuse one machine-global dashboard process while opening
the current workspace selector. From an MCP session, use the `workspace` tool's dashboard operation to
start or reuse the same dashboard without leaving the session. `--port` selects the launch port only
when no healthy dashboard is already running:

```bash
miller dashboard
miller dashboard --port 4977
```

```text
workspace(operation="dashboard")
workspace(operation="dashboard", port=4977)
```

Open the printed URL to view registered workspaces and scoped per-tool telemetry. Set
`MILLER_REGISTRY_DB`, `MILLER_TELEMETRY_DB`, or `MILLER_DASHBOARD_WEBROOT` only when testing
non-default paths. The selected-workspace detail view also surfaces local complexity hotspots and
body-hash clone groups from the artifact, plus metric-history trend sparklines (symbol count,
complexity p90, clone groups, markers) read from the workspace's
`.miller/history.db`. Git churn stays in the CLI-only `miller metrics churn` path because it reads a
revision range from git.

## Local proof commands

These commands are intentionally real Miller-checkout examples. Run them after the restore/build step
from the Miller repo, or replace `dotnet run --project src/Miller.Server -c Release --` with an
installed `miller` binary:

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

- `workspace status`, `workspace health`, `workspace onboarding`, and `workspace list` avoid
  source-file reads and full graph hydration; onboarding adds a read-only telemetry summary plus
  current-index target recovery.
- `workspace leader` reports leader identity/liveness and can request graceful handoff without killing
  processes.
- `workspace onboarding` turns local telemetry into starter commands, hot current-index targets, common
  misses, and friction signals with exact omission counts. Telemetry stores target hashes, not raw
  queries or raw target text.
- `workspace list` marks missing roots without opening their indexes. `workspace remove` deletes only a
  registered target and refuses live, sensitive, machine-global, corrupt-path, or write-locked targets.
- Symbol search stays narrow and structural (`name + signature`). Docs/config use `--mode content`;
  source bodies and imported text use the explicit content corpus modes.
- `patterns --json` discovers extractor-recognized code-shape facts across the full pattern catalog
  (framework routes, language constructs, SQL DDL, data-document structure) without private SQLite
  reads or raw AST queries.
- The dashboard surfaces local complexity and clone facts from workspace artifacts; `miller metrics`
  also exposes local churn without semantic ranking or cleanup workflow ownership.
- `inspect`, `context`, and `impact` use the same projection-specific read paths exposed to MCP tools.
- The dashboard is operational evidence, not a separate product UI: it shows registered workspaces,
  index facts, telemetry, latency/failure signals, and scoped JSON endpoints from the same local state.

## Output expectations

Text output is a compact human-facing contract and JSON output is the integration contract.

- Exit code `0` means success, `2` means usage/argument error, and `3` means no usable index, refused
  workspace operation, missing restore, or another operational failure a script should not ignore.
- `capabilities --json` reports the Miller build, `julie-extract` contract versions, optional feature
  flags, supported JSON commands, and export feeds for Eros/local integrations.
- `--json` is supported by `search`, `todos` (CLI alias for marker audits), `inspect`, `context`,
  `impact`, `trace`, `patterns`, `dashboard`, `content` operations, and `workspace` operations.
- `workspace onboarding --json [--workspace-id SELECTOR|--workspace DIR]` is a read-only, privacy-safe
  startup view for an indexed repo. It summarizes tool mix, successful flows, repeated current-index
  targets, common misses, and friction from the shared telemetry ledger.
- `refresh --json --wait [--workspace-id SELECTOR|--workspace DIR] [--full]` is the Eros-friendly
  top-level convergence command. It wraps the existing registered-workspace refresh path and includes
  sidecar facts.
- `search`, `inspect`, `context`, `impact`, `trace`, and `patterns` accept `--workspace-id <selector>`;
  selectors are the same registry IDs/display IDs/path selectors used by MCP `workspace_id`.
- `search --mode file --json` intentionally returns symbol rows from matching files (`name`, `kind`,
  `file`, `line`, `symbol_id`) for compatibility with the normal search JSON contract. Use compact text
  when an interactive caller wants the file-first rendering, or `mode=content|source|all-text` when the
  caller needs path/line/snippet text hits.
- Text headings and ordering are intended to be stable enough for humans and logs, not for strict
  parsers. Use `--json` when a caller needs fields.
- Search result kinds are deliberately separate: symbol search ranks `name + signature`,
  `--mode markers` audits TODO/FIXME/HACK/XXX comment markers, `--mode content` searches docs-like file
  content, `--mode source|external|web|all-text` searches explicit content-corpus text, and `--regions`
  searches explicit source regions when region indexing is enabled.
- `trace --mode refs` returns name-based identifier references for a resolved target symbol. Use
  `--reference-kind call|variable_ref|type_usage|member_access|import` to narrow the result and
  `--no-definition` when only reference rows are needed. `trace --mode path` no-path and
  `trace --mode bridge` unsupported results include next local calls to try; JSON includes them as
  `next_actions`.
- `todos --json` is a CLI compatibility alias over marker search for Eros/scripts. For agents and
  normal interactive use, prefer `search "TODO,FIXME,HACK,XXX" --mode markers`; it returns marker,
  file:line, snippet, and containing symbol when available, with `--file-pattern` and `--language` for
  scope.
- The `content` CLI stores non-workspace text in `.miller/content.db`. Use `content import` for
  logs/reports and `content add-markdown <path> --url <url>` for browser-fetched pages. Search web
  imports with `content search "<phrase>" --kind web`, then read bounded windows with
  `content read --source-id <id>`.
- `content search "<term>" --workspace-id all --kind source|docs|config|external_file|web` searches
  registered workspace content DBs and reports workspace/display IDs on every hit for audits. Pass the
  hit's workspace ID back to `content read --workspace-id <id>` for external/web hits from another
  workspace.
- `content export [--kind KIND] [--content-workspace-id ID]` writes deterministic JSONL chunk rows for
  Eros and other local consumers. It includes raw chunk text; use it as an integration feed, not as an
  interactive reading shortcut.
- `telemetry export --jsonl [--workspace-id ID|all]` writes machine-global telemetry rows as JSONL for
  local dashboard/history consumers. It exports stored target hashes, not raw queries.
- `symbols export --jsonl`, `references export --jsonl`, and `complexity export --jsonl` write
  deterministic artifact fact feeds for fleet rollups and Eros workflows. `references export` is a
  usage-fact feed, not a dead-code ranking tool.
- Eros-facing CLI contracts live in [`contracts/cli-eros-v1.md`](contracts/cli-eros-v1.md); workspace
  onboarding JSON fields in [`contracts/workspace-onboarding-v1.md`](contracts/workspace-onboarding-v1.md);
  trace JSON fields in [`contracts/trace-json-v1.md`](contracts/trace-json-v1.md); content export
  fields in [`contracts/content-corpus-v1.md`](contracts/content-corpus-v1.md); references export
  fields in [`contracts/references-export-v1.md`](contracts/references-export-v1.md).
- Search defaults to 6 results. Compact symbol rows include name, kind, file, line, and signature when
  available; use `--limit N` when you need a wider page.
