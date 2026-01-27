//! Base infrastructure for symbol extraction.

mod types;
mod extractor;
mod creation_methods;
mod tree_methods;

pub use types::{
    ContextConfig, ExtractionResults, Identifier, IdentifierKind, PendingRelationship,
    Relationship, RelationshipKind, Symbol, SymbolKind, SymbolOptions, TypeInfo, Visibility,
};
pub use extractor::BaseExtractor;
