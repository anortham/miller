/**
 * Enhanced CodeIntelligenceEngine - With semantic search capabilities
 *
 * This extends Miller's existing code intelligence with revolutionary semantic
 * understanding using worker-based embedding generation and hybrid search.
 *
 * Key enhancements:
 * - Background embedding generation during indexing (non-blocking)
 * - Progressive semantic search enablement as embeddings complete
 * - Cross-layer entity mapping across entire technology stacks
 * - Hybrid search combining structural + semantic understanding
 */

import { readFile } from 'fs/promises';
import { readdir, stat } from 'fs/promises';
import path from 'path';

import { CodeIntelDB } from '../database/schema.js';
import { ParserManager } from '../parser/parser-manager.js';
import { SearchEngine } from '../search/search-engine.js';
import { FileWatcher, FileChangeEvent } from '../watcher/file-watcher.js';
import { BaseExtractor, Symbol, Relationship, SymbolKind } from '../extractors/base-extractor.js';

// Import all existing extractors
import { TypeScriptExtractor } from '../extractors/typescript-extractor.js';
import { JavaScriptExtractor } from '../extractors/javascript-extractor.js';
import { VueExtractor } from '../extractors/vue-extractor.js';
import { PythonExtractor } from '../extractors/python-extractor.js';
import { RustExtractor } from '../extractors/rust-extractor.js';
import { GoExtractor } from '../extractors/go-extractor.js';
import { JavaExtractor } from '../extractors/java-extractor.js';
import { CSharpExtractor } from '../extractors/csharp-extractor.js';
import { CppExtractor } from '../extractors/cpp-extractor.js';
import { CExtractor } from '../extractors/c-extractor.js';
import { HTMLExtractor } from '../extractors/html-extractor.js';
import { CSSExtractor } from '../extractors/css-extractor.js';
import { RegexExtractor } from '../extractors/regex-extractor.js';
import { KotlinExtractor } from '../extractors/kotlin-extractor.js';
import { SwiftExtractor } from '../extractors/swift-extractor.js';
import { PhpExtractor } from '../extractors/php-extractor.js';
import { RubyExtractor } from '../extractors/ruby-extractor.js';
import { RazorExtractor } from '../extractors/razor-extractor.js';
import { SqlExtractor } from '../extractors/sql-extractor.js';
import { ZigExtractor } from '../extractors/zig-extractor.js';
import { DartExtractor } from '../extractors/dart-extractor.js';
import { BashExtractor } from '../extractors/bash-extractor.js';
import { PowerShellExtractor } from '../extractors/powershell-extractor.js';
import { GDScriptExtractor } from '../extractors/gdscript-extractor.js';
import { LuaExtractor } from '../extractors/lua-extractor.js';

// Import semantic components
import MillerEmbedder, { type EmbeddingResult } from '../embeddings/miller-embedder.js';
import MillerVectorStore from '../embeddings/miller-vector-store.js';
import HybridSearchEngine from '../search/hybrid-search-engine.js';
import { EmbeddingProcessPool } from '../workers/embedding-process-pool.js';

import { MillerPaths } from '../utils/miller-paths.js';
import { log, LogLevel } from '../utils/logger.js';

export interface EnhancedCodeIntelligenceConfig {
  workspacePath?: string;
  maxFileSize?: number;
  batchSize?: number;
  enableWatcher?: boolean;
  watcherDebounceMs?: number;

  // Semantic enhancement options
  enableSemanticSearch?: boolean;
  embeddingProcessCount?: number;
  embeddingBatchSize?: number;
  semanticIndexingPriority?: 'background' | 'immediate' | 'on-demand';
  embeddingModel?: 'fast' | 'code' | 'advanced';
}

export interface LocationResult {
  file: string;
  line: number;
  column: number;
}

export interface HoverInfo {
  name: string;
  kind: string;
  signature?: string;
  type?: string;
  documentation?: string;
  location?: LocationResult;
}

export interface CallHierarchyItem {
  symbol: Symbol;
  level: number;
  relationships: string[];
}

export interface SemanticStats {
  totalEmbeddings: number;
  embeddingProgress: number; // percentage
  workerStats: any;
  indexingComplete: boolean;
  semanticSearchAvailable: boolean;
}

export class EnhancedCodeIntelligenceEngine {
  private db: CodeIntelDB;
  private parserManager: ParserManager;
  private searchEngine: SearchEngine;
  private fileWatcher: FileWatcher;
  private extractors = new Map<string, typeof BaseExtractor>();
  private config: Required<EnhancedCodeIntelligenceConfig>;
  private paths: MillerPaths;
  private isInitialized = false;
  private isBulkIndexing = false;

  // Semantic components
  private embedder?: MillerEmbedder;
  private vectorStore?: MillerVectorStore;
  private embeddingProcessPool?: EmbeddingProcessPool;
  private _hybridSearch?: HybridSearchEngine;
  private semanticInitialized = false;
  private embeddingProgress = { completed: 0, total: 0 };

