# Codesearch Design Document

**Date:** 2026-01-27
**Status:** Draft
**Authors:** murphy, claude

## Overview

Codesearch is an MCP server providing fast text search, semantic search, and project memory for AI coding assistants. It evolves from previous projects (julie, miller, goldfish) with key improvements:

- **.NET 10 host** instead of Python (better deployment, familiar tooling)
- **Rust engine** for parsing and search (LanceDB + Tantivy)
- **UniFFI interop** between .NET and Rust (zero-copy where possible)
- **Hardware-accelerated embeddings** via ONNX Runtime (Apple Silicon, CUDA, DirectML)
- **Unified memory system** with cross-project reporting

### Goals

1. **Fast text search** - Code-aware tokenization, BM25 ranking, pattern matching
2. **Semantic search** - Vector similarity with hybrid ranking (RRF)
3. **Project memory** - Checkpoints, plans, decisions stored as markdown with frontmatter
4. **Cross-project visibility** - Standup reports, activity aggregation across workspaces
5. **Minimal context overhead** - 3 MCP tools, CLI + skills for advanced workflows

### Non-Goals

- IDE plugins (focus on MCP/CLI interface)
- Real-time collaboration
- Language server protocol (LSP) implementation

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     .NET 10.0 "Host"                        │
│  ┌──────────────┐  ┌───────────────┐  ┌─────────────────┐  │
│  │  MCP Server  │  │ ONNX Runtime  │  │  Memory System  │  │
│  │  (C# SDK)    │  │ (Embeddings)  │  │  (.memories/)   │  │
│  └──────┬───────┘  └───────┬───────┘  └────────┬────────┘  │
│         │                  │                    │           │
│         ▼                  ▼                    ▼           │
│  ┌─────────────────────────────────────────────────────────┐│
│  │                   UniFFI Boundary                       ││
│  │         (Span<T>/Memory<T> for zero-copy)               ││
│  └─────────────────────────────────────────────────────────┘│
│         │                  │                    │           │
│         ▼                  ▼                    ▼           │
│  ┌─────────────────────────────────────────────────────────┐│
│  │                   Rust "Engine"                         ││
│  │  ┌──────────────────┐  ┌────────────────────────────┐  ││
│  │  │ codesearch-      │  │ LanceDB                    │  ││
│  │  │ extractors       │  │ ├─ Vector storage (Lance)  │  ││
│  │  │ (tree-sitter,    │  │ ├─ FTS index (Tantivy)     │  ││
│  │  │  31 languages)   │  │ └─ Hybrid search (RRF)     │  ││
│  │  └──────────────────┘  └────────────────────────────┘  ││
│  └─────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────┘
```

### Why This Split

| Layer | Responsibility | Rationale |
|-------|---------------|-----------|
| **.NET Host** | MCP protocol, embedding inference, memory management, orchestration | First-class ONNX Runtime support (Apple Silicon, CUDA), familiar deployment |
| **Rust Engine** | Parsing, indexing, search | LanceDB + Tantivy are Rust-native, tree-sitter extractors already exist in Rust |
| **UniFFI** | FFI bridge | Clean generated bindings, handles memory/types automatically |

The embedding vector crosses the FFI boundary once per query (`.NET → Rust`), which is negligible. Heavy lifting (parsing, search) stays in Rust.

---

## Data Models

### Symbols (from tree-sitter extraction)

```rust
pub struct Symbol {
    pub id: String,                    // MD5 hash (file:name:line:column)
    pub name: String,
    pub kind: SymbolKind,              // Function, Class, Method, etc. (23 kinds)
    pub language: String,              // "rust", "python", "typescript", etc.
    pub file_path: String,             // Relative Unix-style path
    pub start_line: u32,
    pub start_column: u32,
    pub end_line: u32,
    pub end_column: u32,
    pub signature: Option<String>,
    pub doc_comment: Option<String>,
    pub visibility: Option<Visibility>,
    pub parent_id: Option<String>,     // For nested symbols
    pub code_context: Option<String>,  // Surrounding lines for display
}
```

**SymbolKind** (23 types): Class, Interface, Function, Method, Variable, Constant, Property, Enum, EnumMember, Module, Namespace, Type, Trait, Struct, Union, Field, Constructor, Destructor, Operator, Import, Export, Event, Delegate

### Relationships

```rust
pub struct Relationship {
    pub id: String,
    pub from_symbol_id: String,
    pub to_symbol_id: String,
    pub kind: RelationshipKind,        // Calls, Extends, Implements, etc. (14 kinds)
    pub file_path: String,
    pub line_number: u32,
    pub confidence: f32,
}
```

### Identifiers (usage sites)

```rust
pub struct Identifier {
    pub id: String,
    pub name: String,
    pub kind: IdentifierKind,          // Call, VariableRef, TypeUsage, MemberAccess, Import
    pub file_path: String,
    pub start_line: u32,
    pub containing_symbol_id: Option<String>,
    pub target_symbol_id: Option<String>,  // Resolved on-demand
    pub confidence: f32,
}
```

### LanceDB Schema

```
┌─────────────────┬──────────────────┬─────────────────────────────────────┐
│ Field           │ Type             │ Notes                               │
├─────────────────┼──────────────────┼─────────────────────────────────────┤
│ id              │ string           │ Primary key (MD5 hash)              │
│ name            │ string           │ Symbol/file name                    │
│ kind            │ string           │ "function", "class", "checkpoint"   │
│ language        │ string           │ "rust", "python", "markdown"        │
│ file_path       │ string           │ Relative path                       │
│ signature       │ string?          │ Function signature                  │
│ doc_comment     │ string?          │ Documentation                       │
│ start_line      │ int32?           │ 1-based                             │
│ end_line        │ int32?           │                                     │
│ code_pattern    │ string           │ Tokenized for FTS (name+sig+kind)   │
│ content         │ string?          │ Full content (files, memories)      │
│ vector          │ float32[768]     │ nomic-embed-text-v1.5               │
│ tags            │ string[]?        │ For memories                        │
│ timestamp       │ int64?           │ Unix timestamp (memories)           │
└─────────────────┴──────────────────┴─────────────────────────────────────┘
```

### Memory File Format

**Location:** `{project}/.memories/`

```
.memories/
├── 2026-01-27/
│   ├── 143052_a1b2.md      # Checkpoint
│   └── 161530_c3d4.md
├── plans/
│   └── auth-system.md      # Plan
└── decisions/
    └── jwt-vs-sessions.md  # Decision record
```

**Frontmatter:**

```yaml
---
id: checkpoint_691cb498_2fc504
type: checkpoint                    # checkpoint, plan, decision, learning
timestamp: 1769461109               # Unix seconds
tags:
  - authentication
  - security
git:
  branch: main
  commit: a1e8063
  dirty: false
  files_changed:
    - src/auth/jwt.ts
---

## What I Did

Implemented JWT token verification with refresh token rotation...
```

**Type-specific fields:**

| Type | Additional Fields |
|------|-------------------|
| `plan` | `title`, `status` (pending/in_progress/completed) |
| `decision` | `title`, `options`, `chosen` |
| `learning` | `title`, `confidence` |

### Central Registry

**Location:** `~/.codesearch/registry.json`

```json
{
  "projects": {
    "codesearch": {
      "path": "/Users/murphy/source/codesearch",
      "last_active": "2026-01-27T14:30:00Z",
      "indexed_at": "2026-01-27T14:25:00Z"
    },
    "julie": {
      "path": "/Users/murphy/source/julie",
      "last_active": "2026-01-27T11:00:00Z",
      "indexed_at": "2026-01-26T09:15:00Z"
    }
  }
}
```

**Behavior:**
- Projects register on MCP server startup
- Cross-project queries read registry, aggregate from each project
- Missing paths are skipped and pruned (lazy cleanup)

---

## Search System

### Search Methods

| Method | Implementation | Use Case |
|--------|---------------|----------|
| **Text** | Tantivy BM25 on `code_pattern` + `content` | Keyword search, exact matches |
| **Pattern** | Tantivy with whitespace tokenizer | Code idioms (`: BaseClass`, `ILogger<`) |
| **Semantic** | Vector similarity (L2 → cosine) | Natural language queries |
| **Hybrid** | Reciprocal Rank Fusion (RRF) | Default for most queries |

### Tokenization

**Whitespace tokenizer** (not Unicode) preserves code patterns:
- `authenticate:` → token `authenticate:`
- `ILogger<T>` → token `ILogger<T>`
- `@Inject` → token `@Inject`

This enables searches like `: BaseClass` to find inheritance patterns.

### Score Boosting

Applied after initial retrieval, before final ranking:

**By Match Position:**
| Match Type | Boost |
|------------|-------|
| Exact (name == query) | 3.0x |
| Prefix (name starts with query) | 2.0x |
| Suffix (name ends with query) | 1.5x |
| Substring | 1.0x |

**By Field:**
| Field | Boost |
|-------|-------|
| Name | 3.0x |
| Signature | 1.5x |
| Doc comment | 1.0x |

**By Kind:**
| Kind | Weight |
|------|--------|
| Function, Class | 1.5x |
| Method | 1.3x |
| Interface, Type, Struct | 1.2x |
| Enum | 1.1x |
| Constant | 0.9x |
| Variable, Field | 0.8x |
| File | 0.5x |
| Import | 0.4x (deboosted - noise) |

### Hybrid Search Flow

```
Query: "authentication middleware"
         │
         ▼
┌─────────────────────────────────────────┐
│ 1. Generate embedding (ONNX Runtime)   │
│    → float[768]                         │
└─────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────┐
│ 2. Parallel search in Rust             │
│    ├─ Tantivy FTS → BM25 scores        │
│    └─ LanceDB vector → L2 distances    │
└─────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────┐
│ 3. Reciprocal Rank Fusion (RRF)        │
│    score = Σ 1/(k + rank_i)            │
│    (k = 60 typically)                   │
└─────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────┐
│ 4. Apply score boosting                │
│    (position, field, kind weights)     │
└─────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────┐
│ 5. Return top N results                │
│    (with code_context for display)     │
└─────────────────────────────────────────┘
```

---

## Memory System

### Operations

| Operation | Description |
|-----------|-------------|
| **Remember** | Create checkpoint/plan/decision with auto-extracted git context |
| **Recall** | Search memories by text, tags, time range, or semantically |
| **Standup** | Cross-project recall for reporting (aggregates from registry) |

### Recall Filtering

```
recall({
  workspace: "all",           // or specific project
  days: 1,                    // time range
  since: "2h",                // alternative: human-friendly
  from: "2026-01-20",         // explicit date range
  to: "2026-01-27",
  tags: ["authentication"],   // filter by tags
  search: "JWT implementation", // semantic/text search
  type: "checkpoint",         // filter by type
  limit: 50
})
```

**Time parsing:** Supports `2h`, `30m`, `3d`, `1w` formats.

### Cross-Project Aggregation

```
┌─────────────────────────────────────────┐
│ 1. Read ~/.codesearch/registry.json    │
└─────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────┐
│ 2. For each project (parallel):        │
│    ├─ Check path exists (skip if not)  │
│    ├─ Query .memories/ with filters    │
│    └─ Collect results + workspace meta │
└─────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────┐
│ 3. Merge results                       │
│    ├─ Sort by timestamp (newest first) │
│    ├─ Apply global limit               │
│    └─ Include workspace summaries      │
└─────────────────────────────────────────┘
```

**WorkspaceSummary:**
```csharp
record WorkspaceSummary(
    string Name,
    string Path,
    int CheckpointCount,
    DateTime? LastActivity
);
```

### Memory Indexing

Memory files are indexed alongside code:

1. Parse YAML frontmatter
2. Extract markdown body
3. Prepend tags to content for semantic boost
4. Generate embedding (same model as code)
5. Store in LanceDB with `kind: "checkpoint"` / `"plan"` / etc.

This enables unified search:
```
Search "authentication" →
  • src/auth/jwt.ts:42 - verifyToken() function
  • src/auth/middleware.ts:15 - authMiddleware() function
  • .memories/2026-01-15/091532_a1b2.md - "Decided to use JWT with refresh tokens"
```

---

## MCP Tools

### Tool: `search`

**Purpose:** Find code and memories

```json
{
  "name": "search",
  "description": "Search code and project knowledge",
  "inputSchema": {
    "type": "object",
    "properties": {
      "query": {
        "type": "string",
        "description": "Search query (natural language or code pattern)"
      },
      "method": {
        "type": "string",
        "enum": ["auto", "text", "semantic", "hybrid", "pattern"],
        "default": "auto",
        "description": "Search method (auto-detected if not specified)"
      },
      "kind": {
        "type": "array",
        "items": { "type": "string" },
        "description": "Filter by symbol kind (function, class, etc.)"
      },
      "language": {
        "type": "string",
        "description": "Filter by language"
      },
      "include_memories": {
        "type": "boolean",
        "default": true,
        "description": "Include project memories in results"
      },
      "limit": {
        "type": "integer",
        "default": 20,
        "description": "Maximum results"
      }
    },
    "required": ["query"]
  }
}
```

### Tool: `memory`

**Purpose:** Project knowledge management

```json
{
  "name": "memory",
  "description": "Remember and recall project knowledge",
  "inputSchema": {
    "type": "object",
    "properties": {
      "operation": {
        "type": "string",
        "enum": ["remember", "recall", "standup"],
        "description": "Operation to perform"
      },
      "content": {
        "type": "string",
        "description": "Content to remember (for remember operation)"
      },
      "type": {
        "type": "string",
        "enum": ["checkpoint", "plan", "decision", "learning"],
        "default": "checkpoint",
        "description": "Memory type (for remember operation)"
      },
      "tags": {
        "type": "array",
        "items": { "type": "string" },
        "description": "Tags for filtering or categorization"
      },
      "query": {
        "type": "string",
        "description": "Search query (for recall/standup)"
      },
      "days": {
        "type": "integer",
        "default": 2,
        "description": "Time range in days (for recall/standup)"
      },
      "workspace": {
        "type": "string",
        "default": "current",
        "description": "Workspace scope: 'current', 'all', or specific name"
      },
      "limit": {
        "type": "integer",
        "default": 20
      }
    },
    "required": ["operation"]
  }
}
```

### Tool: `index`

**Purpose:** Workspace management

```json
{
  "name": "index",
  "description": "Manage workspace index",
  "inputSchema": {
    "type": "object",
    "properties": {
      "operation": {
        "type": "string",
        "enum": ["status", "refresh", "full"],
        "default": "status",
        "description": "Operation: status (check health), refresh (update stale), full (rebuild)"
      },
      "path": {
        "type": "string",
        "description": "Specific path to index (optional)"
      }
    }
  }
}
```

---

## Indexing Pipeline

### Startup Flow

```
┌─────────────────────────────────────────┐
│ MCP Server Startup                      │
├─────────────────────────────────────────┤
│ 1. Register project in registry         │
│ 2. Load existing index from .codesearch/│
│ 3. Scan files, compute Blake3 hashes    │
│ 4. Compare to stored hashes             │
│ 5. Batch update stale files             │
│ 6. Start file watcher                   │
└─────────────────────────────────────────┘
```

### File Watcher

- **Mechanism:** OS-native file watching (FSEvents on macOS, inotify on Linux)
- **Debounce:** 500ms (configurable)
- **Events:** Create, Modify, Delete, Rename

### Incremental Update Flow

```
File Changed
     │
     ▼
┌─────────────────────────────────────────┐
│ 1. Compute Blake3 hash                  │
│    (skip if unchanged)                  │
└─────────────────────────────────────────┘
     │
     ▼
┌─────────────────────────────────────────┐
│ 2. Detect language from extension       │
└─────────────────────────────────────────┘
     │
     ▼
┌─────────────────────────────────────────┐
│ 3. Extract symbols (Rust/tree-sitter)  │
│    ├─ Symbols                           │
│    ├─ Relationships                     │
│    └─ Identifiers                       │
└─────────────────────────────────────────┘
     │
     ▼
┌─────────────────────────────────────────┐
│ 4. Generate embeddings (.NET/ONNX)     │
│    ├─ Per-symbol (signature + context)  │
│    └─ File-level (for unsupported langs)│
└─────────────────────────────────────────┘
     │
     ▼
┌─────────────────────────────────────────┐
│ 5. Update LanceDB (Rust)               │
│    ├─ Delete old entries for file       │
│    ├─ Insert new entries                │
│    └─ Rebuild FTS index if needed       │
└─────────────────────────────────────────┘
```

### Supported Languages

31 languages via tree-sitter (from julie-extractors):

| Category | Languages |
|----------|-----------|
| Systems | Rust, C, C++, Go, Zig |
| Web | TypeScript/TSX, JavaScript/JSX, HTML, CSS, Vue, QML |
| Backend | Python, Java, C#, PHP, Ruby, Swift, Kotlin, Dart |
| Scripting | Lua, R, Bash, PowerShell |
| Specialized | GDScript, Razor, SQL, Regex |
| Documentation | Markdown, JSON, TOML, YAML |

Unsupported file types get file-level embedding only (no symbol extraction).

---

## Claude Code Integration

### Hooks

**SessionStart hook** (register project, inject context):

```json
{
  "hooks": {
    "SessionStart": [{
      "hooks": [{
        "type": "command",
        "command": "codesearch register --project-dir \"$CLAUDE_PROJECT_DIR\" --json"
      }]
    }]
  }
}
```

The hook outputs JSON with `additionalContext` containing index status.

**Stop hook** (auto-checkpoint, optional):

```json
{
  "hooks": {
    "Stop": [{
      "hooks": [{
        "type": "command",
        "command": "codesearch auto-checkpoint --project-dir \"$CLAUDE_PROJECT_DIR\""
      }]
    }]
  }
}
```

### Skills

Skills provide guided workflows beyond the 3 core MCP tools:

| Skill | Purpose |
|-------|---------|
| `codesearch:find-callers` | Walk relationship graph to find all callers of a function |
| `codesearch:impact-analysis` | Analyze what would break if a symbol changes |
| `codesearch:explain-symbol` | Full context: definition, usages, documentation, relationships |
| `codesearch:standup` | Generate standup report with time range and optional LLM summary |

Skills are markdown files in the `skills/` directory, loadable by Claude Code.

---

## Embeddings

### Model

**nomic-embed-text-v1.5**
- Dimensions: 768 (can truncate to 384/256 via Matryoshka)
- Size: ~137MB ONNX
- License: Apache 2.0

**Matryoshka property:** Embeddings are trained so truncation preserves quality:
- 768 dims: Full quality
- 512 dims: ~95% quality
- 384 dims: ~90% quality (matches bge-small storage)
- 256 dims: ~85% quality

### Hardware Acceleration

ONNX Runtime execution providers (priority order):

1. **CUDA** (NVIDIA)
2. **CoreML** (Apple Silicon)
3. **DirectML** (Windows AMD/Intel)
4. **ROCm** (AMD Linux)
5. **CPU** (fallback)

### Embedding Strategy

| Content | Embedding Input |
|---------|-----------------|
| Function/Method | `{signature} {name} {doc_comment}` |
| Class/Struct | `{name} {doc_comment} {field_names}` |
| File (unsupported lang) | First 4096 chars of content |
| Memory | `{tags joined} {markdown body}` (first 4096 chars) |

---

## Project Structure

```
codesearch/
├── src/
│   ├── Codesearch.Server/           # .NET MCP server entry point
│   │   ├── Program.cs
│   │   ├── McpServer.cs
│   │   └── Tools/
│   │       ├── SearchTool.cs
│   │       ├── MemoryTool.cs
│   │       └── IndexTool.cs
│   ├── Codesearch.Embeddings/       # ONNX Runtime wrapper
│   │   ├── EmbeddingManager.cs
│   │   └── ModelLoader.cs
│   ├── Codesearch.Memory/           # Memory system
│   │   ├── MemoryStore.cs
│   │   ├── CheckpointWriter.cs
│   │   └── FrontmatterParser.cs
│   ├── Codesearch.Registry/         # Cross-project registry
│   │   └── ProjectRegistry.cs
│   └── Codesearch.Interop/          # UniFFI generated bindings
│       └── (generated)
├── rust/
│   ├── codesearch-core/             # Main Rust library
│   │   ├── src/
│   │   │   ├── lib.rs
│   │   │   ├── engine.rs            # CodeEngine struct (UniFFI export)
│   │   │   ├── search.rs            # Hybrid search implementation
│   │   │   ├── index.rs             # Indexing logic
│   │   │   └── watcher.rs           # File watcher
│   │   └── Cargo.toml
│   ├── codesearch-extractors/       # Fork of julie-extractors
│   │   └── (31 language extractors)
│   └── codesearch-ffi/              # UniFFI definitions
│       ├── src/
│       │   └── lib.rs               # #[uniffi::export] definitions
│       └── codesearch.udl           # UniFFI interface definition
├── models/                          # ONNX models (gitignored)
│   └── nomic-embed-text-v1.5.onnx
├── skills/                          # Claude Code skills
│   ├── find-callers.md
│   ├── impact-analysis.md
│   ├── explain-symbol.md
│   └── standup.md
├── hooks/                           # Example hook configurations
│   └── claude-settings.json
├── docs/
│   └── plans/
│       └── 2026-01-27-codesearch-design.md
├── tests/
│   ├── Codesearch.Tests/            # .NET tests
│   └── rust/                        # Rust tests
├── codesearch.sln                   # .NET solution
├── Directory.Build.props            # Shared .NET build config
└── README.md
```

---

## Dependencies

### .NET

| Package | Purpose |
|---------|---------|
| `ModelContextProtocol` | Official MCP C# SDK |
| `Microsoft.ML.OnnxRuntime` | Embedding inference |
| `Microsoft.ML.OnnxRuntime.Managed` | Managed wrapper |
| `YamlDotNet` | Frontmatter parsing |

### Rust

| Crate | Purpose |
|-------|---------|
| `lancedb` | Vector + columnar storage |
| `tantivy` | Full-text search (via LanceDB) |
| `tree-sitter` | AST parsing |
| `tree-sitter-*` | Language grammars (31) |
| `uniffi` | FFI bindings generation |
| `blake3` | Fast file hashing |
| `notify` | File watching |
| `tokio` | Async runtime |

---

## Open Questions

1. **Matryoshka truncation:** Should we store full 768-dim and truncate at query time, or store truncated to save space?

2. **Relationship resolution:** Julie has `PendingRelationship` for cross-file resolution. Do we need this for the initial version, or can we defer?

3. **CLI interface:** Should `codesearch` be a standalone CLI in addition to MCP, or MCP-only with hooks for CLI-like operations?

4. **Model download:** First-run model download strategy - bundled, or fetch from HuggingFace on demand?

5. **Index location:** `.codesearch/` in project root vs `~/.codesearch/indexes/{project-hash}/` for centralized storage?

---

## Implementation Phases

### Phase 1: Foundation
- [ ] Project scaffolding (.NET solution, Rust workspace)
- [ ] UniFFI setup and basic interop
- [ ] Copy/adapt julie-extractors
- [ ] Basic LanceDB integration

### Phase 2: Core Search
- [ ] ONNX Runtime embedding pipeline
- [ ] Symbol extraction → embedding → storage flow
- [ ] Text search (Tantivy)
- [ ] Semantic search (vector)
- [ ] Hybrid search (RRF)

### Phase 3: MCP Server
- [ ] MCP server setup with C# SDK
- [ ] `search` tool
- [ ] `index` tool
- [ ] File watcher integration

### Phase 4: Memory System
- [ ] Memory file format (frontmatter + markdown)
- [ ] Remember/recall operations
- [ ] Memory indexing (embed alongside code)
- [ ] `memory` tool

### Phase 5: Cross-Project
- [ ] Central registry
- [ ] Cross-project aggregation
- [ ] Standup operation

### Phase 6: Claude Code Integration
- [ ] Hook configurations
- [ ] Skills
- [ ] Documentation

---

## References

> **Note:** Many implementation questions can be answered by examining the existing codebases. When in doubt, look at how julie or miller solved the problem.

| Question Domain | Reference Project | Key Paths |
|----------------|-------------------|-----------|
| Tree-sitter extraction | julie | `crates/julie-extractors/src/` |
| Language detection | julie | `crates/julie-extractors/src/language.rs` |
| Symbol/Relationship types | julie | `crates/julie-extractors/src/base/types.rs` |
| LanceDB schema & search | miller | `python/miller/embeddings/vector_store.py` |
| FTS indexing (Tantivy) | miller | `python/miller/embeddings/fts_index.py` |
| Score boosting | miller | `python/miller/embeddings/search_enhancements.py` |
| Hybrid search | miller | `python/miller/embeddings/search_methods.py` |
| Memory file format | miller | `python/miller/memory_utils.py` |
| Memory indexing | miller | `python/miller/workspace/indexer.py` (lines 141-189) |
| Cross-project recall | goldfish | `src/recall.ts` |
| Workspace registry | goldfish | `src/workspace.ts` |

- [julie](~/source/julie) - Tree-sitter extractors (31 languages)
- [miller](~/source/miller) - LanceDB/Tantivy search, memory system
- [goldfish](~/source/goldfish) - Cross-project standup reporting
- [LanceDB docs](https://lancedb.github.io/lancedb/)
- [UniFFI book](https://mozilla.github.io/uniffi-rs/)
- [MCP specification](https://modelcontextprotocol.io/)
- [ONNX Runtime C# API](https://onnxruntime.ai/docs/api/csharp-api.html)
- [nomic-embed-text-v1.5](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5)
