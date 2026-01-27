//! Codesearch core library - search engine powered by LanceDB

pub mod engine;
pub mod error;
pub mod schema;

pub use engine::CodeEngine;
pub use error::Error;
pub use schema::{Symbol, SymbolKind, VECTOR_DIMENSION};

pub type Result<T> = std::result::Result<T, Error>;
