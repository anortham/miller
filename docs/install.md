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

## Plugin install

The plugin launcher downloads the Miller release archive that matches your platform, verifies its
`.sha256` sidecar, caches it under `~/.miller/plugin-cache/`, and starts `miller serve` as an MCP server.
The launcher consumes the release version pinned in `miller-plugin.json`.

> **Plugin installs require [Node.js](https://nodejs.org/) on `PATH`**: the launcher is a Node script
> (declared as `command: "node"` in the plugin manifests). If Node.js is missing (common with Claude
> Code's native installer, which does not itself need Node), the plugin fails to connect with the opaque
> MCP error `-32000` and writes no Miller log. Install Node.js LTS and fully restart your agent so the
> new `PATH` is picked up; an in-session reconnect keeps the old environment and still fails.

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
Miller binds its workspace from MCP client roots on the first tool call, so one global install works per
editor window without `${workspaceFolder}` placeholders.

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

On Windows, use the full path to `miller.exe` as `command`. Miller resolves the open project via MCP
`roots/list` on the first tool call. For clients without MCP roots support, set
`"env": { "MILLER_WORKSPACE_ROOT": "/absolute/path/to/project" }` on the server entry. Do not use
`${workspaceFolder}` in user-global config; it often stays unresolved.

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
