//! UniFFI bindings for codesearch-core

use codesearch_core::{SearchResult, Symbol, SymbolKind, RelationshipInput as CoreRelationshipInput, RelationshipResult as CoreRelationshipResult};
use julie_extractors::{
    ExtractorManager,
    Symbol as JulieSymbol,
    Identifier as JulieIdentifier,
    Relationship as JulieRelationship,
    detect_language_from_extension,
};
use rayon::prelude::*;
use std::path::Path;
use std::sync::Arc;
use tokio::runtime::Runtime;

uniffi::setup_scaffolding!();

/// FFI-safe symbol input (uses strings for all fields to be FFI-friendly)
#[derive(Debug, Clone, uniffi::Record)]
pub struct SymbolInput {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub language: String,
    pub file_path: String,
    pub signature: Option<String>,
    pub doc_comment: Option<String>,
    pub start_line: Option<i32>,
    pub end_line: Option<i32>,
    pub content: Option<String>,
}

impl From<SymbolInput> for Symbol {
    fn from(input: SymbolInput) -> Self {
        let kind = match input.kind.as_str() {
            "function" => SymbolKind::Function,
            "method" => SymbolKind::Method,
            "class" => SymbolKind::Class,
            "interface" => SymbolKind::Interface,
            "struct" => SymbolKind::Struct,
            "enum" => SymbolKind::Enum,
            "enum_member" => SymbolKind::EnumMember,
            "trait" => SymbolKind::Trait,
            "type" => SymbolKind::Type,
            "module" => SymbolKind::Module,
            "namespace" => SymbolKind::Namespace,
            "variable" => SymbolKind::Variable,
            "constant" => SymbolKind::Constant,
            "property" => SymbolKind::Property,
            "field" => SymbolKind::Field,
            "constructor" => SymbolKind::Constructor,
            "import" => SymbolKind::Import,
            "export" => SymbolKind::Export,
            "file" => SymbolKind::File,
            "checkpoint" => SymbolKind::Checkpoint,
            "plan" => SymbolKind::Plan,
            "decision" => SymbolKind::Decision,
            "learning" => SymbolKind::Learning,
            _ => SymbolKind::Function, // Default fallback
        };

        let mut symbol = Symbol {
            id: input.id,
            name: input.name.clone(),
            kind: kind.clone(),
            language: input.language,
            file_path: input.file_path,
            signature: input.signature.clone(),
            doc_comment: input.doc_comment,
            start_line: input.start_line,
            end_line: input.end_line,
            code_pattern: String::new(),
            content: input.content,
        };
        symbol.code_pattern = symbol.generate_code_pattern();
        symbol
    }
}

/// FFI-safe search result output
#[derive(Debug, Clone, uniffi::Record)]
pub struct SearchResultOutput {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub language: String,
    pub file_path: String,
    pub signature: Option<String>,
    pub doc_comment: Option<String>,
    pub start_line: Option<i32>,
    pub end_line: Option<i32>,
    pub score: f32,
}

impl From<SearchResult> for SearchResultOutput {
    fn from(r: SearchResult) -> Self {
        Self {
            id: r.id,
            name: r.name,
            kind: r.kind,
            language: r.language,
            file_path: r.file_path,
            signature: r.signature,
            doc_comment: r.doc_comment,
            start_line: r.start_line,
            end_line: r.end_line,
            score: r.score,
        }
    }
}

/// FFI-safe relationship input
#[derive(Debug, Clone, uniffi::Record)]
pub struct RelationshipInput {
    pub from_symbol_id: String,
    pub to_symbol_id: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub confidence: f32,
}

impl From<RelationshipInput> for CoreRelationshipInput {
    fn from(r: RelationshipInput) -> Self {
        Self {
            from_symbol_id: r.from_symbol_id,
            to_symbol_id: r.to_symbol_id,
            kind: r.kind,
            file_path: r.file_path,
            line_number: r.line_number,
            confidence: r.confidence,
        }
    }
}

/// FFI-safe relationship result
#[derive(Debug, Clone, uniffi::Record)]
pub struct RelationshipResult {
    pub from_symbol_id: String,
    pub to_symbol_id: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub confidence: f32,
}

impl From<CoreRelationshipResult> for RelationshipResult {
    fn from(r: CoreRelationshipResult) -> Self {
        Self {
            from_symbol_id: r.from_symbol_id,
            to_symbol_id: r.to_symbol_id,
            kind: r.kind,
            file_path: r.file_path,
            line_number: r.line_number,
            confidence: r.confidence,
        }
    }
}

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

