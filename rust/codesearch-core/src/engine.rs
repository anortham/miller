//! Core search engine wrapping LanceDB

use crate::boosting::apply_boosts;
use crate::schema::{symbols_schema, relationships_schema, identifiers_schema, reachability_schema, Symbol, TABLE_NAME, RELATIONSHIPS_TABLE_NAME, IDENTIFIERS_TABLE_NAME, REACHABILITY_TABLE_NAME, VECTOR_DIMENSION};
use crate::search::{distance_to_score, SearchResult};
use crate::{Error, Result};
use arrow::array::{Array, ArrayRef, FixedSizeListArray, Float32Array, Int32Array, StringBuilder};
use arrow_array::{RecordBatch, RecordBatchIterator, StringArray, UInt32Array};
use futures::TryStreamExt;
use lancedb::index::scalar::{FtsIndexBuilder, FullTextSearchQuery, TokenizerConfig};
use lancedb::index::Index;
use lancedb::query::{ExecutableQuery, QueryBase};
use std::collections::HashMap;
use std::path::Path;
use std::sync::Arc;

/// Input for adding a relationship to the database
#[derive(Debug, Clone)]
pub struct RelationshipInput {
    pub from_symbol_id: String,
    pub to_symbol_id: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub confidence: f32,
}

/// Result of a relationship query
#[derive(Debug, Clone)]
pub struct RelationshipResult {
    pub from_symbol_id: String,
    pub to_symbol_id: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub confidence: f32,
}

/// Input for adding an identifier to the database
#[derive(Debug, Clone)]
pub struct IdentifierInput {
    pub name: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub column: u32,
    pub source_symbol_id: Option<String>,
    pub target_symbol_id: Option<String>,
}

/// Result of a reference query
#[derive(Debug, Clone)]
pub struct ReferenceResult {
    pub name: String,
    pub kind: String,
    pub file_path: String,
    pub line_number: u32,
    pub column: u32,
    pub source_symbol_id: Option<String>,
}

/// Input for reachability entry (transitive closure)
#[derive(Debug, Clone)]
pub struct ReachabilityEntry {
    pub source_id: String,
    pub target_id: String,
    pub min_distance: u32,
}

/// Detailed symbol information
#[derive(Debug, Clone)]
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

/// The main search engine struct
pub struct CodeEngine {
    db: Arc<lancedb::Connection>,
    db_path: String,
}

impl CodeEngine {
    /// Create a new CodeEngine with the given database path
    pub async fn new(db_path: &str) -> Result<Self> {
        // Ensure parent directory exists
        if db_path != ":memory:" {
            if let Some(parent) = Path::new(db_path).parent() {
                std::fs::create_dir_all(parent)?;
            }
        }

        let db = lancedb::connect(db_path).execute().await?;

        Ok(Self {
            db: Arc::new(db),
            db_path: db_path.to_string(),
        })
    }

    /// Get the database path
    pub fn db_path(&self) -> &str {
        &self.db_path
    }

    /// Check if the engine is healthy
    pub async fn health_check(&self) -> Result<bool> {
        // List tables to verify connection
        let _tables = self.db.table_names().execute().await?;
        Ok(true)
    }

    /// Add symbols with their embedding vectors to the database
    ///
    /// # Arguments
    /// * `symbols` - Vector of Symbol objects to store
    /// * `vectors` - Vector of embedding vectors (each must be VECTOR_DIMENSION elements)
    ///
    /// # Returns
    /// The number of symbols added
    pub async fn add_symbols(
        &self,
        symbols: Vec<Symbol>,
        vectors: Vec<Vec<f32>>,
    ) -> Result<usize> {
        // Validate inputs
        if symbols.len() != vectors.len() {
            return Err(Error::Validation(format!(
                "symbols count ({}) must match vectors count ({})",
                symbols.len(),
                vectors.len()
            )));
        }

        for (i, v) in vectors.iter().enumerate() {
            if v.len() != VECTOR_DIMENSION {
                return Err(Error::Validation(format!(
                    "vector {} has {} dimensions, expected {}",
                    i,
                    v.len(),
                    VECTOR_DIMENSION
                )));
            }
        }

        let count = symbols.len();
        if count == 0 {
            return Ok(0);
        }

        // Convert to RecordBatch
        let batch = self.symbols_to_record_batch(symbols, vectors)?;
        let schema = symbols_schema();
        let batch_reader = RecordBatchIterator::new(vec![Ok(batch)], schema);

        // Check if table exists
        let table_names = self.db.table_names().execute().await?;

        if table_names.contains(&TABLE_NAME.to_string()) {
            // Append to existing table
            let table = self.db.open_table(TABLE_NAME).execute().await?;
            table.add(Box::new(batch_reader)).execute().await?;
        } else {
            // Create new table
            self.db
                .create_table(TABLE_NAME, Box::new(batch_reader))
                .execute()
                .await?;
        }

        Ok(count)
    }

    /// Get the count of symbols in the database
    pub async fn symbol_count(&self) -> Result<usize> {
        let table_names = self.db.table_names().execute().await?;

        if !table_names.contains(&TABLE_NAME.to_string()) {
            return Ok(0);
        }

        let table = self.db.open_table(TABLE_NAME).execute().await?;
        let count = table.count_rows(None).await?;
        Ok(count)
    }

    /// Add relationships to the database
    pub async fn add_relationships(&self, relationships: Vec<RelationshipInput>) -> Result<usize> {
        if relationships.is_empty() {
            return Ok(0);
        }

        let count = relationships.len();

        // Build arrays
        let from_ids: Vec<&str> = relationships.iter().map(|r| r.from_symbol_id.as_str()).collect();
        let to_ids: Vec<&str> = relationships.iter().map(|r| r.to_symbol_id.as_str()).collect();
        let kinds: Vec<&str> = relationships.iter().map(|r| r.kind.as_str()).collect();
        let paths: Vec<&str> = relationships.iter().map(|r| r.file_path.as_str()).collect();
        let lines: Vec<u32> = relationships.iter().map(|r| r.line_number).collect();
        let confidences: Vec<f32> = relationships.iter().map(|r| r.confidence).collect();

        let batch = RecordBatch::try_new(
            relationships_schema(),
            vec![
                Arc::new(StringArray::from(from_ids)) as ArrayRef,
                Arc::new(StringArray::from(to_ids)) as ArrayRef,
                Arc::new(StringArray::from(kinds)) as ArrayRef,
                Arc::new(StringArray::from(paths)) as ArrayRef,
                Arc::new(arrow::array::UInt32Array::from(lines)) as ArrayRef,
                Arc::new(Float32Array::from(confidences)) as ArrayRef,
            ],
        )?;

        let schema = relationships_schema();
        let batch_reader = RecordBatchIterator::new(vec![Ok(batch)], schema);

        // Check if table exists
        let table_names = self.db.table_names().execute().await?;

        if table_names.contains(&RELATIONSHIPS_TABLE_NAME.to_string()) {
            let table = self.db.open_table(RELATIONSHIPS_TABLE_NAME).execute().await?;
            table.add(Box::new(batch_reader)).execute().await?;
        } else {
            self.db
                .create_table(RELATIONSHIPS_TABLE_NAME, Box::new(batch_reader))
                .execute()
                .await?;
        }

        Ok(count)
    }

    /// Get the count of relationships in the database
    pub async fn relationship_count(&self) -> Result<usize> {
        let table_names = self.db.table_names().execute().await?;

        if !table_names.contains(&RELATIONSHIPS_TABLE_NAME.to_string()) {
            return Ok(0);
        }

        let table = self.db.open_table(RELATIONSHIPS_TABLE_NAME).execute().await?;
        let count = table.count_rows(None).await?;
        Ok(count)
    }

