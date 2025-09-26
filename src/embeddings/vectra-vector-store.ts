/**
 * VectraVectorStore - Local vector storage with Vectra
 *
 * Replaces sqlite-vec with Vectra for reliable vector search.
 * Maintains the same interface as MillerVectorStore for drop-in replacement.
 * Stores embeddings locally and provides semantic search capabilities.
 */

import { LocalIndex } from 'vectra';
import * as path from 'path';
import * as fs from 'fs';
import { Database } from 'bun:sqlite';
import { log, LogLevel } from '../utils/logger.js';
import type { Symbol, EmbeddingResult } from '../database/schema.js';

export interface VectorSearchResult {
  symbolId: number;
  distance: number;
  symbol?: Symbol;
  confidence: number;
}

export interface VectorStoreConfig {
  indexPath?: string;
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

interface VectraItem {
  id: string;
  metadata: {
    symbolId: number;
    originalId: string;
    name?: string;
    file?: string;
    type?: string;
  };
  vector: number[];
}

export class VectraVectorStore {
  private db: Database;
  private config: VectorStoreConfig;
  private isInitialized = false;
  private index: LocalIndex | null = null;
  private indexPath: string;
  private totalVectorCount = 0; // Cache for synchronous getStats()
  private isBatchOperation = false; // Flag to avoid updating cache on every item during batch

  constructor(database: Database, config: Partial<VectorStoreConfig> = {}) {
    this.db = database;
    this.config = {
      indexPath: config.indexPath || './.miller/vectors',
      maxResults: config.maxResults || 50,
      distanceThreshold: config.distanceThreshold || 0.2, // Default similarity threshold for TF-IDF
      batchSize: config.batchSize || 100,
      ...config
    };
    this.indexPath = this.config.indexPath!;
  }

  /**
   * Static setup method for compatibility (no-op since Vectra doesn't need SQLite extensions)
   */
  static setupSQLiteExtensions(): void {
    // No-op for Vectra - included for drop-in compatibility
    log.engine(LogLevel.INFO, 'Using Vectra vector store - no SQLite extensions needed');
  }

  /**
   * Initialize Vectra index
   */
  async initialize(): Promise<void> {
    if (this.isInitialized) {
      return;
    }

    try {
      log.engine(LogLevel.INFO, 'Initializing Vectra vector store');

      // Ensure index directory exists
      const indexDir = path.dirname(this.indexPath);
      if (!fs.existsSync(indexDir)) {
        fs.mkdirSync(indexDir, { recursive: true });
      }

      // Initialize Vectra index
      this.index = new LocalIndex(this.indexPath);

      // Create index if it doesn't exist
      if (!(await this.index.isIndexCreated())) {
        await this.index.createIndex();
        log.engine(LogLevel.INFO, `Created new Vectra index at ${this.indexPath}`);
      } else {
        log.engine(LogLevel.INFO, `Loaded existing Vectra index from ${this.indexPath}`);
      }

      // Create mapping table for symbol ID tracking
      this.createSymbolMappingTable();

      this.isInitialized = true;
      log.engine(LogLevel.INFO, 'Vectra vector store ready');

    } catch (error) {
      log.engine(LogLevel.ERROR, 'Failed to initialize Vectra vector store', { error: error.message });
      throw new Error(`Vector store initialization failed: ${error}`);
    }
  }

  /**
   * Create symbol mapping table (for compatibility with existing code)
   */
  private createSymbolMappingTable(): void {
    try {
      this.db.exec(`
        CREATE TABLE IF NOT EXISTS symbol_id_mapping (
          original_id TEXT PRIMARY KEY,
          integer_id INTEGER UNIQUE
        )
      `);
    } catch (error) {
      log.engine(LogLevel.WARN, 'Failed to create symbol mapping table', { error: error.message });
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
      // Convert symbolId to string for Vectra ID
      const vectraId = `symbol_${symbolId}`;
      const originalId = symbolId.toString();

      // Get symbol metadata from database if available
      let symbolMetadata = {};
      try {
        const symbolData = this.db.prepare(`
          SELECT name, file_path, kind FROM symbols WHERE id = ?
        `).get(symbolId) as { name: string; file_path: string; kind: string } | undefined;

        if (symbolData) {
          symbolMetadata = {
            name: symbolData.name,
            file: symbolData.file_path,
            type: symbolData.kind
          };
        }
      } catch (e) {
        // Symbol might not exist in database yet, continue without metadata
      }

      // Create Vectra item
      const item: VectraItem = {
        id: vectraId,
        metadata: {
          symbolId: symbolId,
          originalId,
          ...symbolMetadata
        },
        vector: Array.from(embedding.vector)
      };

      // Check if item already exists
      const existingItem = await this.index!.getItem(vectraId);

      if (existingItem) {
        // Update existing item
        await this.index!.deleteItem(vectraId);
      }

      // Insert new item
      await this.index!.insertItem(item);

      // Store mapping for compatibility
      await this.storeSymbolMapping(originalId, symbolId);

      log.engine(LogLevel.DEBUG, `Successfully stored embedding for symbol ${symbolId}`);

      // Update cached vector count (but skip in batch operations for efficiency)
      if (!this.isBatchOperation) {
        await this.updateVectorCount();
      }

    } catch (error) {
      log.engine(LogLevel.ERROR, `Failed to store embedding for symbol ${symbolId}`, {
        message: error.message
      });
      throw error;
    }
  }

