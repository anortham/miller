# Phase 2: Core Search Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add vector search, full-text search, and hybrid search capabilities to the codesearch engine.

**Architecture:** LanceDB handles both vector and FTS indexing. Rust engine exposes search methods, .NET provides embeddings via ONNX Runtime. Score boosting applied post-retrieval before returning results.

**Tech Stack:** LanceDB (vector + Tantivy FTS), ONNX Runtime (embeddings), UniFFI (FFI bridge)

---

## Prerequisites

Phase 1 complete with:
- `CodeEngine` with `add_symbols()` and `symbol_count()`
- 768-dim vector storage working
- UniFFI bindings functional
- All tests passing

---

### Task 1: Vector Search in Rust

**Files:**
- Create: `rust/codesearch-core/src/search.rs`
- Modify: `rust/codesearch-core/src/lib.rs`
- Modify: `rust/codesearch-core/src/engine.rs`

**Step 1: Write the failing test**

Add to `rust/codesearch-core/src/engine.rs`:

```rust
#[cfg(test)]
mod search_tests {
    use super::*;
    use crate::schema::SymbolKind;

    fn create_test_symbol(name: &str, kind: SymbolKind) -> Symbol {
        Symbol {
            id: format!("test_{}", name),
            name: name.to_string(),
            kind,
            language: "rust".to_string(),
            file_path: "test.rs".to_string(),
            signature: Some(format!("fn {}()", name)),
            doc_comment: None,
            start_line: Some(1),
            end_line: Some(10),
            code_pattern: format!("{} function", name),
            content: None,
        }
    }

    // 768-dim vector with specific pattern for testing similarity
    fn create_test_vector(seed: f32) -> Vec<f32> {
        let mut v: Vec<f32> = (0..768).map(|i| (seed + i as f32 * 0.001).sin()).collect();
        // L2 normalize
        let norm: f32 = v.iter().map(|x| x * x).sum::<f32>().sqrt();
        v.iter_mut().for_each(|x| *x /= norm);
        v
    }

    #[tokio::test]
    async fn test_search_vector_basic() {
        let dir = tempfile::tempdir().unwrap();
        let db_path = dir.path().join("test.lance");
        let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

        // Add symbols with different vectors
        let symbols = vec![
            create_test_symbol("authenticate", SymbolKind::Function),
            create_test_symbol("validate", SymbolKind::Function),
            create_test_symbol("process", SymbolKind::Function),
        ];
        let vectors = vec![
            create_test_vector(1.0),  // seed 1.0
            create_test_vector(1.1),  // similar to 1.0
            create_test_vector(5.0),  // different
        ];
        engine.add_symbols(symbols, vectors).await.unwrap();

        // Search with vector similar to "authenticate"
        let query_vec = create_test_vector(1.0);
        let results = engine.search_vector(&query_vec, 2).await.unwrap();

        assert_eq!(results.len(), 2);
        // authenticate should be first (exact match)
        assert_eq!(results[0].name, "authenticate");
        // validate should be second (similar vector)
        assert_eq!(results[1].name, "validate");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core test_search_vector_basic`
Expected: FAIL with "no method named `search_vector`"

**Step 3: Create search module with SearchResult type**

Create `rust/codesearch-core/src/search.rs`:

```rust
use serde::{Deserialize, Serialize};

/// Result from a search operation
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

impl SearchResult {
    /// Convert L2 distance to similarity score (0.0 to 1.0)
    /// For L2-normalized vectors, distance ≈ 2*(1 - cosine_similarity)
    pub fn distance_to_score(distance: f32) -> f32 {
        (1.0 - (distance / 2.0)).max(0.0).min(1.0)
    }
}
```

**Step 4: Update lib.rs to export search module**

Add to `rust/codesearch-core/src/lib.rs`:

```rust
pub mod search;

pub use search::SearchResult;
```

**Step 5: Implement search_vector in engine**

Add to `rust/codesearch-core/src/engine.rs`:

```rust
use crate::search::SearchResult;
use lancedb::query::ExecutableQuery;

impl CodeEngine {
    /// Search for symbols by vector similarity
    /// Returns results sorted by similarity (highest first)
    pub async fn search_vector(&self, query_vector: &[f32], limit: usize) -> Result<Vec<SearchResult>> {
        if query_vector.len() != VECTOR_DIMENSION {
            return Err(Error::Validation(format!(
                "Query vector must have {} dimensions, got {}",
                VECTOR_DIMENSION,
                query_vector.len()
            )));
        }

        let table = match self.db.open_table(TABLE_NAME).execute().await {
            Ok(t) => t,
            Err(lancedb::Error::TableNotFound { .. }) => {
                return Ok(Vec::new()); // No data yet
            }
            Err(e) => return Err(e.into()),
        };

        let results = table
            .vector_search(query_vector.to_vec())
            .map_err(|e| Error::LanceDb(e.to_string()))?
            .limit(limit)
            .execute()
            .await?;

        let batches: Vec<_> = results.try_collect().await?;

        let mut search_results = Vec::new();
        for batch in batches {
            search_results.extend(self.batch_to_search_results(&batch)?);
        }

        Ok(search_results)
    }

    fn batch_to_search_results(&self, batch: &RecordBatch) -> Result<Vec<SearchResult>> {
        use arrow::array::{Float32Array, Int32Array, StringArray};

        let ids = batch.column_by_name("id")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
            .ok_or_else(|| Error::Arrow("Missing id column".to_string()))?;
        let names = batch.column_by_name("name")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
            .ok_or_else(|| Error::Arrow("Missing name column".to_string()))?;
        let kinds = batch.column_by_name("kind")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
            .ok_or_else(|| Error::Arrow("Missing kind column".to_string()))?;
        let languages = batch.column_by_name("language")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
            .ok_or_else(|| Error::Arrow("Missing language column".to_string()))?;
        let file_paths = batch.column_by_name("file_path")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
            .ok_or_else(|| Error::Arrow("Missing file_path column".to_string()))?;
        let signatures = batch.column_by_name("signature")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>());
        let doc_comments = batch.column_by_name("doc_comment")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>());
        let start_lines = batch.column_by_name("start_line")
            .and_then(|c| c.as_any().downcast_ref::<Int32Array>());
        let end_lines = batch.column_by_name("end_line")
            .and_then(|c| c.as_any().downcast_ref::<Int32Array>());
        let distances = batch.column_by_name("_distance")
            .and_then(|c| c.as_any().downcast_ref::<Float32Array>());

        let mut results = Vec::new();
        for i in 0..batch.num_rows() {
            let distance = distances.map(|d| d.value(i)).unwrap_or(0.0);
            results.push(SearchResult {
                id: ids.value(i).to_string(),
                name: names.value(i).to_string(),
                kind: kinds.value(i).to_string(),
                language: languages.value(i).to_string(),
                file_path: file_paths.value(i).to_string(),
                signature: signatures.and_then(|s| {
                    if s.is_null(i) { None } else { Some(s.value(i).to_string()) }
                }),
                doc_comment: doc_comments.and_then(|s| {
                    if s.is_null(i) { None } else { Some(s.value(i).to_string()) }
                }),
                start_line: start_lines.and_then(|a| {
                    if a.is_null(i) { None } else { Some(a.value(i)) }
                }),
                end_line: end_lines.and_then(|a| {
                    if a.is_null(i) { None } else { Some(a.value(i)) }
                }),
                score: SearchResult::distance_to_score(distance),
            });
        }
        Ok(results)
    }
}
```

