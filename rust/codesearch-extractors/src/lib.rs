//! Tree-sitter based symbol extraction for codesearch.

pub mod base;
pub mod language;
pub mod rust;
pub mod typescript;
pub mod utils;

pub use base::{
    BaseExtractor, ContextConfig, ExtractionResults, Identifier, IdentifierKind,
    PendingRelationship, Relationship, RelationshipKind, Symbol, SymbolKind,
    SymbolOptions, TypeInfo, Visibility,
};
pub use language::{detect_language, get_tree_sitter_language};
pub use rust::RustExtractor;
pub use typescript::TypeScriptExtractor;
