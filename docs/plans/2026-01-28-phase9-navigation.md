# Phase 9: Code Navigation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add find-references, go-to-definition, and symbol browsing capabilities for full code navigation.

**Architecture:** Add query methods to engine for identifiers and symbols, expose via FFI, create NavigationTool with references/definition/symbols operations. Leverages identifiers extracted in Phase 8.

**Tech Stack:** Rust (LanceDB queries), UniFFI, C# MCP tools

---

## Prerequisites

Phase 8 complete with:
- Julie extraction producing symbols, identifiers, relationships
- Identifiers stored in LanceDB
- 42 passing tests

---

### Task 1: Add get_references Query to Engine

**Files:**
- Modify: `rust/codesearch-core/src/engine.rs`

**Step 1: Add ReferenceResult struct**

Add near other result types:

```rust
/// Result of a reference query
pub struct ReferenceResult {
    pub name: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub column: u32,
    pub source_symbol_id: Option<String>,
}
```

**Step 2: Add get_references method**

Add to `impl CodeEngine`:

```rust
/// Get all references to a symbol (where it's used)
pub async fn get_references(&self, symbol_id: &str, limit: usize) -> Result<Vec<ReferenceResult>> {
    let table_names: Vec<String> = self.db.table_names().await?;
    if !table_names.contains(&schema::IDENTIFIERS_TABLE_NAME.to_string()) {
        return Ok(Vec::new());
    }

    let table = self.db.open_table(schema::IDENTIFIERS_TABLE_NAME).await?;

    let results = table
        .query()
        .filter(format!(
            "target_symbol_id = '{}'",
            symbol_id.replace("'", "''")
        ))
        .limit(limit)
        .execute()
        .await?;

    let mut references = Vec::new();
    let batches: Vec<RecordBatch> = results.try_collect().await?;

    for batch in batches {
        let names = batch.column_by_name("name")
            .unwrap().as_any().downcast_ref::<StringArray>().unwrap();
        let kinds = batch.column_by_name("kind")
            .unwrap().as_any().downcast_ref::<StringArray>().unwrap();
        let paths = batch.column_by_name("file_path")
            .unwrap().as_any().downcast_ref::<StringArray>().unwrap();
        let lines = batch.column_by_name("line_number")
            .unwrap().as_any().downcast_ref::<UInt32Array>().unwrap();
        let cols = batch.column_by_name("column")
            .unwrap().as_any().downcast_ref::<UInt32Array>().unwrap();
        let source_ids = batch.column_by_name("source_symbol_id")
            .unwrap().as_any().downcast_ref::<StringArray>().unwrap();

        for i in 0..batch.num_rows() {
            references.push(ReferenceResult {
                name: names.value(i).to_string(),
                kind: kinds.value(i).to_string(),
                file_path: paths.value(i).to_string(),
                line_number: lines.value(i),
                column: cols.value(i),
                source_symbol_id: if source_ids.is_null(i) {
                    None
                } else {
                    Some(source_ids.value(i).to_string())
                },
            });
        }
    }

    Ok(references)
}
```

**Step 3: Verify it compiles**

Run: `cd /Users/murphy/source/codesearch/rust && cargo build`

**Step 4: Commit**

```bash
git add rust/codesearch-core/src/engine.rs
git commit -m "feat(core): add get_references query for find-references"
```

---

### Task 2: Add Symbol Query Methods to Engine

**Files:**
- Modify: `rust/codesearch-core/src/engine.rs`

**Step 1: Add SymbolInfo struct**

Add near other result types:

```rust
/// Detailed symbol information
pub struct SymbolInfo {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub language: String,
    pub file_path: String,
    pub start_line: i32,
    pub end_line: i32,
    pub signature: Option<String>,
    pub doc_comment: Option<String>,
}
```

**Step 2: Add get_symbol_by_id method**