Add the import at the top of engine.rs:
```rust
use futures::TryStreamExt;
```

And add `futures` to Cargo.toml workspace dependencies:
```toml
futures = "0.3"
```

**Step 6: Run test to verify it passes**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core test_search_vector_basic`
Expected: PASS

**Step 7: Add more vector search tests**

Add to `rust/codesearch-core/src/engine.rs`:

```rust
#[tokio::test]
async fn test_search_vector_empty_db() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("test.lance");
    let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

    let query_vec = create_test_vector(1.0);
    let results = engine.search_vector(&query_vec, 10).await.unwrap();

    assert!(results.is_empty());
}

#[tokio::test]
async fn test_search_vector_validates_dimensions() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("test.lance");
    let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

    let bad_vector = vec![0.1; 100]; // Wrong dimensions
    let result = engine.search_vector(&bad_vector, 10).await;

    assert!(result.is_err());
    assert!(result.unwrap_err().to_string().contains("768 dimensions"));
}
```

**Step 8: Run all tests**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core`
Expected: All tests pass

**Step 9: Commit**

```bash
git add rust/codesearch-core/src/search.rs rust/codesearch-core/src/engine.rs rust/codesearch-core/src/lib.rs rust/Cargo.toml
git commit -m "feat(search): add vector search to engine"
```

---

### Task 2: FTS Index Configuration

**Files:**
- Modify: `rust/codesearch-core/src/engine.rs`
- Modify: `rust/codesearch-core/Cargo.toml`

**Step 1: Write the failing test**

Add to `rust/codesearch-core/src/engine.rs`:

```rust
#[tokio::test]
async fn test_search_text_basic() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("test.lance");
    let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

    let symbols = vec![
        create_test_symbol("authenticate_user", SymbolKind::Function),
        create_test_symbol("validate_token", SymbolKind::Function),
        create_test_symbol("process_request", SymbolKind::Function),
    ];
    let vectors: Vec<_> = (0..3).map(|i| create_test_vector(i as f32)).collect();
    engine.add_symbols(symbols, vectors).await.unwrap();

    // Create FTS index
    engine.create_fts_index().await.unwrap();

    // Search for "authenticate"
    let results = engine.search_text("authenticate", 10).await.unwrap();

    assert!(!results.is_empty());
    assert_eq!(results[0].name, "authenticate_user");
}
```

**Step 2: Run test to verify it fails**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core test_search_text_basic`
Expected: FAIL with "no method named `create_fts_index`"

**Step 3: Implement FTS index creation**

Add to `rust/codesearch-core/src/engine.rs`:

```rust
impl CodeEngine {
    /// Create full-text search index on code_pattern and content fields
    /// Uses whitespace tokenizer to preserve code patterns like `: BaseClass`
    pub async fn create_fts_index(&self) -> Result<()> {
        let table = self.db.open_table(TABLE_NAME).execute().await?;

        // LanceDB FTS uses Tantivy under the hood
        // Use whitespace tokenizer to preserve special characters
        table
            .create_index(
                &["code_pattern", "name"],
                lancedb::index::Index::FTS(
                    lancedb::index::vector::FtsIndexBuilder::default()
                        .with_tokenizer(lancedb::index::scalar::fts::FtsTokenizerType::Whitespace)
                ),
            )
            .execute()
            .await?;

        Ok(())
    }
}
```

**Step 4: Run test to verify it still fails (need search_text)**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core test_search_text_basic`
Expected: FAIL with "no method named `search_text`"

**Step 5: Implement search_text**

Add to `rust/codesearch-core/src/engine.rs`:

```rust
impl CodeEngine {
    /// Search for symbols by text using full-text search (BM25)
    /// Returns results sorted by relevance (highest first)
    pub async fn search_text(&self, query: &str, limit: usize) -> Result<Vec<SearchResult>> {
        let table = match self.db.open_table(TABLE_NAME).execute().await {
            Ok(t) => t,
            Err(lancedb::Error::TableNotFound { .. }) => {
                return Ok(Vec::new());
            }
            Err(e) => return Err(e.into()),
        };

        // Over-fetch to allow for score boosting later (3x limit)
        let fetch_limit = (limit * 3).max(50);

        let results = table
            .query()
            .full_text_search(lancedb::query::FullTextSearchQuery::new(query.to_string()))
            .limit(fetch_limit)
            .execute()
            .await?;

        let batches: Vec<_> = results.try_collect().await?;

        let mut search_results = Vec::new();
        for batch in batches {
            search_results.extend(self.batch_to_search_results_fts(&batch)?);
        }

        // Normalize scores
        if let Some(max_score) = search_results.iter().map(|r| r.score).max_by(|a, b| a.partial_cmp(b).unwrap()) {
            if max_score > 0.0 {
                for result in &mut search_results {
                    result.score /= max_score;
                }
            }
        }

        // Truncate to requested limit
        search_results.truncate(limit);
        Ok(search_results)
    }

    fn batch_to_search_results_fts(&self, batch: &RecordBatch) -> Result<Vec<SearchResult>> {
        use arrow::array::{Float32Array, Int32Array, StringArray};

        let ids = batch.column_by_name("id")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
            .ok_or_else(|| Error::Arrow("Missing id column".to_string()))?;
        let names = batch.column_by_name("name")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
            .ok_or_else(|| Error::Arrow("Missing name column".to_string()))?;
        let kinds = batch.column_by_name("kind")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
            .ok_or_else(|| Error::Arrow("Missing kind column".to_string()))?;
        let languages = batch.column_by_name("language")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
            .ok_or_else(|| Error::Arrow("Missing language column".to_string()))?;
        let file_paths = batch.column_by_name("file_path")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>())
            .ok_or_else(|| Error::Arrow("Missing file_path column".to_string()))?;
        let signatures = batch.column_by_name("signature")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>());
        let doc_comments = batch.column_by_name("doc_comment")
            .and_then(|c| c.as_any().downcast_ref::<StringArray>());
        let start_lines = batch.column_by_name("start_line")
            .and_then(|c| c.as_any().downcast_ref::<Int32Array>());
        let end_lines = batch.column_by_name("end_line")
            .and_then(|c| c.as_any().downcast_ref::<Int32Array>());
        // FTS returns _score (BM25 score), not _distance
        let scores = batch.column_by_name("_score")
            .and_then(|c| c.as_any().downcast_ref::<Float32Array>());

        let mut results = Vec::new();
        for i in 0..batch.num_rows() {
            results.push(SearchResult {
                id: ids.value(i).to_string(),
                name: names.value(i).to_string(),
                kind: kinds.value(i).to_string(),
                language: languages.value(i).to_string(),
                file_path: file_paths.value(i).to_string(),
                signature: signatures.and_then(|s| {
                    if s.is_null(i) { None } else { Some(s.value(i).to_string()) }
                }),
                doc_comment: doc_comments.and_then(|s| {
                    if s.is_null(i) { None } else { Some(s.value(i).to_string()) }
                }),
                start_line: start_lines.and_then(|a| {
                    if a.is_null(i) { None } else { Some(a.value(i)) }
                }),
                end_line: end_lines.and_then(|a| {
                    if a.is_null(i) { None } else { Some(a.value(i)) }
                }),
                score: scores.map(|s| s.value(i)).unwrap_or(1.0),
            });
        }
        Ok(results)
    }
}
```

