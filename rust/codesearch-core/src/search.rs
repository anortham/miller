//! Search result types and helpers

use serde::{Deserialize, Serialize};

/// A search result from vector or hybrid search
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SearchResult {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub language: String,
    pub file_path: String,
    pub signature: Option<String>,
    pub doc_comment: Option<String>,
    pub start_line: Option<i32>,
    pub end_line: Option<i32>,
    pub score: f32,
}

/// Convert L2 distance to similarity score (0.0-1.0)
///
/// L2 distance ranges from 0 (identical) to potentially large values.
/// For normalized vectors, max L2 distance is 2.0 (opposite vectors).
/// This converts to a similarity score where 1.0 = identical, 0.0 = very different.
pub fn distance_to_score(distance: f32) -> f32 {
    (1.0 - (distance / 2.0)).clamp(0.0, 1.0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_distance_to_score_identical() {
        // Distance 0 = identical vectors = score 1.0
        assert_eq!(distance_to_score(0.0), 1.0);
    }

    #[test]
    fn test_distance_to_score_opposite() {
        // Distance 2 = opposite vectors (normalized) = score 0.0
        assert_eq!(distance_to_score(2.0), 0.0);
    }

    #[test]
    fn test_distance_to_score_middle() {
        // Distance 1 = orthogonal = score 0.5
        assert_eq!(distance_to_score(1.0), 0.5);
    }

    #[test]
    fn test_distance_to_score_clamps_large_distance() {
        // Large distance clamps to 0.0
        assert_eq!(distance_to_score(3.0), 0.0);
    }

    #[test]
    fn test_search_result_serialization() {
        let result = SearchResult {
            id: "test_id".to_string(),
            name: "foo".to_string(),
            kind: "function".to_string(),
            language: "rust".to_string(),
            file_path: "src/lib.rs".to_string(),
            signature: Some("fn foo()".to_string()),
            doc_comment: None,
            start_line: Some(10),
            end_line: Some(20),
            score: 0.95,
        };

        let json = serde_json::to_string(&result).unwrap();
        let parsed: SearchResult = serde_json::from_str(&json).unwrap();

        assert_eq!(parsed.id, "test_id");
        assert_eq!(parsed.score, 0.95);
    }
}
