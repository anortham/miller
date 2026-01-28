# Codesearch Plugin for Claude Code

Fast text search, semantic search, and persistent memory for AI coding assistants.

## Installation

1. **Build the MCP server:**
   ```bash
   dotnet build src/Codesearch.Server
   ```

2. **Configure Claude Code** by adding to your MCP settings:
   ```json
   {
     "mcpServers": {
       "codesearch": {
         "type": "stdio",
         "command": "dotnet",
         "args": ["run", "--project", "/path/to/codesearch/src/Codesearch.Server"]
       }
     }
   }
   ```

3. **Copy settings template:**
   ```bash
   cp .claude/settings.local.json.example .claude/settings.local.json
   ```

## Commands

| Command | Description |
|---------|-------------|
| `/remember` | Save a checkpoint of current work |
| `/recall` | Search and retrieve saved memories |
| `/standup` | Generate activity report across projects |

## Skills

| Skill | Description |
|-------|-------------|
| `session-memory` | Auto-restore context at session start |
| `progress-tracking` | Auto-save checkpoints at key moments |

## MCP Tools

### search

Search for symbols in the codebase.

```
search(query="functionName", limit=20)
```

### index

Manage the search index.

```
index(operation="status")
index(operation="full")
index(operation="incremental")
```

### memory

Persistent memory operations.

```
memory(operation="remember", content="...", type="checkpoint", tags="...")
memory(operation="recall", days=7, workspace="current")
memory(operation="standup", days=1)
memory(operation="status")
```

## Memory Types

- **checkpoint**: Regular progress snapshots
- **decision**: Important choices with reasoning
- **plan**: Implementation plans and task lists
- **learning**: Lessons learned, gotchas, tips

## Cross-Project Features

Projects auto-register when the MCP server starts. Use `workspace="all"` to search across all registered projects:

```
memory(operation="recall", workspace="all", days=7)
memory(operation="standup", days=1)
```

## File Locations

- Memories: `.memories/YYYY-MM-DD/HHMMSS_XXXX.md`
- Plans: `.memories/plans/{slug}.md`
- Registry: `~/.codesearch/registry.json`
- Index: `.codesearch/index.lance`
