//! Score boosting module for post-search result enhancement
//!
//! This module provides functions to boost search results based on:
//! - Position match quality (exact, prefix, suffix, substring)
//! - Field matches (name, signature, doc_comment)
//! - Symbol kind importance (function > import)

use crate::search::SearchResult;

/// Boost multiplier based on how well the query matches the symbol name
///
/// Returns:
/// - 3.0: Exact name match (case-insensitive)
/// - 2.0: Prefix match (name starts with query)
/// - 1.5: Suffix match (name ends with query)
/// - 1.0: Substring match (name contains query)
/// - Falls back to field_match boost if no name match
pub fn boost_by_position(result: &SearchResult, query: &str) -> f32 {
    let name_lower = result.name.to_lowercase();
    let query_lower = query.to_lowercase();

    if name_lower == query_lower {
        3.0
    } else if name_lower.starts_with(&query_lower) {
        2.0
    } else if name_lower.ends_with(&query_lower) {
        1.5
    } else if name_lower.contains(&query_lower) {
        1.0
    } else {
        // Fall back to field match when no name match
        boost_by_field_match(result, query)
    }
}

/// Boost multiplier based on which field contains the query
///
/// Returns:
/// - 3.0: Name contains query
/// - 1.5: Signature contains query
/// - 1.0: Doc comment contains query
/// - 0.8: None match
pub fn boost_by_field_match(result: &SearchResult, query: &str) -> f32 {
    let query_lower = query.to_lowercase();

    if result.name.to_lowercase().contains(&query_lower) {
        3.0
    } else if result
        .signature
        .as_ref()
        .map(|s| s.to_lowercase().contains(&query_lower))
        .unwrap_or(false)
    {
        1.5
    } else if result
        .doc_comment
        .as_ref()
        .map(|d| d.to_lowercase().contains(&query_lower))
        .unwrap_or(false)
    {
        1.0
    } else {
        0.8
    }
}

/// Boost multiplier based on symbol kind importance
///
/// Higher-value symbols (functions, classes) get boosted.
/// Lower-value symbols (imports) get deboosted.
pub fn boost_by_kind(result: &SearchResult) -> f32 {
    match result.kind.as_str() {
        "function" => 1.5,
        "class" => 1.5,
        "method" => 1.3,
        "interface" => 1.2,
        "type" => 1.2,
        "struct" => 1.2,
        "trait" => 1.2,
        "enum" => 1.1,
        "constant" => 0.9,
        "variable" => 0.8,
        "field" => 0.8,
        "import" => 0.4,
        "export" => 0.6,
        "namespace" => 0.6,
        "module" => 0.7,
        "file" => 0.5,
        _ => 1.0, // Default for unknown kinds
    }
}