    /// Add identifiers to the database
    pub async fn add_identifiers(&self, identifiers: Vec<IdentifierInput>) -> Result<usize> {
        if identifiers.is_empty() {
            return Ok(0);
        }

        let count = identifiers.len();

        // Build arrays
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
            identifiers_schema(),
            vec![
                Arc::new(StringArray::from(names)) as ArrayRef,
                Arc::new(StringArray::from(kinds)) as ArrayRef,
                Arc::new(StringArray::from(paths)) as ArrayRef,
                Arc::new(arrow::array::UInt32Array::from(lines)) as ArrayRef,
                Arc::new(arrow::array::UInt32Array::from(cols)) as ArrayRef,
                Arc::new(StringArray::from(source_ids)) as ArrayRef,
                Arc::new(StringArray::from(target_ids)) as ArrayRef,
            ],
        )?;

        let schema = identifiers_schema();
        let batch_reader = RecordBatchIterator::new(vec![Ok(batch)], schema);

        // Check if table exists
        let table_names = self.db.table_names().execute().await?;

        if table_names.contains(&IDENTIFIERS_TABLE_NAME.to_string()) {
            let table = self.db.open_table(IDENTIFIERS_TABLE_NAME).execute().await?;
            table.add(Box::new(batch_reader)).execute().await?;
        } else {
            self.db
                .create_table(IDENTIFIERS_TABLE_NAME, Box::new(batch_reader))
                .execute()
                .await?;
        }