**Step 6: Run test to verify it passes**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core test_search_text_basic`
Expected: PASS

**Step 7: Add more FTS tests**

```rust
#[tokio::test]
async fn test_search_text_empty_db() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("test.lance");
    let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

    let results = engine.search_text("anything", 10).await.unwrap();
    assert!(results.is_empty());
}

#[tokio::test]
async fn test_search_text_no_match() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("test.lance");
    let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

    let symbols = vec![create_test_symbol("foo", SymbolKind::Function)];
    let vectors = vec![create_test_vector(1.0)];
    engine.add_symbols(symbols, vectors).await.unwrap();
    engine.create_fts_index().await.unwrap();

    let results = engine.search_text("nonexistent", 10).await.unwrap();
    assert!(results.is_empty());
}
```

**Step 8: Run all tests**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core`
Expected: All tests pass

**Step 9: Commit**

```bash
git add rust/codesearch-core/src/engine.rs
git commit -m "feat(search): add full-text search with Tantivy"
```

---

### Task 3: Hybrid Search with RRF

**Files:**
- Modify: `rust/codesearch-core/src/engine.rs`
- Modify: `rust/codesearch-core/src/search.rs`

**Step 1: Write the failing test**

Add to `rust/codesearch-core/src/engine.rs`:

```rust
#[tokio::test]
async fn test_search_hybrid_basic() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("test.lance");
    let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

    let symbols = vec![
        create_test_symbol("authenticate_user", SymbolKind::Function),
        create_test_symbol("validate_token", SymbolKind::Function),
        create_test_symbol("process_request", SymbolKind::Function),
    ];
    // authenticate and validate have similar vectors
    let vectors = vec![
        create_test_vector(1.0),
        create_test_vector(1.1),
        create_test_vector(5.0),
    ];
    engine.add_symbols(symbols, vectors).await.unwrap();
    engine.create_fts_index().await.unwrap();

    // Hybrid search should find authenticate_user via both text AND vector
    let query_vec = create_test_vector(1.0);
    let results = engine.search_hybrid("authenticate", &query_vec, 10).await.unwrap();

    assert!(!results.is_empty());
    // authenticate_user should rank highest (matches both)
    assert_eq!(results[0].name, "authenticate_user");
}
```

**Step 2: Run test to verify it fails**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core test_search_hybrid_basic`
Expected: FAIL with "no method named `search_hybrid`"

**Step 3: Implement hybrid search with manual RRF**

Add to `rust/codesearch-core/src/engine.rs`:

```rust
use std::collections::HashMap;

impl CodeEngine {
    /// Hybrid search combining FTS and vector search using Reciprocal Rank Fusion
    /// RRF score = Σ 1/(k + rank_i) where k=60
    pub async fn search_hybrid(
        &self,
        query_text: &str,
        query_vector: &[f32],
        limit: usize,
    ) -> Result<Vec<SearchResult>> {
        // Over-fetch from both sources
        let fetch_limit = (limit * 2).max(30);

        // Run both searches
        let text_results = self.search_text(query_text, fetch_limit).await?;
        let vector_results = self.search_vector(query_vector, fetch_limit).await?;

        // Apply RRF
        let mut rrf_scores: HashMap<String, (f32, SearchResult)> = HashMap::new();
        const K: f32 = 60.0;

        // Score from text search
        for (rank, result) in text_results.into_iter().enumerate() {
            let rrf_score = 1.0 / (K + rank as f32 + 1.0);
            rrf_scores
                .entry(result.id.clone())
                .and_modify(|(score, _)| *score += rrf_score)
                .or_insert((rrf_score, result));
        }

        // Score from vector search
        for (rank, result) in vector_results.into_iter().enumerate() {
            let rrf_score = 1.0 / (K + rank as f32 + 1.0);
            rrf_scores
                .entry(result.id.clone())
                .and_modify(|(score, _)| *score += rrf_score)
                .or_insert((rrf_score, result));
        }

        // Collect and sort by RRF score
        let mut results: Vec<_> = rrf_scores
            .into_values()
            .map(|(rrf_score, mut result)| {
                result.score = rrf_score;
                result
            })
            .collect();

        results.sort_by(|a, b| b.score.partial_cmp(&a.score).unwrap_or(std::cmp::Ordering::Equal));

        // Normalize scores
        if let Some(max_score) = results.first().map(|r| r.score) {
            if max_score > 0.0 {
                for result in &mut results {
                    result.score /= max_score;
                }
            }
        }

        results.truncate(limit);
        Ok(results)
    }
}
```

**Step 4: Run test to verify it passes**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core test_search_hybrid_basic`
Expected: PASS

**Step 5: Add edge case tests**