```rust
/// Get a symbol by its ID
pub async fn get_symbol_by_id(&self, symbol_id: &str) -> Result<Option<SymbolInfo>> {
    let table = self.db.open_table(schema::SYMBOLS_TABLE_NAME).await?;

    let results = table
        .query()
        .filter(format!("id = '{}'", symbol_id.replace("'", "''")))
        .limit(1)
        .execute()
        .await?;

    let batches: Vec<RecordBatch> = results.try_collect().await?;

    if batches.is_empty() || batches[0].num_rows() == 0 {
        return Ok(None);
    }

    let batch = &batches[0];
    Ok(Some(self.batch_to_symbol_info(batch, 0)))
}

fn batch_to_symbol_info(&self, batch: &RecordBatch, idx: usize) -> SymbolInfo {
    let ids = batch.column_by_name("id").unwrap().as_any().downcast_ref::<StringArray>().unwrap();
    let names = batch.column_by_name("name").unwrap().as_any().downcast_ref::<StringArray>().unwrap();
    let kinds = batch.column_by_name("kind").unwrap().as_any().downcast_ref::<StringArray>().unwrap();
    let languages = batch.column_by_name("language").unwrap().as_any().downcast_ref::<StringArray>().unwrap();
    let paths = batch.column_by_name("file_path").unwrap().as_any().downcast_ref::<StringArray>().unwrap();
    let start_lines = batch.column_by_name("start_line").unwrap().as_any().downcast_ref::<Int32Array>().unwrap();
    let end_lines = batch.column_by_name("end_line").unwrap().as_any().downcast_ref::<Int32Array>().unwrap();
    let signatures = batch.column_by_name("signature").unwrap().as_any().downcast_ref::<StringArray>().unwrap();
    let doc_comments = batch.column_by_name("doc_comment").unwrap().as_any().downcast_ref::<StringArray>().unwrap();

    SymbolInfo {
        id: ids.value(idx).to_string(),
        name: names.value(idx).to_string(),
        kind: kinds.value(idx).to_string(),
        language: languages.value(idx).to_string(),
        file_path: paths.value(idx).to_string(),
        start_line: start_lines.value(idx),
        end_line: end_lines.value(idx),
        signature: if signatures.is_null(idx) { None } else { Some(signatures.value(idx).to_string()) },
        doc_comment: if doc_comments.is_null(idx) { None } else { Some(doc_comments.value(idx).to_string()) },
    }
}
```

**Step 3: Add get_symbols_by_file method**

```rust
/// Get all symbols in a file
pub async fn get_symbols_by_file(&self, file_path: &str, limit: usize) -> Result<Vec<SymbolInfo>> {
    let table = self.db.open_table(schema::SYMBOLS_TABLE_NAME).await?;

    let results = table
        .query()
        .filter(format!("file_path = '{}'", file_path.replace("'", "''")))
        .limit(limit)
        .execute()
        .await?;

    let mut symbols = Vec::new();
    let batches: Vec<RecordBatch> = results.try_collect().await?;

    for batch in batches {
        for i in 0..batch.num_rows() {
            symbols.push(self.batch_to_symbol_info(&batch, i));
        }
    }

    Ok(symbols)
}
```

**Step 4: Add get_symbols_by_kind method**

```rust
/// Get all symbols of a specific kind
pub async fn get_symbols_by_kind(&self, kind: &str, limit: usize) -> Result<Vec<SymbolInfo>> {
    let table = self.db.open_table(schema::SYMBOLS_TABLE_NAME).await?;

    let results = table
        .query()
        .filter(format!("kind = '{}'", kind.replace("'", "''")))
        .limit(limit)
        .execute()
        .await?;

    let mut symbols = Vec::new();
    let batches: Vec<RecordBatch> = results.try_collect().await?;

    for batch in batches {
        for i in 0..batch.num_rows() {
            symbols.push(self.batch_to_symbol_info(&batch, i));
        }
    }

    Ok(symbols)
}
```

**Step 5: Verify it compiles**

Run: `cd /Users/murphy/source/codesearch/rust && cargo build`

**Step 6: Commit**

```bash
git add rust/codesearch-core/src/engine.rs
git commit -m "feat(core): add symbol query methods for navigation"
```

---

### Task 3: Export New Types from Core

**Files:**
- Modify: `rust/codesearch-core/src/lib.rs`

**Step 1: Export the new types**

Add to the pub use statement:

```rust
pub use engine::{ReferenceResult, SymbolInfo};
```

**Step 2: Verify it compiles**

Run: `cd /Users/murphy/source/codesearch/rust && cargo build`

**Step 3: Commit**

```bash
git add rust/codesearch-core/src/lib.rs
git commit -m "chore(core): export ReferenceResult and SymbolInfo"
```

---

### Task 4: Expose Navigation Methods in FFI

**Files:**
- Modify: `rust/codesearch-ffi/src/lib.rs`

**Step 1: Add FFI types**

Add these UniFFI record types:

