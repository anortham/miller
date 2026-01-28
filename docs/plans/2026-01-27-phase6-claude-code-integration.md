# Phase 6: Claude Code Integration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create a Claude Code plugin with commands, skills, and hooks for seamless codesearch integration.

**Architecture:** Plugin structure with slash commands for user-triggered actions, skills for automatic workflows (session restore, progress tracking), and hooks for session events. All markdown-based, git-friendly.

**Tech Stack:** Markdown (YAML frontmatter), JSON configuration, shell scripts for hooks

---

## Prerequisites

Phase 5 complete with:
- MCP server with search, index, memory tools
- Cross-project registry and standup operations
- 29 passing tests

---

### Task 1: Create Plugin Manifest

**Files:**
- Create: `.claude-plugin/plugin.json`

**Step 1: Create plugin directory and manifest**

Create `.claude-plugin/plugin.json`:

```json
{
  "name": "codesearch",
  "version": "0.1.0",
  "description": "Fast text search, semantic search, and persistent memory for AI coding assistants",
  "author": {
    "name": "murphy"
  },
  "homepage": "https://github.com/murphy/codesearch",
  "license": "MIT",
  "keywords": ["search", "memory", "semantic", "code"],
  "commands": "./commands/",
  "skills": "./skills/"
}
```

**Step 2: Commit**

```bash
git add .claude-plugin/plugin.json
git commit -m "feat(plugin): add Claude Code plugin manifest"
```

---

### Task 2: Create Remember Command

**Files:**
- Create: `.claude-plugin/commands/remember.md`

**Step 1: Create remember command**

Create `.claude-plugin/commands/remember.md`:

```markdown
---
description: Save a checkpoint of current work progress
---

# Remember

Save a checkpoint capturing what you've accomplished and current state.

Use the codesearch memory tool to save a checkpoint:

1. Summarize what was just accomplished
2. Note any decisions made and why
3. List any pending work or blockers

Call the memory tool with:
- operation: "remember"
- type: "checkpoint"
- content: Your summary of current state
- tags: Relevant tags (feature name, task type, etc.)

Example:
```
memory(operation="remember", type="checkpoint", content="Implemented user authentication with JWT tokens. Added login/logout endpoints. Tests passing. Next: add password reset flow.", tags="auth,api")
```
```

**Step 2: Commit**

```bash
git add .claude-plugin/commands/remember.md
git commit -m "feat(plugin): add /remember command"
```

---

### Task 3: Create Recall Command

**Files:**
- Create: `.claude-plugin/commands/recall.md`

**Step 1: Create recall command**

Create `.claude-plugin/commands/recall.md`:

```markdown
---
description: Search and retrieve saved memories
---

# Recall

Search through saved memories to restore context or find past decisions.

Use the codesearch memory tool to search:

1. Search by tags to find related work
2. Search by time to see recent activity
3. Use type filter for specific memory types (checkpoint, decision, plan, learning)

Call the memory tool with:
- operation: "recall"
- days: Time range to search (default: 7)
- tags: Filter by tags (optional)
- type: Filter by type (optional)
- workspace: "current" or "all" for cross-project

Examples:

**Recent checkpoints:**
```
memory(operation="recall", days=3)
```

**Find authentication work:**
```
memory(operation="recall", tags="auth", days=30)
```

**All decisions across projects:**
```
memory(operation="recall", type="decision", workspace="all", days=14)
```
```

**Step 2: Commit**

```bash
git add .claude-plugin/commands/recall.md
git commit -m "feat(plugin): add /recall command"
```

---

### Task 4: Create Standup Command

**Files:**
- Create: `.claude-plugin/commands/standup.md`

**Step 1: Create standup command**

Create `.claude-plugin/commands/standup.md`:

```markdown
---
description: Generate a standup report of recent activity
---

# Standup

Generate a standup report showing recent activity across all registered projects.

Use the codesearch memory tool with the standup operation:

Call the memory tool with:
- operation: "standup"
- days: Number of days to include (default: 1)
- limit: Maximum entries (default: 50)

Examples:

**Today's standup:**
```
memory(operation="standup", days=1)
```

**Weekly summary:**
```
memory(operation="standup", days=7, limit=100)
```

The report groups activity by project, showing:
- Project name and path
- Timestamps and memory types
- First line of each memory as summary
```