```rust
#[tokio::test]
async fn test_search_hybrid_empty_db() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("test.lance");
    let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

    let query_vec = create_test_vector(1.0);
    let results = engine.search_hybrid("test", &query_vec, 10).await.unwrap();
    assert!(results.is_empty());
}

#[tokio::test]
async fn test_search_hybrid_text_only_match() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("test.lance");
    let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

    let symbols = vec![create_test_symbol("unique_name", SymbolKind::Function)];
    let vectors = vec![create_test_vector(1.0)];
    engine.add_symbols(symbols, vectors).await.unwrap();
    engine.create_fts_index().await.unwrap();

    // Search with text match but very different vector
    let query_vec = create_test_vector(100.0);
    let results = engine.search_hybrid("unique_name", &query_vec, 10).await.unwrap();

    // Should still find it via text
    assert_eq!(results.len(), 1);
    assert_eq!(results[0].name, "unique_name");
}
```

**Step 6: Run all tests**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core`
Expected: All tests pass

**Step 7: Commit**

```bash
git add rust/codesearch-core/src/engine.rs
git commit -m "feat(search): add hybrid search with RRF fusion"
```

---

### Task 4: Score Boosting

**Files:**
- Create: `rust/codesearch-core/src/boosting.rs`
- Modify: `rust/codesearch-core/src/lib.rs`
- Modify: `rust/codesearch-core/src/engine.rs`

**Step 1: Write the failing test**

Add to `rust/codesearch-core/src/engine.rs`:

```rust
#[tokio::test]
async fn test_search_with_boosting() {
    let dir = tempfile::tempdir().unwrap();
    let db_path = dir.path().join("test.lance");
    let engine = CodeEngine::new(db_path.to_str().unwrap()).await.unwrap();

    // Create symbols with different kinds
    let symbols = vec![
        Symbol {
            id: "1".to_string(),
            name: "auth".to_string(),
            kind: SymbolKind::Import, // Should be deboosted
            language: "rust".to_string(),
            file_path: "test.rs".to_string(),
            signature: None,
            doc_comment: None,
            start_line: Some(1),
            end_line: Some(1),
            code_pattern: "auth import".to_string(),
            content: None,
        },
        Symbol {
            id: "2".to_string(),
            name: "auth".to_string(),
            kind: SymbolKind::Function, // Should be boosted
            language: "rust".to_string(),
            file_path: "test.rs".to_string(),
            signature: Some("fn auth()".to_string()),
            doc_comment: None,
            start_line: Some(5),
            end_line: Some(10),
            code_pattern: "auth function".to_string(),
            content: None,
        },
    ];
    let vectors: Vec<_> = (0..2).map(|i| create_test_vector(i as f32)).collect();
    engine.add_symbols(symbols, vectors).await.unwrap();
    engine.create_fts_index().await.unwrap();

    // Search with boosting enabled
    let results = engine.search_text_boosted("auth", 10).await.unwrap();

    // Function should rank higher than Import due to kind boosting
    assert_eq!(results.len(), 2);
    assert_eq!(results[0].kind, "function");
    assert_eq!(results[1].kind, "import");
}
```

**Step 2: Run test to verify it fails**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core test_search_with_boosting`
Expected: FAIL with "no method named `search_text_boosted`"

**Step 3: Create boosting module**

Create `rust/codesearch-core/src/boosting.rs`:

```rust
use crate::search::SearchResult;
use std::collections::HashMap;

/// Kind weights for score boosting
/// Higher = more important, lower = noise
fn kind_weights() -> HashMap<&'static str, f32> {
    let mut weights = HashMap::new();
    weights.insert("function", 1.5);
    weights.insert("class", 1.5);
    weights.insert("method", 1.3);
    weights.insert("interface", 1.2);
    weights.insert("type", 1.2);
    weights.insert("struct", 1.2);
    weights.insert("trait", 1.2);
    weights.insert("enum", 1.1);
    weights.insert("constant", 0.9);
    weights.insert("variable", 0.8);
    weights.insert("field", 0.8);
    weights.insert("import", 0.4); // Deboosted - noise
    weights.insert("export", 0.6);
    weights.insert("namespace", 0.6);
    weights.insert("module", 0.7);
    weights.insert("file", 0.5);
    weights
}

/// Boost score by match position in name
fn boost_by_position(result: &SearchResult, query: &str) -> f32 {
    let query_lower = query.to_lowercase();
    let name_lower = result.name.to_lowercase();

    if name_lower == query_lower {
        3.0 // Exact match
    } else if name_lower.starts_with(&query_lower) {
        2.0 // Prefix match
    } else if name_lower.ends_with(&query_lower) {
        1.5 // Suffix match
    } else if name_lower.contains(&query_lower) {
        1.0 // Substring match
    } else {
        // Check signature and doc_comment
        boost_by_field_match(result, &query_lower)
    }
}

/// Boost by which field contains the match
fn boost_by_field_match(result: &SearchResult, query_lower: &str) -> f32 {
    if result.name.to_lowercase().contains(query_lower) {
        3.0 // Name match = highest
    } else if result.signature.as_ref().map_or(false, |s| s.to_lowercase().contains(query_lower)) {
        1.5 // Signature match
    } else if result.doc_comment.as_ref().map_or(false, |d| d.to_lowercase().contains(query_lower)) {
        1.0 // Doc comment match
    } else {
        0.8 // No direct match (found via code_pattern)
    }
}

/// Boost score by symbol kind
fn boost_by_kind(result: &SearchResult) -> f32 {
    let weights = kind_weights();
    *weights.get(result.kind.as_str()).unwrap_or(&1.0)
}

/// Apply all score boosts to search results
pub fn apply_boosts(results: &mut [SearchResult], query: &str) {
    for result in results.iter_mut() {
        let position_boost = boost_by_position(result, query);
        let kind_boost = boost_by_kind(result);
        result.score *= position_boost * kind_boost;
    }

    // Re-normalize to 0.0-1.0
    if let Some(max_score) = results.iter().map(|r| r.score).max_by(|a, b| a.partial_cmp(b).unwrap()) {
        if max_score > 0.0 {
            for result in results.iter_mut() {
                result.score /= max_score;
            }
        }
    }

    // Filter low-quality results (< 5% of max)
    // Note: caller may want to keep all results, so we just sort
    results.sort_by(|a, b| b.score.partial_cmp(&a.score).unwrap_or(std::cmp::Ordering::Equal));
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_result(name: &str, kind: &str, score: f32) -> SearchResult {
        SearchResult {
            id: name.to_string(),
            name: name.to_string(),
            kind: kind.to_string(),
            language: "rust".to_string(),
            file_path: "test.rs".to_string(),
            signature: None,
            doc_comment: None,
            start_line: None,
            end_line: None,
            score,
        }
    }

    #[test]
    fn test_kind_boost_function_over_import() {
        let mut results = vec![
            make_result("auth", "import", 1.0),
            make_result("auth", "function", 1.0),
        ];

        apply_boosts(&mut results, "auth");

        // Function should be first after boosting
        assert_eq!(results[0].kind, "function");
        assert_eq!(results[1].kind, "import");
    }

    #[test]
    fn test_position_boost_exact_match() {
        let mut results = vec![
            make_result("authenticate", "function", 1.0), // Contains "auth"
            make_result("auth", "function", 1.0),         // Exact match
        ];

        apply_boosts(&mut results, "auth");

        // Exact match should be first
        assert_eq!(results[0].name, "auth");
    }
}
```

