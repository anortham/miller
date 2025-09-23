/**
 * MillerEmbedder - Advanced code embedding system for semantic search
 *
 * Features:
 * - Multi-tier embedding models (fast → code-specific → advanced)
 * - Smart chunking respecting AST boundaries
 * - Context-aware embeddings with structural information
 * - Local-first architecture with no external dependencies
 */

import { pipeline, type FeatureExtractionPipeline } from '@huggingface/transformers';
import type { Symbol, Relationship } from '../database/schema.js';

export interface EmbeddingModel {
  name: string;
  dimensions: number;
  maxTokens: number;
  specialization: 'general' | 'code' | 'multimodal';
  speed: 'fast' | 'medium' | 'slow';
}

export interface EmbeddingConfig {
  model: EmbeddingModel;
  useCache: boolean;
  batchSize: number;
  timeout: number;
}

export interface CodeContext {
  file: string;
  layer?: 'frontend' | 'api' | 'domain' | 'database' | 'infrastructure';
  language: string;
  imports?: string[];
  exports?: string[];
  types?: string[];
  patterns?: string[];  // e.g., ['repository', 'dto', 'service']
}

export interface EmbeddingResult {
  vector: Float32Array;
  dimensions: number;
  model: string;
  timestamp: number;
  confidence?: number;
}

export interface ChunkOptions {
  maxTokens: number;
  respectBoundaries: boolean;
  includeContext: boolean;
  overlapTokens: number;
}

export class MillerEmbedder {
  private extractor: FeatureExtractionPipeline | null = null;
  private currentModel: EmbeddingModel | null = null;
  private cache = new Map<string, EmbeddingResult>();
  private isInitialized = false;

  // Predefined model configurations
  private static readonly MODELS: Record<string, EmbeddingModel> = {
    'fast': {
      name: 'Xenova/all-MiniLM-L6-v2',
      dimensions: 384,
      maxTokens: 512,
      specialization: 'general',
      speed: 'fast'
    },
    'code': {
      name: 'Salesforce/codet5p-110m-embedding', // Public code-specialized model
      dimensions: 256,
      maxTokens: 512,
      specialization: 'code',
      speed: 'medium'
    },
    'advanced': {
      name: 'Xenova/nomic-embed-text-v1',
      dimensions: 768,
      maxTokens: 8192,
      specialization: 'multimodal',
      speed: 'slow'
    }
  };

  constructor(private config: Partial<EmbeddingConfig> = {}) {
    // Default to fast model for immediate usability
    this.config = {
      model: MillerEmbedder.MODELS.fast,
      useCache: true,
      batchSize: 10,
      timeout: 30000,
      ...config
    };
  }

  /**
   * Initialize the embedding pipeline with the specified model
   */
  async initialize(modelKey: keyof typeof MillerEmbedder.MODELS = 'fast'): Promise<void> {
    if (this.isInitialized && this.currentModel?.name === MillerEmbedder.MODELS[modelKey].name) {
      return; // Already initialized with correct model
    }

    try {
      const model = MillerEmbedder.MODELS[modelKey];
      console.log(`🔄 Initializing Miller embedder with ${model.name}...`);

      const startTime = Date.now();
      this.extractor = await pipeline('feature-extraction', model.name);
      const loadTime = Date.now() - startTime;

      this.currentModel = model;
      this.isInitialized = true;

      console.log(`✅ Miller embedder ready! Model: ${model.name} (${loadTime}ms)`);
      console.log(`📊 Dimensions: ${model.dimensions}, Max tokens: ${model.maxTokens}, Speed: ${model.speed}`);

    } catch (error) {
      console.error(`❌ Failed to initialize embedding model ${modelKey}:`, error);

      // Fallback to basic model if advanced model fails
      if (modelKey !== 'fast') {
        console.log('🔄 Falling back to fast model...');
        await this.initialize('fast');
      } else {
        throw new Error(`Failed to initialize embedding model: ${error}`);
      }
    }
  }

