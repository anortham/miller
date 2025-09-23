/**
 * UNIQUE Constraint Fix Test - Isolated database testing
 * Tests the fix for hash collision issues in UUID→integer conversion
 */

import { describe, test, expect, beforeAll, afterAll } from 'bun:test';
import { Database } from 'bun:sqlite';
import { unlinkSync, existsSync } from 'fs';
import MillerEmbedder from '../../embeddings/miller-embedder.js';
import MillerVectorStore from '../../embeddings/miller-vector-store.js';
import { initializeLogger, LogLevel } from '../../utils/logger.js';
import { MillerPaths } from '../../utils/miller-paths.js';

describe('UNIQUE Constraint Fix', () => {
  let testDbPath: string;
  let db: Database;
  let embedder: MillerEmbedder;
  let vectorStore: MillerVectorStore;

  beforeAll(async () => {
    // Initialize logger for test environment
    const testPaths = new MillerPaths('/tmp/miller-test');
    initializeLogger(testPaths, LogLevel.ERROR);

    // Create isolated test database
    testDbPath = '/tmp/miller-unique-constraint-test.db';

    // Clean up any existing test database
    if (existsSync(testDbPath)) {
      unlinkSync(testDbPath);
    }

    // Set up custom SQLite for extension support
    MillerVectorStore.setupSQLiteExtensions();

    // Create test database
    db = new Database(testDbPath);

    // Initialize embedder
    embedder = new MillerEmbedder();
    await embedder.initialize('fast');

    // Initialize vector store
    vectorStore = new MillerVectorStore(db);
    await vectorStore.initialize();
  });

  afterAll(() => {
    db.close();
    embedder.clearCache();

    // Clean up test database
    if (existsSync(testDbPath)) {
      unlinkSync(testDbPath);
    }
  });

  test('should handle problematic symbol IDs that caused production UNIQUE violations', async () => {
    // These are the exact symbol IDs from production error logs that caused UNIQUE constraint failures
    const problematicSymbolIds = [
      'af150c7e3729b233ad68744976a0f703',  // div element from razor file
      '344f9ca0920415a4c8f90696575fb7e5',  // another div element
      '18181f4056036b608ae6781ce803cdc9',  // NC constant from build script
      '5751216086672f42364f807049aae2ab',  // PARSERS constant
      '670175d7df34e1be0dac115ca7d43a0f',  // constructor symbol
      'd0668209312d3d4b91e8117311269b21',  // another constructor
      'user-entity-typescript',              // Custom test case
      'user-entity-csharp',                 // Custom test case
      'duplicate-name-test-1',              // Edge case: similar names
      'duplicate-name-test-2'               // Edge case: similar names
    ];

    // Generate embeddings for all problematic symbols
    const testEmbeddings = [];
    for (let i = 0; i < problematicSymbolIds.length; i++) {
      const symbolId = problematicSymbolIds[i];

      // Create unique embeddings (slight variations)
      const baseVector = new Float32Array(384).fill(0.1 + (i * 0.001));
      const embedding = {
        vector: baseVector,
        content: `Test content for symbol ${symbolId}`,
        metadata: { symbolId, testIndex: i }
      };

      testEmbeddings.push({ symbolId, embedding });
    }

    // Store all embeddings - this should work without UNIQUE constraint violations
    let successCount = 0;
    const errors = [];

    for (const { symbolId, embedding } of testEmbeddings) {
      try {
        await vectorStore.storeSymbolEmbedding(symbolId, embedding);
        successCount++;
      } catch (error) {
        errors.push({ symbolId, error: error.message });

        // Fail immediately if we get UNIQUE constraint violation
        if (error.message?.includes('UNIQUE constraint failed')) {
          throw new Error(
            `UNIQUE constraint violation still occurring!\n` +
            `Symbol: ${symbolId}\n` +
            `Error: ${error.message}\n` +
            `This means the sequential assignment fix is not working.`
          );
        }
      }
    }

    // All symbols should be stored successfully
    expect(successCount).toBe(problematicSymbolIds.length);
    expect(errors).toHaveLength(0);

    // Verify each symbol was assigned a unique integer ID
    const integerIds = new Set();
    for (const { symbolId } of testEmbeddings) {
      // Check that mapping exists
      const mapping = db.prepare(`
        SELECT integer_id FROM symbol_id_mapping WHERE original_id = ?
      `).get(symbolId) as { integer_id: number } | undefined;

      expect(mapping).toBeTruthy();
      expect(mapping!.integer_id).toBeGreaterThan(0);

      // Ensure no duplicate integer IDs
      expect(integerIds.has(mapping!.integer_id)).toBe(false);
      integerIds.add(mapping!.integer_id);
    }

    console.log(`✅ Successfully stored ${successCount} problematic symbols with unique integer IDs`);
    console.log(`📊 Integer ID range: ${Math.min(...integerIds)} - ${Math.max(...integerIds)}`);
  });

  test('should handle sequential assignment correctly for new symbols', async () => {
    // Clear existing data to test fresh sequential assignment
    vectorStore.clearVectors();

    const newSymbolIds = [
      'symbol-001',
      'symbol-002',
      'symbol-003',
      'symbol-004',
      'symbol-005'
    ];

    // Store symbols one by one and verify sequential assignment
    for (let i = 0; i < newSymbolIds.length; i++) {
      const symbolId = newSymbolIds[i];
      const embedding = {
        vector: new Float32Array(384).fill(0.2 + (i * 0.01)),
        content: `Content for ${symbolId}`,
        metadata: { index: i }
      };

      await vectorStore.storeSymbolEmbedding(symbolId, embedding);

      // Check that integer ID was assigned sequentially
      const mapping = db.prepare(`
        SELECT integer_id FROM symbol_id_mapping WHERE original_id = ?
      `).get(symbolId) as { integer_id: number };

      expect(mapping.integer_id).toBe(i + 1); // Should be sequential starting from 1
    }

    console.log('✅ Sequential assignment working correctly');
  });

  test('should handle duplicate symbol ID storage (idempotent)', async () => {
    const symbolId = 'duplicate-test-symbol';
    const embedding = {
      vector: new Float32Array(384).fill(0.5),
      content: 'Test content for duplicate test',
      metadata: { test: 'duplicate' }
    };

    // Store the same symbol twice
    await vectorStore.storeSymbolEmbedding(symbolId, embedding);
    await vectorStore.storeSymbolEmbedding(symbolId, embedding);

    // Should only have one mapping
    const mappings = db.prepare(`
      SELECT COUNT(*) as count FROM symbol_id_mapping WHERE original_id = ?
    `).get(symbolId) as { count: number };

    expect(mappings.count).toBe(1);

    // Should only have one vector
    const vectorCount = db.prepare(`
      SELECT COUNT(*) as count FROM symbol_vectors sv
      JOIN symbol_id_mapping sim ON sv.rowid = sim.integer_id
      WHERE sim.original_id = ?
    `).get(symbolId) as { count: number };

    expect(vectorCount.count).toBe(1);

    console.log('✅ Duplicate symbol ID handling working correctly');
  });

  test('should retrieve embeddings correctly by original symbol ID', async () => {
    const testSymbolId = 'retrieval-test-symbol';
    const originalEmbedding = {
      vector: new Float32Array(384).fill(0.7),
      content: 'Test content for retrieval',
      metadata: { retrieval: 'test' }
    };

    // Store embedding
    await vectorStore.storeSymbolEmbedding(testSymbolId, originalEmbedding);

    // Search for the embedding
    const searchResults = await vectorStore.search(originalEmbedding.vector, 1);

    expect(searchResults).toHaveLength(1);
    expect(searchResults[0].distance).toBeLessThan(0.1); // Should be very similar to itself
    expect(searchResults[0].confidence).toBeGreaterThan(0.9);

    console.log('✅ Embedding retrieval working correctly');
  });

  test('should maintain performance with many symbols', async () => {
    // Clear for clean performance test
    vectorStore.clearVectors();

    const symbolCount = 100;
    const testSymbols = Array.from({ length: symbolCount }, (_, i) => ({
      symbolId: `perf-test-symbol-${i.toString().padStart(3, '0')}`,
      embedding: {
        vector: new Float32Array(384).fill(0.1 + (i * 0.001)),
        content: `Performance test symbol ${i}`,
        metadata: { index: i, batch: 'performance' }
      }
    }));

    // Measure storage time
    const startTime = Date.now();

    for (const { symbolId, embedding } of testSymbols) {
      await vectorStore.storeSymbolEmbedding(symbolId, embedding);
    }

    const storageTime = Date.now() - startTime;

    // Performance should be reasonable (under 10 seconds for 100 symbols)
    expect(storageTime).toBeLessThan(10000);

    // Verify all symbols were stored
    const totalMappings = db.prepare(`
      SELECT COUNT(*) as count FROM symbol_id_mapping
    `).get() as { count: number };

    expect(totalMappings.count).toBe(symbolCount);

    console.log(`✅ Performance test: ${symbolCount} symbols stored in ${storageTime}ms`);
    console.log(`📈 Average: ${(storageTime / symbolCount).toFixed(2)}ms per symbol`);
  });
});