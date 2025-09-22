/**
 * MillerVectorStore - SQLite vector storage with sqlite-vec
 *
 * Integrates with Miller's existing database schema to provide semantic search
 * capabilities alongside structural search. Stores embeddings for symbols,
 * files, and code chunks to enable cross-layer entity mapping.
 */

import { Database } from 'bun:sqlite';
import * as sqliteVec from 'sqlite-vec';
import type { Symbol, EmbeddingResult } from '../database/schema.js';

export interface VectorSearchResult {
  symbolId: number;
  distance: number;
  symbol?: Symbol;
  confidence: number;
}

export interface VectorStoreConfig {
  enableQuantization?: boolean;
  maxResults?: number;
  distanceThreshold?: number;
  batchSize?: number;
}

export interface EntityMapping {
  entityName: string;
  symbols: Array<{
    symbolId: number;
    file: string;
    layer: string;
    confidence: number;
    distance: number;
  }>;
  totalConfidence: number;
}

export class MillerVectorStore {
  private db: Database;
  private config: VectorStoreConfig;
  private isInitialized = false;

  // Vector table configurations
  private static readonly VECTOR_TABLES = {
    symbol_vectors: {
      name: 'symbol_vectors',
      dimensions: 384, // MiniLM default
      description: 'Symbol embeddings for semantic search'
    },
    chunk_vectors: {
      name: 'chunk_vectors',
      dimensions: 384,
      description: 'Code chunk embeddings for large file analysis'
    }
  } as const;

  constructor(database: Database, config: Partial<VectorStoreConfig> = {}) {
    this.db = database;
    this.config = {
      enableQuantization: true,
      maxResults: 50,
      distanceThreshold: 0.3, // Lower = more similar (cosine distance)
      batchSize: 100,
      ...config
    };
  }

  /**
   * Set up custom SQLite library for extension support (macOS)
   */
  static setupSQLiteExtensions(): void {
    try {
      // macOS requires custom SQLite library for extension support
      if (process.platform === 'darwin') {
        const sqlitePaths = [
          '/opt/homebrew/lib/libsqlite3.dylib',
          '/opt/homebrew/Cellar/sqlite/3.50.4/lib/libsqlite3.dylib',
          '/usr/local/lib/libsqlite3.dylib',
          '/usr/local/opt/sqlite3/lib/libsqlite3.dylib'
        ];

        // Also try to find any SQLite in homebrew Cellar dynamically
        try {
          const { execSync } = require('child_process');
          const brewPrefix = execSync('brew --prefix sqlite 2>/dev/null', { encoding: 'utf8' }).trim();
          if (brewPrefix) {
            sqlitePaths.unshift(`${brewPrefix}/lib/libsqlite3.dylib`);
          }
        } catch (e) {
          // Continue with static paths
        }

        for (const path of sqlitePaths) {
          try {
            const fs = require('fs');
            if (fs.existsSync(path)) {
              Database.setCustomSQLite(path);
              console.log(`✅ Using custom SQLite: ${path}`);
              return;
            }
          } catch (e) {
            // Continue to next path
          }
        }

        console.log('⚠️  Warning: Could not find custom SQLite library. Extension loading may fail.');
        console.log('   Install with: brew install sqlite3');
      }
    } catch (error) {
      console.log('⚠️  Warning: Failed to set custom SQLite:', error);
    }
  }

  /**
   * Initialize sqlite-vec extension and create vector tables
   */
  async initialize(): Promise<void> {
    if (this.isInitialized) {
      return;
    }

    try {
      console.log('🔄 Initializing Miller vector store with sqlite-vec...');

      // Load sqlite-vec extension
      sqliteVec.load(this.db);

      // Verify extension loaded
      const { vec_version } = this.db
        .prepare("SELECT vec_version() as vec_version")
        .get() as { vec_version: string };

      console.log(`✅ sqlite-vec loaded: v${vec_version}`);

      // Create vector tables
      await this.createVectorTables();

      this.isInitialized = true;
      console.log('✅ Miller vector store ready!');

    } catch (error) {
      console.error('❌ Failed to initialize vector store:', error);
      throw new Error(`Vector store initialization failed: ${error}`);
    }
  }

  /**
   * Create vector tables for different embedding types
   */
  private async createVectorTables(): Promise<void> {
    const tables = Object.values(MillerVectorStore.VECTOR_TABLES);

    for (const table of tables) {
      // Create virtual table for vector storage
      // Note: vec0 virtual tables handle their own rowid, we don't need to specify PRIMARY KEY
      this.db.exec(`
        CREATE VIRTUAL TABLE IF NOT EXISTS ${table.name} USING vec0(
          embedding FLOAT[${table.dimensions}]
        )
      `);

      console.log(`📊 Created vector table: ${table.name} (${table.dimensions}D)`);
    }
  }

