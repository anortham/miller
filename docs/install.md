# Installing Miller

Miller installs four ways. Pick the first one that fits:

| Path | Who it's for | Requirements |
|---|---|---|
| Agent plugin | Claude Code, Codex, and Cursor users | Node.js on `PATH` |
| Release archive | Any MCP client you configure by hand | none (self-contained) |
| Instruction tier | Any other MCP-speaking harness | none beyond the archive |
| Source checkout | Miller development | .NET 10 SDK on `PATH` |

Plugin and release-archive installs bundle the pinned `julie-extract` binary under `.tools/`; you never
install it separately, and no .NET SDK is needed to run the main `miller` binary (it is published with
Native AOT). The packaged dashboard helper is self-contained but non-AOT because ASP.NET Razor Components
do not currently support Native AOT.

Archives also bundle the `julie-semantic-sidecar` runtime but **never the model weights**. Semantic
retrieval is on by default, so every install needs the one-time
[embedding-model download](#enable-semantic-retrieval) before the semantic arm does any work.

## Plugin install

The plugin launcher downloads the Miller release archive that matches your platform, verifies its
`.sha256` sidecar, caches it under `~/.miller/plugin-cache/`, and starts `miller serve` as an MCP server.
The launcher consumes the release version pinned in `miller-plugin.json`.

> **Plugin installs require [Node.js](https://nodejs.org/) on `PATH`**: the launcher is a Node script
> (declared as `command: "node"` in the plugin manifests). If Node.js is missing (common with Claude
> Code's native installer, which does not itself need Node), the plugin fails to connect with the opaque
> MCP error `-32000` and writes no Miller log. Install Node.js LTS and fully restart your agent so the
> new `PATH` is picked up; an in-session reconnect keeps the old environment and still fails.

> **The first launch after a version change downloads the release archive.** Each version caches into its
> own directory (`~/.miller/plugin-cache/<version>/<target>/package/`), so a plugin update is a cold
> download of about 100 MB, not an overwrite. That download runs **before** `miller` starts, inside the
> window your client allows for MCP startup. Claude Code's budget is `MCP_TIMEOUT`, in milliseconds,
> default `30000`. On a slow or proxied link the download can outlast it, and the client reports a
> connect failure. Reconnecting through `/mcp` then succeeds, because the archive is already cached.
> To give the first launch room, raise the budget in `~/.claude/settings.json` (on Windows,
> `%USERPROFILE%\.claude\settings.json`):
>
> ```json
> { "env": { "MCP_TIMEOUT": "180000" } }
> ```
>
> Put it in `settings.json`, not in `~/.claude.json` — that file holds app state and ignores `env`.

### When the plugin fails to connect

A plugin launch runs two processes, and they log to different places. Check them in this order.

1. **The Miller launcher log** — `~/.miller/logs/launcher-<YYYYMMDD>.log`. It records every install stage:
   the resolved version, a cache hit or miss, download progress in MB/s, hash, extract, promote, and the
   spawn. If a launch died before `miller` started, the last line names the stage it died in. The same
   lines also go to stderr, so your client captures them too.
2. **Your client's MCP log** — it holds the launcher's stderr plus the connect result. Claude Code writes
   one file per session per server, with no debug flag needed:
   - Windows: `%LOCALAPPDATA%\claude-cli-nodejs\Cache\<encoded-project-path>\mcp-logs-plugin-miller-miller\<timestamp>.jsonl`
   - macOS and Linux: `~/.cache/claude-cli-nodejs/<encoded-project-path>/mcp-logs-plugin-miller-miller/<timestamp>.jsonl`

   The project path is encoded by replacing each non-alphanumeric character with `-`. List the `Cache`
   directory rather than typing the name. The file records the timeout in force
   (`Starting connection with timeout of 30000ms`) and the outcome.
3. **The Miller server log** — `<workspace>/.miller/logs/miller-<YYYYMMDD>.log`. Miller writes a
   breadcrumb to stderr and to this log as its first line, naming the exact directory it logs to. When
   Miller starts with no usable workspace directory it logs to `~/.miller/logs/` instead for its whole
   life, and the breadcrumb says so. A startup that fails before the logger exists still appends a
   `role:startup` line here, or to `~/.miller/logs/` when the workspace directory cannot take it.

An empty workspace log with no breadcrumb means `miller` never started; the answer is then in the
launcher log or the client's MCP log.

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
entry with an absolute path to `miller serve` (see [MCP configuration](#mcp-configuration) below).
For a user-level GUI registration, call `workspace operation=list`; if the project is absent, call
`workspace operation=open path=/absolute/project`, then pass the returned `workspace_id` on every
workspace-bound call.

### Session hooks

The Claude Code plugin injects a ~2.4KB Miller routing block at session start through a `SessionStart`
hook, so tool-routing guidance stays in context even though clients truncate MCP server instructions.
The Codex plugin ships the same hook: Codex runs it once you review and trust the plugin's hooks, and
current Codex builds load hooks from `~/.codex/hooks.json` rather than from plugin roots
([openai/codex#16430](https://github.com/openai/codex/issues/16430)). Set `MILLER_SESSION_HOOKS=0` to
opt out.

### Plugin components

The plugin distribution lives in the main Miller repository, not a separate plugin repo:

- `.claude-plugin/plugin.json` exposes Miller to Claude Code.
- `.cursor-plugin/plugin.json` exposes Miller to Cursor (plugin marketplace install).
- `.codex-plugin/plugin.json` and `.mcp.json` expose Miller to Codex.
- `skills/` is generated from `.agents/skills/` by `scripts/sync-plugin-skills.sh`.
- `bin/miller-plugin-launcher.cjs` is the Node launcher described above.

The plugin also ships `handoff-out` and `handoff-in` skills for model or harness switches. They use
existing Miller tools plus local git state to write and validate markdown packets under
`.miller/handoffs/`; they are not new MCP tools or CLI commands, and the packets stay local unless you
explicitly share them.

## Manual binary install

Use this path when your MCP client does not use Miller's plugin package.

1. Download the archive for your platform from the
   [v1.14.1 release](https://github.com/anortham/miller/releases/tag/v1.14.1), plus the matching
   `.sha256` sidecar:

   - `miller-1.14.1-aarch64-apple-darwin.tar.gz`
   - `miller-1.14.1-x86_64-apple-darwin.tar.gz`
   - `miller-1.14.1-x86_64-unknown-linux-gnu.tar.gz`
   - `miller-1.14.1-x86_64-pc-windows-msvc.zip`

2. Verify and extract it:

   ```bash
   shasum -a 256 -c miller-1.14.1-aarch64-apple-darwin.tar.gz.sha256
   tar -xzf miller-1.14.1-aarch64-apple-darwin.tar.gz
   cd miller-1.14.1-aarch64-apple-darwin
   ./miller version
   ```

   ```powershell
   (Get-FileHash .\miller-1.14.1-x86_64-pc-windows-msvc.zip -Algorithm SHA256).Hash
   # compare against miller-1.14.1-x86_64-pc-windows-msvc.zip.sha256, then extract
   Expand-Archive .\miller-1.14.1-x86_64-pc-windows-msvc.zip -DestinationPath .
   .\miller-1.14.1-x86_64-pc-windows-msvc\miller.exe version
   ```

3. Point your MCP client at the extracted binary (see [MCP configuration](#mcp-configuration)).

Keep the extracted directory together. Each archive extracts to a versioned top-level directory such as
`miller-1.14.1-aarch64-apple-darwin/` containing `miller`, native runtime libraries such as SQLite and
BLAKE3 bindings, the matching pinned `.tools/julie-extract` binary, the loopback dashboard under
`dashboard/`, `LICENSE`, and `THIRD-PARTY-NOTICES.md`. Do not move `.tools/julie-extract` out of the
directory or replace it with a separately installed copy.

The release workflow builds one archive per pinned `julie-extract` platform, uploads a `.sha256` sidecar
for each, and smoke-runs both `julie-extract --version` and `miller version` before packaging.
Maintainers should use the two-step validation/promote flow in
[`release-process.md`](release-process.md) so publishing reuses already validated artifacts instead of
rebuilding the platform matrix.

## Enable semantic retrieval

Release archives ship the embedding sidecar and its llama/ggml runtime, but packages
[never bundle model weights](https://github.com/anortham/julie-semantic-sidecar) and **no Miller code
path downloads them** — the only path is the `semantic prepare` verb. Because semantic retrieval is
default-on, an install that skips this step serves lexical-only forever while `workspace health` reports
`degraded`. Run once per machine:

```bash
miller semantic prepare
```

| Fact | Value |
|---|---|
| Default encoder | `bge-small-en-v1.5-f32`, 384 dimensions, ~134 MB |
| Optional encoder | `qwen3-0.6b-f16`, 512 dimensions, ~1.2 GB, ~8x build time |
| Cache (Windows) | `%LOCALAPPDATA%\julie-semantic\` |
| Cache (macOS/Linux) | `~/.cache/julie-semantic` (or `$XDG_CACHE_HOME/julie-semantic`) |
| Cache override | `JULIE_EMBEDDING_CACHE_DIR` |
| Encoder selection | `MILLER_SEMANTIC_MODEL=<id>` |
| Disable entirely | `MILLER_SEMANTIC=off` (permanent zero-work; lexical output byte-identical) |

After the download, `prepare` probes any live broker once and prints the outcome as a machine token plus a
plain-English line saying what happens next. `activated` means the running broker reports ready and no
restart is needed. `still_not_ready` and `no_live_broker` both mean this session stays lexical-only until
the MCP server restarts; a later sidecar release picks the model up without one. `semantic_disabled` means
`MILLER_SEMANTIC=off` is set, so nothing was probed. Verify with `miller workspace status`: semantics
are live when `vectors` reads `ready` and `semantic_broker` reads `ready` with a resolved `backend`.

If `workspace status` shows a vector artifact without a completeness stamp, check `workspace health`
first — a missing model is reported as `vectors_model_not_prepared`, which `workspace refresh` cannot fix.

## MCP configuration

For Cursor, put this in `~/.cursor/mcp.json`; for other clients, use their MCP server config. Use an
absolute path inside the versioned directory and the explicit `serve` argument:

```json
{
  "mcpServers": {
    "miller": {
      "type": "stdio",
      "command": "/absolute/path/to/miller-1.14.1-aarch64-apple-darwin/miller",
      "args": ["serve"]
    }
  }
}
```

On Windows, use the full path to `miller.exe` as `command`. This registers Miller; it does not select a
project. In a user-level GUI client, call `workspace` with `operation=list`; if the project is absent, call
`operation=open` with `path=/absolute/project`, then pass the returned `workspace_id` to every workspace-bound
tool. Do not rely on launch cwd, `MILLER_WORKSPACE_ROOT`, `GOLDFISH_WORKSPACE`, MCP Roots, `current`, `primary`,
or session binding. Do not use `${workspaceFolder}` in user-global config; it often stays unresolved. CLI and
source-checkout startup-root behavior is documented separately.

## Other harnesses (instruction tier)

Miller installs at two tiers. The MCP tools are identical in both; an instruction-tier install gives up
the `miller-*` skills and automatic guidance injection.

| Tier | Harnesses | `miller-*` skills | Routing block |
|---|---|---|---|
| Plugin | Claude Code, Codex | yes | injected at session start (Codex: see the hook note above) |
| Plugin | Cursor | yes | add it yourself: `miller rules --harness cursor` |
| Instruction | any other MCP-speaking harness | no | add it yourself: `miller rules --harness <name>` |

With the MCP config above in place, write the routing block into the file your harness always loads:

```bash
miller rules --harness cursor > .cursor/rules/miller.mdc
```

`miller rules` prints the block to stdout and the target path to stderr, so a redirect produces a usable
file; run it with no flag to print the bare block for pasting anywhere. Miller only prints. It never
writes into your project, and it never updates a block you already wrote, so re-run the command after a
Miller upgrade to pick up routing changes.

Supported harnesses: `cursor`, `windsurf`, `cline`, `kiro`, `copilot`, `agents`. Each one's target path
and file format (with the official doc URL it was verified against) lives in
[`contracts/rules-v1.md`](contracts/rules-v1.md); `miller rules --harness <name>` prints the same target
path to stderr.

## Source checkout

Use this path for Miller development or for trying unreleased local changes. It requires the .NET 10 SDK
on `PATH` (`dotnet --version` should report a `10.x` SDK), and the restore script downloads the pinned
`julie-extract` binary into this checkout's `.tools/` directory.

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

To build with a local `julie-extractors` checkout instead of the pinned download:

```bash
MILLER_JULIE_SOURCE=~/source/julie-extractors bash scripts/restore-julie-extract.sh --from-source
```

```powershell
$env:MILLER_JULIE_SOURCE='C:\source\julie-extractors'; scripts/restore-julie-extract.ps1 -FromSource
```

The checked-in [`mcp-config.json`](../mcp-config.json) launches the source-checkout server with
`dotnet run ... -- serve`, so an MCP client using it needs the .NET 10 SDK installed.

### Local plugin development

Claude Code install from a local checkout:

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

That writes `~/.cursor/mcp.json` with your Release build path and retires legacy
`~/.cursor/plugins/local/miller` copies. Reload Cursor and confirm Miller appears under
Settings > Tools & MCP.

To test an unreleased local build through the plugin launcher, set
`MILLER_BINARY=/absolute/path/to/miller`:

```bash
dotnet build Miller.slnx -c Release
export MILLER_BINARY="$PWD/src/Miller.Server/bin/Release/net10.0/miller"
claude
```
