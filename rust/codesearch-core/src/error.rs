use thiserror::Error;

#[derive(Error, Debug)]
pub enum Error {
    #[error("LanceDB error: {0}")]
    LanceDb(String),

    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),

    #[error("Serialization error: {0}")]
    Serialization(#[from] serde_json::Error),

    #[error("Validation error: {0}")]
    Validation(String),

    #[error("Arrow error: {0}")]
    Arrow(String),
}

impl From<lancedb::Error> for Error {
    fn from(e: lancedb::Error) -> Self {
        Error::LanceDb(e.to_string())
    }
}

impl From<arrow::error::ArrowError> for Error {
    fn from(e: arrow::error::ArrowError) -> Self {
        Error::Arrow(e.to_string())
    }
}