**Step 4: Update lib.rs to export boosting module**

Add to `rust/codesearch-core/src/lib.rs`:

```rust
pub mod boosting;
```

**Step 5: Add boosted search methods to engine**

Add to `rust/codesearch-core/src/engine.rs`:

```rust
use crate::boosting::apply_boosts;

impl CodeEngine {
    /// Text search with score boosting applied
    pub async fn search_text_boosted(&self, query: &str, limit: usize) -> Result<Vec<SearchResult>> {
        // Over-fetch to allow boosting to re-rank
        let fetch_limit = (limit * 3).max(50);
        let mut results = self.search_text(query, fetch_limit).await?;

        apply_boosts(&mut results, query);

        results.truncate(limit);
        Ok(results)
    }

    /// Vector search with score boosting applied
    pub async fn search_vector_boosted(
        &self,
        query: &str,
        query_vector: &[f32],
        limit: usize,
    ) -> Result<Vec<SearchResult>> {
        let fetch_limit = (limit * 3).max(50);
        let mut results = self.search_vector(query_vector, fetch_limit).await?;

        apply_boosts(&mut results, query);

        results.truncate(limit);
        Ok(results)
    }

    /// Hybrid search with score boosting applied
    pub async fn search_hybrid_boosted(
        &self,
        query: &str,
        query_vector: &[f32],
        limit: usize,
    ) -> Result<Vec<SearchResult>> {
        let fetch_limit = (limit * 3).max(50);
        let mut results = self.search_hybrid(query, query_vector, fetch_limit).await?;

        apply_boosts(&mut results, query);

        results.truncate(limit);
        Ok(results)
    }
}
```

**Step 6: Run test to verify it passes**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core test_search_with_boosting`
Expected: PASS

**Step 7: Run all tests**

Run: `cd /Users/murphy/source/codesearch && cargo test -p codesearch-core`
Expected: All tests pass

**Step 8: Commit**

```bash
git add rust/codesearch-core/src/boosting.rs rust/codesearch-core/src/lib.rs rust/codesearch-core/src/engine.rs
git commit -m "feat(search): add score boosting by position and kind"
```

---

### Task 5: Update FFI Layer

**Files:**
- Modify: `rust/codesearch-ffi/src/lib.rs`
- Modify: `rust/codesearch-ffi/src/codesearch.udl`

**Step 1: Add SearchResult to FFI**

Update `rust/codesearch-ffi/src/lib.rs`:

```rust
/// FFI-safe search result
#[derive(uniffi::Record)]
pub struct SearchResultOutput {
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

impl From<codesearch_core::SearchResult> for SearchResultOutput {
    fn from(r: codesearch_core::SearchResult) -> Self {
        Self {
            id: r.id,
            name: r.name,
            kind: r.kind,
            language: r.language,
            file_path: r.file_path,
            signature: r.signature,
            doc_comment: r.doc_comment,
            start_line: r.start_line,
            end_line: r.end_line,
            score: r.score,
        }
    }
}
```

**Step 2: Add search methods to CodeSearchEngine**

Add to the `impl CodeSearchEngine` block in `rust/codesearch-ffi/src/lib.rs`:

```rust
#[uniffi::export]
impl CodeSearchEngine {
    /// Create FTS index on the symbols table
    pub fn create_fts_index(&self) -> Result<(), CodeSearchError> {
        self.runtime
            .block_on(self.engine.create_fts_index())
            .map_err(|e| CodeSearchError::Database(e.to_string()))
    }

    /// Search by vector similarity
    pub fn search_vector(
        &self,
        query_vector: Vec<f32>,
        limit: u32,
    ) -> Result<Vec<SearchResultOutput>, CodeSearchError> {
        self.runtime
            .block_on(self.engine.search_vector(&query_vector, limit as usize))
            .map(|results| results.into_iter().map(Into::into).collect())
            .map_err(|e| CodeSearchError::Database(e.to_string()))
    }

    /// Search by text using FTS
    pub fn search_text(
        &self,
        query: String,
        limit: u32,
    ) -> Result<Vec<SearchResultOutput>, CodeSearchError> {
        self.runtime
            .block_on(self.engine.search_text(&query, limit as usize))
            .map(|results| results.into_iter().map(Into::into).collect())
            .map_err(|e| CodeSearchError::Database(e.to_string()))
    }

    /// Hybrid search combining FTS and vector
    pub fn search_hybrid(
        &self,
        query: String,
        query_vector: Vec<f32>,
        limit: u32,
    ) -> Result<Vec<SearchResultOutput>, CodeSearchError> {
        self.runtime
            .block_on(self.engine.search_hybrid(&query, &query_vector, limit as usize))
            .map(|results| results.into_iter().map(Into::into).collect())
            .map_err(|e| CodeSearchError::Database(e.to_string()))
    }

    /// Search by text with score boosting
    pub fn search_text_boosted(
        &self,
        query: String,
        limit: u32,
    ) -> Result<Vec<SearchResultOutput>, CodeSearchError> {
        self.runtime
            .block_on(self.engine.search_text_boosted(&query, limit as usize))
            .map(|results| results.into_iter().map(Into::into).collect())
            .map_err(|e| CodeSearchError::Database(e.to_string()))
    }

