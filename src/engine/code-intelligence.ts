import { readFile } from 'fs/promises';
import { readdir, stat } from 'fs/promises';
import path from 'path';

import { CodeIntelDB } from '../database/schema.js';
import { ParserManager } from '../parser/parser-manager.js';
import { SearchEngine } from '../search/search-engine.js';
import { FileWatcher, FileChangeEvent } from '../watcher/file-watcher.js';
import { BaseExtractor, Symbol, Relationship, SymbolKind } from '../extractors/base-extractor.js';
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
import { MillerPaths } from '../utils/miller-paths.js';
import { log, LogLevel } from '../utils/logger.js';

export interface CodeIntelligenceConfig {
  workspacePath?: string;
  maxFileSize?: number;
  batchSize?: number;
  enableWatcher?: boolean;
  watcherDebounceMs?: number;
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

export class CodeIntelligenceEngine {
  private db: CodeIntelDB;
  private parserManager: ParserManager;
  private searchEngine: SearchEngine;
  private fileWatcher: FileWatcher;
  private extractors = new Map<string, typeof BaseExtractor>();
  private config: Required<CodeIntelligenceConfig>;
  private paths: MillerPaths;
  private isInitialized = false;

  constructor(config: CodeIntelligenceConfig = {}) {
    this.config = {
      workspacePath: config.workspacePath ?? process.cwd(),
      maxFileSize: config.maxFileSize ?? 5 * 1024 * 1024,
      batchSize: config.batchSize ?? 10,
      enableWatcher: config.enableWatcher ?? true,
      watcherDebounceMs: config.watcherDebounceMs ?? 300
    };

    this.paths = new MillerPaths(this.config.workspacePath);
    this.db = new CodeIntelDB(this.paths);
    this.parserManager = new ParserManager();
    this.searchEngine = new SearchEngine(this.db['db']); // Access the underlying Database

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
    // Register language extractors
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
    // Additional extractors can be registered here
  }

  async initialize(): Promise<void> {
    if (this.isInitialized) return;

    log.engine(LogLevel.INFO, 'Initializing Code Intelligence Engine...', {
      workspacePath: this.config.workspacePath,
      enableWatcher: this.config.enableWatcher
    });

    try {
      // Ensure Miller directories exist
      await this.paths.ensureDirectories();
      await this.paths.createGitignore();

      log.engine(LogLevel.INFO, `Created .miller directory structure at ${this.paths.getMillerDir()}`);

      await this.parserManager.initialize();
      await this.searchEngine.indexSymbols();

      this.isInitialized = true;
      log.engine(LogLevel.INFO, 'Code Intelligence Engine initialized successfully');
    } catch (error) {
      log.engine(LogLevel.ERROR, 'Failed to initialize Code Intelligence Engine', error);
      throw error;
    }
  }

  async indexWorkspace(workspacePath: string): Promise<void> {
    if (!this.isInitialized) {
      throw new Error('Engine not initialized. Call initialize() first.');
    }

    const absolutePath = path.resolve(workspacePath);
    log.engine(LogLevel.INFO, `Indexing workspace: ${absolutePath}`);

    try {
      // Get all code files
      const files = await this.getAllCodeFiles(absolutePath);
      log.engine(LogLevel.INFO, `Found ${files.length} code files to index`);

      // Process files in batches for better performance
      const batchSize = this.config.batchSize;
      let processed = 0;

      for (let i = 0; i < files.length; i += batchSize) {
        const batch = files.slice(i, i + batchSize);
        const batchPromises = batch.map(file => this.indexFile(file));

        try {
          await Promise.allSettled(batchPromises);
          processed += batch.length;
          log.engine(LogLevel.INFO, `Indexed ${Math.min(processed, files.length)}/${files.length} files`);
        } catch (error) {
          console.error(`Error processing batch starting at index ${i}:`, error);
        }
      }

      // Rebuild search index with all symbols
      await this.searchEngine.rebuildIndex();

      // Start watching for changes if enabled
      if (this.config.enableWatcher) {
        await this.fileWatcher.watchDirectory(absolutePath);
      }

      log.engine(LogLevel.INFO, 'Workspace indexing complete');
    } catch (error) {
      console.error('Error indexing workspace:', error);
      throw error;
    }
  }

