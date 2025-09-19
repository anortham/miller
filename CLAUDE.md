# Miller - Multi-Language Code Intelligence MCP Server

## Project Overview

Miller is a high-performance MCP (Model Context Protocol) server that provides LSP-quality code intelligence across 15-20 programming languages without the overhead of running multiple language servers. Built on Bun for maximum performance, Miller uses Tree-sitter for parsing, SQLite for storage, and MiniSearch for fast code search.

### Key Features

- **Multi-Language Support**: Parse and analyze JavaScript, TypeScript, Python, Rust, Go, Java, C#, C/C++, Ruby, PHP, and more
- **LSP-Like Features**: Go-to-definition, find-references, hover information, call hierarchy
- **Fast Code Search**: Fuzzy search with MiniSearch, exact search with ripgrep
- **Incremental Updates**: File watching with debounced reindexing
- **Cross-Language Analysis**: Track API calls, FFI bindings, and cross-language references
- **High Performance**: Built on Bun with SQLite for microsecond query times

### Technology Stack

- **Runtime**: Bun (for speed and built-in SQLite)
- **Parsing**: Tree-sitter WASM parsers
- **Database**: SQLite with graph-like schema
- **Search**: MiniSearch (fuzzy) + ripgrep (exact)
- **File Watching**: Node.js fs.watch with debouncing
- **Protocol**: Model Context Protocol (MCP)

## Development Setup

### Prerequisites

