//! LanceDB schema definitions for code symbols

use arrow::datatypes::{DataType, Field, Schema};
use serde::{Deserialize, Serialize};
use std::sync::Arc;

/// Symbol kinds (matches julie-extractors)
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum SymbolKind {
    Function,
    Method,
    Class,
    Interface,
    Struct,
    Enum,
    EnumMember,
    Trait,
    Type,
    Module,
    Namespace,
    Variable,
    Constant,
    Property,
    Field,
    Constructor,
    Import,
    Export,
    File,
    Checkpoint,
    Plan,
    Decision,
    Learning,
}

impl std::fmt::Display for SymbolKind {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let s = serde_json::to_string(self).unwrap_or_default();
        write!(f, "{}", s.trim_matches('"'))
    }
}

/// A searchable symbol (code or memory)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Symbol {
    pub id: String,
    pub name: String,
    pub kind: SymbolKind,
    pub language: String,
    pub file_path: String,
    pub signature: Option<String>,
    pub doc_comment: Option<String>,
    pub start_line: Option<i32>,
    pub end_line: Option<i32>,
    pub code_pattern: String,
    pub content: Option<String>,
    // Vector will be added separately during insertion
}

impl Symbol {
    /// Generate the code_pattern field for FTS indexing
    pub fn generate_code_pattern(&self) -> String {
        let mut parts = Vec::new();
        if let Some(ref sig) = self.signature {
            parts.push(sig.clone());
        }
        parts.push(self.name.clone());
        parts.push(self.kind.to_string());
        parts.join(" ")
    }
}

/// Vector dimension for embeddings (nomic-embed-text-v1.5)
pub const VECTOR_DIMENSION: usize = 768;

/// Create the Arrow schema for the symbols table
pub fn symbols_schema() -> Arc<Schema> {
    Arc::new(Schema::new(vec![
        Field::new("id", DataType::Utf8, false),
        Field::new("name", DataType::Utf8, false),
        Field::new("kind", DataType::Utf8, false),
        Field::new("language", DataType::Utf8, false),
        Field::new("file_path", DataType::Utf8, false),
        Field::new("signature", DataType::Utf8, true),
        Field::new("doc_comment", DataType::Utf8, true),
        Field::new("start_line", DataType::Int32, true),
        Field::new("end_line", DataType::Int32, true),
        Field::new("code_pattern", DataType::Utf8, false),
        Field::new("content", DataType::Utf8, true),
        Field::new(
            "vector",
            DataType::FixedSizeList(
                Arc::new(Field::new("item", DataType::Float32, false)),
                VECTOR_DIMENSION as i32,
            ),
            false,
        ),
    ]))
}

pub const TABLE_NAME: &str = "symbols";

/// Table name for relationships
pub const RELATIONSHIPS_TABLE_NAME: &str = "relationships";

/// Create the Arrow schema for the relationships table
pub fn relationships_schema() -> Arc<Schema> {
    Arc::new(Schema::new(vec![
        Field::new("from_symbol_id", DataType::Utf8, false),
        Field::new("to_symbol_id", DataType::Utf8, false),
        Field::new("kind", DataType::Utf8, false),
        Field::new("file_path", DataType::Utf8, false),
        Field::new("line_number", DataType::UInt32, false),
        Field::new("confidence", DataType::Float32, false),
    ]))
}

/// Table name for identifiers
pub const IDENTIFIERS_TABLE_NAME: &str = "identifiers";

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

/// Table name for reachability (transitive closure)
pub const REACHABILITY_TABLE_NAME: &str = "reachability";

/// Schema for the reachability table (transitive closure)
pub fn reachability_schema() -> Arc<Schema> {
    Arc::new(Schema::new(vec![
        Field::new("source_id", DataType::Utf8, false),
        Field::new("target_id", DataType::Utf8, false),
        Field::new("min_distance", DataType::UInt32, false),
    ]))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_symbol_kind_display() {
        assert_eq!(SymbolKind::Function.to_string(), "function");
        assert_eq!(SymbolKind::EnumMember.to_string(), "enum_member");
    }

    #[test]
    fn test_generate_code_pattern() {
        let symbol = Symbol {
            id: "test".into(),
            name: "authenticate".into(),
            kind: SymbolKind::Function,
            language: "rust".into(),
            file_path: "src/auth.rs".into(),
            signature: Some("fn authenticate(token: &str) -> Result<User>".into()),
            doc_comment: None,
            start_line: Some(10),
            end_line: Some(20),
            code_pattern: String::new(),
            content: None,
        };

        let pattern = symbol.generate_code_pattern();
        assert!(pattern.contains("authenticate"));
        assert!(pattern.contains("function"));
        assert!(pattern.contains("fn authenticate"));
    }

    #[test]
    fn test_schema_has_correct_fields() {
        let schema = symbols_schema();
        assert_eq!(schema.fields().len(), 12);
        assert!(schema.field_with_name("id").is_ok());
        assert!(schema.field_with_name("vector").is_ok());
    }

    #[test]
    fn test_relationships_schema_has_correct_fields() {
        let schema = relationships_schema();
        assert_eq!(schema.fields().len(), 6);
        assert!(schema.field_with_name("from_symbol_id").is_ok());
        assert!(schema.field_with_name("to_symbol_id").is_ok());
        assert!(schema.field_with_name("kind").is_ok());
        assert!(schema.field_with_name("confidence").is_ok());
    }

    #[test]
    fn test_identifiers_schema_has_correct_fields() {
        let schema = identifiers_schema();
        assert_eq!(schema.fields().len(), 7);
        assert!(schema.field_with_name("name").is_ok());
        assert!(schema.field_with_name("kind").is_ok());
        assert!(schema.field_with_name("file_path").is_ok());
        assert!(schema.field_with_name("line_number").is_ok());
        assert!(schema.field_with_name("column").is_ok());
        assert!(schema.field_with_name("source_symbol_id").is_ok());
        assert!(schema.field_with_name("target_symbol_id").is_ok());
    }

    #[test]
    fn test_reachability_schema_has_correct_fields() {
        let schema = reachability_schema();
        assert_eq!(schema.fields().len(), 3);
        assert!(schema.field_with_name("source_id").is_ok());
        assert!(schema.field_with_name("target_id").is_ok());
        assert!(schema.field_with_name("min_distance").is_ok());
    }
}