  /**
   * Store embedding for a symbol
   */
  async storeSymbolEmbedding(
    symbolId: string | number,
    embedding: EmbeddingResult
  ): Promise<void> {
    if (!this.isInitialized) {
      await this.initialize();
    }

    try {
      // Convert string UUID to integer for vec0 compatibility
      const integerRowId = typeof symbolId === 'string'
        ? this.stringToInteger(symbolId)
        : symbolId;

      // Debug logging
      console.log(`🔍 Storing embedding for symbol ${symbolId}:`);
      console.log(`   Original ID: ${symbolId} (${typeof symbolId})`);
      console.log(`   Integer ID: ${integerRowId}`);
      console.log(`   Vector type: ${typeof embedding.vector}`);
      console.log(`   Vector length: ${embedding.vector.length}`);
      console.log(`   Sample values: [${Array.from(embedding.vector.slice(0, 3)).join(', ')}...]`);

      // Store mapping for later retrieval
      await this.storeSymbolMapping(symbolId.toString(), integerRowId);

      // Use integer rowid for the vector table
      const stmt = this.db.prepare(`
        INSERT OR REPLACE INTO symbol_vectors (rowid, embedding)
        VALUES (?, ?)
      `);

      stmt.run(integerRowId, embedding.vector);
      console.log(`✅ Successfully stored embedding for symbol ${symbolId} → ${integerRowId}`);

    } catch (error) {
      console.error(`❌ Failed to store embedding for symbol ${symbolId}:`);
      console.error(`   Error message: ${error.message}`);
      console.error(`   Error code: ${error.code}`);
      console.error(`   Error errno: ${error.errno}`);
      console.error(`   Full error:`, error);
      throw error;
    }
  }

  /**
   * Convert string UUID to consistent integer for vec0 primary key
   */
  private stringToInteger(str: string): number {
    let hash = 0;
    for (let i = 0; i < str.length; i++) {
      const char = str.charCodeAt(i);
      hash = ((hash << 5) - hash) + char;
      hash = hash & hash; // Convert to 32bit integer
    }
    // Ensure positive integer (vec0 expects positive rowids)
    return Math.abs(hash);
  }

  /**
   * Store mapping between original symbol ID and integer rowid
   */
  private async storeSymbolMapping(originalId: string, integerRowId: number): Promise<void> {
    try {
      const stmt = this.db.prepare(`
        INSERT OR REPLACE INTO symbol_id_mapping (original_id, integer_id)
        VALUES (?, ?)
      `);
      stmt.run(originalId, integerRowId);
    } catch (error) {
      // If mapping table doesn't exist, create it
      this.db.exec(`
        CREATE TABLE IF NOT EXISTS symbol_id_mapping (
          original_id TEXT PRIMARY KEY,
          integer_id INTEGER UNIQUE
        )
      `);
      const stmt = this.db.prepare(`
        INSERT OR REPLACE INTO symbol_id_mapping (original_id, integer_id)
        VALUES (?, ?)
      `);
      stmt.run(originalId, integerRowId);
    }
  }

  /**
   * Store embeddings for multiple symbols in batch
   */
  async storeBatch(
    embeddings: Array<{ symbolId: string | number; embedding: EmbeddingResult }>
  ): Promise<void> {
    if (!this.isInitialized) {
      await this.initialize();
    }

    const batchSize = this.config.batchSize || 100;

    for (let i = 0; i < embeddings.length; i += batchSize) {
      const batch = embeddings.slice(i, i + batchSize);

      const transaction = this.db.transaction(() => {
        const stmt = this.db.prepare(`
          INSERT OR REPLACE INTO symbol_vectors (rowid, embedding)
          VALUES (?, ?)
        `);

        for (const { symbolId, embedding } of batch) {
          stmt.run(symbolId, embedding.vector);
        }
      });

      transaction();

      console.log(`📦 Stored batch ${Math.ceil((i + 1) / batchSize)}/${Math.ceil(embeddings.length / batchSize)}`);
    }
  }