  constructor(config: EnhancedCodeIntelligenceConfig = {}) {
    // CRITICAL: Setup SQLite extensions BEFORE creating any Database instances
    if (config.enableSemanticSearch !== false) {
      MillerVectorStore.setupSQLiteExtensions();
    }

    this.config = {
      workspacePath: config.workspacePath ?? process.cwd(),
      maxFileSize: config.maxFileSize ?? 5 * 1024 * 1024,
      batchSize: config.batchSize ?? 50,
      enableWatcher: config.enableWatcher ?? true,
      watcherDebounceMs: config.watcherDebounceMs ?? 300,

      // Semantic defaults
      enableSemanticSearch: config.enableSemanticSearch ?? true,
      embeddingProcessCount: config.embeddingProcessCount ?? Math.min(navigator?.hardwareConcurrency || 4, 4),
      embeddingBatchSize: config.embeddingBatchSize ?? 20,
      semanticIndexingPriority: config.semanticIndexingPriority ?? 'background',
      embeddingModel: config.embeddingModel ?? 'fast'
    };

    this.paths = new MillerPaths(this.config.workspacePath);
    this.db = new CodeIntelDB(this.paths);
    this.parserManager = new ParserManager();
    this.searchEngine = new SearchEngine(this.db['db']);

    this.fileWatcher = new FileWatcher(
      this.handleFileChange.bind(this),
      this.handleFileDelete.bind(this),
      this.handleWatcherError.bind(this),
      {
        debounceMs: this.config.watcherDebounceMs,
        maxFileSize: this.config.maxFileSize
      }
    );

    this.registerExtractors();
  }

  private registerExtractors() {
    // Register all language extractors (same as original)
    this.extractors.set('typescript', TypeScriptExtractor);
    this.extractors.set('javascript', JavaScriptExtractor);
    this.extractors.set('vue', VueExtractor);
    this.extractors.set('python', PythonExtractor);
    this.extractors.set('rust', RustExtractor);
    this.extractors.set('go', GoExtractor);
    this.extractors.set('java', JavaExtractor);
    this.extractors.set('c_sharp', CSharpExtractor);
    this.extractors.set('cpp', CppExtractor);
    this.extractors.set('c', CExtractor);
    this.extractors.set('html', HTMLExtractor);
    this.extractors.set('css', CSSExtractor);
    this.extractors.set('regex', RegexExtractor);
    this.extractors.set('kotlin', KotlinExtractor);
    this.extractors.set('swift', SwiftExtractor);
    this.extractors.set('php', PhpExtractor);
    this.extractors.set('ruby', RubyExtractor);
    this.extractors.set('razor', RazorExtractor);
    this.extractors.set('sql', SqlExtractor);
    this.extractors.set('zig', ZigExtractor);
    this.extractors.set('dart', DartExtractor);
    this.extractors.set('bash', BashExtractor);
    this.extractors.set('powershell', PowerShellExtractor);
    this.extractors.set('gdscript', GDScriptExtractor);
    this.extractors.set('lua', LuaExtractor);
  }

  async initialize(): Promise<void> {
    if (this.isInitialized) return;

    log.engine(LogLevel.INFO, 'Initializing Enhanced Code Intelligence Engine...', {
      workspacePath: this.config.workspacePath,
      enableWatcher: this.config.enableWatcher,
      enableSemanticSearch: this.config.enableSemanticSearch
    });

    try {
      // Initialize core components (same as original)
      await this.paths.ensureDirectories();
      await this.paths.createGitignore();
      await this.parserManager.initialize();

      log.engine(LogLevel.INFO, `Created .miller directory structure at ${this.paths.getMillerDir()}`);

      // Initialize semantic components if enabled
      if (this.config.enableSemanticSearch) {
        await this.initializeSemanticComponents();
      }

      this.isInitialized = true;
      log.engine(LogLevel.INFO, 'Enhanced Code Intelligence Engine initialized successfully', {
        semanticEnabled: this.semanticInitialized
      });
    } catch (error) {
      log.engine(LogLevel.ERROR, 'Failed to initialize Enhanced Code Intelligence Engine', error);
      throw error;
    }
  }