  /**
   * Store mapping between original symbol ID and integer ID (for compatibility)
   */
  private async storeSymbolMapping(originalId: string, symbolId: string | number): Promise<void> {
    try {
      const stmt = this.db.prepare(`
        INSERT OR REPLACE INTO symbol_id_mapping (original_id, integer_id)
        VALUES (?, ?)
      `);
      stmt.run(originalId, symbolId);
    } catch (error) {
      // Ignore mapping errors - they're not critical for Vectra
      log.engine(LogLevel.DEBUG, 'Symbol mapping storage failed (non-critical)', { error: error.message });
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

    // Set batch operation flag to skip individual cache updates
    this.isBatchOperation = true;

    try {
      const batchSize = this.config.batchSize || 100;

      for (let i = 0; i < embeddings.length; i += batchSize) {
        const batch = embeddings.slice(i, i + batchSize);

        // Process batch in parallel for better performance
        await Promise.all(
          batch.map(({ symbolId, embedding }) =>
            this.storeSymbolEmbedding(symbolId, embedding)
          )
        );

        log.engine(LogLevel.INFO, `Stored embedding batch ${Math.ceil((i + 1) / batchSize)}/${Math.ceil(embeddings.length / batchSize)}`);
      }

      // Update cache once after all batches complete
      await this.updateVectorCount();

    } finally {
      // Always reset the flag
      this.isBatchOperation = false;
    }
  }

  /**
   * Perform semantic similarity search
   */
  async search(
    queryEmbedding: Float32Array | number[],
    limit: number = 10,
    threshold?: number
  ): Promise<VectorSearchResult[]> {
    if (!this.isInitialized) {
      await this.initialize();
    }

    const maxResults = Math.min(limit, this.config.maxResults || 50);
    const searchThreshold = threshold || this.config.distanceThreshold || 0.8;

    try {
      // Convert to regular array for Vectra (handle both Float32Array and regular arrays)
      const queryVector = Array.isArray(queryEmbedding) ? queryEmbedding : Array.from(queryEmbedding);

      // Perform vector search with Vectra
      const vectraResults = await this.index!.queryItems(queryVector, maxResults);

      // Filter by threshold and convert to our result format
      // Note: Vectra uses similarity score (0-1), higher is better
      // We use similarity threshold directly - higher threshold = more selective
      // e.g., threshold 0.3 means accept similarity >= 0.3
      const minSimilarity = searchThreshold;
      const results = vectraResults
        .filter(result => result.score >= minSimilarity)
        .map(result => {
          const distance = 1 - result.score; // Convert similarity to distance
          return {
            symbolId: result.item.metadata.symbolId,
            distance,
            confidence: this.distanceToConfidence(distance)
          };
        });

      log.engine(LogLevel.DEBUG, `Vector search found ${results.length} results above threshold`);

      return results;

    } catch (error) {
      log.engine(LogLevel.ERROR, 'Vector search failed', {
        error: error.message,
        queryLength: queryEmbedding.length,
        maxResults,
        searchThreshold
      });
      throw error;
    }
  }

  /**
   * Find cross-layer entity representations
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
      const vectorResults = await this.search(queryEmbedding, limit * 2, 0.9);

      // Join with symbols table to get context
      const entitySymbols = this.db.prepare(`
        SELECT
          s.id,
          s.name,
          s.file_path,
          s.language,
          s.kind,
          s.start_line,
          s.end_line
        FROM symbols s
        WHERE s.id = ?
      `);

      const results = vectorResults.map(result => {
        const symbol = entitySymbols.get(result.symbolId) as Symbol;

        return {
          symbolId: result.symbolId,
          file: symbol?.file_path || 'unknown',
          layer: this.detectLayer(symbol?.file_path || ''),
          confidence: result.confidence,
          distance: result.distance
        };
      }).filter(r => r.file !== 'unknown'); // Filter out results without symbol data

      // Group by architectural layers and calculate total confidence
      const totalConfidence = results.reduce((sum, r) => sum + r.confidence, 0) / (results.length || 1);

      return {
        entityName,
        symbols: results,
        totalConfidence
      };

    } catch (error) {
      log.engine(LogLevel.ERROR, `Cross-layer entity search failed for "${entityName}"`, { error: error.message });
      throw error;
    }
  }

  /**
   * Hybrid search combining structural and semantic results
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
        // Update existing with semantic data
        existing.semanticScore = semanticScore;
        existing.semanticDistance = semanticResult.distance;
        existing.hybridScore = (existing.nameScore * 0.3) + (existing.structureScore * 0.3) + (semanticScore * 0.4);
      } else {
        // Get symbol data for semantic-only result
        const symbolData = this.db.prepare(`
          SELECT * FROM symbols WHERE id = ?
        `).get(semanticResult.symbolId) as Symbol;

        if (symbolData) {
          const nameScore = this.calculateNameSimilarity(symbolData.name, query);

          hybridResults.set(semanticResult.symbolId, {
            ...symbolData,
            nameScore,
            structureScore: 0.2, // Lower structure score for semantic-only matches
            semanticScore,
            semanticDistance: semanticResult.distance,
            hybridScore: (nameScore * 0.3) + (0.2 * 0.3) + (semanticScore * 0.4)
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
   * Convert distance to confidence score (0-1)
   */
  private distanceToConfidence(distance: number): number {
    // For cosine distance, smaller is better. Convert to 0-1 confidence scale
    return Math.max(0, 1 - distance);
  }

  /**
   * Calculate name similarity between symbol name and query
   */
  private calculateNameSimilarity(symbolName: string, query: string): number {
    // Simple Levenshtein-based similarity
    const maxLength = Math.max(symbolName.length, query.length);
    if (maxLength === 0) return 1.0;

    const distance = this.levenshteinDistance(symbolName.toLowerCase(), query.toLowerCase());
    return Math.max(0, 1 - (distance / maxLength));
  }

  /**
   * Calculate Levenshtein distance between two strings
   */
  private levenshteinDistance(str1: string, str2: string): number {
    const matrix = Array(str2.length + 1).fill(null).map(() => Array(str1.length + 1).fill(null));

    for (let i = 0; i <= str1.length; i++) matrix[0][i] = i;
    for (let j = 0; j <= str2.length; j++) matrix[j][0] = j;

    for (let j = 1; j <= str2.length; j++) {
      for (let i = 1; i <= str1.length; i++) {
        const substitutionCost = str1[i - 1] === str2[j - 1] ? 0 : 1;
        matrix[j][i] = Math.min(
          matrix[j][i - 1] + 1, // deletion
          matrix[j - 1][i] + 1, // insertion
          matrix[j - 1][i - 1] + substitutionCost // substitution
        );
      }
    }

    return matrix[str2.length][str1.length];
  }

  /**
   * Detect architectural layer from file path
   */
  private detectLayer(filePath: string): string {
    const path = filePath.toLowerCase();

    if (path.includes('/api/') || path.includes('/routes/') || path.includes('/controllers/')) {
      return 'api';
    }
    if (path.includes('/db/') || path.includes('/database/') || path.includes('/models/')) {
      return 'data';
    }
    if (path.includes('/ui/') || path.includes('/components/') || path.includes('/views/')) {
      return 'frontend';
    }
    if (path.includes('/service/') || path.includes('/business/') || path.includes('/domain/')) {
      return 'domain';
    }

    return 'infrastructure';
  }

  /**
   * Get statistics about the vector store
   */
  getStats(): { totalVectors: number; indexPath: string } {
    return {
      totalVectors: this.totalVectorCount,
      indexPath: this.indexPath
    };
  }

  /**
   * Update the cached vector count (called when vectors are added/removed)
   */
  private async updateVectorCount(): Promise<void> {
    try {
      if (this.index) {
        const items = await this.index.listItems();
        this.totalVectorCount = items.length;
      }
    } catch (error) {
      // Don't throw - just keep the existing count
    }
  }

  /**
   * Clear all data from the index (useful for testing)
   */
  async clear(): Promise<void> {
    if (!this.isInitialized) {
      await this.initialize();
    }

    try {
      // Get all items and delete them
      const items = await this.index!.listItems();
      for (const item of items) {
        await this.index!.deleteItem(item.id);
      }
      log.engine(LogLevel.DEBUG, `Cleared ${items.length} items from vector store`);
    } catch (error) {
      log.engine(LogLevel.WARN, 'Failed to clear vector store', { error: error.message });
    }
  }

  /**
   * Clean up resources
   */
  async close(): Promise<void> {
    if (this.index) {
      // Vectra doesn't require explicit closing, but we can reset our state
      this.index = null;
      this.isInitialized = false;
      log.engine(LogLevel.INFO, 'Vectra vector store closed');
    }
  }
}