  /**
   * Perform semantic similarity search
   */
  async search(
    queryEmbedding: Float32Array,
    limit: number = 10,
    threshold?: number
  ): Promise<VectorSearchResult[]> {
    if (!this.isInitialized) {
      await this.initialize();
    }

    const maxResults = Math.min(limit, this.config.maxResults || 50);
    const searchThreshold = threshold || this.config.distanceThreshold || 0.3;

    try {
      const results = this.db.prepare(`
        SELECT
          rowid,
          distance
        FROM symbol_vectors
        WHERE embedding MATCH ?
        ORDER BY distance ASC
        LIMIT ?
      `).all(queryEmbedding, maxResults) as Array<{
        rowid: number;
        distance: number;
      }>;

      // Filter by threshold after the query
      const filteredResults = results.filter(result => result.distance <= searchThreshold);

      return filteredResults.map(result => ({
        symbolId: result.rowid,
        distance: result.distance,
        confidence: this.distanceToConfidence(result.distance)
      }));

    } catch (error) {
      console.error('❌ Vector search failed:', error);
      throw error;
    }
  }

  /**
   * Find cross-layer entity representations
   * This implements the "Holy Grail" feature from our roadmap
   */
  async findCrossLayerEntity(
    entityName: string,
    queryEmbedding: Float32Array,
    limit: number = 20
  ): Promise<EntityMapping> {
    if (!this.isInitialized) {
      await this.initialize();
    }

    try {
      // Semantic search with vector similarity (use more permissive threshold for cross-layer search)
      const vectorResults = await this.search(queryEmbedding, limit * 2, 0.8);

      // Join with symbols table to get context
      const entitySymbols = this.db.prepare(`
        SELECT
          s.id,
          s.name,
          s.file_path,
          s.language,
          s.type,
          s.start_line,
          s.end_line,
          ? as distance,
          ? as confidence
        FROM symbols s
        WHERE s.id = ?
      `);

      const results = vectorResults.map(result => {
        const symbol = entitySymbols.get(
          result.distance,
          result.confidence,
          result.symbolId
        ) as Symbol & { distance: number; confidence: number };

        return {
          symbolId: result.symbolId,
          file: symbol.file_path,
          layer: this.detectLayer(symbol.file_path),
          confidence: result.confidence,
          distance: result.distance
        };
      });

      // Group by architectural layers and calculate total confidence
      const totalConfidence = results.reduce((sum, r) => sum + r.confidence, 0) / results.length;

      return {
        entityName,
        symbols: results,
        totalConfidence
      };

    } catch (error) {
      console.error(`❌ Cross-layer entity search failed for "${entityName}":`, error);
      throw error;
    }
  }

  /**
   * Hybrid search combining structural and semantic results
   * Implements the hybrid scoring formula: 30% name + 30% structure + 40% semantic
   */
  async hybridSearch(
    query: string,
    queryEmbedding: Float32Array,
    structuralResults: Symbol[],
    limit: number = 10
  ): Promise<Array<Symbol & { hybridScore: number; semanticDistance: number }>> {
    const semanticResults = await this.search(queryEmbedding, limit * 3);

    // Create hybrid scoring
    const hybridResults = new Map<number, Symbol & {
      hybridScore: number;
      semanticDistance: number;
      nameScore: number;
      structureScore: number;
      semanticScore: number;
    }>();

    // Process structural results
    for (const symbol of structuralResults) {
      const nameScore = this.calculateNameSimilarity(symbol.name, query);
      const structureScore = 0.7; // Default high structure score since it was found structurally

      hybridResults.set(symbol.id, {
        ...symbol,
        nameScore,
        structureScore,
        semanticScore: 0,
        semanticDistance: 1.0,
        hybridScore: (nameScore * 0.3) + (structureScore * 0.3) + (0 * 0.4)
      });
    }

    // Enhance with semantic results
    for (const semanticResult of semanticResults) {
      const existing = hybridResults.get(semanticResult.symbolId);
      const semanticScore = semanticResult.confidence;

      if (existing) {
        // Update existing with semantic score
        existing.semanticScore = semanticScore;
        existing.semanticDistance = semanticResult.distance;
        existing.hybridScore = (existing.nameScore * 0.3) +
                             (existing.structureScore * 0.3) +
                             (semanticScore * 0.4);
      } else {
        // Get symbol from database
        const symbol = this.db.prepare(`
          SELECT * FROM symbols WHERE id = ?
        `).get(semanticResult.symbolId) as Symbol;

        if (symbol) {
          const nameScore = this.calculateNameSimilarity(symbol.name, query);
          const structureScore = 0.3; // Lower since not found structurally

          hybridResults.set(symbol.id, {
            ...symbol,
            nameScore,
            structureScore,
            semanticScore,
            semanticDistance: semanticResult.distance,
            hybridScore: (nameScore * 0.3) + (structureScore * 0.3) + (semanticScore * 0.4)
          });
        }
      }
    }

    // Sort by hybrid score and return top results
    return Array.from(hybridResults.values())
      .sort((a, b) => b.hybridScore - a.hybridScore)
      .slice(0, limit);
  }