**Step 2: Commit**

```bash
git add .claude-plugin/commands/standup.md
git commit -m "feat(plugin): add /standup command"
```

---

### Task 5: Create Session Memory Skill

**Files:**
- Create: `.claude-plugin/skills/session-memory/SKILL.md`

**Step 1: Create session memory skill**

Create `.claude-plugin/skills/session-memory/SKILL.md`:

```markdown
---
name: session-memory
description: Automatically restore session context from persistent memory at session start
allowed-tools: mcp__codesearch__memory, Read
---

# Session Memory Skill

When a session starts, automatically restore context from the most recent checkpoint.

## When This Activates

This skill activates at session start (including resume, clear, compact events).

## What To Do

1. **Check for recent checkpoints** by calling the memory tool:
   ```
   memory(operation="recall", type="checkpoint", days=7, limit=3)
   ```

2. **If checkpoints found**, present a brief summary:
   - What was being worked on
   - Key decisions made
   - Any pending work

3. **If no checkpoints**, note that this appears to be a fresh session.

## Example Output

"Restoring context from last session (2 hours ago):
- Working on: User authentication feature
- Last completed: JWT token generation
- Pending: Password reset flow
- Key decision: Using refresh tokens for session extension"

## Notes

- Only restore context, don't take any actions
- Keep the summary concise (3-5 bullet points max)
- If multiple checkpoints exist, focus on the most recent
```

**Step 2: Commit**

```bash
git add .claude-plugin/skills/session-memory/SKILL.md
git commit -m "feat(plugin): add session-memory skill for auto-restore"
```

---

### Task 6: Create Progress Tracking Skill

**Files:**
- Create: `.claude-plugin/skills/progress-tracking/SKILL.md`

**Step 1: Create progress tracking skill**

Create `.claude-plugin/skills/progress-tracking/SKILL.md`:

```markdown
---
name: progress-tracking
description: Automatically save checkpoints at key moments during development
allowed-tools: mcp__codesearch__memory
---

# Progress Tracking Skill

Automatically save checkpoints to preserve work progress.

## When To Checkpoint

Save a checkpoint when ANY of these occur:

1. **Tests pass** after implementing a feature
2. **Significant code changes** are committed
3. **Important decision** is made (architecture, library choice, etc.)
4. **Blocker encountered** that stops progress
5. **Before context switch** (moving to different task/file)

## How To Checkpoint

Call the memory tool with appropriate type:

**For regular progress:**
```
memory(operation="remember", type="checkpoint", content="...", tags="...")
```

**For decisions:**
```
memory(operation="remember", type="decision", content="...", title="...", tags="...")
```

**For learnings:**
```
memory(operation="remember", type="learning", content="...", tags="...")
```

## Content Guidelines

Include in every checkpoint:
- **What**: What was accomplished or discovered
- **Why**: Reasoning behind decisions (if applicable)
- **Next**: What comes next or what's blocking

Example:
```
memory(
  operation="remember",
  type="checkpoint",
  content="Implemented JWT auth middleware. Chose RS256 over HS256 for better security with microservices. Tests passing (8/8). Next: add refresh token rotation.",
  tags="auth,jwt,middleware"
)
```

## Important

- Don't over-checkpoint (aim for 2-5 per significant task)
- Always include context that would help future sessions
- Tag consistently for easy recall
```

**Step 2: Commit**

```bash
git add .claude-plugin/skills/progress-tracking/SKILL.md
git commit -m "feat(plugin): add progress-tracking skill for auto-checkpoint"
```

---

### Task 7: Create Local Settings Template

**Files:**
- Create: `.claude/settings.local.json.example`

**Step 1: Create settings template**

Create `.claude/settings.local.json.example`:

```json
{
  "permissions": {
    "allow": [
      "mcp__codesearch__search",
      "mcp__codesearch__index",
      "mcp__codesearch__memory"
    ],
    "deny": [],
    "ask": []
  }
}
```

**Step 2: Add to .gitignore**

