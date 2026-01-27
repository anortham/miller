//! Core search engine wrapping LanceDB

use crate::schema::{symbols_schema, Symbol, TABLE_NAME, VECTOR_DIMENSION};
use crate::{Error, Result};
use arrow::array::{ArrayRef, FixedSizeListArray, Float32Array, Int32Array, StringBuilder};
use arrow_array::{RecordBatch, RecordBatchIterator};
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
}
