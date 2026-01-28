# Phase 8: Julie Extractors Integration

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace 122k lines of duplicated extractors with julie-extractors, enabling real symbol/relationship extraction during indexing and adding impact analysis.

**Architecture:** Delete codesearch-extractors, add julie-extractors as FFI dependency, expose extraction via UniFFI, update IndexService to extract symbols/identifiers/relationships per file, add transitive closure for O(1) impact queries.

**Tech Stack:** julie-extractors (Rust), UniFFI, LanceDB, C#/.NET

---

## Prerequisites

Phase 7 complete with:
- Relationship storage and query APIs working
- 34 passing tests
- RelationshipTool for callers/callees

---

### Task 1: Delete Custom Extractors

**Files:**
- Delete: `rust/codesearch-extractors/` (entire directory)
- Modify: `rust/Cargo.toml` (remove workspace member)

**Step 1: Remove from workspace**

Edit `rust/Cargo.toml` to remove codesearch-extractors from members:

```toml
[workspace]
members = [
    "codesearch-core",
    "codesearch-ffi",
    # Remove: "codesearch-extractors",
]
```

**Step 2: Delete the directory**

Run: `rm -rf rust/codesearch-extractors`

**Step 3: Verify workspace builds**

Run: `cd rust && cargo build`
Expected: Success (extractors weren't used by core or ffi)

**Step 4: Commit**

```bash
git add -A
git commit -m "refactor: remove duplicate codesearch-extractors (122k lines)

Julie-extractors already provides full tree-sitter support for 31 languages
with comprehensive tests. No need to maintain a duplicate implementation."
```

---

### Task 2: Add Julie-Extractors Dependency

**Files:**
- Modify: `rust/codesearch-ffi/Cargo.toml`

**Step 1: Add julie-extractors dependency**

Add to `rust/codesearch-ffi/Cargo.toml`:

```toml
[dependencies]
codesearch-core = { path = "../codesearch-core" }
uniffi.workspace = true
tokio.workspace = true
thiserror.workspace = true

# Julie's extractors - proven tree-sitter support for 31 languages
julie-extractors = { git = "https://github.com/anortham/julie.git", tag = "v1.20.0" }

# For parallel batch extraction
rayon = "1.10"
```

**Step 2: Verify it builds**

Run: `cd rust && cargo build`
Expected: Downloads julie-extractors and builds successfully

**Step 3: Commit**

```bash
git add rust/codesearch-ffi/Cargo.toml
git commit -m "feat(ffi): add julie-extractors dependency"
```

---

### Task 3: Add FFI Types for Extraction Results

**Files:**
- Modify: `rust/codesearch-ffi/src/lib.rs`

**Step 1: Add extraction result types**

Add these UniFFI-compatible types after the existing types in `lib.rs`:

```rust
use julie_extractors::{
    ExtractorManager,
    Symbol as JulieSymbol,
    Identifier as JulieIdentifier,
    Relationship as JulieRelationship,
    ExtractionResults as JulieExtractionResults,
    detect_language_from_extension,
};
use std::path::Path;

/// FFI-safe extraction results
#[derive(Debug, Clone, uniffi::Record)]
pub struct ExtractionResults {
    pub symbols: Vec<ExtractedSymbol>,
    pub identifiers: Vec<ExtractedIdentifier>,
    pub relationships: Vec<ExtractedRelationship>,
}

/// FFI-safe symbol from extraction
#[derive(Debug, Clone, uniffi::Record)]
pub struct ExtractedSymbol {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub language: String,
    pub file_path: String,
    pub start_line: u32,
    pub end_line: u32,
    pub signature: Option<String>,
    pub doc_comment: Option<String>,
}

/// FFI-safe identifier (usage/reference)
#[derive(Debug, Clone, uniffi::Record)]
pub struct ExtractedIdentifier {
    pub name: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub column: u32,
    pub source_symbol_id: Option<String>,
    pub target_symbol_id: Option<String>,
}

/// FFI-safe relationship from extraction
#[derive(Debug, Clone, uniffi::Record)]
pub struct ExtractedRelationship {
    pub from_symbol_id: String,
    pub to_symbol_id: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub confidence: f32,
}

impl From<JulieSymbol> for ExtractedSymbol {
    fn from(s: JulieSymbol) -> Self {
        Self {
            id: s.id,
            name: s.name,
            kind: format!("{:?}", s.kind),
            language: s.language,
            file_path: s.file_path,
            start_line: s.start_line,
            end_line: s.end_line,
            signature: s.signature,
            doc_comment: s.doc_comment,
        }
    }
}

impl From<JulieIdentifier> for ExtractedIdentifier {
    fn from(i: JulieIdentifier) -> Self {
        Self {
            name: i.name,
            kind: format!("{:?}", i.kind),
            file_path: i.file_path,
            line_number: i.line_number,
            column: i.column,
            source_symbol_id: i.source_symbol_id,
            target_symbol_id: i.target_symbol_id,
        }
    }
}

impl From<JulieRelationship> for ExtractedRelationship {
    fn from(r: JulieRelationship) -> Self {
        Self {
            from_symbol_id: r.from_symbol_id,
            to_symbol_id: r.to_symbol_id,
            kind: format!("{:?}", r.kind),
            file_path: r.file_path,
            line_number: r.line_number,
            confidence: r.confidence,
        }
    }
}
```

**Step 2: Verify it compiles**

Run: `cd rust && cargo build`
Expected: Success

**Step 3: Commit**

```bash
git add rust/codesearch-ffi/src/lib.rs
git commit -m "feat(ffi): add extraction result types for UniFFI"
```

---

### Task 4: Add Extraction Functions to FFI

**Files:**
- Modify: `rust/codesearch-ffi/src/lib.rs`

**Step 1: Add extract_file function**

Add these functions to `lib.rs`:

```rust
/// Extract symbols, identifiers, and relationships from source code
#[uniffi::export]
pub fn extract_file(
    content: String,
    file_path: String,
    workspace_root: String,
) -> Result<ExtractionResults, CodeSearchError> {
    let manager = ExtractorManager::new();
    let workspace_path = Path::new(&workspace_root);

    // Extract symbols
    let symbols = manager
        .extract_symbols(&file_path, &content, workspace_path)
        .map_err(|e| CodeSearchError::Runtime(format!("Symbol extraction failed: {}", e)))?;

    // Extract identifiers
    let identifiers = manager
        .extract_identifiers(&file_path, &content, &symbols)
        .map_err(|e| CodeSearchError::Runtime(format!("Identifier extraction failed: {}", e)))?;

    // Extract relationships
    let relationships = manager
        .extract_relationships(&file_path, &content, &symbols)
        .map_err(|e| CodeSearchError::Runtime(format!("Relationship extraction failed: {}", e)))?;

    Ok(ExtractionResults {
        symbols: symbols.into_iter().map(ExtractedSymbol::from).collect(),
        identifiers: identifiers.into_iter().map(ExtractedIdentifier::from).collect(),
        relationships: relationships.into_iter().map(ExtractedRelationship::from).collect(),
    })
}

/// Detect programming language from file extension
#[uniffi::export]
pub fn detect_language(file_path: String) -> Option<String> {
    let path = Path::new(&file_path);
    let extension = path.extension().and_then(|ext| ext.to_str()).unwrap_or("");
    detect_language_from_extension(extension).map(|s| s.to_string())
}

/// Get list of supported programming languages
#[uniffi::export]
pub fn supported_languages() -> Vec<String> {
    let manager = ExtractorManager::new();
    manager.supported_languages().iter().map(|&s| s.to_string()).collect()
}
```

**Step 2: Verify it compiles**

Run: `cd rust && cargo build`
Expected: Success

**Step 3: Commit**

```bash
git add rust/codesearch-ffi/src/lib.rs
git commit -m "feat(ffi): add extract_file and language detection functions"
```

---

### Task 5: Add Batch Extraction with Parallelism

**Files:**
- Modify: `rust/codesearch-ffi/src/lib.rs`

**Step 1: Add batch extraction function**

Add this function for parallel extraction:

```rust
use rayon::prelude::*;

/// Extract from multiple files in parallel
///
/// Each tuple is (content, file_path). Returns results in same order.
#[uniffi::export]
pub fn extract_files_batch(
    files: Vec<(String, String)>,
    workspace_root: String,
) -> Vec<ExtractionResults> {
    let workspace_path = Path::new(&workspace_root);

    files
        .par_iter()
        .map(|(content, file_path)| {
            let manager = ExtractorManager::new();

            let symbols = manager
                .extract_symbols(file_path, content, workspace_path)
                .unwrap_or_default();

            let identifiers = manager
                .extract_identifiers(file_path, content, &symbols)
                .unwrap_or_default();

            let relationships = manager
                .extract_relationships(file_path, content, &symbols)
                .unwrap_or_default();

            ExtractionResults {
                symbols: symbols.into_iter().map(ExtractedSymbol::from).collect(),
                identifiers: identifiers.into_iter().map(ExtractedIdentifier::from).collect(),
                relationships: relationships.into_iter().map(ExtractedRelationship::from).collect(),
            }
        })
        .collect()
}
```

**Step 2: Verify it compiles**

Run: `cd rust && cargo build --release`
Expected: Success

**Step 3: Commit**

```bash
git add rust/codesearch-ffi/src/lib.rs
git commit -m "feat(ffi): add parallel batch extraction"
```

---

### Task 6: Regenerate C# Bindings

**Files:**
- Regenerate: `src/Codesearch.Interop/Generated/codesearch_ffi.cs`

**Step 1: Build release**

Run: `cd rust && cargo build --release`

**Step 2: Regenerate bindings**

Run: `./scripts/generate-bindings.sh`

**Step 3: Verify C# builds**

Run: `dotnet build src/Codesearch.Interop`
Expected: Success with new extraction types available

**Step 4: Commit**

```bash
git add src/Codesearch.Interop/Generated/codesearch_ffi.cs
git commit -m "chore: regenerate UniFFI C# bindings with extraction APIs"
```

---

### Task 7: Add Identifier Schema to LanceDB

**Files:**
- Modify: `rust/codesearch-core/src/schema.rs`
- Modify: `rust/codesearch-core/src/engine.rs`

**Step 1: Add identifier schema**

Add to `rust/codesearch-core/src/schema.rs`:

```rust
/// Schema for the identifiers table (references/usages)
pub fn identifiers_schema() -> Arc<Schema> {
    Arc::new(Schema::new(vec![
        Field::new("name", DataType::Utf8, false),
        Field::new("kind", DataType::Utf8, false),
        Field::new("file_path", DataType::Utf8, false),
        Field::new("line_number", DataType::UInt32, false),
        Field::new("column", DataType::UInt32, false),
        Field::new("source_symbol_id", DataType::Utf8, true),
        Field::new("target_symbol_id", DataType::Utf8, true),
    ]))
}
```

**Step 2: Create identifiers table in engine**

In `engine.rs`, in the `CodeEngine::new()` method, after relationships table creation:

```rust
// Create identifiers table if it doesn't exist
if !table_names.contains(&"identifiers".to_string()) {
    let empty_batch = RecordBatch::try_new(
        schema::identifiers_schema(),
        vec![
            Arc::new(StringArray::from(Vec::<&str>::new())),
            Arc::new(StringArray::from(Vec::<&str>::new())),
            Arc::new(StringArray::from(Vec::<&str>::new())),
            Arc::new(UInt32Array::from(Vec::<u32>::new())),
            Arc::new(UInt32Array::from(Vec::<u32>::new())),
            Arc::new(StringArray::from(Vec::<Option<&str>>::new())),
            Arc::new(StringArray::from(Vec::<Option<&str>>::new())),
        ],
    )?;
    db.create_table("identifiers", empty_batch).await?;
}
```

**Step 3: Add IdentifierInput and add_identifiers method**

Add to `engine.rs`:

```rust
/// Input for adding an identifier
pub struct IdentifierInput {
    pub name: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub column: u32,
    pub source_symbol_id: Option<String>,
    pub target_symbol_id: Option<String>,
}

impl CodeEngine {
    /// Add identifiers to the database
    pub async fn add_identifiers(&self, identifiers: Vec<IdentifierInput>) -> Result<usize> {
        if identifiers.is_empty() {
            return Ok(0);
        }

        let names: Vec<&str> = identifiers.iter().map(|i| i.name.as_str()).collect();
        let kinds: Vec<&str> = identifiers.iter().map(|i| i.kind.as_str()).collect();
        let paths: Vec<&str> = identifiers.iter().map(|i| i.file_path.as_str()).collect();
        let lines: Vec<u32> = identifiers.iter().map(|i| i.line_number).collect();
        let cols: Vec<u32> = identifiers.iter().map(|i| i.column).collect();
        let source_ids: Vec<Option<&str>> = identifiers.iter()
            .map(|i| i.source_symbol_id.as_deref())
            .collect();
        let target_ids: Vec<Option<&str>> = identifiers.iter()
            .map(|i| i.target_symbol_id.as_deref())
            .collect();

        let batch = RecordBatch::try_new(
            schema::identifiers_schema(),
            vec![
                Arc::new(StringArray::from(names)),
                Arc::new(StringArray::from(kinds)),
                Arc::new(StringArray::from(paths)),
                Arc::new(UInt32Array::from(lines)),
                Arc::new(UInt32Array::from(cols)),
                Arc::new(StringArray::from(source_ids)),
                Arc::new(StringArray::from(target_ids)),
            ],
        )?;

        let table = self.db.open_table("identifiers").await?;
        table.add(vec![batch]).await?;

        Ok(identifiers.len())
    }

    /// Get count of identifiers in database
    pub async fn identifier_count(&self) -> Result<usize> {
        let table = self.db.open_table("identifiers").await?;
        Ok(table.count_rows(None).await?)
    }
}
```

**Step 4: Verify it compiles**

Run: `cd rust && cargo build`
Expected: Success

**Step 5: Commit**

```bash
git add rust/codesearch-core/src/schema.rs rust/codesearch-core/src/engine.rs
git commit -m "feat(core): add identifiers table schema and storage"
```

---

### Task 8: Expose Identifiers in FFI

**Files:**
- Modify: `rust/codesearch-ffi/src/lib.rs`

**Step 1: Add FFI types and methods for identifiers**

Add to `lib.rs`:

```rust
/// FFI-safe identifier input for storage
#[derive(Debug, Clone, uniffi::Record)]
pub struct IdentifierInput {
    pub name: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub column: u32,
    pub source_symbol_id: Option<String>,
    pub target_symbol_id: Option<String>,
}

impl From<IdentifierInput> for codesearch_core::IdentifierInput {
    fn from(i: IdentifierInput) -> Self {
        Self {
            name: i.name,
            kind: i.kind,
            file_path: i.file_path,
            line_number: i.line_number,
            column: i.column,
            source_symbol_id: i.source_symbol_id,
            target_symbol_id: i.target_symbol_id,
        }
    }
}
```

Add to `CodeSearchEngine` impl:

```rust
/// Add identifiers to the database
pub fn add_identifiers(&self, identifiers: Vec<IdentifierInput>) -> Result<u64, CodeSearchError> {
    let inputs: Vec<codesearch_core::IdentifierInput> = identifiers
        .into_iter()
        .map(codesearch_core::IdentifierInput::from)
        .collect();

    self.runtime.block_on(async {
        self.inner
            .add_identifiers(inputs)
            .await
            .map(|n| n as u64)
            .map_err(CodeSearchError::from)
    })
}

/// Get identifier count
pub fn identifier_count(&self) -> Result<u64, CodeSearchError> {
    self.runtime.block_on(async {
        self.inner
            .identifier_count()
            .await
            .map(|n| n as u64)
            .map_err(CodeSearchError::from)
    })
}
```

**Step 2: Verify it compiles**

Run: `cd rust && cargo build`

**Step 3: Commit**

```bash
git add rust/codesearch-ffi/src/lib.rs
git commit -m "feat(ffi): expose identifier storage methods"
```

---

### Task 9: Update IndexService to Use Extractors

**Files:**
- Modify: `src/Codesearch.Server/Services/IndexService.cs`

**Step 1: Update IndexFileAsync to use extractors**

Replace the `IndexFileAsync` method with extraction-based implementation:

```csharp
private async Task IndexFileAsync(string absolutePath, string relativePath)
{
    var content = await File.ReadAllTextAsync(absolutePath);

    // Check if language is supported
    var language = uniffi.codesearch_ffi.CodesearchFfiMethods.DetectLanguage(relativePath);
    if (language == null)
    {
        // Unsupported file type - index as file-level symbol only
        await IndexAsFileSymbol(relativePath, content);
        return;
    }

    // Extract symbols, identifiers, and relationships
    ExtractionResults results;
    try
    {
        results = uniffi.codesearch_ffi.CodesearchFfiMethods.ExtractFile(
            content,
            relativePath,
            _workspaceRoot
        );
    }
    catch (Exception ex)
    {
        // Extraction failed - fall back to file-level indexing
        Console.Error.WriteLine($"Extraction failed for {relativePath}: {ex.Message}");
        await IndexAsFileSymbol(relativePath, content);
        return;
    }

    // Add symbols
    if (results.symbols.Count > 0)
    {
        var symbolInputs = results.symbols.Select(s => new SymbolInput(
            id: s.id,
            name: s.name,
            kind: s.kind.ToLowerInvariant(),
            language: s.language,
            filePath: s.filePath,
            signature: s.signature,
            docComment: s.docComment,
            startLine: (int)s.startLine,
            endLine: (int)s.endLine,
            content: null  // Could add code snippet here
        )).ToList();

        // Placeholder vectors for now
        var vectors = symbolInputs.Select(_ =>
            Enumerable.Repeat(0.0f, 768).ToList()
        ).ToList();

        _searchService.AddSymbols(symbolInputs, vectors);
    }

    // Add relationships
    if (results.relationships.Count > 0)
    {
        var relationshipInputs = results.relationships.Select(r =>
            new uniffi.codesearch_ffi.RelationshipInput(
                fromSymbolId: r.fromSymbolId,
                toSymbolId: r.toSymbolId,
                kind: r.kind,
                filePath: r.filePath,
                lineNumber: r.lineNumber,
                confidence: r.confidence
            )
        ).ToList();

        _searchService.AddRelationships(relationshipInputs);
    }

    // Add identifiers
    if (results.identifiers.Count > 0)
    {
        var identifierInputs = results.identifiers.Select(i =>
            new uniffi.codesearch_ffi.IdentifierInput(
                name: i.name,
                kind: i.kind,
                filePath: i.filePath,
                lineNumber: i.lineNumber,
                column: i.column,
                sourceSymbolId: i.sourceSymbolId,
                targetSymbolId: i.targetSymbolId
            )
        ).ToList();

        _searchService.AddIdentifiers(identifierInputs);
    }
}

private async Task IndexAsFileSymbol(string relativePath, string content)
{
    var extension = Path.GetExtension(relativePath).TrimStart('.');

    // Special handling for memory files
    string embedContent = content;
    string name = Path.GetFileName(relativePath);
    string kind = "file";

    if (IsMemoryFile(relativePath))
    {
        try
        {
            var (metadata, body) = FrontmatterParser.Parse(content);
            var tagPrefix = metadata.Tags.Count > 0 ? string.Join(" ", metadata.Tags) + " " : "";
            embedContent = tagPrefix + body;
            kind = metadata.Type.ToString().ToLowerInvariant();
        }
        catch { }
    }

    // Truncate for embedding
    if (embedContent.Length > 4096)
    {
        embedContent = embedContent[..4096];
    }

    var symbol = new SymbolInput(
        id: $"file_{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(relativePath)))[..16]}",
        name: name,
        kind: kind,
        language: extension,
        filePath: relativePath.Replace('\\', '/'),
        signature: null,
        docComment: null,
        startLine: 1,
        endLine: content.Split('\n').Length,
        content: embedContent
    );

    var vector = Enumerable.Repeat(0.0f, 768).ToList();
    _searchService.AddSymbols(new List<SymbolInput> { symbol }, new List<List<float>> { vector });
}
```

**Step 2: Add AddIdentifiers to SearchService**

Add to `SearchService.cs`:

```csharp
/// <summary>
/// Add identifiers to the database.
/// </summary>
public ulong AddIdentifiers(List<uniffi.codesearch_ffi.IdentifierInput> identifiers)
{
    return _engine.AddIdentifiers(identifiers);
}

/// <summary>
/// Get identifier count.
/// </summary>
public ulong IdentifierCount()
{
    return _engine.IdentifierCount();
}
```

**Step 3: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 4: Commit**

```bash
git add src/Codesearch.Server/Services/IndexService.cs src/Codesearch.Server/Services/SearchService.cs
git commit -m "feat(server): update IndexService to use julie extractors"
```

---

### Task 10: Add Reachability Schema and Storage

**Files:**
- Modify: `rust/codesearch-core/src/schema.rs`
- Modify: `rust/codesearch-core/src/engine.rs`

**Step 1: Add reachability schema**

Add to `schema.rs`:

```rust
/// Schema for the reachability table (transitive closure)
pub fn reachability_schema() -> Arc<Schema> {
    Arc::new(Schema::new(vec![
        Field::new("source_id", DataType::Utf8, false),
        Field::new("target_id", DataType::Utf8, false),
        Field::new("min_distance", DataType::UInt32, false),
    ]))
}
```

**Step 2: Create reachability table in engine**

Add to `CodeEngine::new()`:

```rust
// Create reachability table if it doesn't exist
if !table_names.contains(&"reachability".to_string()) {
    let empty_batch = RecordBatch::try_new(
        schema::reachability_schema(),
        vec![
            Arc::new(StringArray::from(Vec::<&str>::new())),
            Arc::new(StringArray::from(Vec::<&str>::new())),
            Arc::new(UInt32Array::from(Vec::<u32>::new())),
        ],
    )?;
    db.create_table("reachability", empty_batch).await?;
}
```

**Step 3: Add reachability methods**

Add to `engine.rs`:

```rust
/// Input for reachability entry
pub struct ReachabilityEntry {
    pub source_id: String,
    pub target_id: String,
    pub min_distance: u32,
}

impl CodeEngine {
    /// Clear all reachability data
    pub async fn clear_reachability(&self) -> Result<()> {
        let table = self.db.open_table("reachability").await?;
        table.delete("true").await?;  // Delete all rows
        Ok(())
    }

    /// Add reachability entries in batch
    pub async fn add_reachability_batch(&self, entries: Vec<ReachabilityEntry>) -> Result<usize> {
        if entries.is_empty() {
            return Ok(0);
        }

        let sources: Vec<&str> = entries.iter().map(|e| e.source_id.as_str()).collect();
        let targets: Vec<&str> = entries.iter().map(|e| e.target_id.as_str()).collect();
        let distances: Vec<u32> = entries.iter().map(|e| e.min_distance).collect();

        let batch = RecordBatch::try_new(
            schema::reachability_schema(),
            vec![
                Arc::new(StringArray::from(sources)),
                Arc::new(StringArray::from(targets)),
                Arc::new(UInt32Array::from(distances)),
            ],
        )?;

        let table = self.db.open_table("reachability").await?;
        table.add(vec![batch]).await?;

        Ok(entries.len())
    }

    /// Get all symbols reachable from source (impact analysis)
    pub async fn get_impacted(&self, source_id: &str, max_distance: u32) -> Result<Vec<(String, u32)>> {
        let table = self.db.open_table("reachability").await?;

        let results = table
            .query()
            .filter(format!(
                "source_id = '{}' AND min_distance <= {}",
                source_id.replace("'", "''"),
                max_distance
            ))
            .execute()
            .await?;

        let mut impacted = Vec::new();
        let batches: Vec<RecordBatch> = results.try_collect().await?;

        for batch in batches {
            let targets = batch.column_by_name("target_id")
                .unwrap().as_any().downcast_ref::<StringArray>().unwrap();
            let distances = batch.column_by_name("min_distance")
                .unwrap().as_any().downcast_ref::<UInt32Array>().unwrap();

            for i in 0..batch.num_rows() {
                impacted.push((targets.value(i).to_string(), distances.value(i)));
            }
        }

        Ok(impacted)
    }
}
```

**Step 4: Verify it compiles**

Run: `cd rust && cargo build`

**Step 5: Commit**

```bash
git add rust/codesearch-core/src/schema.rs rust/codesearch-core/src/engine.rs
git commit -m "feat(core): add reachability table for transitive closure"
```

---

### Task 11: Expose Reachability in FFI

**Files:**
- Modify: `rust/codesearch-ffi/src/lib.rs`

**Step 1: Add reachability types and methods to FFI**

Add to `lib.rs`:

```rust
/// FFI-safe reachability entry
#[derive(Debug, Clone, uniffi::Record)]
pub struct ReachabilityEntry {
    pub source_id: String,
    pub target_id: String,
    pub min_distance: u32,
}

/// FFI-safe impact result
#[derive(Debug, Clone, uniffi::Record)]
pub struct ImpactResult {
    pub symbol_id: String,
    pub distance: u32,
}
```

Add to `CodeSearchEngine` impl:

```rust
/// Clear reachability table
pub fn clear_reachability(&self) -> Result<(), CodeSearchError> {
    self.runtime.block_on(async {
        self.inner
            .clear_reachability()
            .await
            .map_err(CodeSearchError::from)
    })
}

/// Add reachability entries in batch
pub fn add_reachability_batch(&self, entries: Vec<ReachabilityEntry>) -> Result<u64, CodeSearchError> {
    let inputs: Vec<codesearch_core::ReachabilityEntry> = entries
        .into_iter()
        .map(|e| codesearch_core::ReachabilityEntry {
            source_id: e.source_id,
            target_id: e.target_id,
            min_distance: e.min_distance,
        })
        .collect();

    self.runtime.block_on(async {
        self.inner
            .add_reachability_batch(inputs)
            .await
            .map(|n| n as u64)
            .map_err(CodeSearchError::from)
    })
}

/// Get impacted symbols (what breaks if I change this?)
pub fn get_impacted(&self, symbol_id: String, max_distance: u32) -> Result<Vec<ImpactResult>, CodeSearchError> {
    self.runtime.block_on(async {
        self.inner
            .get_impacted(&symbol_id, max_distance)
            .await
            .map(|results| results.into_iter().map(|(id, dist)| ImpactResult {
                symbol_id: id,
                distance: dist,
            }).collect())
            .map_err(CodeSearchError::from)
    })
}
```

**Step 2: Verify it compiles**

Run: `cd rust && cargo build`

**Step 3: Commit**

```bash
git add rust/codesearch-ffi/src/lib.rs
git commit -m "feat(ffi): expose reachability methods for impact analysis"
```

---

### Task 12: Add Transitive Closure Service

**Files:**
- Create: `src/Codesearch.Server/Services/ClosureService.cs`

**Step 1: Create closure service**

Create `src/Codesearch.Server/Services/ClosureService.cs`:

```csharp
namespace Codesearch.Server.Services;

/// <summary>
/// Service for computing transitive closure (reachability) for impact analysis.
/// </summary>
internal class ClosureService
{
    private readonly SearchService _searchService;

    public ClosureService(SearchService searchService)
    {
        _searchService = searchService;
    }

    /// <summary>
    /// Compute transitive closure from all relationships.
    /// Enables O(1) impact analysis queries.
    /// </summary>
    public int ComputeTransitiveClosure(int maxDepth = 10)
    {
        // Clear existing reachability
        _searchService.ClearReachability();

        // Get all call relationships
        var relationships = _searchService.GetAllRelationships("Calls");

        if (relationships.Count == 0)
        {
            return 0;
        }

        // Build adjacency list
        var downstream = new Dictionary<string, HashSet<string>>();
        var allSymbols = new HashSet<string>();

        foreach (var rel in relationships)
        {
            if (!downstream.ContainsKey(rel.fromSymbolId))
            {
                downstream[rel.fromSymbolId] = new HashSet<string>();
            }
            downstream[rel.fromSymbolId].Add(rel.toSymbolId);
            allSymbols.Add(rel.fromSymbolId);
            allSymbols.Add(rel.toSymbolId);
        }

        // BFS from each symbol to compute reachability
        var entries = new List<uniffi.codesearch_ffi.ReachabilityEntry>();

        foreach (var startSymbol in allSymbols)
        {
            if (!downstream.ContainsKey(startSymbol))
            {
                continue;  // No outgoing edges
            }

            var visited = new Dictionary<string, int> { { startSymbol, 0 } };
            var queue = new Queue<(string symbol, int depth)>();
            queue.Enqueue((startSymbol, 0));

            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();

                if (depth >= maxDepth)
                {
                    continue;
                }

                if (!downstream.TryGetValue(current, out var neighbors))
                {
                    continue;
                }

                foreach (var neighbor in neighbors)
                {
                    if (!visited.ContainsKey(neighbor))
                    {
                        var newDepth = depth + 1;
                        visited[neighbor] = newDepth;
                        queue.Enqueue((neighbor, newDepth));
                        entries.Add(new uniffi.codesearch_ffi.ReachabilityEntry(
                            sourceId: startSymbol,
                            targetId: neighbor,
                            minDistance: (uint)newDepth
                        ));
                    }
                }
            }
        }

        // Bulk insert
        if (entries.Count > 0)
        {
            _searchService.AddReachabilityBatch(entries);
        }

        return entries.Count;
    }
}
```

**Step 2: Add supporting methods to SearchService**

Add to `SearchService.cs`:

```csharp
/// <summary>
/// Clear reachability table.
/// </summary>
public void ClearReachability()
{
    _engine.ClearReachability();
}

/// <summary>
/// Add reachability entries in batch.
/// </summary>
public ulong AddReachabilityBatch(List<uniffi.codesearch_ffi.ReachabilityEntry> entries)
{
    return _engine.AddReachabilityBatch(entries);
}

/// <summary>
/// Get all relationships of a specific kind.
/// </summary>
public List<uniffi.codesearch_ffi.RelationshipResult> GetAllRelationships(string kind)
{
    // This needs to be added to the engine - query all relationships filtered by kind
    return _engine.GetRelationshipsByKind(kind, 100000);
}

/// <summary>
/// Get impacted symbols (what breaks if I change this?).
/// </summary>
public List<uniffi.codesearch_ffi.ImpactResult> GetImpacted(string symbolId, uint maxDistance = 10)
{
    return _engine.GetImpacted(symbolId, maxDistance);
}
```

**Step 3: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`

**Step 4: Commit**

```bash
git add src/Codesearch.Server/Services/ClosureService.cs src/Codesearch.Server/Services/SearchService.cs
git commit -m "feat(server): add ClosureService for transitive closure computation"
```

---

### Task 13: Create Impact Analysis Tool

**Files:**
- Create: `src/Codesearch.Server/Tools/ImpactTool.cs`

**Step 1: Create impact tool**

Create `src/Codesearch.Server/Tools/ImpactTool.cs`:

```csharp
using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class ImpactTool
{
    [McpServerTool]
    [Description("Analyze impact of changing a symbol. Operations: impact, refresh-closure.")]
    internal static string Impact(
        SearchService searchService,
        ClosureService closureService,
        [Description("Operation: impact or refresh-closure")] string operation,
        [Description("Symbol ID or name (for impact operation)")] string? symbol = null,
        [Description("Maximum distance to search")] int maxDistance = 10)
    {
        return operation.ToLowerInvariant() switch
        {
            "impact" => GetImpact(searchService, symbol ?? "", maxDistance),
            "refresh-closure" => RefreshClosure(closureService),
            "status" => GetStatus(searchService),
            _ => $"Unknown operation: {operation}. Use: impact, refresh-closure, or status."
        };
    }

    private static string GetImpact(SearchService searchService, string symbol, int maxDistance)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return "Error: symbol parameter required for impact operation.";
        }

        // Find symbol ID if given a name
        var symbolId = symbol;
        if (!symbol.Contains("::") && !symbol.Contains("/"))
        {
            var searchResults = searchService.SearchText(symbol, 1);
            if (searchResults.Count == 0)
            {
                return $"No symbol found matching '{symbol}'.";
            }
            symbolId = searchResults[0].id;
        }

        var impacted = searchService.GetImpacted(symbolId, (uint)maxDistance);

        if (impacted.Count == 0)
        {
            return $"No symbols would be impacted by changes to '{symbol}'.\n\n" +
                   "_Note: Run `impact(operation=\"refresh-closure\")` after indexing to compute reachability._";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Impact Analysis for `{symbol}`");
        sb.AppendLine();
        sb.AppendLine($"**{impacted.Count} symbols** would be affected by changes:");
        sb.AppendLine();

        // Group by distance
        var byDistance = impacted.GroupBy(i => i.distance).OrderBy(g => g.Key);

        foreach (var group in byDistance)
        {
            sb.AppendLine($"### Distance {group.Key} ({group.Count()} symbols)");
            foreach (var item in group.Take(20))
            {
                sb.AppendLine($"- `{item.symbolId}`");
            }
            if (group.Count() > 20)
            {
                sb.AppendLine($"_...and {group.Count() - 20} more_");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string RefreshClosure(ClosureService closureService)
    {
        var count = closureService.ComputeTransitiveClosure();
        return $"Transitive closure computed. {count} reachability entries created.";
    }

    private static string GetStatus(SearchService searchService)
    {
        var symbolCount = searchService.SymbolCount();
        var relationshipCount = searchService.RelationshipCount();
        var identifierCount = searchService.IdentifierCount();

        return $"""
            ## Index Status

            - **Symbols**: {symbolCount}
            - **Relationships**: {relationshipCount}
            - **Identifiers**: {identifierCount}
            """;
    }
}
```

**Step 2: Register ClosureService in DI**

Update `Program.cs` to register ClosureService:

```csharp
// Add after SearchService registration
builder.Services.AddSingleton<ClosureService>();
```

**Step 3: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`

**Step 4: Commit**

```bash
git add src/Codesearch.Server/Tools/ImpactTool.cs src/Codesearch.Server/Program.cs
git commit -m "feat(server): add ImpactTool for impact analysis"
```

---

### Task 14: Regenerate Bindings and Run Tests

**Step 1: Rebuild everything**

```bash
cd rust && cargo build --release
./scripts/generate-bindings.sh
dotnet build
```

**Step 2: Run tests**

Run: `dotnet test`
Expected: All existing tests pass (may need updates for new types)

**Step 3: Commit any test fixes**

```bash
git add -A
git commit -m "chore: fix tests for new extraction APIs"
```

---

### Task 15: Add Extraction Tests

**Files:**
- Create: `tests/Codesearch.Tests/ExtractionTests.cs`

**Step 1: Create extraction tests**

Create `tests/Codesearch.Tests/ExtractionTests.cs`:

```csharp
using Xunit;

namespace Codesearch.Tests;

public class ExtractionTests
{
    [Fact]
    public void DetectLanguage_ReturnsLanguageForKnownExtension()
    {
        var lang = uniffi.codesearch_ffi.CodesearchFfiMethods.DetectLanguage("test.rs");
        Assert.Equal("rust", lang);
    }

    [Fact]
    public void DetectLanguage_ReturnsNullForUnknownExtension()
    {
        var lang = uniffi.codesearch_ffi.CodesearchFfiMethods.DetectLanguage("test.xyz");
        Assert.Null(lang);
    }

    [Fact]
    public void SupportedLanguages_ReturnsNonEmptyList()
    {
        var langs = uniffi.codesearch_ffi.CodesearchFfiMethods.SupportedLanguages();
        Assert.NotEmpty(langs);
        Assert.Contains("rust", langs);
        Assert.Contains("python", langs);
    }

    [Fact]
    public void ExtractFile_ExtractsRustSymbols()
    {
        var code = """
            fn hello() {
                println!("Hello");
            }

            fn main() {
                hello();
            }
            """;

        var results = uniffi.codesearch_ffi.CodesearchFfiMethods.ExtractFile(
            code, "test.rs", "."
        );

        Assert.NotEmpty(results.symbols);
        Assert.Contains(results.symbols, s => s.name == "hello");
        Assert.Contains(results.symbols, s => s.name == "main");
    }

    [Fact]
    public void ExtractFile_ExtractsRelationships()
    {
        var code = """
            fn helper() {}

            fn caller() {
                helper();
            }
            """;

        var results = uniffi.codesearch_ffi.CodesearchFfiMethods.ExtractFile(
            code, "test.rs", "."
        );

        Assert.NotEmpty(results.relationships);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~ExtractionTests"`
Expected: All tests pass

**Step 3: Commit**

```bash
git add tests/Codesearch.Tests/ExtractionTests.cs
git commit -m "test: add extraction tests for julie integration"
```

---

### Task 16: Update Plugin Documentation

**Files:**
- Modify: `.claude-plugin/README.md`
- Modify: `.claude/settings.local.json.example`

**Step 1: Add impact tool documentation**

Add to MCP Tools section in `.claude-plugin/README.md`:

```markdown
### impact

Analyze impact of code changes using precomputed reachability.

```
impact(operation="impact", symbol="functionName", maxDistance=10)
impact(operation="refresh-closure")
impact(operation="status")
```

Operations:
- **impact**: Find all symbols affected by changes to a symbol
- **refresh-closure**: Recompute transitive closure after indexing
- **status**: Show index statistics (symbols, relationships, identifiers)
```

**Step 2: Update settings template**

Add `mcp__codesearch__impact` to the allow list.

**Step 3: Commit**

```bash
git add .claude-plugin/README.md .claude/settings.local.json.example
git commit -m "docs(plugin): add impact tool documentation"
```

---

### Task 17: Final Verification

**Step 1: Full rebuild**

```bash
cd rust && cargo build --release
./scripts/generate-bindings.sh
dotnet build
```

**Step 2: Run all tests**

Run: `dotnet test`
Expected: All tests pass

**Step 3: Test extraction manually**

```bash
# Start MCP server and test:
# 1. index(operation="full")
# 2. impact(operation="status") - should show symbols/relationships/identifiers
# 3. impact(operation="refresh-closure")
# 4. impact(operation="impact", symbol="some_function")
```

**Step 4: Final commit**

```bash
git add -A
git commit -m "feat: complete Phase 8 - julie integration and impact analysis"
```

---

## Phase 8 Complete

At this point you have:
- Removed 122k lines of duplicate extractors
- Julie-extractors integrated via UniFFI
- Real symbol/relationship/identifier extraction during indexing
- Transitive closure computation for O(1) impact queries
- Impact analysis tool

**Extraction now produces:**
- Symbols with proper kinds, signatures, doc comments
- Relationships (within-file calls)
- Identifiers (all references/usages)

**Impact analysis workflow:**
1. `index(operation="full")` - extract and store everything
2. `impact(operation="refresh-closure")` - compute reachability
3. `impact(operation="impact", symbol="X")` - what breaks if X changes?

**Next Phase (9):** Find-references tool using identifiers, cross-file relationship resolution.
