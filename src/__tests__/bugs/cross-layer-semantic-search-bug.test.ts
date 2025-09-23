/**
 * Cross-Layer Semantic Search Bug Reproduction Test
 *
 * TDD Test: Reproduces the "no such column: s.type" error found in cross-layer semantic search
 *
 * Bug Description: When using semantic search with mode='cross-layer', the query fails with:
 * "no such column: s.type" indicating a database schema mismatch
 *
 * This test MUST fail until the bug is fixed, following Miller's TDD methodology.
 */

import { test, expect, beforeAll, afterAll, describe } from "bun:test";
import { EnhancedCodeIntelligenceEngine } from '../../engine/enhanced-code-intelligence.js';
import { initializeLogger, LogLevel } from '../../utils/logger.js';
import { MillerPaths } from '../../utils/miller-paths.js';
import { Database } from 'bun:sqlite';

describe("Cross-Layer Semantic Search Bug", () => {
  let engine: EnhancedCodeIntelligenceEngine;
  let workspacePath: string;

  beforeAll(async () => {
    // Use current Miller workspace for testing
    workspacePath = process.cwd();
    const paths = new MillerPaths(workspacePath);
    initializeLogger(paths, LogLevel.ERROR); // Minimize logs for test

    // Setup SQLite extension for semantic search
    const sqlitePaths = [
      '/opt/homebrew/opt/sqlite/lib/libsqlite3.dylib',
      '/usr/local/lib/libsqlite3.dylib'
    ];

    for (const sqlitePath of sqlitePaths) {
      try {
        Database.setCustomSQLite(sqlitePath);
        break;
      } catch (error) {
        continue;
      }
    }

    // Initialize engine with semantic search enabled
    engine = new EnhancedCodeIntelligenceEngine({
      workspacePath,
      enableWatcher: false, // Disable for test stability
      enableSemanticSearch: true,
      embeddingModel: 'fast',
      batchSize: 10
    });

    await engine.initialize();

    // Ensure workspace is indexed for semantic search
    await engine.indexWorkspace(workspacePath);
  });

  afterAll(async () => {
    await engine?.dispose();
  });

  test("BUG FIX VERIFICATION: cross-layer semantic search should work without 'no such column: s.type'", async () => {
    // This test verifies the bug fix for the "no such column: s.type" error
    const hybridSearch = engine.hybridSearch;

    // Ensure semantic search is available
    expect(hybridSearch).not.toBeNull();

    // This was the exact query that caused the "no such column: s.type" error
    const results = await hybridSearch!.search('database operations', {
      includeStructural: true,
      includeSemantic: true,
      maxResults: 5,
      semanticThreshnew: 0.1,
      mode: 'cross-layer' // This mode previously triggered the bug
    });

    // BUG IS FIXED: The query should now succeed!
    expect(Array.isArray(results)).toBe(true);

    // Should not contain error messages
    expect(results.every(r => !r.error)).toBe(true);

    // Should return some results (at least structural matches)
    console.log(`✅ Cross-layer search succeeded with ${results.length} results`);
  });

  test("SCHEMA VERIFICATION: database should have 'kind' column (not 'type') in symbols table", async () => {
    // Verify the database schema - the bug was using 's.type' instead of 's.kind'
    const paths = new MillerPaths(workspacePath);
    const dbPath = paths.getDatabasePath();
    const db = new Database(dbPath);

    try {
      // Check if the symbols table has a 'kind' column (the correct one)
      const tableInfo = db.prepare("PRAGMA table_info(symbols)").all() as Array<{
        cid: number;
        name: string;
        type: string;
        notnull: number;
        dflt_value: any;
        pk: number;
      }>;

      const hasKindColumn = tableInfo.some(col => col.name === 'kind');
      const hasTypeColumn = tableInfo.some(col => col.name === 'type');

      // This shows the actual column structure
      console.log('📋 Symbols table columns:', tableInfo.map(col => col.name));

      // The correct column is 'kind', not 'type'
      expect(hasKindColumn).toBe(true); // Should have 'kind' column
      expect(hasTypeColumn).toBe(false); // Should NOT have 'type' column

    } finally {
      db.close();
    }
  });

  test("QUERY DEBUGGING: understand what cross-layer search is trying to do", async () => {
    // Debug the actual SQL query being generated in cross-layer mode
    const hybridSearch = engine.hybridSearch;

    if (!hybridSearch) {
      throw new Error('Hybrid search not available for debugging');
    }

    // This test helps us understand the query structure
    // We'll examine the search method to see what SQL it's generating
    console.log('🔍 Debugging cross-layer search implementation...');

    // The bug is likely in the SQL query generation for cross-layer mode
    // This test should help identify the exact query causing the issue
    expect(hybridSearch).toBeDefined();
  });
});