Append to `.gitignore` (create if doesn't exist):

```
# Claude Code local settings (contains user-specific config)
.claude/settings.local.json
```

**Step 3: Commit**

```bash
git add .claude/settings.local.json.example .gitignore
git commit -m "feat(plugin): add local settings template and gitignore"
```

---

### Task 8: Create Plugin README

**Files:**
- Create: `.claude-plugin/README.md`

**Step 1: Create plugin README**

Create `.claude-plugin/README.md`:

```markdown
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
```

**Step 2: Commit**

```bash
git add .claude-plugin/README.md
git commit -m "docs(plugin): add plugin README with usage instructions"
```

---

### Task 9: Update Project README

**Files:**
- Modify: `README.md` (create if doesn't exist)

**Step 1: Create or update project README**

Create/update `README.md`:

```markdown
# Codesearch

Fast text search, semantic search, and persistent memory for AI coding assistants. Built as an MCP server for Claude Code integration.

## Features

- **Text Search**: Full-text search with BM25 ranking via LanceDB
- **Semantic Search**: Vector similarity search with local embeddings (ONNX)
- **Memory System**: Persistent memory with YAML frontmatter markdown files
- **Cross-Project**: Registry tracks multiple projects, aggregate queries
- **Claude Code Plugin**: Commands, skills, and auto-checkpointing

## Quick Start

```bash
# Build
dotnet build

# Run tests
dotnet test

# Configure in Claude Code MCP settings
```

See [.claude-plugin/README.md](.claude-plugin/README.md) for detailed setup.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Claude Code / MCP Client                  │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                 Codesearch.Server (MCP)                      │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ SearchTool  │  │  IndexTool  │  │    MemoryTool       │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
│         │                │                    │              │
│         ▼                ▼                    ▼              │
│  ┌─────────────────────────────┐  ┌─────────────────────┐  │
│  │      SearchService          │  │   MemoryService     │  │
│  │      IndexService           │  │   RegistryService   │  │
│  └─────────────────────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│            Codesearch.Interop (UniFFI Bindings)              │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│              codesearch-ffi (Rust Core)                      │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │  LanceDB    │  │ Tree-sitter │  │    Embeddings       │  │
│  │  (Storage)  │  │  (Parsing)  │  │    (ONNX)           │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Project Structure

```
codesearch/
├── src/
│   ├── Codesearch.Server/     # MCP server (.NET)
│   │   ├── Tools/             # MCP tool implementations
│   │   ├── Services/          # Business logic
│   │   ├── Memory/            # Memory system
│   │   └── Registry/          # Cross-project registry
│   ├── Codesearch.Interop/    # UniFFI C# bindings
│   └── Codesearch.Embeddings/ # ONNX embedding models
├── crates/
│   └── codesearch-ffi/        # Rust core library
├── tests/
│   └── Codesearch.Tests/      # Integration tests
├── .claude-plugin/            # Claude Code plugin
│   ├── commands/              # Slash commands
│   └── skills/                # Auto-skills
└── docs/
    └── plans/                 # Implementation plans
```

## Development

```bash
# Run all tests
dotnet test

# Build server
dotnet build src/Codesearch.Server

# Watch for changes (development)
dotnet watch --project src/Codesearch.Server run
```

## License

MIT
```

**Step 2: Commit**

```bash
git add README.md
git commit -m "docs: add project README with architecture overview"
```

---

### Task 10: Final Verification

**Step 1: Verify plugin structure**

Run: `find .claude-plugin -type f | sort`

Expected output:
```
.claude-plugin/README.md
.claude-plugin/commands/recall.md
.claude-plugin/commands/remember.md
.claude-plugin/commands/standup.md
.claude-plugin/plugin.json
.claude-plugin/skills/progress-tracking/SKILL.md
.claude-plugin/skills/session-memory/SKILL.md
```

**Step 2: Run all tests**

Run: `dotnet test`
Expected: All 29 tests pass

**Step 3: Final commit**

```bash
git add -A
git commit -m "feat(plugin): complete Claude Code integration (Phase 6)"
```

---

## Phase 6 Complete

At this point you have:
- Plugin manifest with metadata
- 3 slash commands: `/remember`, `/recall`, `/standup`
- 2 auto-skills: `session-memory`, `progress-tracking`
- Settings template with MCP tool permissions
- Plugin README with installation and usage
- Project README with architecture overview

**Next Phase (7):** Advanced features - relationship graph, impact analysis, call hierarchy.