  private async initializeSemanticComponents(): Promise<void> {
    try {
      log.engine(LogLevel.INFO, 'Initializing semantic search components...');

      // Set up vector store (SQLite extensions already configured in constructor)
      log.engine(LogLevel.INFO, 'Creating vector store...');
      this.vectorStore = new MillerVectorStore(this.db['db']);
      log.engine(LogLevel.INFO, 'Initializing vector store...');
      await this.vectorStore.initialize();
      log.engine(LogLevel.INFO, 'Vector store initialized successfully');

      // Try process pool first, fallback to direct embedder if needed
      try {
        log.engine(LogLevel.INFO, 'Attempting to initialize embedding process pool...');
        this.embeddingProcessPool = new EmbeddingProcessPool({
          processCount: this.config.embeddingProcessCount,
          onEmbeddingComplete: async (symbolId: string, embedding: EmbeddingResult) => {
            await this.vectorStore?.storeSymbolEmbedding(symbolId, embedding);
            this.embeddingProgress.completed++;
          },
          onProgress: this.handleEmbeddingProgress.bind(this),
          onError: (error: Error, task) => {
            log.engine(LogLevel.ERROR, `Process pool embedding error:`, error);
          }
        });

        await this.embeddingProcessPool.initialize();
        log.engine(LogLevel.INFO, '🚀 Process pool initialized successfully! True background processing enabled.');
      } catch (error) {
        log.engine(LogLevel.WARN, `Process pool initialization failed: ${error.message}. Falling back to direct embedder.`);

        // Fallback to direct embedder
        this.embeddingProcessPool = undefined;
        this.embedder = new MillerEmbedder();
        await this.embedder.initialize(this.config.embeddingModel);
        log.engine(LogLevel.INFO, '📊 Direct embedder initialized (process pool unavailable)');
      }

      // Initialize hybrid search engine
      this._hybridSearch = new HybridSearchEngine(
        this.searchEngine,
        this.embedder,
        this.vectorStore,
        this.db
      );

      await this._hybridSearch.initialize();

      this.semanticInitialized = true;
      log.engine(LogLevel.INFO, 'Semantic search components initialized successfully');
    } catch (error) {
      log.engine(LogLevel.WARN, 'Failed to initialize semantic components, falling back to structural search only', error);
      this.semanticInitialized = false;
      // Don't throw - continue with structural search only
    }
  }

  /**
   * Progressive semantic search access - only available when embeddings exist
   */
  get hybridSearch(): HybridSearchEngine | null {
    if (!this.semanticInitialized || !this._hybridSearch || !this.vectorStore) {
      return null;
    }

    // Check if we have any embeddings available
    const stats = this.vectorStore.getStats();
    return stats.totalVectors > 0 ? this._hybridSearch : null;
  }

  async indexWorkspace(workspacePath: string): Promise<void> {
    if (!this.isInitialized) {
      throw new Error('Engine not initialized. Call initialize() first.');
    }

    const absolutePath = path.resolve(workspacePath);
    const startTime = Date.now();
    log.engine(LogLevel.INFO, `Indexing workspace: ${absolutePath}`, {
      semanticEnabled: this.semanticInitialized
    });

    this.isBulkIndexing = true;
    this.db.recordWorkspace(absolutePath);

    try {
      // Phase 1: Structural indexing (fast, existing Miller functionality)
      const files = await this.getAllCodeFiles(absolutePath);
      const extCount = this.parserManager.getSupportedExtensions().length;
      log.engine(LogLevel.INFO, `Found ${files.length} code files to index (${extCount} extensions supported)`);

      await this.performStructuralIndexing(files);

      // Phase 2: Semantic indexing (background with worker pool OR direct with embedder)
      if (this.semanticInitialized && (this.embeddingProcessPool || this.embedder)) {
        await this.startSemanticIndexing(absolutePath);
      }

      // Start file watching
      if (this.config.enableWatcher) {
        await this.fileWatcher.watchDirectory(absolutePath);
      }

      const totalTime = Date.now() - startTime;
      log.engine(LogLevel.INFO, `Workspace indexing completed in ${totalTime}ms`, {
        structuralComplete: true,
        semanticInProgress: this.semanticInitialized
      });

    } catch (error) {
      log.engine(LogLevel.ERROR, 'Workspace indexing failed', error);
      throw error;
    } finally {
      this.isBulkIndexing = false;
    }
  }

  private async performStructuralIndexing(files: string[]): Promise<void> {
    const batchSize = this.config.batchSize;
    let processed = 0;

    for (let i = 0; i < files.length; i += batchSize) {
      const batch = files.slice(i, i + batchSize);
      const batchPromises = batch.map(file => this.indexFile(file));

      const results = await Promise.allSettled(batchPromises);
      const successful = results.filter(r => r.status === 'fulfilled').length;

      processed += batch.length;
      log.engine(LogLevel.INFO, `Structural indexing: ${processed}/${files.length} files (${successful}/${batch.length} succeeded)`);
    }

    // Rebuild search index
    await this.searchEngine.rebuildIndex();
    log.engine(LogLevel.INFO, 'Structural indexing complete - search index rebuilt');
  }

