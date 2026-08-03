# Miller

Miller is a local code-intelligence server for coding agents. It keeps a current SQLite-backed index of
a workspace and answers structural questions over MCP and a matching CLI: find symbols, inspect files,
build focused context, trace relationships, and assess change impact, so an agent does not have to grep
and reread the repo by hand.

In a paired benchmark, the same agent got 2.2x more tasks right with Miller than with grep and file
reads (11/15 vs 5/15), with a 0% vs 27% wrong-action rate. Doubling the bare agent's call and token
budget made it worse, not better. Method, caveats, and raw evidence are in
[the calibration finding](docs/findings/2026-07-29-miller-vs-bare-agent-calibration.md), on the
[summary page](https://anortham.github.io/miller/benchmark.html), and on the
[method page](https://anortham.github.io/miller/method.html).

Miller is deterministic, local-first, and runs without a daemon. Semantic retrieval is on by default,
fully local, and off-switchable; lexical-only results stay byte-identical either way. The extraction
layer ([`julie-extractors`](https://github.com/anortham/julie-extractors)) is hand-written across all
[38 supported languages](#supported-languages), so it reaches structure shell search cannot: framework route facts across ~25
framework families, dependency-injection registrations as real graph edges, partial classes linked
across files, SQL DDL/DML shapes, and owned grammar forks (Razor, T-SQL, C#) where the ecosystem had
gaps. The full argument is
[hand-written extractors, not query files](https://anortham.github.io/julie-extractors/extractors.html).

> **Current release: [v1.15.0](https://github.com/anortham/miller/releases/tag/v1.15.0)** ·
> Website: [anortham.github.io/miller](https://anortham.github.io/miller/)

## Quickstart

The fastest path is the agent plugin. Its launcher downloads the Miller release archive for your
platform, verifies the checksum, and starts `miller serve` as an MCP server. The archive bundles the
pinned `julie-extract` binary, so there is nothing else to install.

> Plugin installs need [Node.js](https://nodejs.org/) on `PATH` (the launcher is a Node script). Without
> it the plugin fails with the opaque MCP error `-32000` and writes no Miller log. Install Node.js LTS
> and restart your agent. The manual install paths run the `miller` binary directly and skip Node.

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

Cursor: install Miller from the Cursor plugin marketplace.

Then ask your agent to search, inspect, or trace something. Miller binds the workspace from MCP client
roots on the first tool call and writes its index under that workspace's `.miller/` directory.

Every other install path is covered step by step in [docs/install.md](docs/install.md):

- **Manual binary:** download an archive from the
  [v1.15.0 release](https://github.com/anortham/miller/releases/tag/v1.15.0), verify its `.sha256`
  sidecar, extract it, and point your MCP client at the binary. No .NET SDK or Node.js required.
- **Any other MCP harness:** same binary, plus a routing block from `miller rules --harness <name>`.
- **Source checkout (development):** needs the .NET 10 SDK, then
  `bash scripts/restore-julie-extract.sh && dotnet build Miller.slnx -c Release`.

The minimal MCP config for clients you configure by hand:

```json
{
  "mcpServers": {
    "miller": {
      "type": "stdio",
      "command": "/absolute/path/to/miller-1.15.0-aarch64-apple-darwin/miller",
      "args": ["serve"]
    }
  }
}
```

On Windows, use the full path to `miller.exe` as `command`. If your client lacks MCP roots support, set
`"env": { "MILLER_WORKSPACE_ROOT": "/absolute/path/to/project" }` on the server entry.

## Getting agents to use Miller

Installing the MCP server does not guarantee an agent will use it. Newer harnesses defer MCP tool
schemas behind on-demand tool search, so Miller's tools may not be in the model's context when it picks
an exploration strategy, and the built-in grep and read tools always are. The reliable fix is a short
routing block in instructions the model always sees: `CLAUDE.md` for Claude Code, `AGENTS.md` for
Codex, or a Cursor rule.

```bash
miller rules --harness cursor > .cursor/rules/miller.mdc
```

`miller rules --harness <name>` prints the block framed for that harness's rules file
(`cursor`, `windsurf`, `cline`, `kiro`, `copilot`, `agents`). The Claude Code and Codex plugins inject
the same guidance automatically at session start through a `SessionStart` hook; set
`MILLER_SESSION_HOOKS=0` to opt out. To paste the block by hand, copy it from
[docs/agent-setup-snippet.md](docs/agent-setup-snippet.md).

Miller's embedded MCP server instructions are kept to a ~1,900-character discovery core on purpose
(Claude Code truncates merged server instructions at roughly 2KB). The full workflow catalog, the
subagent-dispatch primer, and per-tool parameter detail live in
[docs/agent-guidance.md](docs/agent-guidance.md); plugin users also get the same depth through the
`miller-*` skills.

## The tools

Nine MCP tools, each with a matching CLI verb and defaults chosen so the common path is the simplest
call. Targets are smart strings, not JSON objects.

| Tool | What it answers |
|---|---|
| `search` | ranked symbol, text, file, marker, docs/config, and source-body search |
| `inspect` | a file's symbols, or a named symbol's signature, docs, refs, callers, body |
| `context` | a token-budgeted bundle of entry-point symbols for a task or unfamiliar area |
| `trace` | exact references, shortest dependency paths, cross-language route bridges |
| `impact` | blast radius and likely tests for a symbol, file, or git diff |
| `edit` | index-aware replace/rename/body rewrite with a diff preview before apply |
| `patterns` | pre-extracted code-shape facts: routes, config keys, document structure |
| `content` | import and search logs, CI output, and web pages without full-file reads |
| `workspace` | index lifecycle: status, refresh, health, list, onboarding, dashboard |

A typical flow: `search` to find candidates, `inspect --depth overview` for a bounded first read of a
symbol, `trace` or `impact` before changing anything, `edit` with its diff preview for the change
itself, and `inspect --depth full` only when you need a complete body.

Read tools accept a `workspace_id` selector (display ID, unique prefix, full ID, registered root path,
`current`, or `primary`) for reading other registered workspaces. The CLI adds `miller metrics`
(churn, clones, complexity, history trends) and a local ops dashboard. Full detail:
[docs/cli.md](docs/cli.md) for the CLI and dashboard,
[docs/findings/miller-toolbox.md](docs/findings/miller-toolbox.md) for the tool catalog, and
[docs/known-limits.md](docs/known-limits.md) for current boundaries.

## Supported languages

Miller indexes what the pinned extractor parses. `julie-extract` 2.23.1 ships hand-written extractors
for **38 languages**:

- **Systems and compiled:** Rust (`.rs`), C (`.c`, `.h`), C++ (`.cpp`, `.cc`, `.cxx`, `.c++`, `.hpp`,
  `.hh`, `.hxx`, `.h++`), Go (`.go`), Zig (`.zig`), Swift (`.swift`), Java (`.java`),
  Kotlin (`.kt`, `.kts`), Scala (`.scala`, `.sc`), C# (`.cs`), VB.NET (`.vb`), Dart (`.dart`)
- **Scripting and dynamic:** Python (`.py`, `.pyi`, `.pyw`), Ruby (`.rb`, `.rbw`),
  PHP (`.php`, `.phtml`), Elixir (`.ex`, `.exs`), Erlang (`.erl`, `.hrl`), Lua (`.lua`), R (`.r`, `.R`), Bash (`.sh`, `.bash`),
  PowerShell (`.ps1`, `.psm1`, `.psd1`), GDScript (`.gd`)
- **Web and UI:** TypeScript (`.ts`, `.mts`, `.cts`), TSX (`.tsx`), JavaScript (`.js`, `.mjs`, `.cjs`),
  JSX (`.jsx`), Vue (`.vue`), HTML (`.html`, `.htm`), CSS (`.css`), Razor (`.razor`, `.cshtml`),
  QML (`.qml`)
- **Data, docs, and query:** SQL (`.sql`), JSON (`.json`, `.jsonl`, `.jsonc`), YAML (`.yml`, `.yaml`),
  TOML (`.toml`), XML (`.xml`, `.xsd`, `.wsdl`), Markdown (`.md`, `.markdown`), Regex (`.regex`)

Depth is not uniform, and it should not be: the programming languages get symbols with real signatures,
doc comments, identifiers, relationships, types, complexity metrics, and structural facts, while the
data and markup formats get the subset that means something for them (JSON/YAML/TOML/Markdown carry
document structure and symbols, not type or reference resolution). Framework route facts,
dependency-injection edges, and cross-file linkage ride on top of the language extractors for roughly
25 framework families.

Two ways to check this list yourself instead of trusting the README:

```bash
.tools/julie-extract languages --json   # authoritative catalog for the pinned extractor
miller workspace health                 # per-language rows for what is actually in your workspace
```

Adding a language is `julie-extractors` work, not Miller work: Miller consumes whatever the pinned
extractor emits, so a new language shows up here after a pin bump.

## How it works

Miller does not own tree-sitter extraction or embedding generation. Parser-backed extraction is
delegated to the pinned `julie-extract` binary (Rust + tree-sitter), which writes symbols, identifiers,
files, and relationships into SQLite; embeddings come from the pinned `julie-semantic-sidecar` binary.
Miller is the pure-.NET host on top:

```
MCP client (Claude Code / Cursor / Codex)
        │ stdio / MCP
        ▼
  Miller.Server      MCP host + CLI + telemetry
   ├─ Miller.Core     pure logic, zero I/O: BM25 ranking, resolver, graph, result contracts
   └─ Miller.Indexing infrastructure: julie-extract subprocess, SQLite readers, sidecar writers
        ▼
  .miller/symbols.db  julie-extract output
  .miller/search.db   symbol FTS recall
  .miller/content.db  source/docs/web text
```

Design choices that follow from this:

- Lexical ranking stays deterministic in C#. The default-on semantic arm lives in a separate
  `vectors.db` and is fused after ranking, so disabling it (`MILLER_SEMANTIC=off`) leaves lexical
  output byte-identical.
- There is no standalone daemon. SQLite WAL is the read-concurrency primitive, so many reader
  instances (agent teams, git worktrees, the dashboard) share local artifacts. Refresh and sidecar
  writes are explicit Miller operations.
- Workspace state matters: freshness, refresh, selectors, registry, telemetry, and the dashboard are
  part of the product, and telemetry-derived onboarding can summarize how agents have used a repo
  without storing raw queries. Stale or corrupt search sidecars fail visibly instead of silently
  lying (refresh the workspace, or set `MILLER_SEARCH_SIDECAR=0` to debug the in-memory fallback).
- CLI and MCP calls share the same read cores, so every example in these docs can be run in a shell
  and trusted to match what an agent sees. Cross-language bridge evidence stays structural and
  provider-scoped, not embedding-driven.
- The logic/infrastructure seam is hard: `Miller.Core` has zero I/O dependencies, so ranking and the
  resolver are unit-tested in milliseconds. This keeps the default test suite fast.

## Replacing Julie

Julie is retired at v7.17.0; Miller plus `julie-extractors` is its replacement. The takeover program
closed with Miller v1.14.0 on 2026-07-28, and the sealed paired gate was
[cancelled as superseded](docs/findings/2026-07-28-sealed-gate-disposition.md) because it would have
measured a retired baseline. Miller took over Julie's
deterministic local agent-tool core (search, inspect, context, references, trace, impact, editing,
workspace lifecycle, content import, structural facts, marker audits, telemetry, JSON/JSONL feeds, and
the local `metrics`/`report` analysis surfaces). `julie-extractors` owns parser-backed extraction.
Fleet-level concerns (cross-workspace ranking, guidance and confidence views, embeddings-as-a-service)
stay out of scope by design; local single-workspace semantic retrieval belongs to Miller per
[ADR-0003](docs/adr/ADR-0003-semantic-retrieval-ownership.md). Julie users should follow the
[migration guide](docs/migration-from-julie.md); Miller does not uninstall Julie or delete `.julie/`.

## Development

```
src/
  Miller.Core/       pure logic, ZERO I/O deps: ranking, resolver, graph, result contracts
  Miller.Indexing/   infrastructure: julie-extract subprocess, SQLite readers, sidecar writers
  Miller.Server/     MCP stdio host, the tool surface, the telemetry interceptor + ledger
  Miller.Dashboard/  narrow loopback ops dashboard reading registry, telemetry, artifact facts
tests/
  Miller.Tests/      unit (Core, fast) + contract tests + tagged scale set
docs/
  README.md          current-vs-historical documentation map
```

```bash
dotnet build Miller.slnx -c Release   # warnings are errors
scripts/test.sh                       # fast suite (default), <30s budget tripwire
scripts/test.sh scale                 # scale suite (spawns the real julie-extract)
scripts/test.sh all                   # both
```

PowerShell mirrors exist as `scripts/test.ps1`. The suite is split so the dev loop stays fast: a bare
`dotnet test` runs only the fast suite (`Category!=Scale`, pure logic and contract tests, no
subprocess), and scale tests skip rather than fail when `.tools/julie-extract` is absent. Convention
guards fail the build if a julie-spawning test is missing the Scale trait. Setup, restore scripts, and
local plugin development are in [docs/install.md](docs/install.md); the full documentation map is
[docs/README.md](docs/README.md).

## Troubleshooting

- Plugin install shows `failed` in `/mcp` with error `-32000` and no Miller log: Node.js is missing
  from `PATH`. Install Node.js LTS (for example `winget install OpenJS.NodeJS.LTS` on Windows), then
  fully restart the agent; an in-session reconnect keeps the old environment and still fails.
- `no Miller index`: run `miller workspace full`, or open the folder in the Miller MCP server so the
  index can be created. If the missing target is another repo, run
  `miller workspace open --path /absolute/repo --full`, then pass that repo's selector as
  `workspace_id`.
- Cursor shows duplicate or stale Miller rows (`user-miller`, `plugin-miller-miller`): remove extra
  `miller` entries from `~/.cursor/mcp.json`, move aside `~/.cursor/plugins/local/miller` if it
  exists, and reload Cursor.
- Cursor fails with `Could not determine a Miller workspace root`: open a project folder (Miller binds
  via MCP `roots/list` on the first tool call), or set `MILLER_WORKSPACE_ROOT` to an absolute project
  path in the MCP server env. Do not use `${workspaceFolder}` in user-global config; it often stays
  unresolved.
- Search results come from the wrong repo: reload the window or run `workspace status` and confirm the
  header root matches the open project. Pass an explicit `workspace_id` for another registered
  workspace when needed.
- Missing `julie-extract`: for a plugin or release-archive install, re-extract the full archive and
  keep its `.tools/` directory beside the `miller` binary; for a source checkout, run the restore
  script for your platform.
- Unsure which server is live: run `miller version` or `miller workspace status`; compare the git SHA
  suffix with the build you expect, and compare `workspace status`'s `pid` before and after a restart.

## License

MIT
