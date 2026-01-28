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
├── rust/
│   ├── codesearch-ffi/        # Rust FFI library
│   ├── codesearch-core/       # Core search functionality
│   └── codesearch-extractors/ # Language-specific extractors
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