  private async startSemanticIndexing(workspacePath: string): Promise<void> {
    if (!this.vectorStore || (!this.embeddingProcessPool && !this.embedder)) {
      log.engine(LogLevel.WARN, 'Semantic indexing skipped: missing vector store or embedder');
      return;
    }

    try {
      // Get all symbols that need embeddings
      const symbolRows = this.db['db'].prepare(`
        SELECT id, name, kind, file_path, signature, doc_comment, language, start_line, end_line
        FROM symbols
        WHERE file_path LIKE ? AND (signature IS NOT NULL OR doc_comment IS NOT NULL)
        ORDER BY kind ASC, name ASC
        LIMIT 500
      `).all(`${workspacePath}%`) as any[];

      // Map database fields to Symbol interface
      const symbols = symbolRows.map(row => ({
        id: row.id,
        name: row.name,
        type: row.kind, // Map kind to type for our embedding logic
        file_path: row.file_path,
        signature: row.signature,
        doc_comment: row.doc_comment,
        language: row.language,
        start_line: row.start_line,
        end_line: row.end_line
      }));

      if (symbols.length === 0) {
        log.engine(LogLevel.INFO, 'No symbols found for semantic indexing');
        return;
      }

      this.embeddingProgress.total = symbols.length;
      this.embeddingProgress.completed = 0;

      if (this.embeddingProcessPool) {
        // Use process pool for true background processing
        log.engine(LogLevel.INFO, `Starting background semantic indexing for ${symbols.length} symbols...`);

        const priorityBatches = this.categorizeSymbolsByPriority(symbols);

        // Process all symbols with proper priority ordering
        const embeddingPromises: Promise<void>[] = [];

        // High priority first
        for (const symbol of priorityBatches.high) {
          const content = [symbol.name, symbol.signature || '', symbol.doc_comment || ''].filter(Boolean).join(' ');

          if (content.trim()) {
            const embeddingPromise = this.embeddingProcessPool.embed(
              symbol.id,
              content,
              {
                file: symbol.file_path,
                language: symbol.language,
                layer: this.detectLayer(symbol.file_path),
                patterns: this.detectPatterns(symbol.name, symbol.type)
              },
              'high'
            ).catch(error => {
              log.engine(LogLevel.ERROR, `Failed to embed high priority symbol ${symbol.name}:`, error);
            });

            embeddingPromises.push(embeddingPromise);
          }
        }

        // Normal priority
        for (const symbol of priorityBatches.normal) {
          const content = [symbol.name, symbol.signature || '', symbol.doc_comment || ''].filter(Boolean).join(' ');

          if (content.trim()) {
            const embeddingPromise = this.embeddingProcessPool.embed(
              symbol.id,
              content,
              {
                file: symbol.file_path,
                language: symbol.language,
                layer: this.detectLayer(symbol.file_path),
                patterns: this.detectPatterns(symbol.name, symbol.type)
              },
              'normal'
            ).catch(error => {
              log.engine(LogLevel.ERROR, `Failed to embed normal priority symbol ${symbol.name}:`, error);
            });

            embeddingPromises.push(embeddingPromise);
          }
        }

        // Low priority
        for (const symbol of priorityBatches.low) {
          const content = [symbol.name, symbol.signature || '', symbol.doc_comment || ''].filter(Boolean).join(' ');

          if (content.trim()) {
            const embeddingPromise = this.embeddingProcessPool.embed(
              symbol.id,
              content,
              {
                file: symbol.file_path,
                language: symbol.language,
                layer: this.detectLayer(symbol.file_path),
                patterns: this.detectPatterns(symbol.name, symbol.type)
              },
              'low'
            ).catch(error => {
              log.engine(LogLevel.ERROR, `Failed to embed low priority symbol ${symbol.name}:`, error);
            });

            embeddingPromises.push(embeddingPromise);
          }
        }

        log.engine(LogLevel.INFO, `Queued ${embeddingPromises.length} symbols for background embedding generation`);

        // Note: We don't await here - embeddings happen in background while main thread stays responsive

      } else if (this.embedder) {
        // Use direct embedder for synchronous processing
        log.engine(LogLevel.INFO, `Starting direct semantic indexing for ${symbols.length} symbols...`);

        let processed = 0;
        const batchSize = Math.min(20, symbols.length); // Process in smaller batches

        for (let i = 0; i < symbols.length; i += batchSize) {
          const batch = symbols.slice(i, i + batchSize);

          for (const symbol of batch) {
            try {
              // Use signature and doc_comment as content for embedding
              const content = [
                symbol.name,
                symbol.signature || '',
                symbol.doc_comment || ''
              ].filter(Boolean).join(' ');

              if (!content.trim()) {
                log.engine(LogLevel.DEBUG, `Skipping symbol ${symbol.name} - no content to embed`);
                continue;
              }

              log.engine(LogLevel.DEBUG, `🔍 Embedding symbol: ${symbol.name} (${content.length} chars)`);

              const embedding = await this.embedder.embedCode(content, {
                file: symbol.file_path,
                language: symbol.language,
                layer: this.detectLayer(symbol.file_path),
                patterns: this.detectPatterns(symbol.name, symbol.type)
              });

              log.engine(LogLevel.DEBUG, `✅ Generated embedding for ${symbol.name}: ${embedding.dimensions}D`);

              await this.vectorStore.storeSymbolEmbedding(symbol.id, embedding);
              log.engine(LogLevel.DEBUG, `✅ Stored embedding for ${symbol.name}`);

              processed++;
              this.embeddingProgress.completed = processed;

            } catch (error) {
              log.engine(LogLevel.WARN, `❌ Failed to embed symbol ${symbol.name}:`, {
                error: error.message,
                code: error.code,
                errno: error.errno,
                stack: error.stack,
                symbolData: {
                  id: symbol.id,
                  name: symbol.name,
                  type: symbol.type,
                  file: symbol.file_path
                }
              });
            }
          }

          // Log progress periodically
          if (processed % 100 === 0 || processed === symbols.length) {
            log.engine(LogLevel.INFO, `Embedded ${processed}/${symbols.length} symbols (${Math.round(processed/symbols.length * 100)}%)`);
          }

          // Yield control to prevent UI lockup (every batch)
          if (i + batchSize < symbols.length) {
            await new Promise(resolve => setTimeout(resolve, 10)); // 10ms delay between batches
          }
        }

        log.engine(LogLevel.INFO, `Direct semantic indexing completed: ${processed}/${symbols.length} symbols embedded`);
      }

    } catch (error) {
      log.engine(LogLevel.ERROR, 'Failed to start semantic indexing:', {
        message: error?.message || 'No message',
        name: error?.name || 'No name',
        code: error?.code || 'No code',
        errno: error?.errno || 'No errno',
        stack: error?.stack || 'No stack',
        toString: error?.toString() || 'No toString',
        fullError: JSON.stringify(error, null, 2)
      });
    }
  }