```rust
/// FFI-safe reference result
#[derive(Debug, Clone, uniffi::Record)]
pub struct ReferenceResult {
    pub name: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub column: u32,
    pub source_symbol_id: Option<String>,
}

/// FFI-safe symbol info
#[derive(Debug, Clone, uniffi::Record)]
pub struct SymbolInfo {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub language: String,
    pub file_path: String,
    pub start_line: i32,
    pub end_line: i32,
    pub signature: Option<String>,
    pub doc_comment: Option<String>,
}

impl From<codesearch_core::ReferenceResult> for ReferenceResult {
    fn from(r: codesearch_core::ReferenceResult) -> Self {
        Self {
            name: r.name,
            kind: r.kind,
            file_path: r.file_path,
            line_number: r.line_number,
            column: r.column,
            source_symbol_id: r.source_symbol_id,
        }
    }
}

impl From<codesearch_core::SymbolInfo> for SymbolInfo {
    fn from(s: codesearch_core::SymbolInfo) -> Self {
        Self {
            id: s.id,
            name: s.name,
            kind: s.kind,
            language: s.language,
            file_path: s.file_path,
            start_line: s.start_line,
            end_line: s.end_line,
            signature: s.signature,
            doc_comment: s.doc_comment,
        }
    }
}
```

**Step 2: Add methods to CodeSearchEngine**

Add to `impl CodeSearchEngine`:

```rust
/// Get all references to a symbol
pub fn get_references(&self, symbol_id: String, limit: u32) -> Result<Vec<ReferenceResult>, CodeSearchError> {
    self.runtime.block_on(async {
        self.inner
            .get_references(&symbol_id, limit as usize)
            .await
            .map(|results| results.into_iter().map(ReferenceResult::from).collect())
            .map_err(CodeSearchError::from)
    })
}

/// Get a symbol by ID
pub fn get_symbol_by_id(&self, symbol_id: String) -> Result<Option<SymbolInfo>, CodeSearchError> {
    self.runtime.block_on(async {
        self.inner
            .get_symbol_by_id(&symbol_id)
            .await
            .map(|opt| opt.map(SymbolInfo::from))
            .map_err(CodeSearchError::from)
    })
}

/// Get symbols in a file
pub fn get_symbols_by_file(&self, file_path: String, limit: u32) -> Result<Vec<SymbolInfo>, CodeSearchError> {
    self.runtime.block_on(async {
        self.inner
            .get_symbols_by_file(&file_path, limit as usize)
            .await
            .map(|results| results.into_iter().map(SymbolInfo::from).collect())
            .map_err(CodeSearchError::from)
    })
}

/// Get symbols by kind
pub fn get_symbols_by_kind(&self, kind: String, limit: u32) -> Result<Vec<SymbolInfo>, CodeSearchError> {
    self.runtime.block_on(async {
        self.inner
            .get_symbols_by_kind(&kind, limit as usize)
            .await
            .map(|results| results.into_iter().map(SymbolInfo::from).collect())
            .map_err(CodeSearchError::from)
    })
}
```

**Step 3: Verify it compiles**

Run: `cd /Users/murphy/source/codesearch/rust && cargo build`

**Step 4: Commit**

```bash
git add rust/codesearch-ffi/src/lib.rs
git commit -m "feat(ffi): expose navigation methods"
```

---

### Task 5: Regenerate C# Bindings

**Step 1: Build release and regenerate**

```bash
cd /Users/murphy/source/codesearch
./scripts/generate-bindings.sh
```

**Step 2: Verify C# builds**

Run: `dotnet build src/Codesearch.Interop`

**Step 3: Commit**

```bash
git add src/Codesearch.Interop/Generated/codesearch_ffi.cs
git commit -m "chore: regenerate C# bindings with navigation APIs"
```

---

### Task 6: Add Navigation Methods to SearchService

**Files:**
- Modify: `src/Codesearch.Server/Services/SearchService.cs`

**Step 1: Add navigation methods**

Add to `SearchService`:

```csharp
/// <summary>
/// Get all references to a symbol (find usages).
/// </summary>
public List<uniffi.codesearch_ffi.ReferenceResult> GetReferences(string symbolId, uint limit = 100)
{
    return _engine.GetReferences(symbolId, limit);
}

/// <summary>
/// Get a symbol by its ID (go to definition).
/// </summary>
public uniffi.codesearch_ffi.SymbolInfo? GetSymbolById(string symbolId)
{
    return _engine.GetSymbolById(symbolId);
}

/// <summary>
/// Get all symbols in a file.
/// </summary>
public List<uniffi.codesearch_ffi.SymbolInfo> GetSymbolsByFile(string filePath, uint limit = 1000)
{
    return _engine.GetSymbolsByFile(filePath, limit);
}

/// <summary>
/// Get all symbols of a specific kind.
/// </summary>
public List<uniffi.codesearch_ffi.SymbolInfo> GetSymbolsByKind(string kind, uint limit = 1000)
{
    return _engine.GetSymbolsByKind(kind, limit);
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Services/SearchService.cs
git commit -m "feat(server): add navigation methods to SearchService"
```

