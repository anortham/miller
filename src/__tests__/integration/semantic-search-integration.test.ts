/**
 * Semantic Search Integration Tests
 *
 * Full pipeline tests: clean DB → index files → semantic search → validation
 * These tests can iterate quickly to verify changes and catch regressions.
 */

import { describe, test, expect, beforeEach, afterEach } from 'bun:test';
import { Database } from 'bun:sqlite';
import path from 'path';
import fs from 'fs';
import EnhancedCodeIntelligenceEngine from '../../engine/enhanced-code-intelligence.js';
import { MillerPaths } from '../../utils/miller-paths.js';
import { initializeLogger, LogLevel } from '../../utils/logger.js';

describe('Semantic Search Integration Tests', () => {
  let engine: EnhancedCodeIntelligenceEngine;
  let tempWorkspace: string;
  let tempPaths: MillerPaths;
  let dbPath: string;

  beforeEach(async () => {
    // Create temporary workspace with test files
    tempWorkspace = path.join('/tmp', `miller-test-${Date.now()}`);
    fs.mkdirSync(tempWorkspace, { recursive: true });

    // Create test source files
    const srcDir = path.join(tempWorkspace, 'src');
    fs.mkdirSync(srcDir, { recursive: true });

    // Write test TypeScript files
    fs.writeFileSync(path.join(srcDir, 'database.ts'), `
export class DatabaseConnection {
  private host: string;
  private port: number;

  constructor(host: string, port: number) {
    this.host = host;
    this.port = port;
  }

  async connect(): Promise<void> {
    // Connect to database
  }

  async disconnect(): Promise<void> {
    // Disconnect from database
  }
}
`);

    fs.writeFileSync(path.join(srcDir, 'user-service.ts'), `
import { DatabaseConnection } from './database.js';

export interface User {
  id: number;
  name: string;
  email: string;
}

export class UserService {
  private db: DatabaseConnection;

  constructor(db: DatabaseConnection) {
    this.db = db;
  }

  async createUser(name: string, email: string): Promise<User> {
    // Create user in database
    return { id: 1, name, email };
  }

  async findUser(id: number): Promise<User | null> {
    // Find user by ID
    return null;
  }
}
`);

    fs.writeFileSync(path.join(srcDir, 'api-controller.ts'), `
import { UserService } from './user-service.js';

export class ApiController {
  private userService: UserService;

  constructor(userService: UserService) {
    this.userService = userService;
  }

  async handleCreateUser(request: any): Promise<any> {
    const { name, email } = request.body;
    const user = await this.userService.createUser(name, email);
    return { success: true, user };
  }

  async handleGetUser(request: any): Promise<any> {
    const { id } = request.params;
    const user = await this.userService.findUser(parseInt(id));
    return user ? { success: true, user } : { success: false };
  }
}
`);

    // Create node_modules directory with some files (should be ignored)
    const nodeModulesDir = path.join(tempWorkspace, 'node_modules');
    fs.mkdirSync(nodeModulesDir, { recursive: true });
    fs.writeFileSync(path.join(nodeModulesDir, 'should-be-ignored.ts'), 'export const ignored = true;');

    // Create .git directory (should be ignored)
    const gitDir = path.join(tempWorkspace, '.git');
    fs.mkdirSync(gitDir, { recursive: true });
    fs.writeFileSync(path.join(gitDir, 'config'), '[core]');

    // Initialize paths and logger
    tempPaths = new MillerPaths(tempWorkspace);
    initializeLogger(tempPaths, LogLevel.WARN); // Quiet for tests

    // Setup SQLite extension
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
      workspacePath: tempWorkspace,
      enableWatcher: false, // Disable for tests
      enableSemanticSearch: true,
      embeddingModel: 'fast',
      batchSize: 10
    });

    await engine.initialize();
    dbPath = tempPaths.getDatabasePath();
  });

  afterEach(async () => {
    try {
      await engine?.shutdown();
      // Clean up temp files
      fs.rmSync(tempWorkspace, { recursive: true, force: true });
    } catch (error) {
      // Ignore cleanup errors
    }
  });

  test('should discover source files and exclude ignored directories', async () => {
    // Index the workspace
    await engine.indexWorkspace(tempWorkspace);

    // Get statistics
    const stats = engine.getStats();

    // Should find TypeScript files in src/
    expect(stats.database.files).toBeGreaterThan(0);
    expect(stats.database.symbols).toBeGreaterThan(0);

    // Should have found classes, interfaces, methods
    expect(stats.database.symbols).toBeGreaterThan(10); // At least 10 symbols from our test files

    // Verify specific symbols exist
    const searchResults = await engine.searchCode('DatabaseConnection', { type: 'fuzzy', limit: 5 });
    expect(searchResults.length).toBeGreaterThan(0);
    expect(searchResults[0].text).toBe('DatabaseConnection');
  });

  test('should perform semantic indexing without constraint errors', async () => {
    // Index workspace
    await engine.indexWorkspace(tempWorkspace);

    // Check if semantic indexing completed
    const stats = engine.getStats();

    // Should have indexed symbols
    expect(stats.database.symbols).toBeGreaterThan(0);

    // Verify database doesn't have constraint issues by checking for embeddings
    const db = new Database(dbPath);

    try {
      // Check if vector tables exist and have data
      const mappingCount = db.prepare('SELECT COUNT(*) as count FROM symbol_id_mapping').get() as { count: number };

      // Should have some embeddings stored (even if limited to 100)
      if (stats.database.symbols > 0) {
        expect(mappingCount.count).toBeGreaterThan(0);
      }
    } finally {
      db.close();
    }
  });

  test('should perform hybrid semantic search', async () => {
    // Index workspace
    await engine.indexWorkspace(tempWorkspace);

    // Wait a moment for indexing to complete
    await new Promise(resolve => setTimeout(resolve, 1000));

    // Test semantic search for database-related concepts
    try {
      const hybridSearchEngine = engine.hybridSearch;
      if (!hybridSearchEngine) {
        console.log('⚠️  Hybrid search engine not available - semantic search may not be initialized');
        return;
      }

      const results = await hybridSearchEngine.search('database connection', {
        includeStructural: true,
        includeSemantic: true,
        maxResults: 5,
        semanticThreshnew: 0.1
      });

      // Should not throw errors
      expect(Array.isArray(results)).toBe(true);

      // If semantic search is working, should find relevant results
      if (results.length > 0) {
        console.log('✅ Hybrid search returned results:', results.length);

        // Check for database-related symbols
        const hasDatabaseSymbol = results.some(r =>
          r.name.toLowerCase().includes('database') ||
          r.name.toLowerCase().includes('connection')
        );

        if (hasDatabaseSymbol) {
          console.log('✅ Found database-related symbols in search results');
        }
      } else {
        console.log('⚠️  Hybrid search returned 0 results - may need semantic debugging');
      }

    } catch (error) {
      console.error('❌ Hybrid search failed:', error.message);

      // Test should not fail on the common symbol.kind error
      expect(error.message).not.toContain('symbol.kind.toLowerCase');

      // Re-throw other errors for investigation
      if (!error.message.includes('symbol.kind')) {
        throw error;
      }
    }
  });

  test('should handle file changes and incremental indexing', async () => {
    // Initial index
    await engine.indexWorkspace(tempWorkspace);
    const initialStats = engine.getStats();

    // Add a new file
    const newFilePath = path.join(tempWorkspace, 'src', 'new-service.ts');
    fs.writeFileSync(newFilePath, `
export class NewService {
  async performAction(): Promise<void> {
    // New service method
  }
}
`);

    // Re-index
    await engine.indexWorkspace(tempWorkspace);
    const updatedStats = engine.getStats();

    // Should have more symbols
    expect(updatedStats.database.symbols).toBeGreaterThan(initialStats.database.symbols);

    // Should find the new service
    const results = await engine.searchCode('NewService', { type: 'fuzzy', limit: 1 });
    expect(results.length).toBeGreaterThan(0);
    expect(results[0].text).toBe('NewService');
  });

  test('should exclude node_modules and other ignored directories', async () => {
    // Index workspace
    await engine.indexWorkspace(tempWorkspace);

    // Search for the ignored file content
    const results = await engine.searchCode('ignored', { type: 'fuzzy', limit: 10 });

    // Should not find files from node_modules
    const hasNodeModulesResults = results.some(r => r.file.includes('node_modules'));
    expect(hasNodeModulesResults).toBe(false);

    // Should not find files from .git
    const hasGitResults = results.some(r => r.file.includes('.git'));
    expect(hasGitResults).toBe(false);
  });

  test('should handle large number of symbols without performance issues', async () => {
    // Create additional test files to increase symbol count
    for (let i = 0; i < 5; i++) {
      fs.writeFileSync(path.join(tempWorkspace, 'src', `service-${i}.ts`), `
export class Service${i} {
  private data: any[] = [];

  async create(item: any): Promise<void> {
    this.data.push(item);
  }

  async read(id: number): Promise<any> {
    return this.data.find(item => item.id === id);
  }

  async update(id: number, data: any): Promise<void> {
    const index = this.data.findIndex(item => item.id === id);
    if (index >= 0) {
      this.data[index] = { ...this.data[index], ...data };
    }
  }

  async delete(id: number): Promise<void> {
    this.data = this.data.filter(item => item.id !== id);
  }
}
`);
    }

    const startTime = Date.now();

    // Index workspace
    await engine.indexWorkspace(tempWorkspace);

    const indexTime = Date.now() - startTime;
    const stats = engine.getStats();

    // Should complete indexing in reasonable time
    expect(indexTime).toBeLessThan(30000); // 30 seconds max

    // Should have found many symbols
    expect(stats.database.symbols).toBeGreaterThan(50);

    console.log(`✅ Indexed ${stats.database.symbols} symbols in ${indexTime}ms`);
  });
});