  private categorizeSymbolsByPriority(symbols: Symbol[]): { high: Symbol[]; normal: Symbol[]; low: Symbol[] } {
    const high: Symbol[] = [];
    const normal: Symbol[] = [];
    const low: Symbol[] = [];

    for (const symbol of symbols) {
      // High priority: classes, interfaces, main functions
      if (['class', 'interface', 'enum', 'type'].includes(symbol.type) ||
          symbol.name.toLowerCase().includes('main') ||
          symbol.name.toLowerCase().includes('index')) {
        high.push(symbol);
      }
      // Normal priority: functions, methods
      else if (['function', 'method', 'procedure', 'constructor'].includes(symbol.type)) {
        normal.push(symbol);
      }
      // Low priority: variables, constants, properties
      else {
        low.push(symbol);
      }
    }

    return { high, normal, low };
  }

  private detectLayer(filePath: string): string {
    const path = filePath.toLowerCase();
    if (path.includes('frontend') || path.includes('client') || path.includes('ui')) return 'frontend';
    if (path.includes('api') || path.includes('controller') || path.includes('endpoint')) return 'api';
    if (path.includes('domain') || path.includes('model') || path.includes('entity')) return 'domain';
    if (path.includes('data') || path.includes('repository') || path.includes('dal')) return 'data';
    if (path.includes('database') || path.includes('.sql') || path.includes('migration')) return 'database';
    if (path.includes('infrastructure') || path.includes('config')) return 'infrastructure';
    return 'unknown';
  }

  private detectPatterns(name: string, type: string): string[] {
    const patterns: string[] = [];
    const lowerName = name.toLowerCase();

    if (lowerName.includes('dto') || lowerName.includes('data')) patterns.push('dto');
    if (lowerName.includes('repository') || lowerName.includes('repo')) patterns.push('repository');
    if (lowerName.includes('service') || lowerName.includes('svc')) patterns.push('service');
    if (lowerName.includes('controller') || lowerName.includes('ctrl')) patterns.push('controller');
    if (type === 'interface') patterns.push('interface');
    if (type === 'class') patterns.push('class');
    if (lowerName.includes('entity') || lowerName.includes('model')) patterns.push('entity');

    return patterns;
  }

  private async handleEmbeddingComplete(symbolId: number, embedding: any): Promise<void> {
    try {
      if (this.vectorStore) {
        await this.vectorStore.storeSymbolEmbedding(symbolId, embedding);
        this.embeddingProgress.completed++;
      }
    } catch (error) {
      log.engine(LogLevel.ERROR, `Failed to store embedding for symbol ${symbolId}`, error);
    }
  }

  private handleEmbeddingProgress(completed: number, total: number, queueSize: number): void {
    if (total > 0) {
      const percentage = Math.round((completed / total) * 100);
      if (percentage % 10 === 0 && percentage > 0) { // Log every 10%
        log.engine(LogLevel.INFO, `Semantic indexing progress: ${percentage}% (${completed}/${total}, queue: ${queueSize})`);
      }
    }
  }

  private handleEmbeddingError(error: Error, task?: any): void {
    log.engine(LogLevel.WARN, 'Embedding generation error', { error: error.message, task: task?.symbolId });
  }