---

### Task 7: Create NavigationTool

**Files:**
- Create: `src/Codesearch.Server/Tools/NavigationTool.cs`

**Step 1: Create the navigation tool**

Create `src/Codesearch.Server/Tools/NavigationTool.cs`:

```csharp
using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class NavigationTool
{
    [McpServerTool]
    [Description("Navigate code: find references, go to definition, browse symbols. Operations: references, definition, symbols.")]
    internal static string Navigate(
        SearchService searchService,
        [Description("Operation: references, definition, or symbols")] string operation,
        [Description("Symbol ID or name")] string? symbol = null,
        [Description("File path (for symbols operation)")] string? file = null,
        [Description("Symbol kind filter (function, class, method, etc.)")] string? kind = null,
        [Description("Maximum results")] int limit = 50)
    {
        return operation.ToLowerInvariant() switch
        {
            "references" => FindReferences(searchService, symbol ?? "", limit),
            "definition" => GoToDefinition(searchService, symbol ?? ""),
            "symbols" => BrowseSymbols(searchService, file, kind, limit),
            _ => $"Unknown operation: {operation}. Use: references, definition, or symbols."
        };
    }

    private static string FindReferences(SearchService searchService, string symbol, int limit)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return "Error: symbol parameter required for references operation.";
        }

        // Resolve symbol name to ID if needed
        var symbolId = ResolveSymbolId(searchService, symbol);
        if (symbolId == null)
        {
            return $"No symbol found matching '{symbol}'.";
        }

        var references = searchService.GetReferences(symbolId, (uint)limit);

        if (references.Count == 0)
        {
            return $"No references found for '{symbol}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## References to `{symbol}` ({references.Count} found)");
        sb.AppendLine();

        // Group by file
        var byFile = references.GroupBy(r => r.filePath).OrderBy(g => g.Key);

        foreach (var fileGroup in byFile)
        {
            sb.AppendLine($"### {fileGroup.Key}");
            foreach (var r in fileGroup.OrderBy(r => r.lineNumber))
            {
                sb.AppendLine($"- Line {r.lineNumber}:{r.column} - `{r.name}` ({r.kind})");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GoToDefinition(SearchService searchService, string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return "Error: symbol parameter required for definition operation.";
        }

        // Try direct ID lookup first
        var symbolInfo = searchService.GetSymbolById(symbol);

        if (symbolInfo == null)
        {
            // Try searching by name
            var searchResults = searchService.SearchText(symbol, 5);
            if (searchResults.Count == 0)
            {
                return $"No definition found for '{symbol}'.";
            }

            // Return top matches
            var sb = new StringBuilder();
            sb.AppendLine($"## Definitions matching `{symbol}`");
            sb.AppendLine();

            foreach (var result in searchResults)
            {
                sb.AppendLine($"### {result.name} ({result.kind})");
                sb.AppendLine($"- **File**: `{result.filePath}:{result.startLine}-{result.endLine}`");
                sb.AppendLine($"- **Language**: {result.language}");
                if (!string.IsNullOrEmpty(result.signature))
                {
                    sb.AppendLine($"- **Signature**: `{result.signature}`");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // Format single definition
        var output = new StringBuilder();
        output.AppendLine($"## Definition of `{symbolInfo.name}`");
        output.AppendLine();
        output.AppendLine($"- **Kind**: {symbolInfo.kind}");
        output.AppendLine($"- **Language**: {symbolInfo.language}");
        output.AppendLine($"- **File**: `{symbolInfo.filePath}:{symbolInfo.startLine}-{symbolInfo.endLine}`");

        if (!string.IsNullOrEmpty(symbolInfo.signature))
        {
            output.AppendLine($"- **Signature**: `{symbolInfo.signature}`");
        }

        if (!string.IsNullOrEmpty(symbolInfo.docComment))
        {
            output.AppendLine();
            output.AppendLine("### Documentation");
            output.AppendLine(symbolInfo.docComment);
        }

        return output.ToString();
    }

    private static string BrowseSymbols(SearchService searchService, string? file, string? kind, int limit)
    {
        List<uniffi.codesearch_ffi.SymbolInfo> symbols;
        string filterDesc;

        if (!string.IsNullOrEmpty(file))
        {
            symbols = searchService.GetSymbolsByFile(file, (uint)limit);
            filterDesc = $"in `{file}`";
        }
        else if (!string.IsNullOrEmpty(kind))
        {
            symbols = searchService.GetSymbolsByKind(kind, (uint)limit);
            filterDesc = $"of kind `{kind}`";
        }
        else
        {
            return "Error: Provide either 'file' or 'kind' parameter for symbols operation.";
        }

        if (symbols.Count == 0)
        {
            return $"No symbols found {filterDesc}.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Symbols {filterDesc} ({symbols.Count} found)");
        sb.AppendLine();

        // Group by kind when filtering by file, by file when filtering by kind
        if (!string.IsNullOrEmpty(file))
        {
            var byKind = symbols.GroupBy(s => s.kind).OrderBy(g => g.Key);
            foreach (var group in byKind)
            {
                sb.AppendLine($"### {group.Key} ({group.Count()})");
                foreach (var s in group.OrderBy(s => s.startLine))
                {
                    var sig = !string.IsNullOrEmpty(s.signature) ? $" - `{s.signature}`" : "";
                    sb.AppendLine($"- `{s.name}` (line {s.startLine}){sig}");
                }
                sb.AppendLine();
            }
        }
        else
        {
            var byFile = symbols.GroupBy(s => s.filePath).OrderBy(g => g.Key);
            foreach (var group in byFile)
            {
                sb.AppendLine($"### {group.Key}");
                foreach (var s in group.OrderBy(s => s.startLine))
                {
                    sb.AppendLine($"- `{s.name}` (line {s.startLine})");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string? ResolveSymbolId(SearchService searchService, string symbol)
    {
        // If it looks like an ID (contains :: or /), use directly
        if (symbol.Contains("::") || symbol.Contains("/"))
        {
            return symbol;
        }

        // Otherwise search for it
        var results = searchService.SearchText(symbol, 1);
        return results.Count > 0 ? results[0].id : null;
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Tools/NavigationTool.cs
git commit -m "feat(server): add NavigationTool for code navigation"
```

