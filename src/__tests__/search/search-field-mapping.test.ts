import { describe, test, expect, beforeEach, afterEach } from 'bun:test';
import { SearchEngine } from '../../search/search-engine.js';
import { Database } from 'bun:sqlite';
import { promises as fs } from 'fs';
import { initializeLogger } from '../../utils/logger.js';
import path from 'path';

describe('SearchEngine Field Mapping', () => {
  let searchEngine: SearchEngine;
  let db: Database;
  let tempDbPath: string;

  beforeEach(async () => {
    // Initialize logger for tests
    const paths = {
      logFile: '/tmp/test.log',
      dbPath: '/tmp/test.db'
    };
    initializeLogger(paths);

    // Create temporary database
    tempDbPath = path.join('/tmp', `test-search-${Date.now()}.db`);
    db = new Database(tempDbPath);

    // Create symbols table
    db.run(`
      CREATE TABLE symbols (
        id TEXT PRIMARY KEY,
        name TEXT NOT NULL,
        kind TEXT NOT NULL,
        language TEXT NOT NULL,
        file_path TEXT NOT NULL,
        start_line INTEGER NOT NULL,
        start_column INTEGER NOT NULL,
        end_line INTEGER NOT NULL,
        end_column INTEGER NOT NULL,
        start_byte INTEGER NOT NULL,
        end_byte INTEGER NOT NULL,
        signature TEXT,
        doc_comment TEXT,
        visibility TEXT,
        parent_id TEXT,
        metadata JSON
      )
    `);

    // Insert test data with known values
    db.prepare(`
      INSERT INTO symbols (
        id, name, kind, language, file_path, start_line, start_column,
        end_line, end_column, start_byte, end_byte, signature
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    `).run(
      'test-symbol-1',
      'TestClass',
      'class',
      'typescript',
      '/test/path/file.ts',
      10,
      0,
      20,
      1,
      100,
      200,
      'class TestClass {}'
    );

    searchEngine = new SearchEngine(db);
    await searchEngine.indexSymbols();
  });

  afterEach(async () => {
    db.close();
    try {
      await fs.unlink(tempDbPath);
    } catch (e) {
      // Ignore cleanup errors
    }
  });

  test('should return correct file path, line, and column from search', async () => {
    const results = await searchEngine.searchFuzzy('TestClass', { limit: 1 });

    console.log('=== TEST DEBUG ===');
    console.log('Results length:', results.length);
    console.log('First result:', JSON.stringify(results[0], null, 2));
    console.log('==================');

    expect(results).toHaveLength(1);

    const result = results[0];
    expect(result.file).toBe('/test/path/file.ts');
    expect(result.line).toBe(10);
    expect(result.column).toBe(0);
    expect(result.text).toBe('TestClass');
    expect(result.kind).toBe('class');
  });

  test('should handle multiple results correctly', async () => {
    // Add another symbol
    db.prepare(`
      INSERT INTO symbols (
        id, name, kind, language, file_path, start_line, start_column,
        end_line, end_column, start_byte, end_byte, signature
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    `).run(
      'test-symbol-2',
      'TestFunction',
      'function',
      'typescript',
      '/test/path/other.ts',
      5,
      4,
      8,
      5,
      50,
      100,
      'function TestFunction() {}'
    );

    // Rebuild index with new data
    await searchEngine.indexSymbols();

    const results = await searchEngine.searchFuzzy('Test', { limit: 10 });

    console.log('=== MULTIPLE RESULTS TEST ===');
    console.log('Results count:', results.length);
    results.forEach((r, i) => {
      console.log(`Result ${i}:`, {
        file: r.file,
        line: r.line,
        column: r.column,
        text: r.text,
        kind: r.kind
      });
    });
    console.log('==============================');

    expect(results.length).toBeGreaterThan(0);

    // Verify each result has required fields
    results.forEach(result => {
      expect(result.file).toBeDefined();
      expect(result.file).not.toBe('undefined');
      expect(result.line).toBeDefined();
      expect(result.column).toBeDefined();
      expect(result.text).toBeDefined();
    });
  });
});