  private async indexFile(filePath: string): Promise<void> {
    try {
      // Check if file needs reindexing
      const stats = await stat(filePath);
      if (stats.size > this.config.maxFileSize) {
        console.warn(`Skipping large file: ${filePath} (${stats.size} bytes)`);
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
        console.warn(`No extractor for language: ${parseResult.language}`);
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

      // Update search index
      await this.searchEngine.updateIndex(filePath, symbols);

      // Update file metadata
      await this.updateFileMetadata(filePath, parseResult.hash, parseResult.language);

    } catch (error) {
      console.error(`Error indexing file ${filePath}:`, error);
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

  private async handleFileChange(event: FileChangeEvent): Promise<void> {
    log.watcher(LogLevel.INFO, `File ${event.type}: ${event.filePath}`);

    try {
      if (event.type === 'delete') {
        await this.handleFileDelete(event.filePath);
        return;
      }

      // For create/change events, reindex the file
      if (event.content) {
        // Clear old data first
        await this.clearFileData(event.filePath);

        // Reindex with new content
        await this.indexFile(event.filePath);
      }
    } catch (error) {
      console.error(`Error handling file change for ${event.filePath}:`, error);
    }
  }

  private async handleFileDelete(filePath: string): Promise<void> {
    log.watcher(LogLevel.INFO, `File deleted: ${filePath}`);
    await this.clearFileData(filePath);
    await this.searchEngine.removeFromIndex(filePath);
  }

  private handleWatcherError(error: Error, filePath?: string): void {
    console.error(`File watcher error${filePath ? ` for ${filePath}` : ''}:`, error);
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

    // This is a simplified implementation - a full implementation would use recursive CTEs
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

  // Cross-language features
  async findCrossLanguageBindings(filePath: string): Promise<any[]> {
    // Find API calls, FFI bindings, etc.
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

  // Search features (delegated to SearchEngine)
  async searchCode(query: string, options: any = {}) {
    return this.searchEngine.searchFuzzy(query, options);
  }

  async searchExact(pattern: string, options: any = {}) {
    return this.searchEngine.searchExact(pattern, options);
  }

  async searchByType(typeName: string, options: any = {}) {
    return this.searchEngine.searchByType(typeName, options);
  }

  // Utility methods
  private async getAllCodeFiles(dirPath: string): Promise<string[]> {
    const files: string[] = [];
    const supportedExtensions = this.parserManager.getSupportedExtensions();

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
        console.warn(`Cannot read directory ${dir}:`, error);
      }
    }

    await walk(dirPath);
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
      0 // parse_time_ms - could be measured
    );
  }

  private async clearFileData(filePath: string): Promise<void> {
    this.db.clearFileData(filePath);
  }

  // Statistics and monitoring
  getStats() {
    const dbStats = this.db.getStats();
    const parserStats = this.parserManager.getStats();
    const searchStats = this.searchEngine.getStats();
    const watcherStats = this.fileWatcher.getStats();

    return {
      database: dbStats,
      parser: parserStats,
      search: searchStats,
      watcher: watcherStats,
      extractors: {
        registered: this.extractors.size,
        languages: Array.from(this.extractors.keys())
      },
      isInitialized: this.isInitialized
    };
  }

  // Configuration
  getConfig(): Required<CodeIntelligenceConfig> {
    return { ...this.config };
  }

  updateConfig(newConfig: Partial<CodeIntelligenceConfig>): void {
    this.config = { ...this.config, ...newConfig };
    log.engine(LogLevel.INFO, 'Configuration updated', this.config);
  }

  // Cleanup
  async dispose(): Promise<void> {
    log.engine(LogLevel.INFO, 'Disposing Code Intelligence Engine...');

    this.fileWatcher.dispose();
    this.parserManager.cleanup();
    this.searchEngine.clearIndex();
    this.db.close();

    this.isInitialized = false;
    log.engine(LogLevel.INFO, 'Code Intelligence Engine disposed');
  }

  // Get comprehensive workspace statistics
  getWorkspaceStats() {
    const stats = this.getStats();

    return {
      totalSymbols: stats.database.symbols || 0,
      totalFiles: stats.database.files || 0,
      languages: stats.parser.languages || [],
      symbolsByKind: stats.database.symbolsByKind || {},
      symbolsByLanguage: stats.database.symbolsByLanguage || {},
      searchEngine: {
        indexedDocuments: stats.search.indexedDocuments || 0,
        isIndexed: stats.search.isIndexed || false
      },
      parser: {
        initialized: stats.parser.initialized || false,
        loadedLanguages: stats.parser.loadedLanguages || 0,
        supportedExtensions: stats.parser.supportedExtensions || 0
      },
      fileWatcher: {
        watchedPaths: stats.watcher.watchedPaths || 0,
        pendingUpdates: stats.watcher.pendingUpdates || 0,
        processingFiles: stats.watcher.processingFiles || 0
      },
      extractors: stats.extractors,
      engineStatus: this.isInitialized ? 'Initialized' : 'Not Initialized'
    };
  }

  // Health check
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
          fileWatcher: this.fileWatcher.isWatching() ? 'healthy' : 'unhealthy'
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
          }
        }
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        details: { error: error instanceof Error ? error.message : 'Unknown error' }
      };
    }
  }
}