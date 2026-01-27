//! Codesearch core library - search engine powered by LanceDB

pub mod engine;
pub mod error;

pub use engine::CodeEngine;
pub use error::Error;

pub type Result<T> = std::result::Result<T, Error>;