- [Bun](https://bun.sh/) >= 1.0.0
- Git
- Optional: ripgrep (for exact search performance)

### Installation

```bash
# Clone the repository
git clone <repository-url>
cd miller

# Install dependencies
bun install

# Run in development mode
bun run dev

# Or start the server
bun start
```

### Project Structure

```
miller/
├── src/
│   ├── mcp-server.ts           # Main MCP server entry point
│   ├── database/
│   │   └── schema.ts           # SQLite schema and database operations
│   ├── parser/
│   │   └── parser-manager.ts   # Tree-sitter parser management
│   ├── extractors/
│   │   ├── base-extractor.ts   # Abstract base class for language extractors
│   │   └── typescript-extractor.ts # TypeScript/JavaScript symbol extraction
│   ├── search/
│   │   └── search-engine.ts    # MiniSearch + ripgrep search engine
│   ├── watcher/
│   │   └── file-watcher.ts     # File system watching with debouncing
│   └── engine/
│       └── code-intelligence.ts # Main orchestration engine
├── docs/
│   └── mcp-code-intelligence-guide.md # Detailed implementation guide
├── package.json
├── tsconfig.json
└── CLAUDE.md                   # This file
```

## Architecture Overview

### Component Relationships

```
MCP Server (stdio)
    ↓
Code Intelligence Engine (orchestrator)
    ├── Database (SQLite) - stores symbols, relationships, types
    ├── Parser Manager - handles Tree-sitter WASM parsers
    ├── Search Engine - MiniSearch + ripgrep
    ├── File Watcher - monitors workspace changes
    └── Extractors - language-specific symbol extraction
```

### Key Components

1. **Database Schema** (`src/database/schema.ts`)
   - Stores symbols, relationships, types, bindings, and files
   - Optimized with indexes for fast queries
   - Supports FTS5 for full-text search

2. **Parser Manager** (`src/parser/parser-manager.ts`)
   - Initializes Tree-sitter WASM parsers for each language
   - Maps file extensions to language parsers
   - Provides parsing utilities and error handling

3. **Base Extractor** (`src/extractors/base-extractor.ts`)
   - Abstract class defining the extraction interface
   - Utility methods for Tree-sitter node manipulation
   - Common symbol and relationship creation patterns

4. **Search Engine** (`src/search/search-engine.ts`)
   - Fuzzy search with MiniSearch (code-aware tokenization)
   - Exact search with ripgrep (fallback to SQLite)
   - Type-based search and reference finding

5. **File Watcher** (`src/watcher/file-watcher.ts`)
   - Monitors workspace for file changes
   - Debounces rapid changes to prevent excessive processing
   - Filters supported file types and ignore patterns

6. **Code Intelligence Engine** (`src/engine/code-intelligence.ts`)
   - Orchestrates all components
   - Provides high-level API for LSP-like features
   - Handles incremental updates and workspace indexing

## Usage Instructions

### MCP Client Configuration

Add Miller to your MCP client configuration:

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

### Available MCP Tools

1. **search_code** - Search for symbols with fuzzy matching
2. **goto_definition** - Find symbol definitions
3. **find_references** - Find all symbol references
4. **get_hover_info** - Get symbol type and documentation
5. **get_call_hierarchy** - Get caller/callee relationships
6. **find_cross_language_bindings** - Find API calls between languages
7. **index_workspace** - Index a workspace directory
8. **get_workspace_stats** - Get indexing statistics
9. **health_check** - Check engine health

### Example Usage

```typescript
// Search for a function
await tools.search_code({
  query: "getUserData",
  type: "fuzzy",
  limit: 10,
  includeSignature: true
});

// Go to definition
await tools.goto_definition({
  file: "src/user.ts",
  line: 42,
  column: 15
});

// Find all references
await tools.find_references({
  file: "src/user.ts",
  line: 10,
  column: 20
});
```

## Development Guidelines

### Adding New Language Support

1. **Install Tree-sitter parser**:
   ```bash
   bun add tree-sitter-<language>
   ```

2. **Register in ParserManager**:
   ```typescript
   // In src/parser/parser-manager.ts
   { name: 'python', extensions: ['.py', '.pyw'] }
   ```

3. **Create language extractor**:
   ```typescript
   // src/extractors/python-extractor.ts
   export class PythonExtractor extends BaseExtractor {
     extractSymbols(tree: Parser.Tree): Symbol[] {
       // Implement Python-specific symbol extraction
     }
     // ... other methods
   }
   ```

4. **Register extractor**:
   ```typescript
   // In src/engine/code-intelligence.ts
   this.extractors.set('python', PythonExtractor);
   ```

### Testing

```bash
# Run specific file parsing test
bun run src/mcp-server.ts

# Test with a sample workspace
cd /path/to/test/workspace
bun run /path/to/miller/src/mcp-server.ts

# Monitor logs for parsing/indexing issues
tail -f console.log
```

### Performance Optimization

1. **Batch Processing**: Files are processed in configurable batches
2. **Debouncing**: File changes are debounced to prevent excessive reindexing
3. **Incremental Updates**: Only changed files are reprocessed
4. **Prepared Statements**: All database queries use prepared statements
5. **Indexing**: Strategic database indexes for fast queries

### Database Queries

Key queries for debugging:

```sql
-- Show all symbols
SELECT * FROM symbols LIMIT 10;

-- Show relationships
SELECT * FROM relationships LIMIT 10;

-- Search symbols by name
SELECT * FROM symbols WHERE name LIKE '%function%';

-- Get file statistics
SELECT language, COUNT(*) as count FROM symbols GROUP BY language;
```

## Troubleshooting

### Common Issues

1. **WASM Parser Not Found**
   ```
   Error: Failed to load parser for typescript
   ```
   **Solution**: Ensure Tree-sitter WASM files are installed:
   ```bash
   bun add tree-sitter-typescript
   ```

2. **File Too Large**
   ```
   Warning: Skipping large file: example.js (10MB)
   ```
   **Solution**: Increase maxFileSize in engine config or exclude large files

3. **No Symbols Found**
   ```
   No definition found at the specified location
   ```
   **Solution**:
   - Check if file was indexed: `get_workspace_stats`
   - Verify file extension is supported
   - Check parsing errors in logs

4. **Permission Errors**
   ```
   Error: Cannot read directory /restricted/path
   ```
   **Solution**: Ensure proper file system permissions or exclude directory

5. **Memory Issues**
   ```
   JavaScript heap out of memory
   ```
   **Solution**:
   - Reduce batch size in config
   - Add more ignore patterns for large directories
   - Increase Node.js memory: `--max-old-space-size=4096`

### Debug Mode

Enable verbose logging:

```bash
DEBUG=1 bun run src/mcp-server.ts
```

### Performance Monitoring

Check performance with stats:

```javascript
// Get comprehensive statistics
await tools.get_workspace_stats();

// Check health status
await tools.health_check();
```

## Contributing

### Code Style

- Use TypeScript strict mode
- Follow existing naming conventions
- Add JSDoc comments for public APIs
- Use meaningful variable names

### Pull Request Guidelines

1. **Test thoroughly** with multiple language types
2. **Update documentation** if adding new features
3. **Benchmark performance** for significant changes
4. **Add error handling** for edge cases

### Extension Points

- **New Extractors**: Add support for more programming languages
- **Cross-Language Analysis**: Enhance API call detection
- **Search Improvements**: Add semantic search capabilities
- **Performance**: Optimize for very large codebases
- **Features**: Add code actions, refactoring suggestions

## Performance Expectations

### Typical Performance

- **Parsing**: 100-500 files/second (depends on file size)
- **Search**: < 10ms for fuzzy search, < 50ms for exact search
- **Queries**: < 1ms for go-to-definition, find-references
- **Memory**: ~100MB for 10,000 files, ~500MB for 50,000 files
- **Startup**: 2-10 seconds for initial workspace indexing

### Scaling Recommendations

- **Small Projects** (< 1,000 files): Default settings work well
- **Medium Projects** (1,000-10,000 files): Increase batch size to 20-50
- **Large Projects** (> 10,000 files):
  - Increase batch size to 100
  - Add ignore patterns for build/dist directories
  - Consider running on a machine with more RAM

## License

MIT License - see LICENSE file for details.

---

For detailed implementation information, see `docs/mcp-code-intelligence-guide.md`.