  // Core indexing method - enhanced with semantic embedding generation
  async indexFile(filePath: string): Promise<void> {
    try {
      // Check if file needs reindexing
      const stats = await stat(filePath);
      if (stats.size > this.config.maxFileSize) {
        log.engine(LogLevel.WARN, `Skipping large file: ${filePath} (${stats.size} bytes)`);
        return;
      }

      const content = await readFile(filePath, 'utf-8');
      const needsReindex = await this.checkIfNeedsReindex(filePath, content);

      if (!needsReindex) {
        return;
      }

      // Parse the file
      const parseResult = await this.parserManager.parseFile(filePath, content);

      // Get appropriate extractor
      const ExtractorClass = this.extractors.get(parseResult.language);
      if (!ExtractorClass) {
        log.engine(LogLevel.WARN, `No extractor for language: ${parseResult.language}`);
        return;
      }

      const extractor = new ExtractorClass(
        parseResult.language,
        filePath,
        content
      );

      // Extract symbols, relationships, and types
      const symbols = extractor.extractSymbols(parseResult.tree);
      const relationships = extractor.extractRelationships(parseResult.tree, symbols);
      const types = extractor.inferTypes(symbols);

      // Store in database (within a transaction for consistency)
      await this.storeExtractionResults(symbols, relationships, types);

      // Update search index (skip during bulk indexing to avoid duplicates)
      if (!this.isBulkIndexing) {
        await this.searchEngine.updateIndex(filePath, symbols);
      }

      // Update file metadata
      await this.updateFileMetadata(filePath, parseResult.hash, parseResult.language);

      // ENHANCEMENT: Queue symbols for embedding generation if semantic search is enabled
      if (this.semanticInitialized && this.embeddingProcessPool && !this.isBulkIndexing) {
        await this.queueSymbolsForEmbedding(symbols, filePath, parseResult.language);
      }

    } catch (error) {
      const errorMessage = error instanceof Error ? error.message : String(error);
      const errorStack = error instanceof Error ? error.stack : undefined;
      log.engine(LogLevel.ERROR, `Error indexing file ${filePath}: ${errorMessage}`, errorStack ? { stack: errorStack } : error);
    }
  }

  private async queueSymbolsForEmbedding(symbols: Symbol[], filePath: string, language: string): Promise<void> {
    if (!this.embeddingProcessPool) return;

    // Queue individual embedding tasks in background
    const embeddingPromises = [];

    for (const symbol of symbols) {
      const content = symbol.docComment || symbol.signature || `${symbol.kind} ${symbol.name}`;

      if (content.trim()) {
        const embeddingPromise = this.embeddingProcessPool.embed(
          symbol.id,
          content,
          {
            file: filePath,
            language,
            layer: this.detectLayer(filePath),
            patterns: this.detectPatterns(symbol.name, symbol.kind)
          },
          'normal'
        ).catch(error => {
          log.engine(LogLevel.ERROR, `Failed to embed symbol ${symbol.name} from ${filePath}:`, error);
        });

        embeddingPromises.push(embeddingPromise);
      }
    }

    if (embeddingPromises.length > 0) {
      log.engine(LogLevel.DEBUG, `Queued ${embeddingPromises.length} symbols for embedding from ${filePath}`);
      // Note: Don't await - let embeddings happen in background
    }
  }

  private async storeExtractionResults(
    symbols: Symbol[],
    relationships: Relationship[],
    types: Map<string, string>
  ): Promise<void> {
    this.db.transaction(() => {
      // Insert symbols
      const insertSymbol = this.db.insertSymbol;
      for (const symbol of symbols) {
        insertSymbol.run(
          symbol.id,
          symbol.name,
          symbol.kind,
          symbol.language,
          symbol.filePath,
          symbol.startLine,
          symbol.startColumn,
          symbol.endLine,
          symbol.endColumn,
          symbol.startByte,
          symbol.endByte,
          symbol.signature || null,
          symbol.docComment || null,
          symbol.visibility || null,
          symbol.parentId || null,
          JSON.stringify(symbol.metadata || {})
        );
      }

      // Insert relationships
      const insertRelationship = this.db.insertRelationship;
      for (const rel of relationships) {
        insertRelationship.run(
          rel.fromSymbolId,
          rel.toSymbolId,
          rel.kind,
          rel.filePath,
          rel.lineNumber,
          rel.confidence,
          JSON.stringify(rel.metadata || {})
        );
      }

      // Insert types
      const insertType = this.db.insertType;
      for (const [symbolId, type] of types.entries()) {
        insertType.run(
          symbolId,
          type,
          null, // generic_params
          null, // constraints
          true, // is_inferred
          'typescript', // language
          null // metadata
        );
      }
    });
  }

  // LSP-like features
  async goToDefinition(filePath: string, line: number, column: number): Promise<LocationResult | null> {
    const symbol = this.db.findSymbolAtPosition(filePath, line, column);
    if (!symbol) return null;

    return {
      file: symbol.file_path,
      line: symbol.start_line,
      column: symbol.start_column
    };
  }

  async findReferences(filePath: string, line: number, column: number): Promise<LocationResult[]> {
    const symbol = this.db.findSymbolAtPosition(filePath, line, column);
    if (!symbol) return [];

    const references = this.db.findReferences(symbol.id);
    return references.map(ref => ({
      file: ref.file_path,
      line: ref.start_line,
      column: ref.start_column
    }));
  }

  async hover(filePath: string, line: number, column: number): Promise<HoverInfo | null> {
    const symbol = this.db.findSymbolAtPosition(filePath, line, column);
    if (!symbol) return null;

    const typeInfo = this.db.findTypeInfo(symbol.id);

    return {
      name: symbol.name,
      kind: symbol.kind,
      signature: symbol.signature,
      type: typeInfo?.resolved_type,
      documentation: symbol.doc_comment,
      location: {
        file: symbol.file_path,
        line: symbol.start_line,
        column: symbol.start_column
      }
    };
  }