        Ok(count)
    }

    /// Get the count of identifiers in the database
    pub async fn identifier_count(&self) -> Result<usize> {
        let table_names = self.db.table_names().execute().await?;

        if !table_names.contains(&IDENTIFIERS_TABLE_NAME.to_string()) {
            return Ok(0);
        }

        let table = self.db.open_table(IDENTIFIERS_TABLE_NAME).execute().await?;
        let count = table.count_rows(None).await?;
        Ok(count)
    }

    /// Clear all reachability data
    pub async fn clear_reachability(&self) -> Result<()> {
        let table_names = self.db.table_names().execute().await?;
        if table_names.contains(&REACHABILITY_TABLE_NAME.to_string()) {
            let table = self.db.open_table(REACHABILITY_TABLE_NAME).execute().await?;
            table.delete("true").await?;  // Delete all rows
        }
        Ok(())
    }

    /// Add reachability entries in batch
    pub async fn add_reachability_batch(&self, entries: Vec<ReachabilityEntry>) -> Result<usize> {
        if entries.is_empty() {
            return Ok(0);
        }

        let count = entries.len();

        // Ensure table exists (lazy creation)
        let table_names = self.db.table_names().execute().await?;
        if !table_names.contains(&REACHABILITY_TABLE_NAME.to_string()) {
            let empty_batch = RecordBatch::try_new(
                reachability_schema(),
                vec![
                    Arc::new(StringArray::from(Vec::<&str>::new())),
                    Arc::new(StringArray::from(Vec::<&str>::new())),
                    Arc::new(UInt32Array::from(Vec::<u32>::new())),
                ],
            )?;
            let schema = reachability_schema();
            let batch_reader = RecordBatchIterator::new(vec![Ok(empty_batch)], schema);
            self.db.create_table(REACHABILITY_TABLE_NAME, Box::new(batch_reader)).execute().await?;
        }

        let sources: Vec<&str> = entries.iter().map(|e| e.source_id.as_str()).collect();
        let targets: Vec<&str> = entries.iter().map(|e| e.target_id.as_str()).collect();
        let distances: Vec<u32> = entries.iter().map(|e| e.min_distance).collect();

        let batch = RecordBatch::try_new(
            reachability_schema(),
            vec![
                Arc::new(StringArray::from(sources)),
                Arc::new(StringArray::from(targets)),
                Arc::new(UInt32Array::from(distances)),
            ],
        )?;

        let schema = reachability_schema();
        let batch_reader = RecordBatchIterator::new(vec![Ok(batch)], schema);

        let table = self.db.open_table(REACHABILITY_TABLE_NAME).execute().await?;
        table.add(Box::new(batch_reader)).execute().await?;

        Ok(count)
    }

    /// Get all symbols reachable from source (impact analysis)
    pub async fn get_impacted(&self, source_id: &str, max_distance: u32) -> Result<Vec<(String, u32)>> {
        let table_names = self.db.table_names().execute().await?;
        if !table_names.contains(&REACHABILITY_TABLE_NAME.to_string()) {
            return Ok(Vec::new());
        }

        let table = self.db.open_table(REACHABILITY_TABLE_NAME).execute().await?;

        let filter = format!(
            "source_id = '{}' AND min_distance <= {}",
            source_id.replace('\'', "''"),
            max_distance
        );

        let stream = table
            .query()
            .only_if(filter)
            .execute()
            .await?;

        let mut impacted = Vec::new();
        let batches: Vec<RecordBatch> = stream.try_collect().await?;

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

    /// Get symbols that call the given symbol (reverse lookup)
    pub async fn get_callers(&self, symbol_id: &str, limit: usize) -> Result<Vec<RelationshipResult>> {
        self.query_relationships_by_target(symbol_id, "Calls", limit).await
    }

    /// Get symbols that the given symbol calls (forward lookup)
    pub async fn get_callees(&self, symbol_id: &str, limit: usize) -> Result<Vec<RelationshipResult>> {
        self.query_relationships_by_source(symbol_id, "Calls", limit).await
    }

    /// Get all relationships for a symbol (both directions)
    pub async fn get_relationships(&self, symbol_id: &str, limit: usize) -> Result<Vec<RelationshipResult>> {
        let table_names = self.db.table_names().execute().await?;

        if !table_names.contains(&RELATIONSHIPS_TABLE_NAME.to_string()) {
            return Ok(Vec::new());
        }

        let table = self.db.open_table(RELATIONSHIPS_TABLE_NAME).execute().await?;

        // Query for relationships where symbol is source OR target
        let escaped_id = symbol_id.replace('\'', "''");
        let filter = format!(
            "from_symbol_id = '{}' OR to_symbol_id = '{}'",
            escaped_id, escaped_id
        );

        let stream = table
            .query()
            .only_if(filter)
            .limit(limit)
            .execute()
            .await?;

        let batches: Vec<RecordBatch> = stream.try_collect().await?;
        self.batches_to_relationships(batches)
    }

    /// Get all references to a symbol (where it's used)
    pub async fn get_references(&self, symbol_id: &str, limit: usize) -> Result<Vec<ReferenceResult>> {
        let table_names = self.db.table_names().execute().await?;
        if !table_names.contains(&IDENTIFIERS_TABLE_NAME.to_string()) {
            return Ok(Vec::new());
        }

        let table = self.db.open_table(IDENTIFIERS_TABLE_NAME).execute().await?;

        let filter = format!(
            "target_symbol_id = '{}'",
            symbol_id.replace('\'', "''")
        );

        let stream = table
            .query()
            .only_if(filter)
            .limit(limit)
            .execute()
            .await?;

        let mut references = Vec::new();
        let batches: Vec<RecordBatch> = stream.try_collect().await?;

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

    /// Get a symbol by its ID
    pub async fn get_symbol_by_id(&self, symbol_id: &str) -> Result<Option<SymbolInfo>> {
        let table_names = self.db.table_names().execute().await?;
        if !table_names.contains(&TABLE_NAME.to_string()) {
            return Ok(None);
        }

        let table = self.db.open_table(TABLE_NAME).execute().await?;

        let results = table
            .query()
            .only_if(format!("id = '{}'", symbol_id.replace("'", "''")))
            .limit(1)
            .execute()
            .await?;

        let batches: Vec<RecordBatch> = results.try_collect().await?;

        if batches.is_empty() || batches[0].num_rows() == 0 {
            return Ok(None);
        }

        Ok(Some(self.batch_to_symbol_info(&batches[0], 0)))
    }

    /// Get all symbols in a file
    pub async fn get_symbols_by_file(&self, file_path: &str, limit: usize) -> Result<Vec<SymbolInfo>> {
        let table_names = self.db.table_names().execute().await?;
        if !table_names.contains(&TABLE_NAME.to_string()) {
            return Ok(Vec::new());
        }

        let table = self.db.open_table(TABLE_NAME).execute().await?;

        let results = table
            .query()
            .only_if(format!("file_path = '{}'", file_path.replace("'", "''")))
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

    /// Get all symbols of a specific kind
    pub async fn get_symbols_by_kind(&self, kind: &str, limit: usize) -> Result<Vec<SymbolInfo>> {
        let table_names = self.db.table_names().execute().await?;
        if !table_names.contains(&TABLE_NAME.to_string()) {
            return Ok(Vec::new());
        }

        let table = self.db.open_table(TABLE_NAME).execute().await?;

        let results = table
            .query()
            .only_if(format!("kind = '{}'", kind.replace("'", "''")))
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

    /// Convert a record batch row to SymbolInfo
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
            start_line: if start_lines.is_null(idx) { 0 } else { start_lines.value(idx) },
            end_line: if end_lines.is_null(idx) { 0 } else { end_lines.value(idx) },
            signature: if signatures.is_null(idx) { None } else { Some(signatures.value(idx).to_string()) },
            doc_comment: if doc_comments.is_null(idx) { None } else { Some(doc_comments.value(idx).to_string()) },
        }
    }

    /// Query relationships where symbol_id is the target (for callers)
    async fn query_relationships_by_target(
        &self,
        symbol_id: &str,
        kind: &str,
        limit: usize,
    ) -> Result<Vec<RelationshipResult>> {
        let table_names = self.db.table_names().execute().await?;

        if !table_names.contains(&RELATIONSHIPS_TABLE_NAME.to_string()) {
            return Ok(Vec::new());
        }

        let table = self.db.open_table(RELATIONSHIPS_TABLE_NAME).execute().await?;

        let escaped_id = symbol_id.replace('\'', "''");
        let escaped_kind = kind.replace('\'', "''");
        let filter = format!(
            "to_symbol_id = '{}' AND kind = '{}'",
            escaped_id, escaped_kind
        );

        let stream = table
            .query()
            .only_if(filter)
            .limit(limit)
            .execute()
            .await?;

        let batches: Vec<RecordBatch> = stream.try_collect().await?;
        self.batches_to_relationships(batches)
    }

    /// Query relationships where symbol_id is the source (for callees)
    async fn query_relationships_by_source(
        &self,
        symbol_id: &str,
        kind: &str,
        limit: usize,
    ) -> Result<Vec<RelationshipResult>> {
        let table_names = self.db.table_names().execute().await?;

        if !table_names.contains(&RELATIONSHIPS_TABLE_NAME.to_string()) {
            return Ok(Vec::new());
        }

        let table = self.db.open_table(RELATIONSHIPS_TABLE_NAME).execute().await?;

        let escaped_id = symbol_id.replace('\'', "''");
        let escaped_kind = kind.replace('\'', "''");
        let filter = format!(
            "from_symbol_id = '{}' AND kind = '{}'",
            escaped_id, escaped_kind
        );

        let stream = table
            .query()
            .only_if(filter)
            .limit(limit)
            .execute()
            .await?;

        let batches: Vec<RecordBatch> = stream.try_collect().await?;
        self.batches_to_relationships(batches)
    }

    /// Convert record batches to RelationshipResult vector
    fn batches_to_relationships(&self, batches: Vec<RecordBatch>) -> Result<Vec<RelationshipResult>> {
        let mut results = Vec::new();

        for batch in batches {
            let from_ids = Self::get_required_string_column(&batch, "from_symbol_id")?;
            let to_ids = Self::get_required_string_column(&batch, "to_symbol_id")?;
            let kinds = Self::get_required_string_column(&batch, "kind")?;
            let paths = Self::get_required_string_column(&batch, "file_path")?;

            let lines = batch
                .column_by_name("line_number")
                .ok_or_else(|| Error::Validation("missing 'line_number' column".to_string()))?
                .as_any()
                .downcast_ref::<arrow::array::UInt32Array>()
                .ok_or_else(|| Error::Validation("'line_number' column is not a uint32 array".to_string()))?;

            let confidences = Self::get_required_float_column(&batch, "confidence")?;

            for i in 0..batch.num_rows() {
                results.push(RelationshipResult {
                    from_symbol_id: from_ids.value(i).to_string(),
                    to_symbol_id: to_ids.value(i).to_string(),
                    kind: kinds.value(i).to_string(),
                    file_path: paths.value(i).to_string(),
                    line_number: lines.value(i),
                    confidence: confidences.value(i),
                });
            }
        }

        Ok(results)
    }

    /// Search for symbols by vector similarity
    ///
    /// # Arguments
    /// * `query_vector` - The query embedding vector (must be VECTOR_DIMENSION elements)
    /// * `limit` - Maximum number of results to return
    ///
    /// # Returns
    /// Vector of SearchResult ordered by similarity (highest score first)
    pub async fn search_vector(
        &self,
        query_vector: &[f32],
        limit: usize,
    ) -> Result<Vec<SearchResult>> {
        // Validate query vector dimensions
        if query_vector.len() != VECTOR_DIMENSION {
            return Err(Error::Validation(format!(
                "query vector has {} dimensions, expected {}",
                query_vector.len(),
                VECTOR_DIMENSION
            )));
        }

        // Return empty if table doesn't exist
        let table_names = self.db.table_names().execute().await?;
        if !table_names.contains(&TABLE_NAME.to_string()) {
            return Ok(Vec::new());
        }

        let table = self.db.open_table(TABLE_NAME).execute().await?;

        // Perform vector search
        let stream = table
            .vector_search(query_vector)?
            .limit(limit)
            .execute()
            .await?;

        // Collect all record batches
        let batches: Vec<RecordBatch> = stream.try_collect().await?;

        // Convert batches to SearchResults
        let mut results = Vec::new();
        for batch in batches {
            let batch_results = self.record_batch_to_search_results(&batch)?;
            results.extend(batch_results);
        }

        Ok(results)
    }

    /// Create a full-text search index on the code_pattern field
    ///
    /// Uses whitespace tokenizer to preserve code patterns like `: BaseClass`
    /// Note: The name field is already embedded in code_pattern, so searching
    /// code_pattern also searches by symbol name.
    pub async fn create_fts_index(&self) -> Result<()> {
        // Return early if table doesn't exist
        let table_names = self.db.table_names().execute().await?;
        if !table_names.contains(&TABLE_NAME.to_string()) {
            return Ok(());
        }

        let table = self.db.open_table(TABLE_NAME).execute().await?;

        // Configure FTS index with whitespace tokenizer to preserve code patterns
        let tokenizer_config = TokenizerConfig::default()
            .base_tokenizer("whitespace".to_string());

        let fts_builder = FtsIndexBuilder {
            with_position: true,
            tokenizer_configs: tokenizer_config,
        };

        // Create index on code_pattern field
        // Note: LanceDB doesn't yet support multi-column FTS indices,
        // but code_pattern includes the symbol name so this still enables name search
        table
            .create_index(&["code_pattern"], Index::FTS(fts_builder))
            .execute()
            .await?;

        Ok(())
    }

    /// Search for symbols using full-text search
    ///
    /// # Arguments
    /// * `query` - The text query to search for
    /// * `limit` - Maximum number of results to return
    ///
    /// # Returns
    /// Vector of SearchResult ordered by BM25 score (highest first), normalized to 0.0-1.0
    pub async fn search_text(&self, query: &str, limit: usize) -> Result<Vec<SearchResult>> {
        // Return empty if table doesn't exist
        let table_names = self.db.table_names().execute().await?;
        if !table_names.contains(&TABLE_NAME.to_string()) {
            return Ok(Vec::new());
        }

        let table = self.db.open_table(TABLE_NAME).execute().await?;

        // Over-fetch to allow re-ranking after score boosting (see search_text_boosted)
        let fetch_limit = std::cmp::max(limit * 3, 50);

        // Perform full-text search
        let stream = table
            .query()
            .full_text_search(FullTextSearchQuery::new(query.to_string()))
            .limit(fetch_limit)
            .execute()
            .await?;

        // Collect all record batches
        let batches: Vec<RecordBatch> = stream.try_collect().await?;

        // Convert batches to SearchResults (with _score instead of _distance)
        let mut results = Vec::new();
        for batch in batches {
            let batch_results = self.fts_record_batch_to_search_results(&batch)?;
            results.extend(batch_results);
        }

        // Normalize scores to 0.0-1.0 (divide by max score)
        if !results.is_empty() {
            let max_score = results.iter().map(|r| r.score).fold(0.0f32, f32::max);
            if max_score > 0.0 {
                for result in &mut results {
                    result.score /= max_score;
                }
            }
        }

        // Truncate to requested limit
        results.truncate(limit);

        Ok(results)
    }

    /// Search for symbols using hybrid search combining FTS and vector search
    ///
    /// Uses Reciprocal Rank Fusion (RRF) to combine rankings from both search methods.
    /// Results that appear in both rankings get boosted higher.
    ///
    /// # Arguments
    /// * `query_text` - The text query for full-text search
    /// * `query_vector` - The embedding vector for vector search (must be VECTOR_DIMENSION elements)
    /// * `limit` - Maximum number of results to return
    ///
    /// # Returns
    /// Vector of SearchResult ordered by RRF score (highest first), normalized to 0.0-1.0
    pub async fn search_hybrid(
        &self,
        query_text: &str,
        query_vector: &[f32],
        limit: usize,
    ) -> Result<Vec<SearchResult>> {
        // Validate query vector dimensions
        if query_vector.len() != VECTOR_DIMENSION {
            return Err(Error::Validation(format!(
                "query vector has {} dimensions, expected {}",
                query_vector.len(),
                VECTOR_DIMENSION
            )));
        }

        // Over-fetch: 2x limit with minimum of 30 from each source
        // Minimum 30 ensures enough candidates for meaningful RRF fusion
        let fetch_limit = std::cmp::max(limit * 2, 30);

        // Run both searches in parallel since they're independent
        let (text_results, vector_results) = tokio::join!(
            self.search_text(query_text, fetch_limit),
            self.search_vector(query_vector, fetch_limit)
        );
        let text_results = text_results?;
        let vector_results = vector_results?;

        // If both are empty, return empty
        if text_results.is_empty() && vector_results.is_empty() {
            return Ok(Vec::new());
        }

        // RRF constant k=60 (standard value)
        const RRF_K: f32 = 60.0;

        // Track RRF scores and results by ID
        let mut rrf_scores: HashMap<String, f32> = HashMap::new();
        let mut results_by_id: HashMap<String, SearchResult> = HashMap::new();

        // Calculate RRF scores for text results
        for (rank, result) in text_results.into_iter().enumerate() {
            let rrf_score = 1.0 / (RRF_K + rank as f32 + 1.0);
            *rrf_scores.entry(result.id.clone()).or_insert(0.0) += rrf_score;
            // First wins deduplication is safe: both sources return identical Symbol data for same ID
            results_by_id.entry(result.id.clone()).or_insert(result);
        }

        // Calculate RRF scores for vector results
        for (rank, result) in vector_results.into_iter().enumerate() {
            let rrf_score = 1.0 / (RRF_K + rank as f32 + 1.0);
            *rrf_scores.entry(result.id.clone()).or_insert(0.0) += rrf_score;
            // First wins deduplication is safe: both sources return identical Symbol data for same ID
            results_by_id.entry(result.id.clone()).or_insert(result);
        }

        // Convert to vector and sort by RRF score descending
        let mut scored_results: Vec<(String, f32)> = rrf_scores.into_iter().collect();
        scored_results.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));

        // Normalize scores to 0.0-1.0 (divide by max)
        let max_score = scored_results.first().map(|(_, s)| *s).unwrap_or(1.0);

        // Build final results
        let mut final_results: Vec<SearchResult> = Vec::new();
        for (id, rrf_score) in scored_results.into_iter().take(limit) {
            if let Some(mut result) = results_by_id.remove(&id) {
                result.score = if max_score > 0.0 {
                    rrf_score / max_score
                } else {
                    0.0
                };
                final_results.push(result);
            }
        }

        Ok(final_results)
    }

    /// Search for symbols using full-text search with score boosting
    ///
    /// Fetches 3x the requested limit, applies boosts, then truncates.
    ///
    /// # Arguments
    /// * `query` - The text query to search for
    /// * `limit` - Maximum number of results to return
    ///
    /// # Returns
    /// Vector of SearchResult ordered by boosted score (highest first)
    pub async fn search_text_boosted(
        &self,
        query: &str,
        limit: usize,
    ) -> Result<Vec<SearchResult>> {
        let fetch_limit = limit * 3;
        let mut results = self.search_text(query, fetch_limit).await?;
        apply_boosts(&mut results, query);
        results.truncate(limit);
        Ok(results)
    }

    /// Search for symbols by vector similarity with score boosting
    ///
    /// Fetches 3x the requested limit, applies boosts, then truncates.
    ///
    /// # Arguments
    /// * `query` - The text query for boosting (not used for vector search itself)
    /// * `query_vector` - The query embedding vector (must be VECTOR_DIMENSION elements)
    /// * `limit` - Maximum number of results to return
    ///
    /// # Returns
    /// Vector of SearchResult ordered by boosted score (highest first)
    pub async fn search_vector_boosted(
        &self,
        query: &str,
        query_vector: &[f32],
        limit: usize,
    ) -> Result<Vec<SearchResult>> {
        let fetch_limit = limit * 3;
        let mut results = self.search_vector(query_vector, fetch_limit).await?;
        apply_boosts(&mut results, query);
        results.truncate(limit);
        Ok(results)
    }

    /// Search for symbols using hybrid search with score boosting
    ///
    /// Fetches 3x the requested limit, applies boosts, then truncates.
    ///
    /// # Arguments
    /// * `query` - The text query for FTS search and boosting
    /// * `query_vector` - The embedding vector for vector search (must be VECTOR_DIMENSION elements)
    /// * `limit` - Maximum number of results to return
    ///
    /// # Returns
    /// Vector of SearchResult ordered by boosted score (highest first)
    pub async fn search_hybrid_boosted(
        &self,
        query: &str,
        query_vector: &[f32],
        limit: usize,
    ) -> Result<Vec<SearchResult>> {
        let fetch_limit = limit * 3;
        let mut results = self.search_hybrid(query, query_vector, fetch_limit).await?;
        apply_boosts(&mut results, query);
        results.truncate(limit);
        Ok(results)
    }

    /// Helper to extract a required string column from a RecordBatch
    fn get_required_string_column<'a>(
        batch: &'a RecordBatch,
        name: &str,
    ) -> Result<&'a StringArray> {
        let col = batch
            .column_by_name(name)
            .ok_or_else(|| Error::Validation(format!("missing '{}' column", name)))?;
        col.as_any()
            .downcast_ref::<StringArray>()
            .ok_or_else(|| Error::Validation(format!("'{}' column is not a string array", name)))
    }

    /// Helper to extract an optional string column from a RecordBatch
    fn get_optional_string_column<'a>(
        batch: &'a RecordBatch,
        name: &str,
    ) -> Option<&'a StringArray> {
        batch
            .column_by_name(name)
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
    }

    /// Helper to extract an optional int32 column from a RecordBatch
    fn get_optional_int_column<'a>(
        batch: &'a RecordBatch,
        name: &str,
    ) -> Option<&'a Int32Array> {
        batch
            .column_by_name(name)
            .and_then(|c| c.as_any().downcast_ref::<Int32Array>())
    }

    /// Helper to extract a required float32 column from a RecordBatch
    fn get_required_float_column<'a>(
        batch: &'a RecordBatch,
        name: &str,
    ) -> Result<&'a Float32Array> {
        let col = batch
            .column_by_name(name)
            .ok_or_else(|| Error::Validation(format!("missing '{}' column", name)))?;
        col.as_any()
            .downcast_ref::<Float32Array>()
            .ok_or_else(|| Error::Validation(format!("'{}' column is not a float array", name)))
    }

    /// Convert a RecordBatch to SearchResults with configurable score extraction
    ///
    /// # Arguments
    /// * `batch` - The RecordBatch to convert
    /// * `score_column` - Name of the score column ("_distance" or "_score")
    /// * `score_transform` - Function to transform the raw score value
    fn batch_to_search_results(
        &self,
        batch: &RecordBatch,
        score_column: &str,
        score_transform: impl Fn(f32) -> f32,
    ) -> Result<Vec<SearchResult>> {
        let num_rows = batch.num_rows();
        let mut results = Vec::with_capacity(num_rows);

        // Extract required columns using helper functions
        let id_array = Self::get_required_string_column(batch, "id")?;
        let name_array = Self::get_required_string_column(batch, "name")?;
        let kind_array = Self::get_required_string_column(batch, "kind")?;
        let language_array = Self::get_required_string_column(batch, "language")?;
        let file_path_array = Self::get_required_string_column(batch, "file_path")?;
        let score_array = Self::get_required_float_column(batch, score_column)?;

        // Extract optional columns using helper functions
        let signature_array = Self::get_optional_string_column(batch, "signature");
        let doc_comment_array = Self::get_optional_string_column(batch, "doc_comment");
        let start_line_array = Self::get_optional_int_column(batch, "start_line");
        let end_line_array = Self::get_optional_int_column(batch, "end_line");

        for i in 0..num_rows {
            let raw_score = score_array.value(i);
            let score = score_transform(raw_score);

            let result = SearchResult {
                id: id_array.value(i).to_string(),
                name: name_array.value(i).to_string(),
                kind: kind_array.value(i).to_string(),
                language: language_array.value(i).to_string(),
                file_path: file_path_array.value(i).to_string(),
                signature: signature_array.and_then(|a| {
                    if a.is_null(i) {
                        None
                    } else {
                        Some(a.value(i).to_string())
                    }
                }),
                doc_comment: doc_comment_array.and_then(|a| {
                    if a.is_null(i) {
                        None
                    } else {
                        Some(a.value(i).to_string())
                    }
                }),
                start_line: start_line_array.and_then(|a| {
                    if a.is_null(i) {
                        None
                    } else {
                        Some(a.value(i))
                    }
                }),
                end_line: end_line_array.and_then(|a| {
                    if a.is_null(i) {
                        None
                    } else {
                        Some(a.value(i))
                    }
                }),
                score,
            };

            results.push(result);
        }

        Ok(results)
    }

    /// Convert a RecordBatch from vector search to SearchResults
    fn record_batch_to_search_results(&self, batch: &RecordBatch) -> Result<Vec<SearchResult>> {
        self.batch_to_search_results(batch, "_distance", distance_to_score)
    }

    /// Convert a RecordBatch from FTS search to SearchResults
    fn fts_record_batch_to_search_results(&self, batch: &RecordBatch) -> Result<Vec<SearchResult>> {
        self.batch_to_search_results(batch, "_score", |score| score)
    }

    /// Convert symbols and vectors to an Arrow RecordBatch
    fn symbols_to_record_batch(
        &self,
        symbols: Vec<Symbol>,
        vectors: Vec<Vec<f32>>,
    ) -> Result<RecordBatch> {
        // Build string arrays for required fields
        let ids: Vec<&str> = symbols.iter().map(|s| s.id.as_str()).collect();
        let names: Vec<&str> = symbols.iter().map(|s| s.name.as_str()).collect();
        let kinds: Vec<String> = symbols.iter().map(|s| s.kind.to_string()).collect();
        let languages: Vec<&str> = symbols.iter().map(|s| s.language.as_str()).collect();
        let file_paths: Vec<&str> = symbols.iter().map(|s| s.file_path.as_str()).collect();
        let code_patterns: Vec<&str> = symbols.iter().map(|s| s.code_pattern.as_str()).collect();

        // Build arrays
        let id_array = Arc::new(arrow::array::StringArray::from(ids)) as ArrayRef;
        let name_array = Arc::new(arrow::array::StringArray::from(names)) as ArrayRef;
        let kind_array = Arc::new(arrow::array::StringArray::from(
            kinds.iter().map(|s| s.as_str()).collect::<Vec<_>>(),
        )) as ArrayRef;
        let language_array = Arc::new(arrow::array::StringArray::from(languages)) as ArrayRef;
        let file_path_array = Arc::new(arrow::array::StringArray::from(file_paths)) as ArrayRef;

        // Build nullable string arrays
        let signature_array = self.build_nullable_string_array(
            symbols.iter().map(|s| s.signature.as_deref()).collect(),
        );
        let doc_comment_array = self.build_nullable_string_array(
            symbols.iter().map(|s| s.doc_comment.as_deref()).collect(),
        );
        let code_pattern_array = Arc::new(arrow::array::StringArray::from(code_patterns)) as ArrayRef;
        let content_array = self.build_nullable_string_array(
            symbols.iter().map(|s| s.content.as_deref()).collect(),
        );

        // Build nullable int arrays
        let start_line_array = self.build_nullable_int_array(
            symbols.iter().map(|s| s.start_line).collect(),
        );
        let end_line_array = self.build_nullable_int_array(
            symbols.iter().map(|s| s.end_line).collect(),
        );

        // Build vector array (FixedSizeList of Float32)
        let vector_array = self.build_vector_array(vectors)?;

        // Create RecordBatch
        let schema = symbols_schema();
        let batch = RecordBatch::try_new(
            schema,
            vec![
                id_array,
                name_array,
                kind_array,
                language_array,
                file_path_array,
                signature_array,
                doc_comment_array,
                start_line_array,
                end_line_array,
                code_pattern_array,
                content_array,
                vector_array,
            ],
        )?;

        Ok(batch)
    }

    /// Build a nullable string array from Option values
    fn build_nullable_string_array(&self, values: Vec<Option<&str>>) -> ArrayRef {
        let mut builder = StringBuilder::new();
        for v in values {
            match v {
                Some(s) => builder.append_value(s),
                None => builder.append_null(),
            }
        }
        Arc::new(builder.finish())
    }

    /// Build a nullable int32 array from Option values
    fn build_nullable_int_array(&self, values: Vec<Option<i32>>) -> ArrayRef {
        let array: Int32Array = values.into_iter().collect();
        Arc::new(array)
    }

    /// Build a FixedSizeList array for vectors
    fn build_vector_array(&self, vectors: Vec<Vec<f32>>) -> Result<ArrayRef> {
        let flat_values: Vec<f32> = vectors.iter().flatten().copied().collect();
        let values_array = Float32Array::from(flat_values);

        let field = Arc::new(arrow::datatypes::Field::new("item", arrow::datatypes::DataType::Float32, false));
        let array = FixedSizeListArray::try_new(field, VECTOR_DIMENSION as i32, Arc::new(values_array), None)?;

        Ok(Arc::new(array))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema::SymbolKind;
    use tempfile::TempDir;

    #[tokio::test]
    async fn test_engine_creation() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();
        assert!(engine.health_check().await.unwrap());
    }

    #[tokio::test]
    async fn test_symbol_count_empty() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();
        assert_eq!(engine.symbol_count().await.unwrap(), 0);
    }

    #[tokio::test]
    async fn test_add_symbols() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        let symbols = vec![Symbol {
            id: "test_foo".to_string(),
            name: "foo".to_string(),
            kind: SymbolKind::Function,
            language: "rust".to_string(),
            file_path: "src/test.rs".to_string(),
            signature: Some("fn foo()".to_string()),
            doc_comment: None,
            start_line: Some(1),
            end_line: Some(10),
            code_pattern: "fn foo() function".to_string(),
            content: None,
        }];

        let vectors = vec![vec![0.1f32; VECTOR_DIMENSION]];

        let count = engine.add_symbols(symbols, vectors).await.unwrap();
        assert_eq!(count, 1);
        assert_eq!(engine.symbol_count().await.unwrap(), 1);
    }

    #[tokio::test]
    async fn test_add_multiple_symbols() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        let symbols = vec![
            Symbol {
                id: "sym1".to_string(),
                name: "foo".to_string(),
                kind: SymbolKind::Function,
                language: "rust".to_string(),
                file_path: "src/a.rs".to_string(),
                signature: Some("fn foo()".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(10),
                code_pattern: "fn foo() function".to_string(),
                content: None,
            },
            Symbol {
                id: "sym2".to_string(),
                name: "Bar".to_string(),
                kind: SymbolKind::Struct,
                language: "rust".to_string(),
                file_path: "src/b.rs".to_string(),
                signature: Some("struct Bar".to_string()),
                doc_comment: Some("A bar struct".to_string()),
                start_line: Some(5),
                end_line: Some(20),
                code_pattern: "struct Bar struct".to_string(),
                content: Some("struct Bar { x: i32 }".to_string()),
            },
        ];

        let vectors = vec![
            vec![0.1f32; VECTOR_DIMENSION],
            vec![0.2f32; VECTOR_DIMENSION],
        ];

        let count = engine.add_symbols(symbols, vectors).await.unwrap();
        assert_eq!(count, 2);
        assert_eq!(engine.symbol_count().await.unwrap(), 2);
    }

    #[tokio::test]
    async fn test_add_symbols_validates_vector_count() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        let symbols = vec![Symbol {
            id: "test".to_string(),
            name: "foo".to_string(),
            kind: SymbolKind::Function,
            language: "rust".to_string(),
            file_path: "src/test.rs".to_string(),
            signature: None,
            doc_comment: None,
            start_line: None,
            end_line: None,
            code_pattern: "foo function".to_string(),
            content: None,
        }];

        // Mismatched vector count
        let vectors: Vec<Vec<f32>> = vec![];

        let result = engine.add_symbols(symbols, vectors).await;
        assert!(result.is_err());
    }

    #[tokio::test]
    async fn test_add_symbols_validates_vector_dimensions() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        let symbols = vec![Symbol {
            id: "test".to_string(),
            name: "foo".to_string(),
            kind: SymbolKind::Function,
            language: "rust".to_string(),
            file_path: "src/test.rs".to_string(),
            signature: None,
            doc_comment: None,
            start_line: None,
            end_line: None,
            code_pattern: "foo function".to_string(),
            content: None,
        }];

        // Wrong dimension
        let vectors = vec![vec![0.1f32; 100]];

        let result = engine.add_symbols(symbols, vectors).await;
        assert!(result.is_err());
    }

    #[tokio::test]
    async fn test_search_vector_basic() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Add 3 symbols with different vectors
        let symbols = vec![
            Symbol {
                id: "sym1".to_string(),
                name: "foo".to_string(),
                kind: SymbolKind::Function,
                language: "rust".to_string(),
                file_path: "src/a.rs".to_string(),
                signature: Some("fn foo()".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(10),
                code_pattern: "fn foo() function".to_string(),
                content: None,
            },
            Symbol {
                id: "sym2".to_string(),
                name: "bar".to_string(),
                kind: SymbolKind::Function,
                language: "rust".to_string(),
                file_path: "src/b.rs".to_string(),
                signature: Some("fn bar()".to_string()),
                doc_comment: Some("A bar function".to_string()),
                start_line: Some(5),
                end_line: Some(15),
                code_pattern: "fn bar() function".to_string(),
                content: None,
            },
            Symbol {
                id: "sym3".to_string(),
                name: "baz".to_string(),
                kind: SymbolKind::Struct,
                language: "rust".to_string(),
                file_path: "src/c.rs".to_string(),
                signature: Some("struct Baz".to_string()),
                doc_comment: None,
                start_line: Some(20),
                end_line: Some(30),
                code_pattern: "struct Baz struct".to_string(),
                content: None,
            },
        ];

        // Create distinct vectors - sym1 has all 0.1, sym2 has all 0.5, sym3 has all 0.9
        let vectors = vec![
            vec![0.1f32; VECTOR_DIMENSION],
            vec![0.5f32; VECTOR_DIMENSION],
            vec![0.9f32; VECTOR_DIMENSION],
        ];

        engine.add_symbols(symbols, vectors).await.unwrap();

        // Search with a vector similar to sym1 (all 0.1)
        let query_vector = vec![0.1f32; VECTOR_DIMENSION];
        let results = engine.search_vector(&query_vector, 2).await.unwrap();

        assert_eq!(results.len(), 2);
        // sym1 should be the closest match
        assert_eq!(results[0].id, "sym1");
        assert_eq!(results[0].name, "foo");
        assert_eq!(results[0].kind, "function");
        assert_eq!(results[0].file_path, "src/a.rs");
        assert_eq!(results[0].signature, Some("fn foo()".to_string()));
        // Score should be high (close to 1.0) for near-identical vector
        assert!(results[0].score > 0.9, "Expected score > 0.9, got {}", results[0].score);

        // sym2 should be second (all 0.5 is closer to 0.1 than all 0.9)
        assert_eq!(results[1].id, "sym2");
    }

    #[tokio::test]
    async fn test_search_vector_empty_db() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Search on empty DB should return empty results
        let query_vector = vec![0.5f32; VECTOR_DIMENSION];
        let results = engine.search_vector(&query_vector, 10).await.unwrap();

        assert!(results.is_empty());
    }

    #[tokio::test]
    async fn test_search_vector_validates_dimensions() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Wrong dimension vector should return error
        let query_vector = vec![0.5f32; 100]; // Wrong dimension
        let result = engine.search_vector(&query_vector, 10).await;

        assert!(result.is_err());
        let err = result.unwrap_err();
        assert!(err.to_string().contains("768"), "Error should mention expected dimension 768: {}", err);
    }

    #[tokio::test]
    async fn test_search_text_basic() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Add symbols with distinct code patterns
        let symbols = vec![
            Symbol {
                id: "sym1".to_string(),
                name: "UserService".to_string(),
                kind: SymbolKind::Struct,
                language: "rust".to_string(),
                file_path: "src/user.rs".to_string(),
                signature: Some("struct UserService".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(50),
                code_pattern: "struct UserService : BaseService implements IUserService".to_string(),
                content: None,
            },
            Symbol {
                id: "sym2".to_string(),
                name: "ProductService".to_string(),
                kind: SymbolKind::Struct,
                language: "rust".to_string(),
                file_path: "src/product.rs".to_string(),
                signature: Some("struct ProductService".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(30),
                code_pattern: "struct ProductService : BaseService implements IProductService".to_string(),
                content: None,
            },
            Symbol {
                id: "sym3".to_string(),
                name: "OrderManager".to_string(),
                kind: SymbolKind::Struct,
                language: "rust".to_string(),
                file_path: "src/order.rs".to_string(),
                signature: Some("struct OrderManager".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(40),
                code_pattern: "struct OrderManager implements IOrderManager".to_string(),
                content: None,
            },
        ];

        let vectors = vec![
            vec![0.1f32; VECTOR_DIMENSION],
            vec![0.2f32; VECTOR_DIMENSION],
            vec![0.3f32; VECTOR_DIMENSION],
        ];

        engine.add_symbols(symbols, vectors).await.unwrap();

        // Create FTS index
        engine.create_fts_index().await.unwrap();

        // Search for "BaseService" - should match sym1 and sym2
        let results = engine.search_text("BaseService", 10).await.unwrap();

        assert_eq!(results.len(), 2, "Should find 2 symbols with BaseService");

        // Verify results contain the expected symbols
        let ids: Vec<&str> = results.iter().map(|r| r.id.as_str()).collect();
        assert!(ids.contains(&"sym1"), "Should find UserService");
        assert!(ids.contains(&"sym2"), "Should find ProductService");

        // All scores should be normalized (0.0-1.0)
        for result in &results {
            assert!(result.score >= 0.0 && result.score <= 1.0,
                "Score {} should be normalized to 0.0-1.0", result.score);
        }
    }

    #[tokio::test]
    async fn test_search_text_empty_db() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Search on empty DB should return empty results (table doesn't exist)
        let results = engine.search_text("anything", 10).await.unwrap();

        assert!(results.is_empty(), "Search on empty DB should return empty results");
    }

    #[tokio::test]
    async fn test_search_text_no_match() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Add a symbol
        let symbols = vec![Symbol {
            id: "sym1".to_string(),
            name: "UserService".to_string(),
            kind: SymbolKind::Function,
            language: "rust".to_string(),
            file_path: "src/user.rs".to_string(),
            signature: None,
            doc_comment: None,
            start_line: None,
            end_line: None,
            code_pattern: "fn user_service()".to_string(),
            content: None,
        }];

        let vectors = vec![vec![0.1f32; VECTOR_DIMENSION]];
        engine.add_symbols(symbols, vectors).await.unwrap();

        // Create FTS index
        engine.create_fts_index().await.unwrap();

        // Search for nonexistent term
        let results = engine.search_text("xyznonexistent", 10).await.unwrap();

        assert!(results.is_empty(), "Search for nonexistent term should return empty results");
    }

    #[tokio::test]
    async fn test_search_hybrid_basic() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Add symbols with distinct patterns and vectors
        // sym1: matches BOTH text query "AuthService" and vector (all 0.1)
        // sym2: matches only text query "AuthService"
        // sym3: matches only vector (all 0.1)
        let symbols = vec![
            Symbol {
                id: "sym1".to_string(),
                name: "AuthService".to_string(),
                kind: SymbolKind::Struct,
                language: "rust".to_string(),
                file_path: "src/auth.rs".to_string(),
                signature: Some("struct AuthService".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(50),
                code_pattern: "struct AuthService : BaseService implements IAuthService".to_string(),
                content: None,
            },
            Symbol {
                id: "sym2".to_string(),
                name: "AuthValidator".to_string(),
                kind: SymbolKind::Struct,
                language: "rust".to_string(),
                file_path: "src/auth_validator.rs".to_string(),
                signature: Some("struct AuthValidator".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(30),
                code_pattern: "struct AuthValidator for AuthService authentication".to_string(),
                content: None,
            },
            Symbol {
                id: "sym3".to_string(),
                name: "DatabasePool".to_string(),
                kind: SymbolKind::Struct,
                language: "rust".to_string(),
                file_path: "src/db.rs".to_string(),
                signature: Some("struct DatabasePool".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(40),
                code_pattern: "struct DatabasePool connection pooling".to_string(),
                content: None,
            },
        ];

        // sym1 vector is 0.1, sym2 is 0.9, sym3 is 0.1
        // Query vector is 0.1, so sym1 and sym3 are similar by vector
        let vectors = vec![
            vec![0.1f32; VECTOR_DIMENSION],  // sym1 - matches vector query
            vec![0.9f32; VECTOR_DIMENSION],  // sym2 - different from vector query
            vec![0.1f32; VECTOR_DIMENSION],  // sym3 - matches vector query
        ];

        engine.add_symbols(symbols, vectors).await.unwrap();

        // Create FTS index
        engine.create_fts_index().await.unwrap();

        // Search with text "AuthService" and vector [0.1, 0.1, ...]
        // sym1 should rank highest (matches both)
        let query_vector = vec![0.1f32; VECTOR_DIMENSION];
        let results = engine.search_hybrid("AuthService", &query_vector, 10).await.unwrap();

        assert!(!results.is_empty(), "Hybrid search should return results");

        // sym1 should be first because it matches BOTH text and vector
        assert_eq!(results[0].id, "sym1", "sym1 should rank highest (matches both text and vector)");

        // All scores should be normalized (0.0-1.0)
        for result in &results {
            assert!(result.score >= 0.0 && result.score <= 1.0,
                "Score {} should be normalized to 0.0-1.0", result.score);
        }

        // First result should have score 1.0 (max after normalization)
        assert!((results[0].score - 1.0).abs() < 0.001, "Top result should have normalized score 1.0");
    }

    #[tokio::test]
    async fn test_search_hybrid_empty_db() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Search on empty DB should return empty results
        let query_vector = vec![0.5f32; VECTOR_DIMENSION];
        let results = engine.search_hybrid("anything", &query_vector, 10).await.unwrap();

        assert!(results.is_empty(), "Hybrid search on empty DB should return empty results");
    }

    #[tokio::test]
    async fn test_search_hybrid_text_only_match() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Add symbols where text match is different from vector match
        let symbols = vec![
            Symbol {
                id: "sym1".to_string(),
                name: "UniqueTextMatch".to_string(),
                kind: SymbolKind::Function,
                language: "rust".to_string(),
                file_path: "src/unique.rs".to_string(),
                signature: Some("fn unique_text_match()".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(20),
                code_pattern: "fn UniqueTextMatch special_keyword_xyz".to_string(),
                content: None,
            },
            Symbol {
                id: "sym2".to_string(),
                name: "OtherFunction".to_string(),
                kind: SymbolKind::Function,
                language: "rust".to_string(),
                file_path: "src/other.rs".to_string(),
                signature: Some("fn other_function()".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(15),
                code_pattern: "fn other_function different content".to_string(),
                content: None,
            },
        ];

        // sym1 has dissimilar vector (0.9), sym2 has similar vector (0.1)
        // Text query "special_keyword_xyz" only matches sym1
        let vectors = vec![
            vec![0.9f32; VECTOR_DIMENSION],  // sym1 - dissimilar to query vector
            vec![0.1f32; VECTOR_DIMENSION],  // sym2 - similar to query vector
        ];

        engine.add_symbols(symbols, vectors).await.unwrap();

        // Create FTS index
        engine.create_fts_index().await.unwrap();

        // Search with text that only matches sym1, but vector that is closer to sym2
        let query_vector = vec![0.1f32; VECTOR_DIMENSION];
        let results = engine.search_hybrid("special_keyword_xyz", &query_vector, 10).await.unwrap();

        // sym1 should be found even though its vector is dissimilar,
        // because it matches the text query
        let ids: Vec<&str> = results.iter().map(|r| r.id.as_str()).collect();
        assert!(ids.contains(&"sym1"), "sym1 should be found via text match even with dissimilar vector");

        // Both should be found (sym1 via text, sym2 via vector)
        assert!(ids.contains(&"sym2"), "sym2 should be found via vector match");
    }

    #[tokio::test]
    async fn test_search_with_boosting() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Add a function and import with same name and same vector
        // The function should rank higher after boosting
        let symbols = vec![
            Symbol {
                id: "sym_function".to_string(),
                name: "authenticate".to_string(),
                kind: SymbolKind::Function,
                language: "rust".to_string(),
                file_path: "src/auth.rs".to_string(),
                signature: Some("fn authenticate()".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(20),
                code_pattern: "fn authenticate function authentication".to_string(),
                content: None,
            },
            Symbol {
                id: "sym_import".to_string(),
                name: "authenticate".to_string(),
                kind: SymbolKind::Import,
                language: "rust".to_string(),
                file_path: "src/main.rs".to_string(),
                signature: Some("use auth::authenticate".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(1),
                code_pattern: "use authenticate import".to_string(),
                content: None,
            },
        ];

        // Give both the same vector so vector similarity is identical
        let vectors = vec![
            vec![0.5f32; VECTOR_DIMENSION],
            vec![0.5f32; VECTOR_DIMENSION],
        ];

        engine.add_symbols(symbols, vectors).await.unwrap();

        // Create FTS index
        engine.create_fts_index().await.unwrap();

        // Search with boosting - function should rank higher than import
        let results = engine.search_text_boosted("authenticate", 10).await.unwrap();

        assert!(results.len() >= 2, "Should find at least 2 results");

        // Function should be first due to kind boost (1.5 vs 0.4)
        assert_eq!(
            results[0].id, "sym_function",
            "Function should rank higher than import after boosting"
        );
        assert_eq!(
            results[1].id, "sym_import",
            "Import should be second"
        );

        // Verify scores are normalized and sorted
        assert!(results[0].score >= results[1].score);
        assert!(results[0].score <= 1.0);
        assert!(results[1].score >= 0.0);
    }

    #[tokio::test]
    async fn test_search_vector_boosted_ranks_correctly() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Add a function and import with same name and same vector
        // The function should rank higher after boosting
        let symbols = vec![
            Symbol {
                id: "sym_function".to_string(),
                name: "authenticate".to_string(),
                kind: SymbolKind::Function,
                language: "rust".to_string(),
                file_path: "src/auth.rs".to_string(),
                signature: Some("fn authenticate()".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(20),
                code_pattern: "fn authenticate function authentication".to_string(),
                content: None,
            },
            Symbol {
                id: "sym_import".to_string(),
                name: "authenticate".to_string(),
                kind: SymbolKind::Import,
                language: "rust".to_string(),
                file_path: "src/main.rs".to_string(),
                signature: Some("use auth::authenticate".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(1),
                code_pattern: "use authenticate import".to_string(),
                content: None,
            },
        ];

        // Give both the same vector so vector similarity is identical
        let vectors = vec![
            vec![0.5f32; VECTOR_DIMENSION],
            vec![0.5f32; VECTOR_DIMENSION],
        ];

        engine.add_symbols(symbols, vectors).await.unwrap();

        // Search with vector boosting - function should rank higher than import
        let query_vector = vec![0.5f32; VECTOR_DIMENSION];
        let results = engine.search_vector_boosted("authenticate", &query_vector, 10).await.unwrap();

        assert!(results.len() >= 2, "Should find at least 2 results");

        // Function should be first due to kind boost (1.5 vs 0.4)
        assert_eq!(
            results[0].id, "sym_function",
            "Function should rank higher than import after boosting"
        );
        assert_eq!(
            results[1].id, "sym_import",
            "Import should be second"
        );

        // Verify scores are normalized and sorted
        assert!(results[0].score >= results[1].score);
        assert!(results[0].score <= 1.0);
        assert!(results[1].score >= 0.0);
    }

    #[tokio::test]
    async fn test_search_hybrid_boosted_ranks_correctly() {
        let temp_dir = TempDir::new().unwrap();
        let db_path = temp_dir.path().join("test.lance");

        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Add a function and import with same name and same vector
        // The function should rank higher after boosting
        let symbols = vec![
            Symbol {
                id: "sym_function".to_string(),
                name: "authenticate".to_string(),
                kind: SymbolKind::Function,
                language: "rust".to_string(),
                file_path: "src/auth.rs".to_string(),
                signature: Some("fn authenticate()".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(20),
                code_pattern: "fn authenticate function authentication".to_string(),
                content: None,
            },
            Symbol {
                id: "sym_import".to_string(),
                name: "authenticate".to_string(),
                kind: SymbolKind::Import,
                language: "rust".to_string(),
                file_path: "src/main.rs".to_string(),
                signature: Some("use auth::authenticate".to_string()),
                doc_comment: None,
                start_line: Some(1),
                end_line: Some(1),
                code_pattern: "use authenticate import".to_string(),
                content: None,
            },
        ];

        // Give both the same vector so vector similarity is identical
        let vectors = vec![
            vec![0.5f32; VECTOR_DIMENSION],
            vec![0.5f32; VECTOR_DIMENSION],
        ];

        engine.add_symbols(symbols, vectors).await.unwrap();

        // Create FTS index
        engine.create_fts_index().await.unwrap();

        // Search with hybrid boosting - function should rank higher than import
        let query_vector = vec![0.5f32; VECTOR_DIMENSION];
        let results = engine.search_hybrid_boosted("authenticate", &query_vector, 10).await.unwrap();

        assert!(results.len() >= 2, "Should find at least 2 results");

        // Function should be first due to kind boost (1.5 vs 0.4)
        assert_eq!(
            results[0].id, "sym_function",
            "Function should rank higher than import after boosting"
        );
        assert_eq!(
            results[1].id, "sym_import",
            "Import should be second"
        );

        // Verify scores are normalized and sorted
        assert!(results[0].score >= results[1].score);
        assert!(results[0].score <= 1.0);
        assert!(results[1].score >= 0.0);
    }
}
