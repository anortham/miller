# Miller - Multi-Language Code Intelligence MCP Server

## Project Overview

Miller is a high-performance MCP (Model Context Protocol) server that provides LSP-quality code intelligence across **20 programming languages** without the overhead of running multiple language servers. Built on Bun for maximum performance, Miller uses Tree-sitter for parsing, SQLite for storage, and MiniSearch for fast code search.

### Key Features

- **Multi-Language Support**: Parse and analyze 20 languages including:
  - **Web**: JavaScript, TypeScript, HTML, CSS, Vue
  - **Backend**: Python, Rust, Go, Java, C#, PHP, Ruby
  - **Systems**: C, C++
  - **Mobile**: Swift, Kotlin
  - **Game Development**: GDScript, Lua
  - **Web Frameworks**: Razor (Blazor)
  - **Utilities**: Regex patterns
- **LSP-Like Features**: Go-to-definition, find-references, hover information, call hierarchy
- **Fast Code Search**: Fuzzy search with MiniSearch, exact search with ripgrep
- **Semantic Search**: AI-powered code understanding with cross-language concept matching
- **Incremental Updates**: File watching with debounced reindexing
- **Cross-Language Analysis**: Track API calls, FFI bindings, and cross-language references
- **High Performance**: Built on Bun with SQLite for microsecond query times

### Technology Stack

- **Runtime**: Bun (for speed and built-in SQLite)
- **Parsing**: Tree-sitter WASM parsers
- **Database**: SQLite with graph-like schema + Vectra for embeddings
- **Search**: MiniSearch (fuzzy) + ripgrep (exact) + Semantic (AI embeddings)
- **AI Models**: Xenova/all-MiniLM-L6-v2 for code embeddings
- **File Watching**: Node.js fs.watch with debouncing
- **Protocol**: Model Context Protocol (MCP)

## Development Setup

### Prerequisites