// =============================================================================
// FFI Types for julie-extractors Integration
// =============================================================================

/// FFI-safe extraction results from julie-extractors
#[derive(Debug, Clone, uniffi::Record)]
pub struct ExtractionResults {
    pub symbols: Vec<ExtractedSymbol>,
    pub identifiers: Vec<ExtractedIdentifier>,
    pub relationships: Vec<ExtractedRelationship>,
}

/// FFI-safe file input for batch extraction
#[derive(Debug, Clone, uniffi::Record)]
pub struct FileInput {
    pub content: String,
    pub file_path: String,
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

// =============================================================================
// Conversion implementations from julie-extractors types to FFI types
// =============================================================================

impl From<JulieSymbol> for ExtractedSymbol {
    fn from(s: JulieSymbol) -> Self {
        Self {
            id: s.id,
            name: s.name,
            kind: s.kind.to_string(),
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
            kind: i.kind.to_string(),
            file_path: i.file_path,
            line_number: i.start_line,
            column: i.start_column,
            source_symbol_id: i.containing_symbol_id,
            target_symbol_id: i.target_symbol_id,
        }
    }
}

impl From<JulieRelationship> for ExtractedRelationship {
    fn from(r: JulieRelationship) -> Self {
        Self {
            from_symbol_id: r.from_symbol_id,
            to_symbol_id: r.to_symbol_id,
            kind: r.kind.to_string(),
            file_path: r.file_path,
            line_number: r.line_number,
            confidence: r.confidence,
        }
    }
}

// =============================================================================
// Extraction Functions (freestanding UniFFI exports)
// =============================================================================

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

/// Extract from multiple files in parallel
///
/// Returns results in same order as input files.
#[uniffi::export]
pub fn extract_files_batch(
    files: Vec<FileInput>,
    workspace_root: String,
) -> Vec<ExtractionResults> {
    let workspace_path_str = workspace_root;

    files
        .par_iter()
        .map(|file| {
            let workspace_path = Path::new(&workspace_path_str);
            let manager = ExtractorManager::new();

            let symbols = manager
                .extract_symbols(&file.file_path, &file.content, workspace_path)
                .unwrap_or_default();

            let identifiers = manager
                .extract_identifiers(&file.file_path, &file.content, &symbols)
                .unwrap_or_default();

            let relationships = manager
                .extract_relationships(&file.file_path, &file.content, &symbols)
                .unwrap_or_default();

            ExtractionResults {
                symbols: symbols.into_iter().map(ExtractedSymbol::from).collect(),
                identifiers: identifiers.into_iter().map(ExtractedIdentifier::from).collect(),
                relationships: relationships.into_iter().map(ExtractedRelationship::from).collect(),
            }
        })
        .collect()
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

/// FFI-safe wrapper around CodeEngine
#[derive(uniffi::Object)]
pub struct CodeSearchEngine {
    inner: codesearch_core::CodeEngine,
    runtime: Arc<Runtime>,
}

#[uniffi::export]
impl CodeSearchEngine {
    /// Create a new CodeSearchEngine
    #[uniffi::constructor]
    pub fn new(db_path: String) -> Result<Arc<Self>, CodeSearchError> {
        let runtime = Arc::new(
            Runtime::new().map_err(|e| CodeSearchError::Runtime(e.to_string()))?
        );

        let inner = runtime.block_on(async {
            codesearch_core::CodeEngine::new(&db_path).await
        })?;

        Ok(Arc::new(Self { inner, runtime }))
    }

    /// Get the database path
    pub fn db_path(&self) -> String {
        self.inner.db_path().to_string()
    }

    /// Check if the engine is healthy
    pub fn health_check(&self) -> Result<bool, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner.health_check().await.map_err(CodeSearchError::from)
        })
    }

    /// Add symbols with their embedding vectors to the database
    pub fn add_symbols(
        &self,
        symbols: Vec<SymbolInput>,
        vectors: Vec<Vec<f32>>,
    ) -> Result<u64, CodeSearchError> {
        let symbols: Vec<Symbol> = symbols.into_iter().map(Symbol::from).collect();
        self.runtime.block_on(async {
            self.inner
                .add_symbols(symbols, vectors)
                .await
                .map(|count| count as u64)
                .map_err(CodeSearchError::from)
        })
    }

    /// Get the count of symbols in the database
    pub fn symbol_count(&self) -> Result<u64, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .symbol_count()
                .await
                .map(|count| count as u64)
                .map_err(CodeSearchError::from)
        })
    }

    /// Create a full-text search index on the code_pattern field
    pub fn create_fts_index(&self) -> Result<(), CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .create_fts_index()
                .await
                .map_err(CodeSearchError::from)
        })
    }

    /// Search for symbols by vector similarity
    pub fn search_vector(
        &self,
        query_vector: Vec<f32>,
        limit: u32,
    ) -> Result<Vec<SearchResultOutput>, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .search_vector(&query_vector, limit as usize)
                .await
                .map(|results| results.into_iter().map(SearchResultOutput::from).collect())
                .map_err(CodeSearchError::from)
        })
    }

    /// Search for symbols using full-text search
    pub fn search_text(
        &self,
        query: String,
        limit: u32,
    ) -> Result<Vec<SearchResultOutput>, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .search_text(&query, limit as usize)
                .await
                .map(|results| results.into_iter().map(SearchResultOutput::from).collect())
                .map_err(CodeSearchError::from)
        })
    }

    /// Search for symbols using hybrid search (FTS + vector combined with RRF)
    pub fn search_hybrid(
        &self,
        query: String,
        query_vector: Vec<f32>,
        limit: u32,
    ) -> Result<Vec<SearchResultOutput>, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .search_hybrid(&query, &query_vector, limit as usize)
                .await
                .map(|results| results.into_iter().map(SearchResultOutput::from).collect())
                .map_err(CodeSearchError::from)
        })
    }

    /// Search for symbols using full-text search with score boosting
    pub fn search_text_boosted(
        &self,
        query: String,
        limit: u32,
    ) -> Result<Vec<SearchResultOutput>, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .search_text_boosted(&query, limit as usize)
                .await
                .map(|results| results.into_iter().map(SearchResultOutput::from).collect())
                .map_err(CodeSearchError::from)
        })
    }

    /// Search for symbols using hybrid search with score boosting
    pub fn search_hybrid_boosted(
        &self,
        query: String,
        query_vector: Vec<f32>,
        limit: u32,
    ) -> Result<Vec<SearchResultOutput>, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .search_hybrid_boosted(&query, &query_vector, limit as usize)
                .await
                .map(|results| results.into_iter().map(SearchResultOutput::from).collect())
                .map_err(CodeSearchError::from)
        })
    }

    /// Add relationships to the database
    pub fn add_relationships(
        &self,
        relationships: Vec<RelationshipInput>,
    ) -> Result<u64, CodeSearchError> {
        let relationships: Vec<CoreRelationshipInput> = relationships.into_iter().map(CoreRelationshipInput::from).collect();
        self.runtime.block_on(async {
            self.inner
                .add_relationships(relationships)
                .await
                .map(|count| count as u64)
                .map_err(CodeSearchError::from)
        })
    }

    /// Get the count of relationships in the database
    pub fn relationship_count(&self) -> Result<u64, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .relationship_count()
                .await
                .map(|count| count as u64)
                .map_err(CodeSearchError::from)
        })
    }

    /// Get symbols that call the given symbol
    pub fn get_callers(
        &self,
        symbol_id: String,
        limit: u32,
    ) -> Result<Vec<RelationshipResult>, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .get_callers(&symbol_id, limit as usize)
                .await
                .map(|results| results.into_iter().map(RelationshipResult::from).collect())
                .map_err(CodeSearchError::from)
        })
    }

    /// Get symbols that the given symbol calls
    pub fn get_callees(
        &self,
        symbol_id: String,
        limit: u32,
    ) -> Result<Vec<RelationshipResult>, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .get_callees(&symbol_id, limit as usize)
                .await
                .map(|results| results.into_iter().map(RelationshipResult::from).collect())
                .map_err(CodeSearchError::from)
        })
    }

    /// Get all relationships for a symbol
    pub fn get_relationships(
        &self,
        symbol_id: String,
        limit: u32,
    ) -> Result<Vec<RelationshipResult>, CodeSearchError> {
        self.runtime.block_on(async {
            self.inner
                .get_relationships(&symbol_id, limit as usize)
                .await
                .map(|results| results.into_iter().map(RelationshipResult::from).collect())
                .map_err(CodeSearchError::from)
        })
    }

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
}

/// FFI-safe error type
#[derive(Debug, thiserror::Error, uniffi::Error)]
pub enum CodeSearchError {
    #[error("Database error: {0}")]
    Database(String),

    #[error("Runtime error: {0}")]
    Runtime(String),
}

impl From<codesearch_core::Error> for CodeSearchError {
    fn from(e: codesearch_core::Error) -> Self {
        CodeSearchError::Database(e.to_string())
    }
}
