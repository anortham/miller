//! Codesearch core library - search engine powered by LanceDB

pub mod boosting;
pub mod engine;
pub mod error;
pub mod indexer;
pub mod schema;
pub mod search;

pub use boosting::{apply_boosts, boost_by_field_match, boost_by_kind, boost_by_position};
pub use engine::{CodeEngine, IdentifierInput, ReachabilityEntry, ReferenceResult, RelationshipInput, RelationshipResult, SymbolInfo};
pub use error::Error;
pub use schema::{Symbol, SymbolKind, VECTOR_DIMENSION};
pub use search::SearchResult;

pub type Result<T> = std::result::Result<T, Error>;
