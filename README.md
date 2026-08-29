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
fully local, and off-switchable; lexical-only results stay byte-identical either way. It does need a
one-time embedding-model download — run [`miller semantic prepare`](#enable-semantic-retrieval) once, or
Miller keeps serving lexical-only. [Continuous testing](#continuous-testing) is opt-in and off by
default: `tests status` is a cheap read that starts nothing; `tests start` / `miller tests serve` is the
only daemon start. Set `MILLER_CT=off` for a permanent zero-work switch. The extraction
layer ([`julie-extractors`](https://github.com/anortham/julie-extractors)) is hand-written across all
[38 supported languages](#supported-languages), so it reaches structure shell search cannot: framework route facts across ~25
framework families, dependency-injection registrations as real graph edges, partial classes linked
across files, SQL DDL/DML shapes, and owned grammar forks (Razor, T-SQL, C#) where the ecosystem had
gaps. The full argument is
[hand-written extractors, not query files](https://anortham.github.io/julie-extractors/extractors.html).

> **Current release: [v1.25.2](https://github.com/anortham/miller/releases/tag/v1.25.2)** ·
> Website: [anortham.github.io/miller](https://anortham.github.io/miller/)

## Quickstart

The fastest path is the agent plugin. Its launcher downloads the Miller release archive for your
platform, verifies the checksum, and starts `miller serve` as an MCP server. The archive bundles the
pinned `julie-extract` binary and the embedding sidecar, so the only extra step is the one-time
[embedding-model download](#enable-semantic-retrieval) that turns semantic retrieval on.

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
roots on the first tool call and writes its index under that workspace's `.miller/` directory. To watch
tests, ask the agent to enable continuous testing (`tests enable`) then start it (`tests start`); status
never starts the daemon.

Every other install path is covered step by step in [docs/install.md](docs/install.md):

- **Manual binary:** download a platform archive directly — [macOS arm64](https://github.com/anortham/miller/releases/download/v1.25.1/miller-1.25.1-aarch64-apple-darwin.tar.gz),
  [macOS x64](https://github.com/anortham/miller/releases/download/v1.25.1/miller-1.25.1-x86_64-apple-darwin.tar.gz),
  [Linux x64](https://github.com/anortham/miller/releases/download/v1.25.1/miller-1.25.1-x86_64-unknown-linux-gnu.tar.gz),
  or [Windows x64](https://github.com/anortham/miller/releases/download/v1.25.1/miller-1.25.1-x86_64-pc-windows-msvc.zip). Verify the matching `.sha256`
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
      "command": "/absolute/path/to/miller-1.25.1-aarch64-apple-darwin/miller",
      "args": ["serve"]
    }
  }
}
```

On Windows, use the full path to `miller.exe` as `command`. If your client lacks MCP roots support, set
`"env": { "MILLER_WORKSPACE_ROOT": "/absolute/path/to/project" }` on the server entry.

### Enable semantic retrieval

Release archives bundle the embedding sidecar but **never the model weights**, and no Miller code path
downloads them for you. Semantic retrieval is on by default, so until the model is installed Miller
serves lexical-only and `workspace health` reports `degraded`. Install it once per machine:

```bash
miller semantic prepare
```

That fetches the default encoder (`bge-small-en-v1.5-f32`, 384 dimensions, ~134 MB) into a cache shared
with Julie — `%LOCALAPPDATA%\julie-semantic\` on Windows, `~/.cache/julie-semantic` elsewhere, or
`JULIE_EMBEDDING_CACHE_DIR` when set. `prepare` reports `activated` when it activates the running broker;
no restart is needed in that case. If it reports `no_live_broker` or `still_not_ready`, restart the MCP
server after preparation.

Confirm it took with `miller workspace status` — semantics are live when you see `vectors: ready` and
`semantic_broker: ready` with a resolved `backend` (`vulkan`, `metal`, or `cpu`). The larger
`qwen3-0.6b-f16` encoder is available via `miller semantic prepare --model qwen3-0.6b-f16` plus
`MILLER_SEMANTIC_MODEL=qwen3-0.6b-f16`, at ~1.2 GB and roughly 8x the build time. To skip semantics
entirely, set `MILLER_SEMANTIC=off` — a permanent zero-work guarantee that leaves lexical output
byte-identical.

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

Ten MCP tools, each with a matching CLI verb and defaults chosen so the common path is the simplest
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
| `tests` | continuous-test status (cheap, starts nothing); start is explicit; enable is opt-in |

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

QML support includes component symbols from `.qml`, module exports from `qmldir` and `.qmltypes` files,
visibility-aware component resolution, and the same `search`, `inspect`, `trace`, `patterns`, and `edit`
tools used for other first-class languages.

Two ways to check this list yourself instead of trusting the README:

```bash
.tools/julie-extract languages --json   # authoritative catalog for the pinned extractor
miller workspace health                 # per-language rows for what is actually in your workspace
```

Adding a language is `julie-extractors` work, not Miller work: Miller consumes whatever the pinned
extractor emits, so a new language shows up here after a pin bump.

## Continuous testing

Miller can watch a workspace and keep test verdicts as current as the index. It is opt-in per
workspace and off by default.

The unit of freshness is a test case, not a run. Every result is stamped with the index generation and
revision it was proved at. When a file changes, Miller advances that revision and asks the index which
symbols the change can reach: green cases the change cannot reach carry their verdict forward, and only
the impacted cases go stale. A run then executes the stale set as an explicit test-ID list, so a
one-line edit runs the tests that edit can break instead of the whole suite. An explicit run
(`tests run`) also retries every red case, because asking for a run means prove it again. Automatic
runs debounce on the trailing edge (2 seconds, `MILLER_CT_DEBOUNCE`); changes during a run queue a
follow-up instead of killing it.

Green means complete results at the current index key. If impact data is truncated, degraded, or
unavailable, Miller marks everything stale and runs nothing — there is no whole-suite fallback and no
optimistic green. An index rebuild changes the generation identity, which stales every stored result.

### Providers

| Ecosystem | Frameworks | Verification evidence |
|---|---|---|
| .NET | `dotnet`, `xunit`, `nunit`, `mstest` | yes — Miller's own suite |
| Rust | `cargo` | yes — `julie-extractors`, 4,173 cases |
| Python | `pytest` | yes — `more-itertools`, 736 tests |
| JavaScript and TypeScript | `vitest`, `jest`, `node-test` | yes — `jest` proven on `vercel/ms` (runs the suite once, under jest's default environment) |
| QML and Qt | `qt-quick-test` (CMake/CTest and qmake/QTest) | fixture and focused-test proof; no host with the Qt Quick Test development package |
| Go | `go` | yes — guarded single-module and multi-module fixtures on Go 1.24+ |

That is the whole supported set today. Support for more languages and frameworks is ongoing:
F#, Ruby, Java, PHP, and every other toolchain are not supported yet. `miller tests enable` on a
repo with no supported test project refuses with exit `3` and writes nothing, rather than leaving
the workspace enabled with zero projects.

`miller tests enable` discovers projects from the files already in the repo: test-signal `.csproj`
files, `.vbproj` and `.fsproj`, `Cargo.toml`, `go.mod`, `package.json`, the usual Python config
files, and Qt Quick Test `CMakeLists.txt` or `.pro` projects with runner evidence. JavaScript and
Python cases are discovered by each runner's own naming —
`*.test.*` / `*.spec.*` for vitest and jest (plus jest's `__tests__/` default and a literal
`testMatch` / `include` array when the config is readable), `test_*.py` or `*_test.py` for Python —
so a suite named some other way reports no cases rather than a false green. Go projects are one
project per `go.mod`; an in-root `go.work` supplies context but does not merge modules.

For .NET, CT runs either a built self-executing xUnit v3/Microsoft.Testing.Platform assembly or
the VSTest-compatible `dotnet test` path. MTP requires the built test application to prove MTP 1.7+
and a TRX report extension; unsupported or conflicting runner evidence fails closed. An xUnit v2
project builds no self-executing assembly and is refused with a migration diagnostic; migrate the
project to xUnit v3. `dotnet new xunit` still scaffolds v2 on SDK 10.0.400.

Go CT requires Go 1.24 or newer, uses `go list -json` and `go test -list` for discovery, and runs
package groups with anchored top-level `TestXxx` selectors. Child `t.Run` cases, benchmarks, fuzz
targets, examples, and function-level source identity are outside the current contract. QML
supports CMake/CTest and qmake Qt Quick Test projects; qmake selection is target-level and
requires a generated `check` target. CMake requires static Qt Quick Test and CTest registration.

The authoritative supported matrix, the full discovery rules, and the known limits live in
[docs/continuous-testing.md](docs/continuous-testing.md). Cross-repo evidence and the open provider
gaps are recorded in [the CT dogfood finding](docs/findings/2026-08-21-ct-cross-repo-dogfood.md).

### Safety

Continuous testing runs real test processes, so every part of it is explicit and bounded.

- Opt-in per workspace (`.miller/ct.enabled`), off until you enable it. `MILLER_CT=off` is a permanent
  zero-work switch: no daemon, no `ct.db` writes, honest status.
- `tests status` is a cheap read: it never creates `ct.db`, never creates `.miller/ct/`, and never
  starts the daemon. `tests start` / `miller tests serve` is the only start path.
- One workspace executes tests at a time, worktrees included, under a user-global budget. A run that
  finds the budget held reports it and executes nothing.
- The daemon runs from a private per-build copy under `~/.miller/ct-daemon/`, so a running daemon never
  locks the installed binary or your build output.
- A test process that goes silent for 10 minutes is treated as wedged: Miller kills its process tree and
  fails the run. The bound is on silence, not total duration, so a slow suite survives
  (`MILLER_CT_STALL_TIMEOUT`).
- Providers write build, result, and temp artifacts only under supervised CT paths, never into your
  workspace `bin`/`obj`.

### Quickstart

```bash
miller tests enable     # discover test projects and opt this workspace in
miller tests serve      # start the daemon: the only start path
miller tests status     # verdict, stale count, daemon state
miller tests failures   # the red cases with failure summaries
miller tests run        # run the stale set plus every red case
miller tests stop
```

Agents reach the same operations through the `tests` MCP tool
(`status|failures|start|stop|enable|disable|run`). A linked git worktree inherits the main checkout's
opt-in and is adopted by one family daemon, with its own `ct.db` and its own index key; a local
`tests disable` writes a tombstone that beats the inheritance. The JSON shapes are documented in
[docs/contracts/tests-cli-v1.md](docs/contracts/tests-cli-v1.md).

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
   ├─ Miller.Indexing infrastructure: julie-extract subprocess, SQLite readers, sidecar writers
   └─ Miller.Testing  continuous testing: ct.db, providers, explicit-start daemon
        ▼
  .miller/symbols.db  julie-extract output
  .miller/search.db   symbol FTS recall
  .miller/content.db  source/docs/web text
  .miller/ct.db       continuous-test verdicts (opt-in)
```

Design choices that follow from this:

- The versioned family store is on by default: one producer-owned store serves a repository family,
  while each checkout reads its own coherent view. Set `MILLER_INDEX_STORE=off` only for legacy
  standalone compatibility; Miller exports the current view before rolling back.
- Lexical ranking stays deterministic in C#. The default-on semantic arm lives in a separate
  `vectors.db` and is fused after ranking, so disabling it (`MILLER_SEMANTIC=off`) leaves lexical
  output byte-identical.
- The index host is not a daemon. SQLite WAL is the read-concurrency primitive, so many reader
  instances (agent teams, git worktrees, the dashboard) share local artifacts. Refresh and sidecar
  writes are explicit Miller operations. Continuous testing, when enabled, runs as `miller tests serve`
  — the same binary, a separate process, started only by an explicit serve/start.
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
scripts/test.sh                       # fast suite (default), report-only local wall time
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