- [Bun](https://bun.sh/) >= 1.0.0
- Git

#### Required for Semantic Search

**Vector Search Dependencies:**
```bash
# Vectra is automatically installed via npm/bun
# No additional system dependencies required
```

**Performance Optimization:**
```bash
# Install ripgrep for fast exact search (recommended)
brew install ripgrep

# Verify installation
rg --version
```

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

### Setup Verification

**Check semantic search is working:**
```bash
# Index a workspace
bun run src/mcp-server.ts

# In another terminal, test semantic search
# Should show >0% semantic scores if working properly
```

### Common Setup Issues

1. **Vector search issues**
   - **Solution**: Vectra handles vector storage automatically
   - **Cause**: No system dependencies required

2. **"Ripgrep not available or failed, falling back to database search"**
   - **Solution**: Install ripgrep (above)
   - **Impact**: Slower exact searches, but semantic search unaffected

3. **"Semantic search returns 0% scores"** ✅ **FIXED**
   - **Status**: Semantic search now works with 38-39% relevance scores
   - **Check**: Vectra index is properly initialized
   - **Check**: Database has >0 embeddings stored
   - **Note**: Uses optimized distance scoring for best results

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
│   │   ├── typescript-extractor.ts # TypeScript/JavaScript symbol extraction
│   │   ├── gdscript-extractor.ts # GDScript (Godot) game dev language
│   │   ├── lua-extractor.ts    # Lua scripting language
│   │   └── [17 other language extractors...]
│   ├── search/
│   │   └── search-engine.ts    # MiniSearch + ripgrep search engine
│   ├── watcher/
│   │   └── file-watcher.ts     # File system watching with debouncing
│   ├── workers/                # Worker processes for semantic operations
│   │   ├── embedding-process.ts # Standalone embedding generation process
│   │   └── embedding-process-pool.ts # Process pool for parallel embedding
│   ├── __tests__/              # Comprehensive test suites
│   │   ├── parser/             # Language extractor tests (TDD)
│   │   │   ├── gdscript-extractor.test.ts
│   │   │   ├── lua-extractor.test.ts
│   │   │   └── [18 other language tests...]
│   │   ├── mcp/                # MCP integration tests
│   │   └── integration/        # End-to-end tests
│   └── engine/
│       └── code-intelligence.ts # Main orchestration engine
├── wasm/                       # Tree-sitter WASM parsers
│   ├── tree-sitter-gdscript.wasm
│   ├── tree-sitter-lua.wasm
│   └── [18 other WASM parsers...]
├── debug/                      # Debug scripts (development tools)
├── docs/
│   └── mcp-code-intelligence-guide.md # Detailed implementation guide
├── CROWN_JEWEL_STANDARDS.md   # Quality standards and roadmap
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

## Worker Process Architecture

Miller uses isolated child processes to prevent UI lockup during CPU-intensive operations:

### Components:
- **embedding-process.ts**: Standalone Bun script for embedding generation
- **embedding-process-pool.ts**: Pool manager for parallel processing
- **IPC Communication**: Message-based communication between processes

### Benefits:
- UI remains responsive during semantic indexing (reduced from 30-60s lockup to <100ms perceived lag)
- Parallel embedding generation for improved speed
- Graceful error isolation and recovery
- Memory efficiency through process isolation

### Architecture Flow:
```
Main Process (MCP Server)
    ↓
Embedding Process Pool
    ├── Worker Process 1 (embedding-process.ts)
    ├── Worker Process 2 (embedding-process.ts)
    └── Worker Process N (embedding-process.ts)
```

### Key Features:
- **Process Pool Management**: Automatic worker spawning and cleanup
- **Health Monitoring**: Process health checks and automatic restarts
- **Graceful Shutdown**: Clean process termination on server shutdown
- **Error Handling**: Isolated failures don't crash main process
- **Performance**: 10ms yield intervals prevent UI blocking

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
10. **explore** - Advanced code exploration with semantic understanding
11. **navigate** - Surgical navigation with 100% accuracy
12. **semantic** - AI-powered semantic search across languages

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

### Test-Driven Development (TDD) Methodology

Miller follows strict TDD principles that have proven essential for:
- **Bug Prevention**: Tests written before code catch issues early
- **Debugging Complex Issues**: TDD helped us fix 7 critical semantic search bugs
- **Confidence in Refactoring**: Comprehensive test coverage enables safe changes
- **Documentation through Tests**: Tests serve as living documentation

#### TDD Process for New Features:
1. **Write comprehensive tests FIRST** (5-8 test scenarios)
2. **Run tests to ensure they fail** (validates test correctness)
3. **Implement minimal code** to make tests pass
4. **Refactor with confidence** knowing tests will catch regressions

#### TDD Success Stories:
- **Fixed UNIQUE constraint violations** through test-first debugging
- **Resolved UI lockup issues** by testing yield strategies with performance tests
- **Debugged threshold mismatches** with comprehensive integration tests
- **Semantic search bugs**: All 7 critical bugs were fixed using TDD approach

#### When Debugging:
**ALWAYS create a test that reproduces the bug BEFORE attempting to fix it.**
This ensures the bug is truly fixed and won't regress.

#### Test Categories in Miller:
- **Unit Tests**: Individual component testing (extractors, embedders, search engines)
- **Integration Tests**: Multi-component workflows (semantic search, cross-layer mapping)
- **Regression Tests**: Prevent known bugs from returning
- **Performance Tests**: Validate speed and scale requirements

### Adding New Language Support

Miller follows a proven TDD process for adding new languages:

1. **Install Tree-sitter parser**:
   ```bash
   bun add tree-sitter-<language>
   # Build WASM parser (if needed)
   npx tree-sitter build-wasm node_modules/tree-sitter-<language>
   ```

2. **Create comprehensive test suite FIRST** (TDD):
   ```typescript
   // src/__tests__/parser/language-extractor.test.ts
   describe('LanguageExtractor', () => {
     // Create 5-8 test scenarios covering all language features
     // Example: classes, functions, variables, language-specific constructs
   });
   ```

3. **Register in ParserManager**:
   ```typescript
   // In src/parser/parser-manager.ts
   { name: 'language', extensions: ['.ext'] }
   ```

4. **Create language extractor**:
   ```typescript
   // src/extractors/language-extractor.ts
   export class LanguageExtractor extends BaseExtractor {
     extractSymbols(tree: Parser.Tree): Symbol[] {
       // Implement language-specific symbol extraction
     }
     extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
       // Implement relationship extraction
     }
     inferTypes(symbols: Symbol[]): Map<string, string> {
       // Implement type inference
     }
   }
   ```

5. **Register extractor**:
   ```typescript
   // In src/engine/code-intelligence.ts
   this.extractors.set('language', LanguageExtractor);
   ```

### Game Development Language Examples

**GDScript** (Godot game engine):
- Classes with inheritance: `class Player extends CharacterBody2D`
- Signals: `signal health_changed(new_health: int)`
- Export annotations: `@export var speed: float = 200.0`
- Constants and enums: `const MAX_LIVES: int = 3`, `enum State { IDLE, WALKING }`

**Lua** (Scripting language):
- Functions: `function calculateDamage(base, modifier) end`
- Local variables: `local health = 100`
- Tables: `player = { name = "Hero", level = 1 }`
- Metatables and modules for advanced patterns

### Testing

**This is a test first TDD project**

Miller follows strict TDD methodology: **tests before implementation**. Recent examples include:
- GDScript: 5 comprehensive test scenarios created before extractor implementation
- Lua: 8 test scenarios covering functions, variables, tables, and modules

```bash
# Run all tests
bun test

# Run specific language extractor tests
bun test src/__tests__/parser/gdscript-extractor.test.ts
bun test src/__tests__/parser/lua-extractor.test.ts

# Run MCP integration tests
bun test src/__tests__/mcp/integration.test.ts

# Test with a sample workspace
cd /path/to/test/workspace
bun run /path/to/miller/src/mcp-server.ts

# Monitor logs for parsing/indexing issues
tail -f console.log
```

### Current Test Status
- **Core Languages**: 90-100% test success rates (JavaScript, TypeScript, Python, etc.)
- **Game Development**:
  - GDScript: 20% (1/5 tests passing) - classes, inheritance, constants, enums working
  - Lua: 15% foundation implemented
- **Systems Languages**: 80-95% success rates
- **MCP Integration**: 100% (21/21 tests passing)

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

### Log Files

Miller maintains detailed logs in the workspace directory:

```bash
# Log directory location
.miller/logs/

# Log file types
- miller-YYYY-MM-DD.log     # General application logs
- errors-YYYY-MM-DD.log     # Error-specific logs for debugging

# Example usage
tail -f .miller/logs/errors-$(date +%Y-%m-%d).log  # Follow current error log
grep -i "semantic" .miller/logs/miller-$(date +%Y-%m-%d).log  # Search semantic logs
```

Common log locations for troubleshooting:
- **Semantic indexing issues**: Check errors log for embedding/vector failures
- **Performance problems**: Check miller log for timing and stats
- **Database issues**: Look for SQLite constraint errors in errors log

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
- **Semantic Enhancements**: Improve embedding models and cross-layer entity mapping
- **Performance**: Optimize for very large codebases
- **Features**: Add code actions, refactoring suggestions

## Performance Expectations

### Typical Performance

- **Parsing**: 100-500 files/second (depends on file size)
- **Search**: < 10ms for fuzzy search, < 50ms for exact search, < 100ms for semantic search
- **Queries**: < 1ms for go-to-definition, find-references
- **Semantic Indexing**: 500 symbols with 10ms yield intervals to prevent UI lockup
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
Always check the TODO.md doc for pending instructions.
