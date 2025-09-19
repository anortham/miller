# Miller - Multi-Language Code Intelligence MCP Server

[![Language Support](https://img.shields.io/badge/languages-17-blue)](docs/implementation-checklist.md)
[![Tests](https://img.shields.io/badge/tests-passing-green)](src/__tests__)
[![MCP](https://img.shields.io/badge/MCP-compatible-orange)](https://modelcontextprotocol.io/)

Miller is a high-performance MCP (Model Context Protocol) server that provides LSP-quality code intelligence across 17 programming languages without the overhead of running multiple language servers. Built on Bun with Tree-sitter parsers, Miller delivers fast code search, go-to-definition, find-references, and cross-language analysis.

## ✨ Features

### 🌍 Multi-Language Support (17 Languages)
- **Web**: JavaScript, TypeScript, HTML, CSS, Vue
- **Backend**: Python, Rust, Go, Java, C#, PHP, Ruby
- **Systems**: C, C++
- **Mobile**: Swift, Kotlin
- **Web Frameworks**: Razor (Blazor)
- **Utilities**: Regex patterns

### 🚀 LSP-Like Capabilities
- **Go-to-Definition**: Jump to symbol declarations
- **Find References**: Locate all symbol usages
- **Hover Information**: Get type and documentation details
- **Call Hierarchy**: Explore caller/callee relationships
- **Cross-Language Analysis**: Track API calls across language boundaries

### ⚡ High Performance
- **Fast Search**: Fuzzy search with MiniSearch + exact search with ripgrep
- **Incremental Updates**: File watching with intelligent debouncing
- **Efficient Storage**: SQLite with optimized schema and indexes
- **Memory Efficient**: ~100MB for 10,000 files, ~500MB for 50,000 files

### 🔧 Developer Experience
- **MCP Integration**: Works seamlessly with Claude Code and other MCP clients
- **Real-time Updates**: Automatic reindexing on file changes
- **Comprehensive Testing**: 100% MCP integration test pass rate
- **Professional Logging**: Categorized file-based logging system

## 🚀 Quick Start

### Prerequisites
- [Bun](https://bun.sh/) >= 1.0.0
- Git
- Optional: [ripgrep](https://github.com/BurntSushi/ripgrep) for enhanced search performance

### Installation

```bash
# Clone the repository
git clone https://github.com/anortham/miller.git
cd miller

# Install dependencies
bun install

# Start the MCP server
bun start
```

### MCP Client Configuration

Add Miller to your MCP client configuration (e.g., Claude Code):

```json
{
  "mcpServers": {
    "miller": {
      "command": "bun",
      "args": ["run", "/path/to/miller/src/mcp-server.ts"],
      "cwd": "/your/workspace/path"
    }
  }
}
```

Or use Claude Code's MCP management:
```bash
claude mcp add miller "bun run /path/to/miller/src/mcp-server.ts"
```

## 📖 Usage Examples

### Search for Code Symbols
```typescript
// Find all functions named "getUserData"
await tools.search_code({
  query: "getUserData",
  type: "fuzzy",
  limit: 10,
  includeSignature: true
});
```

### Go to Definition
```typescript
// Jump to symbol definition
await tools.goto_definition({
  file: "src/user.ts",
  line: 42,
  column: 15
});
```

### Find All References
```typescript
// Find all usages of a symbol
await tools.find_references({
  file: "src/user.ts",
  line: 10,
  column: 20
});
```

### Cross-Language Analysis
```typescript
// Find API calls between languages
await tools.find_cross_language_bindings({
  file: "src/api-client.js"
});
```

## 🏗️ Architecture

Miller uses a modular architecture designed for performance and extensibility:

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   MCP Client    │───▶│   Miller Server  │───▶│   Tree-sitter   │
│  (Claude Code)  │    │                  │    │   WASM Parsers  │
└─────────────────┘    └──────────────────┘    └─────────────────┘
                                │
                                ▼
                   ┌──────────────────────────┐
                   │    Code Intelligence     │
                   │       Engine            │
                   └──────────────────────────┘
                                │
        ┌───────────────────────┼───────────────────────┐
        ▼                       ▼                       ▼
┌─────────────┐        ┌─────────────┐        ┌─────────────┐
│   SQLite    │        │ MiniSearch  │        │ File Watcher│
│  Database   │        │   Engine    │        │   System    │
└─────────────┘        └─────────────┘        └─────────────┘
```

### Key Components

- **MCP Server**: Handles Model Context Protocol communication
- **Code Intelligence Engine**: Orchestrates all components
- **Parser Manager**: Manages Tree-sitter WASM parsers for 17 languages
- **Database Layer**: SQLite with optimized schema for symbols and relationships
- **Search Engine**: Dual-mode search (fuzzy + exact) with performance optimization
- **File Watcher**: Real-time workspace monitoring with intelligent debouncing

## 🧪 Testing

Miller includes comprehensive testing covering all major functionality:

```bash
# Run all tests
bun test

# Run specific test suites
bun test src/__tests__/mcp/integration.test.ts
bun test src/__tests__/parser/
bun test src/__tests__/integration/
```

**Test Coverage:**
- ✅ 21 MCP integration tests (100% pass rate)
- ✅ 33 parser tests across all languages
- ✅ WASM compatibility tests
- ✅ Language expansion tests
- ✅ Real dogfooding (Miller indexing itself)

## 🌟 Language Support Details

| Language    | Parser Source | Status | Features |
|-------------|---------------|--------|----------|
| JavaScript  | Microsoft     | ✅ Full | Classes, functions, imports, arrow functions |
| TypeScript  | Microsoft     | ✅ Full | All JS features + types, interfaces, generics |
| Python      | Microsoft     | ✅ Full | Functions, classes, decorators, async/await |
| Rust        | Microsoft     | ✅ Full | Functions, structs, traits, macros |
| Go          | Microsoft     | ✅ Full | Functions, structs, interfaces, packages |
| Java        | Microsoft     | ✅ Full | Classes, methods, annotations, inheritance |
| C#          | Microsoft     | ✅ Full | Classes, methods, properties, LINQ |
| C/C++       | Microsoft     | ✅ Full | Functions, structs, classes, templates |
| Swift       | Custom WASM   | ✅ Full | Classes, protocols, extensions, closures |
| Kotlin      | Custom WASM   | ✅ Full | Classes, functions, data classes, coroutines |
| Razor       | Custom WASM   | ✅ Full | Components, directives, code blocks, events |
| Vue         | Custom Parser | ✅ Full | SFCs, templates, scripts, styles |
| HTML        | Microsoft     | ✅ Basic | Tags, attributes |
| CSS         | Microsoft     | ✅ Basic | Selectors, properties |
| PHP         | Microsoft     | ✅ Full | Functions, classes, namespaces |
| Ruby        | Microsoft     | ✅ Full | Classes, methods, modules |
| Regex       | Microsoft     | ✅ Basic | Pattern analysis |

## 🔧 Configuration

Miller can be configured via environment variables or configuration files:

```typescript
// Example configuration
const config = {
  maxFileSize: 1024 * 1024,      // 1MB max file size
  batchSize: 10,                 // Files processed per batch
  debounceMs: 500,               // File change debounce time
  ignorePatterns: [              // Directories to ignore
    'node_modules',
    '.git',
    'dist',
    'build'
  ]
};
```

## 📈 Performance

Miller is optimized for large codebases:

- **Indexing Speed**: 100-500 files/second
- **Search Performance**: <10ms fuzzy search, <50ms exact search
- **Go-to-Definition**: <1ms response time
- **Memory Usage**: ~100MB for 10K files, ~500MB for 50K files
- **Startup Time**: 2-10 seconds for initial workspace indexing

## 🤝 Contributing

We welcome contributions! Please see our [implementation checklist](docs/implementation-checklist.md) for current priorities.

### Development Setup

```bash
# Clone and install
git clone https://github.com/anortham/miller.git
cd miller
bun install

# Run in development mode
bun run dev

# Run tests
bun test
```

### Adding Language Support

1. Install Tree-sitter parser: `bun add tree-sitter-<language>`
2. Register in ParserManager
3. Create language extractor extending BaseExtractor
4. Add comprehensive tests
5. Update documentation

See [CLAUDE.md](CLAUDE.md) for detailed development guidelines.

## 📚 Documentation

- [Implementation Checklist](docs/implementation-checklist.md) - Current status and roadmap
- [Development Guide](CLAUDE.md) - Detailed implementation guidelines
- [MCP Integration Guide](docs/mcp-code-intelligence-guide.md) - Technical implementation details

## 📄 License

MIT License - see [LICENSE](LICENSE) file for details.

## 🚀 Roadmap

- **Phase 1**: ✅ Complete (17 languages, comprehensive testing)
- **Phase 2**: Enhanced semantic analysis and cross-language features
- **Phase 3**: AI-powered code understanding and suggestions
- **Phase 4**: Enterprise features and scalability improvements

---

**Built with ❤️ using Bun, Tree-sitter, and the Model Context Protocol**