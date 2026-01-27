//! Core search engine wrapping LanceDB

use crate::schema::{symbols_schema, Symbol, TABLE_NAME, VECTOR_DIMENSION};
use crate::search::{distance_to_score, SearchResult};
use crate::{Error, Result};
use arrow::array::{Array, ArrayRef, FixedSizeListArray, Float32Array, Int32Array, StringBuilder};
use arrow_array::{RecordBatch, RecordBatchIterator, StringArray};
use futures::TryStreamExt;
use lancedb::query::{ExecutableQuery, QueryBase};
use std::path::Path;
use std::sync::Arc;

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

    /// Convert a RecordBatch from vector search to SearchResults
    fn record_batch_to_search_results(&self, batch: &RecordBatch) -> Result<Vec<SearchResult>> {
        let num_rows = batch.num_rows();
        let mut results = Vec::with_capacity(num_rows);

        // Extract required columns using helper functions
        let id_array = Self::get_required_string_column(batch, "id")?;
        let name_array = Self::get_required_string_column(batch, "name")?;
        let kind_array = Self::get_required_string_column(batch, "kind")?;
        let language_array = Self::get_required_string_column(batch, "language")?;
        let file_path_array = Self::get_required_string_column(batch, "file_path")?;
        let distance_array = Self::get_required_float_column(batch, "_distance")?;

        // Extract optional columns using helper functions
        let signature_array = Self::get_optional_string_column(batch, "signature");
        let doc_comment_array = Self::get_optional_string_column(batch, "doc_comment");
        let start_line_array = Self::get_optional_int_column(batch, "start_line");
        let end_line_array = Self::get_optional_int_column(batch, "end_line");

        for i in 0..num_rows {
            let distance = distance_array.value(i);
            let score = distance_to_score(distance);

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
}
