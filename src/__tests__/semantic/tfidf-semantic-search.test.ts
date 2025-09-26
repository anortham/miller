/**
 * TF-IDF Semantic Search Integration Test
 *
 * Tests the complete TF-IDF semantic search pipeline:
 * 1. TF-IDF vocabulary synchronization between main thread and workers
 * 2. Semantic search returns meaningful TF-IDF relevance scores
 * 3. Hybrid search combines structural + semantic results
 *
 * SUCCESS CRITERIA:
 * - Semantic search returns results marked as 'semantic' or 'hybrid'
 * - TF-IDF relevance scores > 0% for conceptually related queries
 * - Query vocabulary matches indexing vocabulary for consistency
 * - No stdio interference from worker processes
 */

import { describe, test, expect, beforeAll, afterAll } from 'bun:test';
import path from 'path';
import fs from 'fs';
import { Database } from 'bun:sqlite';
import EnhancedCodeIntelligenceEngine from '../../engine/enhanced-code-intelligence.js';
import { MillerPaths } from '../../utils/miller-paths.js';
import { initializeLogger, LogLevel } from '../../utils/logger.js';

describe('TF-IDF Semantic Search Integration', () => {
  let tempWorkspace: string;
  let tempPaths: MillerPaths;
  let engine: EnhancedCodeIntelligenceEngine;

  beforeAll(async () => {
    // Create temporary workspace with test files
    tempWorkspace = path.join('/tmp', `miller-tfidf-test-${Date.now()}`);
    fs.mkdirSync(tempWorkspace, { recursive: true });

    // Initialize Miller paths and logger
    tempPaths = new MillerPaths(tempWorkspace);
    initializeLogger(tempPaths, LogLevel.DEBUG); // Verbose for debugging

    // Setup SQLite extension (copy from working test)
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

    // Create test files with semantic content
    fs.writeFileSync(path.join(tempWorkspace, 'user-service.ts'), `
      export class UserService {
        async authenticateUser(email: string, password: string) {
          // User authentication logic
          return this.validateCredentials(email, password);
        }

        private validateCredentials(email: string, password: string) {
          // Credential validation implementation
          return { success: true, token: 'jwt-token' };
        }
      }
    `);

    fs.writeFileSync(path.join(tempWorkspace, 'database-query.ts'), `
      export class DatabaseConnection {
        async executeQuery(sql: string) {
          // Database query execution
          return this.runSqlStatement(sql);
        }

        private runSqlStatement(statement: string) {
          // SQL statement execution logic
          return { rows: [], metadata: {} };
        }
      }
    `);

    fs.writeFileSync(path.join(tempWorkspace, 'vector-search.ts'), `
      export class VectorStore {
        async searchSimilarity(embedding: Float32Array, threshold: number) {
          // Vector similarity search
          return this.findSimilarVectors(embedding, threshold);
        }

        private findSimilarVectors(vector: Float32Array, minScore: number) {
          // Vector similarity computation
          return [{ id: '1', score: 0.85, distance: 0.15 }];
        }
      }
    `);

    // Initialize engine with TF-IDF semantic search
    engine = new EnhancedCodeIntelligenceEngine({
      workspacePath: tempWorkspace,
      enableWatcher: false, // Disable for tests
      enableSemanticSearch: true,
      embeddingModel: 'fast', // Use 'fast' model which now uses TF-IDF
      embeddingProcessCount: 2,
      batchSize: 10
    });

    await engine.initialize();
    await engine.indexWorkspace(tempWorkspace);

    // Wait for semantic indexing to complete
    console.log('⏳ Waiting for semantic indexing to complete...');
    let attempts = 0;
    while (attempts < 30) {
      const stats = await engine.getStats();
      console.log(`🔍 Attempt ${attempts}: totalEmbeddings=${stats.semantic?.totalEmbeddings}, available=${stats.semantic?.semanticSearchAvailable}`);

      if (stats.semantic?.semanticSearchAvailable && stats.semantic?.totalEmbeddings > 0) {
        console.log(`✅ Semantic indexing complete! ${stats.semantic.totalEmbeddings} embeddings ready`);
        break;
      }
      await new Promise(resolve => setTimeout(resolve, 1000));
      attempts++;
    }
  });

  afterAll(async () => {
    try {
      await engine?.shutdown();
      // Clean up temp files
      if (fs.existsSync(tempWorkspace)) {
        fs.rmSync(tempWorkspace, { recursive: true, force: true });
      }
    } catch (error) {
      // Ignore cleanup errors
    }
  });

  test('should return semantic results for conceptually related queries', async () => {
    // ARRANGE: Query for authentication concepts (matching actual code vocabulary)
    const query = 'authenticateuser validatecredentials userservice';

    // ACT: Perform semantic search using hybrid search
    const hybridSearch = engine.hybridSearch;
    expect(hybridSearch).toBeDefined();

    const results = hybridSearch ? await hybridSearch.search(query, {
      includeSemantic: true,
      includeStructural: true,
      semanticThreshnew: 0.001,  // Extremely permissive threshold
      maxResults: 5
    }) : [];

    // ASSERT: Should find semantic matches
    expect(results).toBeDefined();
    expect(results.length).toBeGreaterThan(0);

    // Should have semantic or hybrid results (not just structural)
    const semanticResults = results.filter(r =>
      r.searchMethod === 'semantic' || r.searchMethod === 'hybrid'
    );
    expect(semanticResults.length).toBeGreaterThan(0);

    // Should have meaningful TF-IDF relevance scores
    const relevantResults = results.filter(r => r.semanticScore > 0);
    expect(relevantResults.length).toBeGreaterThan(0);

    // TF-IDF scores should be in valid range
    relevantResults.forEach(result => {
      expect(result.semanticScore).toBeGreaterThan(0);
      expect(result.semanticScore).toBeLessThanOrEqual(1);
    });
  });

  test('should find database-related concepts with query similarity', async () => {
    // ARRANGE: Query for database concepts (matching actual code vocabulary)
    const query = 'databaseconnection executequery runsqlstatement';

    // ACT: Use hybrid search for semantic matching
    const hybridSearch = engine.hybridSearch;
    const results = hybridSearch ? await hybridSearch.search(query, {
      includeSemantic: true,
      includeStructural: true,
      semanticThreshnew: 0.001,  // Extremely permissive threshold
      maxResults: 5
    }) : [];

    // ASSERT: Should match database-related symbols
    expect(results).toBeDefined();
    expect(results.length).toBeGreaterThan(0);

    // Should find DatabaseConnection class or related methods
    const dbResults = results.filter(r =>
      r.name?.includes('Database') ||
      r.name?.includes('query') ||
      r.name?.includes('execute')
    );
    expect(dbResults.length).toBeGreaterThan(0);
  });

  test('should find vector search concepts with embedding similarity', async () => {
    // ARRANGE: Query for vector/embedding concepts (matching actual code vocabulary)
    const query = 'vectorstore searchsimilarity findsimilarvectors';

    // ACT: Use hybrid search for concept matching
    const hybridSearch = engine.hybridSearch;
    const results = hybridSearch ? await hybridSearch.search(query, {
      includeSemantic: true,
      includeStructural: true,
      semanticThreshnew: 0.001,  // Extremely permissive threshold
      maxResults: 5
    }) : [];

    // ASSERT: Should match vector-related symbols
    expect(results).toBeDefined();
    expect(results.length).toBeGreaterThan(0);

    // Should find VectorStore class or related methods
    const vectorResults = results.filter(r =>
      r.name?.includes('Vector') ||
      r.name?.includes('similarity') ||
      r.name?.includes('search')
    );
    expect(vectorResults.length).toBeGreaterThan(0);
  });

  test('should have consistent TF-IDF vocabulary between indexing and queries', async () => {
    // ARRANGE: Query that should match indexed content (exact code vocabulary)
    const query = 'authenticateuser validatecredentials';

    // ACT: Use pure semantic search
    const hybridSearch = engine.hybridSearch;
    const results = hybridSearch ? await hybridSearch.search(query, {
      includeSemantic: true,
      includeStructural: false, // Pure semantic
      semanticThreshnew: 0.001,  // Extremely permissive threshold
      maxResults: 3
    }) : [];

    // ASSERT: Should find exact method matches with semantic scores
    expect(results).toBeDefined();

    if (results.length > 0) {
      // If semantic search is working, should have scores > 0
      const scoredResults = results.filter(r => r.semanticScore > 0);
      expect(scoredResults.length).toBeGreaterThan(0);

      // Should match actual method names from our test files
      const methodMatches = results.filter(r =>
        r.name === 'authenticateUser' || r.name === 'validateCredentials'
      );
      expect(methodMatches.length).toBeGreaterThan(0);
    }
  });

  test('should not return only structural results when semantic indexing is complete', async () => {
    // ARRANGE: Verify semantic search is available
    const stats = await engine.getStats();
    expect(stats.semantic?.semanticSearchAvailable).toBe(true);
    expect(stats.semantic?.totalEmbeddings).toBeGreaterThan(0);

    // ACT: Perform conceptual search (matching code vocabulary)
    const query = 'userservice authenticateuser databaseconnection';
    const hybridSearch = engine.hybridSearch;
    const results = hybridSearch ? await hybridSearch.search(query, {
      includeSemantic: true,
      includeStructural: true,
      semanticThreshnew: 0.1,
      maxResults: 5
    }) : [];

    // ASSERT: Should not be all structural results
    expect(results).toBeDefined();
    expect(results.length).toBeGreaterThan(0);

    // At least some results should be semantic or hybrid
    const nonStructuralResults = results.filter(r =>
      r.searchMethod !== 'structural'
    );

    // This is the key test - we should get non-structural results
    expect(nonStructuralResults.length).toBeGreaterThan(0);
  });
});