  /**
   * Get vector storage statistics
   */
  getStats(): {
    totalVectors: number;
    vectorTables: Array<{ name: string; count: number; dimensions: number }>;
    storageSize: string;
  } {
    if (!this.isInitialized) {
      return { totalVectors: 0, vectorTables: [], storageSize: '0 KB' };
    }

    const tables = Object.values(MillerVectorStore.VECTOR_TABLES);
    const vectorTables = tables.map(table => {
      const { count } = this.db.prepare(`
        SELECT COUNT(*) as count FROM ${table.name}
      `).get() as { count: number };

      return {
        name: table.name,
        count,
        dimensions: table.dimensions
      };
    });

    const totalVectors = vectorTables.reduce((sum, table) => sum + table.count, 0);

    // Estimate storage size (rough calculation)
    const bytesPerVector = 384 * 4; // 384 floats * 4 bytes each
    const totalBytes = totalVectors * bytesPerVector;
    const storageSize = this.formatBytes(totalBytes);

    return {
      totalVectors,
      vectorTables,
      storageSize
    };
  }

  /**
   * Clear all vector data
   */
  clearVectors(): void {
    if (!this.isInitialized) {
      return;
    }

    const tables = Object.values(MillerVectorStore.VECTOR_TABLES);
    for (const table of tables) {
      this.db.exec(`DELETE FROM ${table.name}`);
    }

    console.log('🧹 All vector data cleared');
  }

  /**
   * Convert distance to confidence score (0-1)
   */
  private distanceToConfidence(distance: number): number {
    // Cosine distance: 0 = identical, 2 = opposite
    // Convert to confidence: 1 = identical, 0 = opposite
    return Math.max(0, Math.min(1, 1 - (distance / 2)));
  }

  /**
   * Calculate name similarity score
   */
  private calculateNameSimilarity(name1: string, name2: string): number {
    const norm1 = name1.toLowerCase();
    const norm2 = name2.toLowerCase();

    if (norm1 === norm2) return 1.0;
    if (norm1.includes(norm2) || norm2.includes(norm1)) return 0.8;

    // Levenshtein distance based similarity
    const maxLen = Math.max(norm1.length, norm2.length);
    const distance = this.levenshteinDistance(norm1, norm2);
    return Math.max(0, 1 - (distance / maxLen));
  }

  /**
   * Detect architectural layer from file path
   */
  private detectLayer(filePath: string): string {
    const path = filePath.toLowerCase();

    if (path.includes('frontend') || path.includes('client') || path.includes('ui')) return 'frontend';
    if (path.includes('api') || path.includes('controller') || path.includes('endpoint')) return 'api';
    if (path.includes('domain') || path.includes('model') || path.includes('entity')) return 'domain';
    if (path.includes('data') || path.includes('repository') || path.includes('dal')) return 'data';
    if (path.includes('database') || path.includes('.sql') || path.includes('migration')) return 'database';
    if (path.includes('infrastructure') || path.includes('config')) return 'infrastructure';

    // Try to infer from file extension and content patterns
    if (filePath.endsWith('.sql')) return 'database';
    if (filePath.endsWith('.cs') && path.includes('dto')) return 'api';
    if (filePath.endsWith('.ts') && path.includes('interface')) return 'frontend';

    return 'unknown';
  }

  /**
   * Simple Levenshtein distance calculation
   */
  private levenshteinDistance(str1: string, str2: string): number {
    const matrix = Array(str2.length + 1).fill(null).map(() => Array(str1.length + 1).fill(null));

    for (let i = 0; i <= str1.length; i++) matrix[0][i] = i;
    for (let j = 0; j <= str2.length; j++) matrix[j][0] = j;

    for (let j = 1; j <= str2.length; j++) {
      for (let i = 1; i <= str1.length; i++) {
        const indicator = str1[i - 1] === str2[j - 1] ? 0 : 1;
        matrix[j][i] = Math.min(
          matrix[j][i - 1] + 1,
          matrix[j - 1][i] + 1,
          matrix[j - 1][i - 1] + indicator
        );
      }
    }

    return matrix[str2.length][str1.length];
  }

  /**
   * Format bytes to human readable string
   */
  private formatBytes(bytes: number): string {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  }
}

export default MillerVectorStore;