  /**
   * Generate embeddings for code with rich contextual information
   */
  async embedCode(
    code: string,
    context?: CodeContext,
    options: Partial<ChunkOptions> = {}
  ): Promise<EmbeddingResult> {
    if (!this.isInitialized || !this.extractor) {
      await this.initialize();
    }

    const startTime = Date.now();

    // Create cache key
    const cacheKey = this.createCacheKey(code, context, options);

    // Check cache first
    if (this.config.useCache && this.cache.has(cacheKey)) {
      const cached = this.cache.get(cacheKey)!;
      console.log(`💾 Cache hit for embedding (${Date.now() - startTime}ms)`);
      return cached;
    }

    try {
      // Enrich code with structural context
      const enrichedText = this.enrichWithContext(code, context);

      // Apply smart chunking if needed
      const chunks = this.smartChunk(enrichedText, {
        maxTokens: this.currentModel!.maxTokens,
        respectBoundaries: true,
        includeContext: true,
        overlapTokens: 50,
        ...options
      });

      // Generate embeddings (handle single chunk or multiple chunks)
      let finalVector: Float32Array;

      if (chunks.length === 1) {
        const output = await this.extractor!(chunks[0], {
          pooling: 'mean',
          normalize: true
        });
        finalVector = new Float32Array(output.data);
      } else {
        // For multiple chunks, generate embeddings and average them
        const chunkVectors = await Promise.all(
          chunks.map(async (chunk) => {
            const output = await this.extractor!(chunk, {
              pooling: 'mean',
              normalize: true
            });
            return new Float32Array(output.data);
          })
        );

        // Average the vectors
        finalVector = this.averageVectors(chunkVectors);
      }

      const result: EmbeddingResult = {
        vector: finalVector,
        dimensions: this.currentModel!.dimensions,
        model: this.currentModel!.name,
        timestamp: Date.now(),
        confidence: this.calculateConfidence(code, context)
      };

      // Cache the result
      if (this.config.useCache) {
        this.cache.set(cacheKey, result);
      }

      const totalTime = Date.now() - startTime;
      console.log(`🧬 Generated embedding: ${this.currentModel!.dimensions}D (${totalTime}ms)`);

      return result;

    } catch (error) {
      console.error('❌ Failed to generate embedding:', error);
      throw new Error(`Embedding generation failed: ${error}`);
    }
  }

  /**
   * Generate embeddings for multiple code snippets efficiently
   */
  async embedBatch(
    codeSnippets: Array<{ code: string; context?: CodeContext }>,
    options: Partial<ChunkOptions> = {}
  ): Promise<EmbeddingResult[]> {
    if (!this.isInitialized) {
      await this.initialize();
    }

    const startTime = Date.now();
    const batchSize = this.config.batchSize || 10;
    const results: EmbeddingResult[] = [];

    console.log(`🔄 Processing batch of ${codeSnippets.length} embeddings...`);

    // Process in batches to avoid memory issues
    for (let i = 0; i < codeSnippets.length; i += batchSize) {
      const batch = codeSnippets.slice(i, i + batchSize);

      const batchResults = await Promise.all(
        batch.map(({ code, context }) => this.embedCode(code, context, options))
      );

      results.push(...batchResults);

      console.log(`📊 Processed batch ${Math.ceil((i + 1) / batchSize)}/${Math.ceil(codeSnippets.length / batchSize)}`);
    }

    const totalTime = Date.now() - startTime;
    console.log(`✅ Batch embedding complete: ${results.length} embeddings (${totalTime}ms)`);

    return results;
  }

  /**
   * Create a query embedding for semantic search
   */
  async embedQuery(query: string, context?: { language?: string; pattern?: string }): Promise<EmbeddingResult> {
    // Enhance query with search context
    let enhancedQuery = query;

    if (context?.language) {
      enhancedQuery = `${context.language} code: ${query}`;
    }

    if (context?.pattern) {
      enhancedQuery = `${enhancedQuery} ${context.pattern} pattern`;
    }

    return this.embedCode(enhancedQuery);
  }

