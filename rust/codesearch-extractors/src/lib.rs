//! Tree-sitter based symbol extraction for codesearch.

pub mod base;
pub mod language;

pub use base::{
    ExtractionResults, Identifier, IdentifierKind, PendingRelationship,
    Relationship, RelationshipKind, Symbol, SymbolKind, TypeInfo, Visibility,
};
pub use language::{detect_language, get_tree_sitter_language};