---

### Task 8: Add Navigation Tests

**Files:**
- Create: `tests/Codesearch.Tests/NavigationTests.cs`

**Step 1: Create navigation tests**

Create `tests/Codesearch.Tests/NavigationTests.cs`:

```csharp
using Xunit;
using Codesearch.Server.Services;

namespace Codesearch.Tests;

public class NavigationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SearchService _searchService;

    public NavigationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_nav_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var dbPath = Path.Combine(_tempDir, "test.lance");
        _searchService = new SearchService(dbPath);
    }

    public void Dispose()
    {
        _searchService.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GetSymbolById_ReturnsSymbol()
    {
        // Add a test symbol
        var symbol = new uniffi.codesearch_ffi.SymbolInput(
            id: "test::my_function",
            name: "my_function",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn my_function()",
            docComment: "A test function",
            startLine: 1,
            endLine: 5,
            content: null
        );
        var vector = Enumerable.Repeat(0.0f, 768).ToList();
        _searchService.AddSymbols(
            new List<uniffi.codesearch_ffi.SymbolInput> { symbol },
            new List<List<float>> { vector }
        );

        // Query by ID
        var result = _searchService.GetSymbolById("test::my_function");

        Assert.NotNull(result);
        Assert.Equal("my_function", result.name);
        Assert.Equal("function", result.kind);
    }

    [Fact]
    public void GetSymbolById_ReturnsNullForMissing()
    {
        var result = _searchService.GetSymbolById("nonexistent::symbol");
        Assert.Null(result);
    }

    [Fact]
    public void GetSymbolsByFile_ReturnsSymbolsInFile()
    {
        // Add symbols in same file
        var symbols = new List<uniffi.codesearch_ffi.SymbolInput>
        {
            new("file1::func1", "func1", "function", "rust", "src/lib.rs", null, null, 1, 5, null),
            new("file1::func2", "func2", "function", "rust", "src/lib.rs", null, null, 10, 15, null),
            new("file2::func3", "func3", "function", "rust", "src/main.rs", null, null, 1, 5, null),
        };
        var vectors = symbols.Select(_ => Enumerable.Repeat(0.0f, 768).ToList()).ToList();
        _searchService.AddSymbols(symbols, vectors);

        // Query by file
        var results = _searchService.GetSymbolsByFile("src/lib.rs", 100);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("src/lib.rs", r.filePath));
    }

    [Fact]
    public void GetSymbolsByKind_ReturnsSymbolsOfKind()
    {
        // Add symbols of different kinds
        var symbols = new List<uniffi.codesearch_ffi.SymbolInput>
        {
            new("sym1", "MyClass", "class", "python", "test.py", null, null, 1, 10, null),
            new("sym2", "my_func", "function", "python", "test.py", null, null, 15, 20, null),
            new("sym3", "OtherClass", "class", "python", "test.py", null, null, 25, 35, null),
        };
        var vectors = symbols.Select(_ => Enumerable.Repeat(0.0f, 768).ToList()).ToList();
        _searchService.AddSymbols(symbols, vectors);

        // Query by kind
        var results = _searchService.GetSymbolsByKind("class", 100);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("class", r.kind));
    }

    [Fact]
    public void GetReferences_ReturnsIdentifiersTargetingSymbol()
    {
        // Add a symbol
        var symbol = new uniffi.codesearch_ffi.SymbolInput(
            id: "target::symbol",
            name: "target_func",
            kind: "function",
            language: "rust",
            filePath: "lib.rs",
            signature: null,
            docComment: null,
            startLine: 1,
            endLine: 5,
            content: null
        );
        var vector = Enumerable.Repeat(0.0f, 768).ToList();
        _searchService.AddSymbols(new List<uniffi.codesearch_ffi.SymbolInput> { symbol }, new List<List<float>> { vector });

        // Add identifiers that reference the symbol
        var identifiers = new List<uniffi.codesearch_ffi.IdentifierInput>
        {
            new("target_func", "Call", "main.rs", 10, 5, "caller::func", "target::symbol"),
            new("target_func", "Call", "other.rs", 20, 8, "other::func", "target::symbol"),
        };
        _searchService.AddIdentifiers(identifiers);

        // Query references
        var refs = _searchService.GetReferences("target::symbol", 100);

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.filePath == "main.rs");
        Assert.Contains(refs, r => r.filePath == "other.rs");
    }
}
```

**Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~NavigationTests"`
Expected: All tests pass

**Step 3: Commit**

```bash
git add tests/Codesearch.Tests/NavigationTests.cs
git commit -m "test: add navigation tests"
```

---

### Task 9: Update Plugin Documentation

**Files:**
- Modify: `.claude-plugin/README.md`
- Modify: `.claude/settings.local.json.example`

**Step 1: Add navigate tool documentation**

Add to the MCP Tools section in `.claude-plugin/README.md`:

```markdown
### navigate

Navigate code: find references, go to definition, browse symbols.

```
navigate(operation="references", symbol="functionName", limit=50)
navigate(operation="definition", symbol="functionName")
navigate(operation="symbols", file="src/main.rs")
navigate(operation="symbols", kind="function", limit=100)
```

Operations:
- **references**: Find all places where a symbol is used
- **definition**: Go to the definition of a symbol
- **symbols**: Browse symbols by file or kind
```

**Step 2: Update settings template**

Add `mcp__codesearch__navigate` to the allow list in `.claude/settings.local.json.example`.

**Step 3: Commit**

```bash
git add .claude-plugin/README.md .claude/settings.local.json.example
git commit -m "docs(plugin): add navigate tool documentation"
```

---

### Task 10: Final Verification

**Step 1: Build everything**

```bash
cd /Users/murphy/source/codesearch
dotnet build
```

**Step 2: Run all tests**

Run: `dotnet test`
Expected: All tests pass (42 + 5 = 47 tests)

**Step 3: Commit any fixes**

```bash
git add -A
git commit -m "chore: final Phase 9 fixes" --allow-empty
```

---

## Phase 9 Complete

At this point you have:
- **Find References**: `navigate(operation="references", symbol="X")` - all usages of X
- **Go to Definition**: `navigate(operation="definition", symbol="X")` - where X is defined
- **Symbol Browsing**: `navigate(operation="symbols", file="path")` or `kind="function"`

**Navigation workflow:**
1. `index(operation="full")` - extract symbols and identifiers
2. `navigate(operation="symbols", file="src/main.rs")` - see what's in a file
3. `navigate(operation="definition", symbol="my_function")` - find where it's defined
4. `navigate(operation="references", symbol="my_function")` - find all usages

**Next Phase (10):** Semantic search with embeddings.
