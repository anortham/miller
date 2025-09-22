/**
 * MillerVectorStore Tests - Validate sqlite-vec integration and cross-layer entity mapping
 */

import { describe, test, expect, beforeAll, afterAll } from 'bun:test';
import { Database } from 'bun:sqlite';
import MillerEmbedder from '../../embeddings/miller-embedder.js';
import MillerVectorStore from '../../embeddings/miller-vector-store.js';
import type { Symbol } from '../../database/schema.js';

describe('MillerVectorStore', () => {
  let db: Database;
  let embedder: MillerEmbedder;
  let vectorStore: MillerVectorStore;

  beforeAll(async () => {
    // Set up custom SQLite for extension support (macOS)
    MillerVectorStore.setupSQLiteExtensions();

    // Create in-memory database
    db = new Database(':memory:');

    // Create symbols table (simplified for testing)
    db.exec(`
      CREATE TABLE symbols (
        id INTEGER PRIMARY KEY,
        name TEXT NOT NULL,
        type TEXT NOT NULL,
        file_path TEXT NOT NULL,
        language TEXT NOT NULL,
        start_line INTEGER,
        end_line INTEGER,
        parent_id INTEGER,
        content TEXT
      )
    `);

    // Insert test symbols representing cross-layer entities
    const testSymbols = [
      {
        name: 'IUserDto',
        type: 'interface',
        file_path: 'src/types/user.ts',
        language: 'typescript',
        start_line: 1,
        end_line: 6,
        content: 'interface IUserDto { id: string; email: string; name: string; }'
      },
      {
        name: 'UserDto',
        type: 'class',
        file_path: 'src/api/DTOs/UserDto.cs',
        language: 'csharp',
        start_line: 1,
        end_line: 8,
        content: 'public class UserDto { public string Id { get; set; } public string Email { get; set; } }'
      },
      {
        name: 'User',
        type: 'class',
        file_path: 'src/domain/entities/User.cs',
        language: 'csharp',
        start_line: 1,
        end_line: 12,
        content: 'public class User : Entity { public string Id { get; private set; } }'
      },
      {
        name: 'users',
        type: 'table',
        file_path: 'database/migrations/001_create_users.sql',
        language: 'sql',
        start_line: 1,
        end_line: 5,
        content: 'CREATE TABLE users (id VARCHAR(50) PRIMARY KEY, email VARCHAR(255), name VARCHAR(100))'
      }
    ];

    const insertStmt = db.prepare(`
      INSERT INTO symbols (name, type, file_path, language, start_line, end_line, content)
      VALUES (?, ?, ?, ?, ?, ?, ?)
    `);

    for (const symbol of testSymbols) {
      insertStmt.run(
        symbol.name,
        symbol.type,
        symbol.file_path,
        symbol.language,
        symbol.start_line,
        symbol.end_line,
        symbol.content
      );
    }

    // Initialize embedder and vector store
    embedder = new MillerEmbedder();
    await embedder.initialize('fast');

    vectorStore = new MillerVectorStore(db);
    await vectorStore.initialize();
  });

  afterAll(() => {
    db.close();
    embedder.clearCache();
  });

  test('should initialize sqlite-vec extension successfully', async () => {
    // Vector store should be initialized without errors
    expect(vectorStore).toBeTruthy();

    // Check that vector tables were created
    const tables = db.prepare(`
      SELECT name FROM sqlite_master
      WHERE type='table' AND name LIKE '%_vectors'
    `).all();

    expect(tables.length).toBeGreaterThan(0);
  });

  test('should store and retrieve symbol embeddings', async () => {
    // Get a symbol from the database
    const symbol = db.prepare(`
      SELECT * FROM symbols WHERE name = 'IUserDto' LIMIT 1
    `).get() as Symbol;

    expect(symbol).toBeTruthy();

    // Generate embedding for the symbol
    const embedding = await embedder.embedCode(symbol.content || '', {
      file: symbol.file_path,
      language: symbol.language,
      patterns: ['dto', 'interface']
    });

    // Store the embedding
    await vectorStore.storeSymbolEmbedding(symbol.id, embedding);

    // Verify storage by searching
    const searchResults = await vectorStore.search(embedding.vector, 1);

    expect(searchResults).toHaveLength(1);
    expect(searchResults[0].symbolId).toBe(symbol.id);
    expect(searchResults[0].distance).toBeLessThan(0.1); // Should be very similar to itself
    expect(searchResults[0].confidence).toBeGreaterThan(0.9);
  });

  test('should perform cross-layer entity mapping', async () => {
    // Clear any existing vector data to avoid conflicts
    vectorStore.clearVectors();

    // Generate embeddings for all User-related symbols
    const userSymbols = db.prepare(`
      SELECT * FROM symbols WHERE name LIKE '%User%' OR name = 'users'
    `).all() as Symbol[];

    const embeddings = [];

    for (const symbol of userSymbols) {
      const embedding = await embedder.embedCode(symbol.content || '', {
        file: symbol.file_path,
        language: symbol.language,
        patterns: ['entity', 'dto', 'table']
      });

      embeddings.push({ symbolId: symbol.id, embedding });
    }

    // Store all embeddings
    await vectorStore.storeBatch(embeddings);

    // Generate query embedding for "User entity"
    const queryEmbedding = await embedder.embedQuery('User entity data model');

    // Perform cross-layer entity mapping with more permissive threshold
    const entityMapping = await vectorStore.findCrossLayerEntity(
      'User',
      queryEmbedding.vector,
      10
    );

    // Debug: let's see what we actually get
    console.log('🔍 Entity mapping results:', {
      entityName: entityMapping.entityName,
      symbolCount: entityMapping.symbols.length,
      totalConfidence: entityMapping.totalConfidence
    });

    expect(entityMapping.entityName).toBe('User');
    expect(entityMapping.symbols.length).toBeGreaterThan(0);
    expect(entityMapping.totalConfidence).toBeGreaterThan(0);

    // Should find different layers
    const layers = new Set(entityMapping.symbols.map(s => s.layer));
    expect(layers.size).toBeGreaterThan(1); // Multiple architectural layers

    // Should include frontend, api, domain, and database layers
    const layerNames = Array.from(layers);
    expect(layerNames).toContain('frontend'); // IUserDto.ts
    expect(layerNames).toContain('api');      // UserDto.cs
    expect(layerNames).toContain('domain');   // User.cs
    expect(layerNames).toContain('database'); // users.sql
  });

  test('should perform hybrid search with structural and semantic results', async () => {
    // Simulate structural results (found by name/AST analysis)
    const structuralResults = db.prepare(`
      SELECT * FROM symbols WHERE name LIKE '%User%'
    `).all() as Symbol[];

    // Generate query embedding
    const queryEmbedding = await embedder.embedQuery('User data transfer object');

    // Perform hybrid search
    const hybridResults = await vectorStore.hybridSearch(
      'User',
      queryEmbedding.vector,
      structuralResults,
      5
    );

    expect(hybridResults.length).toBeGreaterThan(0);

    // Results should have hybrid scores
    for (const result of hybridResults) {
      expect(result.hybridScore).toBeGreaterThan(0);
      expect(result.hybridScore).toBeLessThanOrEqual(1);
      expect(typeof result.semanticDistance).toBe('number');
    }

    // Results should be sorted by hybrid score (descending)
    for (let i = 1; i < hybridResults.length; i++) {
      expect(hybridResults[i].hybridScore).toBeLessThanOrEqual(hybridResults[i - 1].hybridScore);
    }
  });

  test('should detect architectural layers correctly', async () => {
    const testCases = [
      { path: 'src/frontend/components/User.tsx', expected: 'frontend' },
      { path: 'src/api/controllers/UserController.cs', expected: 'api' },
      { path: 'src/domain/entities/User.cs', expected: 'domain' },
      { path: 'src/data/repositories/UserRepository.cs', expected: 'data' },
      { path: 'database/migrations/001_users.sql', expected: 'database' },
      { path: 'src/infrastructure/config.ts', expected: 'infrastructure' }
    ];

    for (const testCase of testCases) {
      // Create a temporary symbol to test layer detection
      const symbolId = db.prepare(`
        INSERT INTO symbols (name, type, file_path, language, start_line, end_line)
        VALUES (?, ?, ?, ?, ?, ?)
        RETURNING id
      `).get('TestSymbol', 'class', testCase.path, 'typescript', 1, 5) as { id: number };

      const embedding = await embedder.embedCode('class TestSymbol {}');
      await vectorStore.storeSymbolEmbedding(symbolId.id, embedding);

      const queryEmbedding = await embedder.embedQuery('TestSymbol');
      const entityMapping = await vectorStore.findCrossLayerEntity(
        'TestSymbol',
        queryEmbedding.vector,
        1
      );

      if (entityMapping.symbols.length > 0) {
        expect(entityMapping.symbols[0].layer).toBe(testCase.expected);
      }
    }
  });

  test('should provide accurate vector storage statistics', async () => {
    const stats = vectorStore.getStats();

    expect(stats.totalVectors).toBeGreaterThan(0);
    expect(stats.vectorTables.length).toBeGreaterThan(0);
    expect(stats.storageSize).toMatch(/\d+(\.\d+)?\s(Bytes|KB|MB)/);

    // Should have symbol_vectors table
    const symbolTable = stats.vectorTables.find(t => t.name === 'symbol_vectors');
    expect(symbolTable).toBeTruthy();
    expect(symbolTable?.dimensions).toBe(384);
    expect(symbolTable?.count).toBeGreaterThan(0);
  });

  test('should handle batch embedding storage efficiently', async () => {
    // Create multiple test symbols
    const batchSymbols = Array.from({ length: 10 }, (_, i) => ({
      name: `TestClass${i}`,
      type: 'class',
      file_path: `src/test/TestClass${i}.ts`,
      language: 'typescript',
      start_line: 1,
      end_line: 5,
      content: `class TestClass${i} { constructor(public id: number) {} }`
    }));

    // Insert symbols and generate embeddings
    const batchEmbeddings = [];
    for (const symbol of batchSymbols) {
      const symbolId = db.prepare(`
        INSERT INTO symbols (name, type, file_path, language, start_line, end_line, content)
        VALUES (?, ?, ?, ?, ?, ?, ?)
        RETURNING id
      `).get(
        symbol.name,
        symbol.type,
        symbol.file_path,
        symbol.language,
        symbol.start_line,
        symbol.end_line,
        symbol.content
      ) as { id: number };

      const embedding = await embedder.embedCode(symbol.content);
      batchEmbeddings.push({ symbolId: symbolId.id, embedding });
    }

    // Store batch
    const start = Date.now();
    await vectorStore.storeBatch(batchEmbeddings);
    const batchTime = Date.now() - start;

    expect(batchTime).toBeLessThan(1000); // Should complete within 1 second

    // Verify all embeddings were stored
    const finalStats = vectorStore.getStats();
    expect(finalStats.totalVectors).toBeGreaterThanOrEqual(batchEmbeddings.length);
  });

  test('should clear vector data when requested', () => {
    // Clear all vectors
    vectorStore.clearVectors();

    // Verify clearing worked
    const stats = vectorStore.getStats();
    expect(stats.totalVectors).toBe(0);

    for (const table of stats.vectorTables) {
      expect(table.count).toBe(0);
    }
  });
});