    /// Hybrid search with score boosting
    pub fn search_hybrid_boosted(
        &self,
        query: String,
        query_vector: Vec<f32>,
        limit: u32,
    ) -> Result<Vec<SearchResultOutput>, CodeSearchError> {
        self.runtime
            .block_on(self.engine.search_hybrid_boosted(&query, &query_vector, limit as usize))
            .map(|results| results.into_iter().map(Into::into).collect())
            .map_err(|e| CodeSearchError::Database(e.to_string()))
    }
}
```

**Step 3: Update UDL file**

Update `rust/codesearch-ffi/src/codesearch.udl`:

```
namespace codesearch {};

[Error]
enum CodeSearchError {
    "Database",
    "Runtime",
};

dictionary SymbolInput {
    string id;
    string name;
    string kind;
    string language;
    string file_path;
    string? signature;
    string? doc_comment;
    i32? start_line;
    i32? end_line;
    string? content;
};

dictionary SearchResultOutput {
    string id;
    string name;
    string kind;
    string language;
    string file_path;
    string? signature;
    string? doc_comment;
    i32? start_line;
    i32? end_line;
    f32 score;
};

interface CodeSearchEngine {
    [Throws=CodeSearchError]
    constructor(string db_path);

    string db_path();

    [Throws=CodeSearchError]
    boolean health_check();

    [Throws=CodeSearchError]
    u64 add_symbols(sequence<SymbolInput> symbols, sequence<sequence<f32>> vectors);

    [Throws=CodeSearchError]
    u64 symbol_count();

    [Throws=CodeSearchError]
    void create_fts_index();

    [Throws=CodeSearchError]
    sequence<SearchResultOutput> search_vector(sequence<f32> query_vector, u32 limit);

    [Throws=CodeSearchError]
    sequence<SearchResultOutput> search_text(string query, u32 limit);

    [Throws=CodeSearchError]
    sequence<SearchResultOutput> search_hybrid(string query, sequence<f32> query_vector, u32 limit);

    [Throws=CodeSearchError]
    sequence<SearchResultOutput> search_text_boosted(string query, u32 limit);

    [Throws=CodeSearchError]
    sequence<SearchResultOutput> search_hybrid_boosted(string query, sequence<f32> query_vector, u32 limit);
};
```

**Step 4: Build and verify**

Run: `cd /Users/murphy/source/codesearch && cargo build --release -p codesearch-ffi`
Expected: Build succeeds

**Step 5: Regenerate C# bindings**

Run: `cd /Users/murphy/source/codesearch && ./scripts/generate-bindings.sh`
Expected: Bindings regenerated with search methods

**Step 6: Commit**

```bash
git add rust/codesearch-ffi/src/lib.rs rust/codesearch-ffi/src/codesearch.udl src/Codesearch.Interop/Generated/
git commit -m "feat(ffi): expose search methods to .NET"
```

---

### Task 6: .NET Search Tests

**Files:**
- Modify: `tests/Codesearch.Tests/EngineTests.cs`

**Step 1: Add search tests**

Add to `tests/Codesearch.Tests/EngineTests.cs`:

```csharp
[Fact]
public void CanSearchByVector()
{
    using var engine = new CodeSearchEngine(_dbPath);

    // Add test symbols
    var symbols = new List<SymbolInput>
    {
        new SymbolInput(
            id: "1",
            name: "authenticate",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn authenticate()",
            docComment: null,
            startLine: 1,
            endLine: 10,
            content: null
        ),
        new SymbolInput(
            id: "2",
            name: "validate",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn validate()",
            docComment: null,
            startLine: 11,
            endLine: 20,
            content: null
        )
    };

    var vector1 = CreateTestVector(1.0f);
    var vector2 = CreateTestVector(5.0f);
    var vectors = new List<List<float>> { vector1, vector2 };

    engine.AddSymbols(symbols, vectors);

    // Search with vector similar to first symbol
    var queryVector = CreateTestVector(1.0f);
    var results = engine.SearchVector(queryVector, 10);

    Assert.NotEmpty(results);
    Assert.Equal("authenticate", results[0].Name);
}

[Fact]
public void CanSearchByText()
{
    using var engine = new CodeSearchEngine(_dbPath);

    var symbols = new List<SymbolInput>
    {
        new SymbolInput(
            id: "1",
            name: "authenticate_user",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn authenticate_user()",
            docComment: null,
            startLine: 1,
            endLine: 10,
            content: null
        )
    };

    var vectors = new List<List<float>> { CreateTestVector(1.0f) };
    engine.AddSymbols(symbols, vectors);
    engine.CreateFtsIndex();

    var results = engine.SearchText("authenticate", 10);

    Assert.NotEmpty(results);
    Assert.Equal("authenticate_user", results[0].Name);
}

[Fact]
public void CanSearchHybrid()
{
    using var engine = new CodeSearchEngine(_dbPath);

    var symbols = new List<SymbolInput>
    {
        new SymbolInput(
            id: "1",
            name: "authenticate",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn authenticate()",
            docComment: null,
            startLine: 1,
            endLine: 10,
            content: null
        )
    };

    var vectors = new List<List<float>> { CreateTestVector(1.0f) };
    engine.AddSymbols(symbols, vectors);
    engine.CreateFtsIndex();

    var queryVector = CreateTestVector(1.0f);
    var results = engine.SearchHybrid("authenticate", queryVector, 10);

    Assert.NotEmpty(results);
    Assert.Equal("authenticate", results[0].Name);
    Assert.True(results[0].Score > 0);
}

[Fact]
public void SearchTextBoostedRanksCorrectly()
{
    using var engine = new CodeSearchEngine(_dbPath);

    var symbols = new List<SymbolInput>
    {
        new SymbolInput(
            id: "1",
            name: "auth",
            kind: "import",  // Should be deboosted
            language: "rust",
            filePath: "test.rs",
            signature: null,
            docComment: null,
            startLine: 1,
            endLine: 1,
            content: null
        ),
        new SymbolInput(
            id: "2",
            name: "auth",
            kind: "function",  // Should be boosted
            language: "rust",
            filePath: "test.rs",
            signature: "fn auth()",
            docComment: null,
            startLine: 5,
            endLine: 10,
            content: null
        )
    };

    var vectors = new List<List<float>>
    {
        CreateTestVector(1.0f),
        CreateTestVector(2.0f)
    };
    engine.AddSymbols(symbols, vectors);
    engine.CreateFtsIndex();

    var results = engine.SearchTextBoosted("auth", 10);

    Assert.Equal(2, results.Count);
    // Function should rank higher than import
    Assert.Equal("function", results[0].Kind);
    Assert.Equal("import", results[1].Kind);
}

private static List<float> CreateTestVector(float seed)
{
    var vector = new List<float>(768);
    for (int i = 0; i < 768; i++)
    {
        vector.Add((float)Math.Sin(seed + i * 0.001));
    }
    // L2 normalize
    var norm = (float)Math.Sqrt(vector.Sum(x => x * x));
    return vector.Select(x => x / norm).ToList();
}
```

**Step 2: Run tests to verify**

Run: `cd /Users/murphy/source/codesearch && dotnet test`
Expected: All tests pass

**Step 3: Commit**

```bash
git add tests/Codesearch.Tests/EngineTests.cs
git commit -m "test: add .NET search tests"
```

---

### Task 7: ONNX Runtime Embedding Setup

**Files:**
- Create: `src/Codesearch.Embeddings/Codesearch.Embeddings.csproj`
- Create: `src/Codesearch.Embeddings/EmbeddingModel.cs`
- Modify: `codesearch.slnx`

**Step 1: Create Embeddings project**

Create `src/Codesearch.Embeddings/Codesearch.Embeddings.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.20.1" />
    <PackageReference Include="Microsoft.ML.OnnxRuntime.Managed" Version="1.20.1" />
  </ItemGroup>
</Project>
```

**Step 2: Add project to solution**

Update `codesearch.slnx` to include the new project:

```xml
<Project Path="src/Codesearch.Embeddings/Codesearch.Embeddings.csproj" />
```

**Step 3: Create EmbeddingModel class**

Create `src/Codesearch.Embeddings/EmbeddingModel.cs`:

```csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Codesearch.Embeddings;

/// <summary>
/// ONNX Runtime embedding model wrapper.
/// Supports nomic-embed-text-v1.5 (768 dimensions).
/// </summary>
public sealed class EmbeddingModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _dimension;
    private bool _disposed;