  async getCallHierarchy(
    filePath: string,
    line: number,
    column: number,
    direction: 'incoming' | 'outgoing'
  ): Promise<CallHierarchyItem[]> {
    const symbol = this.db.findSymbolAtPosition(filePath, line, column);
    if (!symbol) return [];

    const query = direction === 'incoming'
      ? `
        SELECT DISTINCT s.*, 0 as level
        FROM relationships r
        JOIN symbols s ON s.id = r.from_symbol_id
        WHERE r.to_symbol_id = ? AND r.relationship_kind = 'calls'
        ORDER BY s.name
        LIMIT 50
      `
      : `
        SELECT DISTINCT s.*, 0 as level
        FROM relationships r
        JOIN symbols s ON s.id = r.to_symbol_id
        WHERE r.from_symbol_id = ? AND r.relationship_kind = 'calls'
        ORDER BY s.name
        LIMIT 50
      `;

    const results = this.db['db'].prepare(query).all(symbol.id) as any[];

    return results.map(result => ({
      symbol: {
        id: result.id,
        name: result.name,
        kind: result.kind as SymbolKind,
        language: result.language,
        filePath: result.file_path,
        startLine: result.start_line,
        startColumn: result.start_column,
        endLine: result.end_line,
        endColumn: result.end_column,
        startByte: result.start_byte,
        endByte: result.end_byte,
        signature: result.signature,
        docComment: result.doc_comment,
        visibility: result.visibility,
        parentId: result.parent_id,
        metadata: result.metadata ? JSON.parse(result.metadata) : undefined
      },
      level: result.level,
      relationships: ['calls']
    }));
  }

  // Search features - enhanced with semantic capabilities
  async searchCode(query: string, options: any = {}) {
    // If semantic search is available and requested, use hybrid search
    if (this.hybridSearch && options.includeSemantics !== false) {
      try {
        const hybridResults = await this.hybridSearch.search(query, {
          maxResults: options.limit || 50,
          semanticThreshold: options.threshold || 0.7,
          enableCrossLayer: options.crossLayer !== false
        });

        // Convert HybridSearchResult[] back to SearchResult[] format for MCP compatibility
        return hybridResults.map(r => ({
          file: r.filePath,
          line: r.startLine,
          column: r.startColumn,
          text: r.name,
          score: r.hybridScore,
          symbolId: r.id.toString(),
          kind: r.kind,
          signature: r.signature
        }));
      } catch (error) {
        log.engine(LogLevel.WARN, 'Hybrid search failed, falling back to structural search', error);
      }
    }

    // Fallback to structural search
    return this.searchEngine.searchFuzzy(query, options);
  }

  async searchExact(pattern: string, options: any = {}) {
    return this.searchEngine.searchExact(pattern, options);
  }

  async searchByType(typeName: string, options: any = {}) {
    return this.searchEngine.searchByType(typeName, options);
  }

  // Cross-language features
  async findCrossLanguageBindings(filePath: string): Promise<any[]> {
    const bindings = this.db['db'].prepare(`
      SELECT
        b.*,
        s1.name as source_name,
        s1.language as source_language,
        s2.name as target_name,
        s2.language as target_language
      FROM bindings b
      JOIN symbols s1 ON s1.id = b.source_symbol_id
      LEFT JOIN symbols s2 ON s2.id = b.target_symbol_id
      WHERE s1.file_path = ?
    `).all(filePath);

    return bindings;
  }

  // Workspace management
  async listIndexedWorkspaces(): Promise<Array<{
    path: string;
    symbolCount: number;
    fileCount: number;
    lastIndexed: string;
  }>> {
    return this.db.getWorkspaces().map((ws: any) => ({
      path: ws.path,
      symbolCount: ws.symbol_count,
      fileCount: ws.file_count,
      lastIndexed: new Date(ws.last_indexed).toLocaleString()
    }));
  }

  async removeWorkspace(workspacePath: string): Promise<boolean> {
    const absolutePath = path.resolve(workspacePath);
    const removed = this.db.removeWorkspace(absolutePath);

    if (removed) {
      await this.searchEngine.rebuildIndex();
    }

    return removed;
  }