/// Apply all boosts to search results, re-normalize scores, and sort by score descending
///
/// For each result:
/// 1. Multiply score by position_boost * kind_boost
/// 2. Re-normalize all scores to 0.0-1.0
/// 3. Sort by score descending
pub fn apply_boosts(results: &mut [SearchResult], query: &str) {
    if results.is_empty() {
        return;
    }

    // Apply boosts
    for result in results.iter_mut() {
        let position_boost = boost_by_position(result, query);
        let kind_boost = boost_by_kind(result);
        result.score *= position_boost * kind_boost;
    }

    // Re-normalize to 0.0-1.0
    let max_score = results
        .iter()
        .map(|r| r.score)
        .fold(0.0f32, f32::max);

    if max_score > 0.0 {
        for result in results.iter_mut() {
            result.score /= max_score;
        }
    }

    // Sort by score descending
    results.sort_by(|a, b| b.score.partial_cmp(&a.score).unwrap_or(std::cmp::Ordering::Equal));
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_result(name: &str, kind: &str, score: f32) -> SearchResult {
        SearchResult {
            id: format!("id_{}", name),
            name: name.to_string(),
            kind: kind.to_string(),
            language: "rust".to_string(),
            file_path: format!("src/{}.rs", name),
            signature: None,
            doc_comment: None,
            start_line: Some(1),
            end_line: Some(10),
            score,
        }
    }

    fn make_result_with_sig_doc(
        name: &str,
        kind: &str,
        score: f32,
        signature: Option<&str>,
        doc_comment: Option<&str>,
    ) -> SearchResult {
        SearchResult {
            id: format!("id_{}", name),
            name: name.to_string(),
            kind: kind.to_string(),
            language: "rust".to_string(),
            file_path: format!("src/{}.rs", name),
            signature: signature.map(|s| s.to_string()),
            doc_comment: doc_comment.map(|d| d.to_string()),
            start_line: Some(1),
            end_line: Some(10),
            score,
        }
    }

    #[test]
    fn test_kind_boost_function_over_import() {
        // Function and import with same base score
        let mut results = vec![
            make_result("authenticate", "import", 0.8),
            make_result("authenticate", "function", 0.8),
        ];

        apply_boosts(&mut results, "authenticate");

        // Function should rank higher than import
        assert_eq!(results[0].kind, "function", "Function should rank higher than import");
        assert_eq!(results[1].kind, "import");

        // Both should have normalized scores
        assert!(results[0].score > results[1].score);
    }

    #[test]
    fn test_position_boost_exact_match() {
        // Exact match should rank highest
        let mut results = vec![
            make_result("authenticate_user", "function", 0.9),  // prefix match
            make_result("user_authenticate", "function", 0.9),  // suffix match
            make_result("authenticate", "function", 0.8),       // exact match, lower base score
            make_result("do_authenticate_now", "function", 0.95), // substring match
        ];

        apply_boosts(&mut results, "authenticate");

        // Exact match should be first despite lower base score
        assert_eq!(
            results[0].name, "authenticate",
            "Exact match should rank highest even with lower base score"
        );
    }

    #[test]
    fn test_boost_by_position_exact() {
        let result = make_result("foo", "function", 1.0);
        assert_eq!(boost_by_position(&result, "foo"), 3.0);
        assert_eq!(boost_by_position(&result, "FOO"), 3.0); // case-insensitive
    }

    #[test]
    fn test_boost_by_position_prefix() {
        let result = make_result("foobar", "function", 1.0);
        assert_eq!(boost_by_position(&result, "foo"), 2.0);
    }

    #[test]
    fn test_boost_by_position_suffix() {
        let result = make_result("barfoo", "function", 1.0);
        assert_eq!(boost_by_position(&result, "foo"), 1.5);
    }

    #[test]
    fn test_boost_by_position_substring() {
        let result = make_result("afoob", "function", 1.0);
        assert_eq!(boost_by_position(&result, "foo"), 1.0);
    }

    #[test]
    fn test_boost_by_position_fallback_to_field() {
        // Name doesn't match, but signature does
        let result = make_result_with_sig_doc(
            "bar",
            "function",
            1.0,
            Some("fn bar(foo: i32)"),
            None,
        );
        // Should fall back to field match - signature contains "foo" -> 1.5
        assert_eq!(boost_by_position(&result, "foo"), 1.5);
    }

    #[test]
    fn test_boost_by_field_match_name() {
        let result = make_result("foobar", "function", 1.0);
        assert_eq!(boost_by_field_match(&result, "foo"), 3.0);
    }

    #[test]
    fn test_boost_by_field_match_signature() {
        let result = make_result_with_sig_doc(
            "bar",
            "function",
            1.0,
            Some("fn bar(foo: i32)"),
            None,
        );
        assert_eq!(boost_by_field_match(&result, "foo"), 1.5);
    }

    #[test]
    fn test_boost_by_field_match_doc_comment() {
        let result = make_result_with_sig_doc(
            "bar",
            "function",
            1.0,
            Some("fn bar()"),
            Some("This function handles foo operations"),
        );
        assert_eq!(boost_by_field_match(&result, "foo"), 1.0);
    }

    #[test]
    fn test_boost_by_field_match_none() {
        let result = make_result_with_sig_doc(
            "bar",
            "function",
            1.0,
            Some("fn bar()"),
            Some("This function handles xyz"),
        );
        assert_eq!(boost_by_field_match(&result, "foo"), 0.8);
    }

    #[test]
    fn test_boost_by_kind_function() {
        let result = make_result("foo", "function", 1.0);
        assert_eq!(boost_by_kind(&result), 1.5);
    }

    #[test]
    fn test_boost_by_kind_import() {
        let result = make_result("foo", "import", 1.0);
        assert_eq!(boost_by_kind(&result), 0.4);
    }

    #[test]
    fn test_boost_by_kind_class() {
        let result = make_result("foo", "class", 1.0);
        assert_eq!(boost_by_kind(&result), 1.5);
    }

    #[test]
    fn test_boost_by_kind_unknown() {
        let result = make_result("foo", "unknown_kind", 1.0);
        assert_eq!(boost_by_kind(&result), 1.0);
    }

    #[test]
    fn test_apply_boosts_normalizes_scores() {
        let mut results = vec![
            make_result("foo", "function", 0.5),
            make_result("bar", "function", 0.5),
        ];

        apply_boosts(&mut results, "foo");

        // All scores should be in 0.0-1.0 range
        for result in &results {
            assert!(result.score >= 0.0 && result.score <= 1.0);
        }

        // Top result should have score 1.0
        assert!((results[0].score - 1.0).abs() < 0.001);
    }

    #[test]
    fn test_apply_boosts_sorts_descending() {
        let mut results = vec![
            make_result("bar", "variable", 0.9),
            make_result("foo", "function", 0.5),
        ];

        apply_boosts(&mut results, "foo");

        // Results should be sorted by score descending
        assert!(results[0].score >= results[1].score);
    }

    #[test]
    fn test_apply_boosts_empty() {
        let mut results: Vec<SearchResult> = vec![];
        apply_boosts(&mut results, "foo");
        assert!(results.is_empty());
    }
}