    public int Dimension => _dimension;

    /// <summary>
    /// Load embedding model from ONNX file.
    /// </summary>
    /// <param name="modelPath">Path to .onnx file</param>
    /// <param name="dimension">Expected embedding dimension (default: 768)</param>
    public EmbeddingModel(string modelPath, int dimension = 768)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Model file not found: {modelPath}");
        }

        _dimension = dimension;

        // Configure session with available execution providers
        var options = new SessionOptions();

        // Try to use hardware acceleration (order matters - first available wins)
        try
        {
            // CoreML for Apple Silicon
            if (OperatingSystem.IsMacOS())
            {
                options.AppendExecutionProvider_CoreML();
            }
        }
        catch
        {
            // CoreML not available, fall through to CPU
        }

        _session = new InferenceSession(modelPath, options);
    }

    /// <summary>
    /// Generate embedding for a single text.
    /// </summary>
    public float[] Embed(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // nomic-embed-text expects input_ids and attention_mask
        // For simplicity, we'll use a basic tokenization approach
        // In production, you'd use a proper tokenizer
        var inputIds = TokenizeSimple(text);
        var attentionMask = new long[inputIds.Length];
        Array.Fill(attentionMask, 1L);

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, inputIds.Length]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, attentionMask.Length]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor)
        };

        using var results = _session.Run(inputs);

        // Get the embedding output (usually named "sentence_embedding" or "last_hidden_state")
        var output = results.FirstOrDefault(r => r.Name.Contains("embedding") || r.Name == "last_hidden_state");
        if (output == null)
        {
            output = results.First();
        }

        var tensor = output.AsTensor<float>();
        var embedding = new float[_dimension];

        // Mean pooling if needed (for models that output per-token embeddings)
        if (tensor.Dimensions.Length == 3)
        {
            // Shape: [batch, seq_len, hidden_dim]
            var seqLen = (int)tensor.Dimensions[1];
            for (int d = 0; d < _dimension; d++)
            {
                float sum = 0;
                for (int s = 0; s < seqLen; s++)
                {
                    sum += tensor[0, s, d];
                }
                embedding[d] = sum / seqLen;
            }
        }
        else
        {
            // Shape: [batch, hidden_dim] - already pooled
            for (int d = 0; d < _dimension; d++)
            {
                embedding[d] = tensor[0, d];
            }
        }

        // L2 normalize
        var norm = MathF.Sqrt(embedding.Sum(x => x * x));
        if (norm > 0)
        {
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] /= norm;
            }
        }

        return embedding;
    }

    /// <summary>
    /// Generate embeddings for multiple texts.
    /// </summary>
    public float[][] EmbedBatch(IReadOnlyList<string> texts)
    {
        // For now, process sequentially. Can optimize with batching later.
        return texts.Select(Embed).ToArray();
    }

    /// <summary>
    /// Simple whitespace tokenization (placeholder).
    /// In production, use the model's actual tokenizer.
    /// </summary>
    private static long[] TokenizeSimple(string text)
    {
        // This is a very simplified tokenizer
        // Real implementation would use the model's vocabulary
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokens = new long[Math.Min(words.Length + 2, 512)]; // +2 for [CLS] and [SEP]

        tokens[0] = 101; // [CLS] token
        for (int i = 0; i < Math.Min(words.Length, 510); i++)
        {
            // Simple hash to token ID (not real tokenization)
            tokens[i + 1] = Math.Abs(words[i].GetHashCode()) % 30000 + 1000;
        }
        tokens[^1] = 102; // [SEP] token

        return tokens;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _session.Dispose();
            _disposed = true;
        }
    }
}
```

**Step 4: Build to verify**

Run: `cd /Users/murphy/source/codesearch && dotnet build src/Codesearch.Embeddings`
Expected: Build succeeds

**Step 5: Add reference from Server project**

Update `src/Codesearch.Server/Codesearch.Server.csproj` to add:

```xml
<ItemGroup>
  <ProjectReference Include="..\Codesearch.Embeddings\Codesearch.Embeddings.csproj" />
</ItemGroup>
```

**Step 6: Commit**

```bash
git add src/Codesearch.Embeddings/ codesearch.slnx src/Codesearch.Server/Codesearch.Server.csproj
git commit -m "feat(embeddings): add ONNX Runtime embedding model wrapper"
```

---

### Task 8: End-to-End Integration Test

**Files:**
- Modify: `src/Codesearch.Server/Program.cs`
- Create: `tests/Codesearch.Tests/IntegrationTests.cs`

**Step 1: Update Server demo**

Update `src/Codesearch.Server/Program.cs`:

```csharp
using uniffi.codesearch_ffi;

Console.WriteLine("Codesearch Server Demo");
Console.WriteLine("======================");

var tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_demo_{Guid.NewGuid():N}");
var dbPath = Path.Combine(tempDir, "demo.lance");

