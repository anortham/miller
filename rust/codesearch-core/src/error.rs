use thiserror::Error;

#[derive(Error, Debug)]
pub enum Error {
    #[error("LanceDB error: {0}")]
    LanceDb(String),

    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),

    #[error("Serialization error: {0}")]
    Serialization(#[from] serde_json::Error),
}

impl From<lancedb::Error> for Error {
    fn from(e: lancedb::Error) -> Self {
        Error::LanceDb(e.to_string())
    }
}