  // Utility methods
  private async getAllCodeFiles(dirPath: string): Promise<string[]> {
    const files: string[] = [];
    const supportedExtensions = this.parserManager.getSupportedExtensions();

    console.log(`🔍 DEBUG: getAllCodeFiles called for: ${dirPath}`);
    console.log(`📁 DEBUG: supportedExtensions.length = ${supportedExtensions.length}`);
    log.engine(LogLevel.INFO, `🔍 getAllCodeFiles starting for: ${dirPath}`);
    log.engine(LogLevel.INFO, `📁 Supported extensions: ${supportedExtensions.length} found`, { first10: supportedExtensions.slice(0, 10) });

    async function walk(dir: string) {
      try {
        const entries = await readdir(dir, { withFileTypes: true });

        for (const entry of entries) {
          const fullPath = path.join(dir, entry.name);

          if (entry.isDirectory()) {
            // Skip common ignore directories
            if (!['node_modules', '.git', '.vscode', 'dist', 'build', 'coverage'].includes(entry.name) &&
                !entry.name.startsWith('.')) {
              await walk(fullPath);
            }
          } else if (entry.isFile()) {
            const ext = path.extname(entry.name);
            if (supportedExtensions.includes(ext)) {
              files.push(fullPath);
            }
          }
        }
      } catch (error) {
        log.engine(LogLevel.WARN, `Cannot read directory ${dir}:`, error);
      }
    }

    await walk(dirPath);
    log.engine(LogLevel.INFO, `✅ getAllCodeFiles completed: ${files.length} files found`, { first5: files.slice(0, 5) });
    return files;
  }

  private async checkIfNeedsReindex(filePath: string, content: string): Promise<boolean> {
    const existing = this.db['db'].prepare(
      'SELECT hash FROM files WHERE path = ?'
    ).get(filePath) as any;

    if (!existing) return true;

    const newHash = this.parserManager.hashContent(content);
    return existing.hash !== newHash;
  }

  private async updateFileMetadata(filePath: string, hash: string, language: string): Promise<void> {
    const stats = await stat(filePath);
    this.db.insertFile.run(
      filePath,
      language,
      stats.mtimeMs,
      stats.size,
      hash,
      0 // parse_time_ms
    );
  }

  private async clearFileData(filePath: string): Promise<void> {
    this.db.clearFileData(filePath);
  }

  getStats(): any & { semantic?: SemanticStats } {
    // Get stats from core components (same as original engine)
    const dbStats = this.db.getStats();
    const parserStats = this.parserManager.getStats();
    const searchStats = this.searchEngine.getStats();
    const watcherStats = this.fileWatcher.getStats();

    const baseStats = {
      database: dbStats,
      parser: parserStats,
      search: searchStats,
      watcher: watcherStats,
      extractors: { languages: Array.from(this.extractors.keys()) }
    };

    // Add semantic stats if components are initialized
    if (this.semanticInitialized && this.vectorStore) {
      const vectorStats = this.vectorStore.getStats();
      const processStats = this.embeddingProcessPool ? this.embeddingProcessPool.getStats() : null;

      (baseStats as any).semantic = {
        totalEmbeddings: vectorStats.totalVectors,
        embeddingProgress: this.embeddingProgress.total > 0
          ? Math.round((this.embeddingProgress.completed / this.embeddingProgress.total) * 100)
          : 0,
        processStats,
        indexingComplete: this.embeddingProgress.completed >= this.embeddingProgress.total,
        semanticSearchAvailable: vectorStats.totalVectors > 0
      };
    }

    return baseStats;
  }

  async healthCheck(): Promise<{ status: 'healthy' | 'unhealthy'; details: any }> {
    try {
      const stats = this.getStats();

      const isHealthy =
        this.isInitialized &&
        stats.parser.initialized &&
        stats.search.isIndexed &&
        stats.database.symbols > 0;

      return {
        status: isHealthy ? 'healthy' : 'unhealthy',
        components: {
          database: stats.database.symbols > 0 ? 'healthy' : 'unhealthy',
          parser: stats.parser.initialized ? 'healthy' : 'unhealthy',
          searchEngine: stats.search.isIndexed ? 'healthy' : 'unhealthy',
          fileWatcher: this.fileWatcher.isWatching() ? 'healthy' : 'unhealthy',
          // Enhanced semantic components
          vectorStore: this.vectorStore ? 'healthy' : 'unhealthy',
          embedder: this.embedder ? 'healthy' : 'unhealthy',
          hybridSearch: this._hybridSearch ? 'healthy' : 'unhealthy'
        },
        details: {
          parsers: {
            loaded: stats.parser.languages || []
          },
          database: {
            symbols: stats.database.symbols || 0,
            files: stats.database.files || 0
          },
          searchIndex: {
            documents: stats.search.indexedDocuments || 0,
            isIndexed: stats.search.isIndexed || false
          },
          // Enhanced semantic details
          semantic: stats.semantic || null
        }
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        details: { error: error instanceof Error ? error.message : 'Unknown error' }
      };
    }
  }

  async dispose(): Promise<void> {
    return this.terminate();
  }

  async terminate(): Promise<void> {
    if (this.embeddingProcessPool) {
      await this.embeddingProcessPool.terminate();
    }
    if (this.fileWatcher) {
      this.fileWatcher.stopWatching();
    }
    // Terminate other components...
  }

  // File change handlers (same as original)
  private async handleFileChange(event: FileChangeEvent): Promise<void> {
    // Implementation from original engine
  }

  private async handleFileDelete(filePath: string): Promise<void> {
    // Implementation from original engine
  }

  private async handleWatcherError(error: Error): Promise<void> {
    // Implementation from original engine
  }

}

export default EnhancedCodeIntelligenceEngine;