try
{
    using var engine = new CodeSearchEngine(dbPath);
    Console.WriteLine($"Created engine at: {engine.DbPath()}");
    Console.WriteLine($"Health check: {engine.HealthCheck()}");

    // Add sample symbols with mock vectors
    var symbols = new List<SymbolInput>
    {
        new("fn_1", "authenticate_user", "function", "rust", "src/auth.rs",
            "pub fn authenticate_user(token: &str) -> Result<User>", "Authenticates a user by token",
            10, 25, null),
        new("fn_2", "validate_token", "function", "rust", "src/auth.rs",
            "fn validate_token(token: &str) -> bool", "Validates JWT token",
            30, 40, null),
        new("fn_3", "hash_password", "function", "rust", "src/crypto.rs",
            "pub fn hash_password(password: &str) -> String", "Hashes password with bcrypt",
            5, 15, null),
        new("imp_1", "bcrypt", "import", "rust", "src/crypto.rs",
            null, null, 1, 1, null),
    };

    // Generate mock vectors (in production, use EmbeddingModel)
    var vectors = symbols.Select((_, i) => CreateMockVector(i * 0.1f)).ToList();

    var added = engine.AddSymbols(symbols, vectors);
    Console.WriteLine($"Added {added} symbols");

    // Create FTS index
    engine.CreateFtsIndex();
    Console.WriteLine("Created FTS index");

    // Demo searches
    Console.WriteLine("\n--- Text Search for 'authenticate' ---");
    var textResults = engine.SearchTextBoosted("authenticate", 5);
    foreach (var r in textResults)
    {
        Console.WriteLine($"  [{r.Score:F3}] {r.Kind}: {r.Name} ({r.FilePath}:{r.StartLine})");
    }

    Console.WriteLine("\n--- Text Search for 'password' ---");
    var passwordResults = engine.SearchTextBoosted("password", 5);
    foreach (var r in passwordResults)
    {
        Console.WriteLine($"  [{r.Score:F3}] {r.Kind}: {r.Name} ({r.FilePath}:{r.StartLine})");
    }

    Console.WriteLine("\n--- Hybrid Search for 'auth' ---");
    var queryVec = CreateMockVector(0.0f); // Similar to authenticate_user
    var hybridResults = engine.SearchHybridBoosted("auth", queryVec, 5);
    foreach (var r in hybridResults)
    {
        Console.WriteLine($"  [{r.Score:F3}] {r.Kind}: {r.Name} ({r.FilePath}:{r.StartLine})");
    }

    Console.WriteLine($"\nTotal symbols in database: {engine.SymbolCount()}");
}
finally
{
    // Cleanup
    if (Directory.Exists(tempDir))
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

Console.WriteLine("\nDemo complete!");

static List<float> CreateMockVector(float seed)
{
    var vector = new List<float>(768);
    for (int i = 0; i < 768; i++)
    {
        vector.Add((float)Math.Sin(seed + i * 0.001));
    }
    var norm = (float)Math.Sqrt(vector.Sum(x => x * x));
    return vector.Select(x => x / norm).ToList();
}
```

**Step 2: Run demo**

Run: `cd /Users/murphy/source/codesearch && dotnet run --project src/Codesearch.Server`
Expected: Demo runs showing search results

**Step 3: Create integration test**

Create `tests/Codesearch.Tests/IntegrationTests.cs`:

```csharp
using uniffi.codesearch_ffi;

namespace Codesearch.Tests;

public class IntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public IntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_test_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_tempDir, "test.lance");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void EndToEndSearchWorkflow()
    {
        using var engine = new CodeSearchEngine(_dbPath);

        // 1. Add symbols
        var symbols = new List<SymbolInput>
        {
            new("1", "find_user", "function", "rust", "src/db.rs",
                "pub fn find_user(id: i64) -> Option<User>", "Finds user by ID",
                10, 20, null),
            new("2", "create_user", "function", "rust", "src/db.rs",
                "pub fn create_user(data: UserData) -> User", null,
                25, 40, null),
            new("3", "User", "struct", "rust", "src/models.rs",
                "pub struct User", "User model",
                5, 15, null),
            new("4", "user", "import", "rust", "src/main.rs",
                null, null, 1, 1, null),
        };

        var vectors = symbols.Select((_, i) => CreateTestVector(i * 0.5f)).ToList();
        engine.AddSymbols(symbols, vectors);

        // 2. Create FTS index
        engine.CreateFtsIndex();

        // 3. Test text search
        var textResults = engine.SearchText("user", 10);
        Assert.NotEmpty(textResults);

        // 4. Test boosted search (function should rank higher than import)
        var boostedResults = engine.SearchTextBoosted("user", 10);
        Assert.NotEmpty(boostedResults);
        Assert.NotEqual("import", boostedResults[0].Kind); // Import should not be first

        // 5. Test vector search
        var queryVec = CreateTestVector(0.0f); // Similar to find_user
        var vectorResults = engine.SearchVector(queryVec, 10);
        Assert.NotEmpty(vectorResults);
        Assert.Equal("find_user", vectorResults[0].Name);

        // 6. Test hybrid search
        var hybridResults = engine.SearchHybrid("find", queryVec, 10);
        Assert.NotEmpty(hybridResults);

        // 7. Verify count
        Assert.Equal(4UL, engine.SymbolCount());
    }

    private static List<float> CreateTestVector(float seed)
    {
        var vector = new List<float>(768);
        for (int i = 0; i < 768; i++)
        {
            vector.Add((float)Math.Sin(seed + i * 0.001));
        }
        var norm = (float)Math.Sqrt(vector.Sum(x => x * x));
        return vector.Select(x => x / norm).ToList();
    }
}
```

**Step 4: Run all tests**

Run: `cd /Users/murphy/source/codesearch && dotnet test`
Expected: All tests pass

**Step 5: Commit**

```bash
git add src/Codesearch.Server/Program.cs tests/Codesearch.Tests/IntegrationTests.cs
git commit -m "test: add end-to-end integration tests"
```

---

## Phase 2 Complete

At this point you have:
- Vector search with L2 distance → similarity conversion
- Full-text search with Tantivy (whitespace tokenizer)
- Hybrid search with Reciprocal Rank Fusion
- Score boosting by match position and symbol kind
- FFI exposure to .NET
- ONNX Runtime embedding model wrapper (needs real tokenizer for production)
- End-to-end integration tests

**Next Phase (2.5 or 3):** Tree-sitter extractors - copy/adapt from julie-extractors for symbol extraction from source files.