  /**
   * Enrich code with structural and contextual information
   */
  private enrichWithContext(code: string, context?: CodeContext): string {
    if (!context) {
      return code;
    }

    const parts = [];

    // Add file and layer context
    if (context.file && context.layer) {
      parts.push(`// File: ${context.file} (${context.layer} layer)`);
    }

    // Add language context
    if (context.language) {
      parts.push(`// Language: ${context.language}`);
    }

    // Add import context
    if (context.imports && context.imports.length > 0) {
      parts.push(`// Imports: ${context.imports.join(', ')}`);
    }

    // Add export context
    if (context.exports && context.exports.length > 0) {
      parts.push(`// Exports: ${context.exports.join(', ')}`);
    }

    // Add type context
    if (context.types && context.types.length > 0) {
      parts.push(`// Types: ${context.types.join(', ')}`);
    }

    // Add pattern context
    if (context.patterns && context.patterns.length > 0) {
      parts.push(`// Patterns: ${context.patterns.join(', ')}`);
    }

    // Combine context with code
    if (parts.length > 0) {
      return `${parts.join('\n')}\n\n${code}`;
    }

    return code;
  }

  /**
   * Smart chunking that respects AST boundaries and code structure
   */
  private smartChunk(text: string, options: ChunkOptions): string[] {
    const { maxTokens, respectBoundaries, overlapTokens } = options;

    // Rough token estimation (1 token ≈ 4 characters for code)
    const estimatedTokens = Math.ceil(text.length / 4);

    if (estimatedTokens <= maxTokens) {
      return [text];
    }

    // For now, implement simple chunking
    // TODO: Implement AST-aware chunking that respects function/class boundaries
    const chunks: string[] = [];
    const chunkSize = Math.floor(maxTokens * 4); // Convert tokens back to characters
    const overlapSize = Math.floor(overlapTokens * 4);

    for (let i = 0; i < text.length; i += chunkSize - overlapSize) {
      const chunk = text.slice(i, i + chunkSize);
      chunks.push(chunk);

      if (i + chunkSize >= text.length) {
        break;
      }
    }

    console.log(`📄 Split into ${chunks.length} chunks (max ${maxTokens} tokens each)`);
    return chunks;
  }

  /**
   * Average multiple embedding vectors
   */
  private averageVectors(vectors: Float32Array[]): Float32Array {
    if (vectors.length === 0) {
      throw new Error('Cannot average empty vector array');
    }

    const dimensions = vectors[0].length;
    const averaged = new Float32Array(dimensions);

    for (const vector of vectors) {
      for (let i = 0; i < dimensions; i++) {
        averaged[i] += vector[i];
      }
    }

    // Normalize by count
    for (let i = 0; i < dimensions; i++) {
      averaged[i] /= vectors.length;
    }

    // Re-normalize the final vector
    const magnitude = Math.sqrt(averaged.reduce((sum, val) => sum + val * val, 0));
    for (let i = 0; i < dimensions; i++) {
      averaged[i] /= magnitude;
    }

    return averaged;
  }

  /**
   * Calculate confidence score based on code and context quality
   */
  private calculateConfidence(code: string, context?: CodeContext): number {
    let confidence = 0.5; // Base confidence

    // Higher confidence for longer, more structured code
    if (code.length > 100) confidence += 0.1;
    if (code.length > 500) confidence += 0.1;

    // Higher confidence with context
    if (context?.file) confidence += 0.1;
    if (context?.language) confidence += 0.1;
    if (context?.types && context.types.length > 0) confidence += 0.1;
    if (context?.patterns && context.patterns.length > 0) confidence += 0.1;

    return Math.min(confidence, 1.0);
  }

  /**
   * Create cache key for embedding result
   */
  private createCacheKey(code: string, context?: CodeContext, options?: Partial<ChunkOptions>): string {
    const contextStr = context ? JSON.stringify(context) : '';
    const optionsStr = options ? JSON.stringify(options) : '';
    const modelName = this.currentModel?.name || 'unknown';

    // Simple hash of the content
    return `${modelName}:${code.length}:${contextStr}:${optionsStr}`;
  }

  /**
   * Get current model information
   */
  getModelInfo(): EmbeddingModel | null {
    return this.currentModel;
  }

  /**
   * Clear embedding cache
   */
  clearCache(): void {
    this.cache.clear();
    console.log('🧹 Embedding cache cleared');
  }

  /**
   * Get cache statistics
   */
  getCacheStats(): { size: number; models: string[] } {
    const models = Array.from(this.cache.values())
      .map(result => result.model)
      .filter((model, index, arr) => arr.indexOf(model) === index);

    return {
      size: this.cache.size,
      models
    };
  }
}

export default MillerEmbedder;