import MiniSearch from 'minisearch';
import { $ } from 'bun';
import { Database } from 'bun:sqlite';
import { Symbol } from '../extractors/base-extractor.js';
import { log, LogLevel } from '../utils/logger.js';

export interface SearchResult {
  file: string;
  line: number;
  column: number;
  text: string;
  score?: number;
  symbolId?: string;
  kind?: string;
  signature?: string;
  context?: string;
}

export interface SearchOptions {
  limit?: number;
  includeSignature?: boolean;
  includeContext?: boolean;
  filePattern?: string;
  language?: string;
  symbolKinds?: string[];
  path?: string;
}

export class SearchEngine {
  private miniSearch: MiniSearch;
  private db: Database;
  private indexedDocuments = new Map<string, any>();

  constructor(db: Database) {
    this.db = db;
    this.miniSearch = new MiniSearch({
      fields: ['name', 'content', 'signature', 'docComment'],
      storeFields: ['name', 'file', 'line', 'column', 'kind', 'symbolId', 'signature', 'language'],
      searchOptions: {
        boost: {
          name: 3,           // Boost exact name matches
          signature: 2,      // Boost signature matches
          content: 1,        // Normal boost for content
          docComment: 1.5    // Boost documentation matches
        },
        fuzzy: 0.2,
        prefix: true,
        combineWith: 'AND'
      },
      // Use simple tokenization for fast indexing, complex search-time processing
      tokenize: this.simpleTokenizer.bind(this),
      processTerm: (term: string) => {
        // Simple single-term processing for fast indexing
        return term.toLowerCase();
      }
    });
  }

  /**
   * Simple fast tokenizer for indexing - prioritizes speed over search sophistication
   */
  private simpleTokenizer(text: string): string[] {
    if (!text) return [];

    // Simple split on word boundaries and common separators
    return text
      .split(/[\s\W]+/)
      .filter(token => token.length > 1)
      .map(token => token.toLowerCase());
  }

  /**
   * Custom tokenizer for code that handles camelCase, snake_case, etc.
   */
  private codeTokenizer(text: string): string[] {
    if (!text) return [];

    const tokens: string[] = [];

    // First, extract and preserve generic type patterns like ILogger<T>, List<string>
    const genericMatches = text.match(/\w+<[^>]+>/g) || [];
    genericMatches.forEach(match => {
      tokens.push(match.toLowerCase());
      // Also add the base type without generics
      const baseType = match.split('<')[0];
      if (baseType) tokens.push(baseType.toLowerCase());
    });

    // Then do normal tokenization on the remaining text
    const normalTokens = text
      // Split on camelCase boundaries
      .split(/(?=[A-Z])/)
      // Split on underscores and hyphens
      .flatMap(part => part.split(/[_\-\s]+/))
      // Split on some special characters but preserve generics
      .flatMap(part => part.split(/([^\w<>])/))
      // Filter out empty strings and very short tokens
      .filter(token => token.length > 1)
      // Convert to lowercase for better matching
      .map(token => token.toLowerCase())
      // Remove duplicates
      .filter((token, index, array) => array.indexOf(token) === index);

    return [...tokens, ...normalTokens].filter((token, index, array) => array.indexOf(token) === index);
  }

  async indexSymbols() {
    const startTime = Date.now();
    const totalSymbols = this.db.prepare(`SELECT COUNT(*) as count FROM symbols`).get() as { count: number };

    // Indexing symbols for search - use logger to avoid stdio interference

    // Clear existing index first
    this.miniSearch.removeAll();
    this.indexedDocuments.clear();

    // Collect all documents first, then index once (avoid O(n²) performance)
    const allDocuments: any[] = [];
    const chunkSize = 5000; // Larger chunks for fewer DB queries
    let processed = 0;

    for (let offset = 0; offset < totalSymbols.count; offset += chunkSize) {
      const symbols = this.db.prepare(`
        SELECT
          s.id as symbolId,
          s.name,
          s.kind,
          s.signature,
          s.doc_comment as docComment,
          s.file_path as file,
          s.start_line as line,
          s.start_column as column,
          s.language
        FROM symbols s
        LIMIT ? OFFSET ?
      `).all(chunkSize, offset);

      const documents = symbols.map((s: any) => {
        const doc = {
          id: s.symbolId,
          symbolId: s.symbolId,
          name: s.name,
          content: this.buildSearchContent(s),
          signature: s.signature || s.name,
          docComment: s.docComment || '',
          file: s.file,
          line: s.line,
          column: s.column,
          kind: s.kind,
          language: s.language
        };

        this.indexedDocuments.set(s.symbolId, doc);
        return doc;
      });

      allDocuments.push(...documents);
      processed += documents.length;

      if (processed % 10000 === 0 || processed === totalSymbols.count) {
        log.engine(LogLevel.INFO, `Collected ${processed}/${totalSymbols.count} symbols for indexing`);
      }
    }

    // Index all documents at once - massive performance improvement
    log.engine(LogLevel.INFO, `Building search index for ${allDocuments.length} documents...`);
    this.miniSearch.addAll(allDocuments);

    const duration = Date.now() - startTime;
    log.engine(LogLevel.INFO, `Search index built with ${processed} documents in ${duration}ms`);
  }

  /**
   * Build searchable content by combining symbol info
   */
  private buildSearchContent(symbol: any): string {
    const parts = [
      symbol.name,
      symbol.signature || '',
      symbol.kind,
      symbol.docComment || ''
    ].filter(Boolean);

    return parts.join(' ');
  }

  /**
   * Fuzzy search using MiniSearch
   */
  async searchFuzzy(query: string, options: SearchOptions = {}): Promise<SearchResult[]> {
    const { limit = 50, includeSignature = true, includeContext = false } = options;

    // Prepare search options
    const searchOptions: any = {
      limit,
      fuzzy: 0.2,
      prefix: true
    };

    // Add filters if specified
    if (options.language) {
      searchOptions.filter = (result: any) => result.language === options.language;
    }

    if (options.symbolKinds && options.symbolKinds.length > 0) {
      const existingFilter = searchOptions.filter;
      searchOptions.filter = (result: any) => {
        const languageMatch = existingFilter ? existingFilter(result) : true;
        return languageMatch && options.symbolKinds!.includes(result.kind);
      };
    }

    // Add path filtering - if path is specified, only include results from that path
    if (options.path) {
      const existingFilter = searchOptions.filter;
      searchOptions.filter = (result: any) => {
        const previousMatch = existingFilter ? existingFilter(result) : true;
        const normalizedResultPath = result.file.replace(/\\/g, '/');
        const normalizedFilterPath = options.path!.replace(/\\/g, '/');
        return previousMatch && normalizedResultPath.startsWith(normalizedFilterPath);
      };
    }

    const results = this.miniSearch.search(query, searchOptions);

    const processedResults = await Promise.all(results.map(async (r: any) => {
      const result: SearchResult = {
        file: r.file,
        line: r.line,
        column: r.column,
        text: r.name,
        score: r.score,
        symbolId: r.symbolId,
        kind: r.kind
      };

      if (includeSignature && r.signature) {
        result.signature = r.signature;
      }

      if (includeContext) {
        result.context = await this.getSymbolContext(r.symbolId);
      }

      return result;
    }));

    // Ensure we respect the limit after processing
    return processedResults.slice(0, limit);
  }

  /**
   * Exact pattern search using ripgrep with fallback to database
   */
  async searchExact(pattern: string, options: SearchOptions = {}): Promise<SearchResult[]> {
    const { limit = 100, filePattern, path } = options;

    try {
      // Try ripgrep first for fast file content search
      let rgCommand = `rg --json --line-number --column --max-count=${limit}`;

      if (filePattern) {
        rgCommand += ` --glob="${filePattern}"`;
      }

      // Add path filtering - search specific directory if path is provided
      const searchPath = path ? path.replace(/\\/g, '/') : '.';
      rgCommand += ` "${pattern}" ${searchPath}`;

      const result = await $`sh -c ${rgCommand}`.text();

      const hits: SearchResult[] = [];
      for (const line of result.split('\n')) {
        if (!line) continue;

        try {
          const data = JSON.parse(line);
          if (data.type === 'match') {
            hits.push({
              file: data.data.path.text,
              line: data.data.line_number,
              column: data.data.submatches[0].start,
              text: data.data.lines.text.trim()
            });
          }
        } catch (e) {
          // Skip invalid JSON lines
        }
      }

      return hits.slice(0, limit);
    } catch (error) {
      log.engine(LogLevel.WARN, 'Ripgrep not available or failed, falling back to database search');
      return this.searchDatabase(pattern, options);
    }
  }

  /**
   * Database-based search fallback
   */
  private async searchDatabase(pattern: string, options: SearchOptions = {}): Promise<SearchResult[]> {
    const { limit = 100, language, symbolKinds, path } = options;

    let query = `
      SELECT
        file_path as file,
        start_line as line,
        start_column as column,
        name as text,
        id as symbolId,
        kind,
        signature
      FROM symbols
      WHERE (name LIKE ? OR signature LIKE ?)
    `;

    const params = [`%${pattern}%`, `%${pattern}%`];

    if (language) {
      query += ` AND language = ?`;
      params.push(language);
    }

    if (symbolKinds && symbolKinds.length > 0) {
      const placeholders = symbolKinds.map(() => '?').join(',');
      query += ` AND kind IN (${placeholders})`;
      params.push(...symbolKinds);
    }

    if (path) {
      query += ` AND file_path LIKE ?`;
      params.push(`${path.replace(/\\/g, '/')}%`);
    }

    query += ` ORDER BY
      CASE
        WHEN name = ? THEN 1
        WHEN name LIKE ? THEN 2
        ELSE 3
      END,
      name
      LIMIT ?`;

    params.push(pattern, `${pattern}%`, limit);

    const results = this.db.prepare(query).all(...params);
    return results as SearchResult[];
  }

  /**
   * Search by type information
   */
  async searchByType(typeName: string, options: SearchOptions = {}): Promise<SearchResult[]> {
    const { limit = 100, language, path } = options;

    let query = `
      SELECT
        s.file_path as file,
        s.start_line as line,
        s.start_column as column,
        s.name as text,
        s.id as symbolId,
        s.kind,
        s.signature,
        t.resolved_type
      FROM types t
      JOIN symbols s ON s.id = t.symbol_id
      WHERE t.resolved_type LIKE ?
    `;

    const params = [`%${typeName}%`];

    if (language) {
      query += ` AND s.language = ?`;
      params.push(language);
    }

    if (path) {
      query += ` AND s.file_path LIKE ?`;
      params.push(`${path.replace(/\\/g, '/')}%`);
    }

    query += ` ORDER BY
      CASE
        WHEN t.resolved_type = ? THEN 1
        WHEN t.resolved_type LIKE ? THEN 2
        ELSE 3
      END,
      s.name
      LIMIT ?`;

    params.push(typeName, `${typeName}%`, limit);

    const results = this.db.prepare(query).all(...params);
    return results as SearchResult[];
  }

  /**
   * Search symbols by name with advanced filtering
   */
  async searchSymbols(query: string, options: SearchOptions = {}): Promise<SearchResult[]> {
    const { limit = 50, language, symbolKinds, path } = options;

    // Use FTS5 if available, otherwise fall back to LIKE
    let sqlQuery = `
      SELECT
        s.id as symbolId,
        s.name as text,
        s.kind,
        s.file_path as file,
        s.start_line as line,
        s.start_column as column,
        s.signature
      FROM symbols s
      WHERE s.name MATCH ?
    `;

    const params = [query];

    if (language) {
      sqlQuery += ` AND s.language = ?`;
      params.push(language);
    }

    if (symbolKinds && symbolKinds.length > 0) {
      const placeholders = symbolKinds.map(() => '?').join(',');
      sqlQuery += ` AND s.kind IN (${placeholders})`;
      params.push(...symbolKinds);
    }

    if (path) {
      sqlQuery += ` AND s.file_path LIKE ?`;
      params.push(`${path.replace(/\\/g, '/')}%`);
    }

    sqlQuery += ` ORDER BY
      CASE
        WHEN s.name = ? THEN 1
        WHEN s.name LIKE ? THEN 2
        ELSE 3
      END,
      s.name
      LIMIT ?`;

    params.push(query, `${query}%`, limit);

    try {
      const results = this.db.prepare(sqlQuery).all(...params);
      return results as SearchResult[];
    } catch (error) {
      // Fallback to LIKE if FTS5 query fails
      return this.searchDatabase(query, options);
    }
  }

  /**
   * Get context around a symbol (surrounding code)
   */
  private async getSymbolContext(symbolId: string): Promise<string> {
    const symbol = this.db.prepare(`
      SELECT file_path, start_line, end_line FROM symbols WHERE id = ?
    `).get(symbolId) as any;

    if (!symbol) return '';

    try {
      // Read a few lines before and after the symbol
      const file = Bun.file(symbol.file_path);
      const content = await file.text();
      const lines = content.split('\n');

      const startLine = Math.max(0, symbol.start_line - 3);
      const endLine = Math.min(lines.length - 1, symbol.end_line + 2);

      return lines.slice(startLine, endLine + 1).join('\n');
    } catch (error) {
      return '';
    }
  }

  /**
   * Update search index for a specific file
   */
  async updateIndex(filePath: string, symbols: Symbol[]) {
    // Remove old entries for this file
    const oldSymbolIds = Array.from(this.indexedDocuments.entries())
      .filter(([id, doc]) => doc.file === filePath)
      .map(([id]) => id);

    oldSymbolIds.forEach(id => {
      this.miniSearch.discard(id);
      this.indexedDocuments.delete(id);
    });

    // Add new entries
    const documents = symbols.map(symbol => {
      const doc = {
        id: symbol.id,
        symbolId: symbol.id,
        name: symbol.name,
        content: this.buildSearchContent({
          name: symbol.name,
          signature: symbol.signature,
          kind: symbol.kind,
          docComment: symbol.docComment
        }),
        signature: symbol.signature || symbol.name,
        docComment: symbol.docComment || '',
        file: filePath,
        line: symbol.startLine,
        column: symbol.startColumn,
        kind: symbol.kind,
        language: symbol.language
      };

      this.indexedDocuments.set(symbol.id, doc);
      return doc;
    });

    this.miniSearch.addAll(documents);
    // Log to file for index updates to avoid MCP noise
  }

  /**
   * Remove file from search index
   */
  async removeFromIndex(filePath: string) {
    const symbolIds = Array.from(this.indexedDocuments.entries())
      .filter(([id, doc]) => doc.file === filePath)
      .map(([id]) => id);

    symbolIds.forEach(id => {
      this.miniSearch.discard(id);
      this.indexedDocuments.delete(id);
    });

    // Log to file for index removals to avoid MCP noise
  }

  /**
   * Search for symbols that reference a given symbol
   */
  async findReferences(symbolId: string): Promise<SearchResult[]> {
    const results = this.db.prepare(`
      SELECT
        s.file_path as file,
        s.start_line as line,
        s.start_column as column,
        s.name as text,
        s.id as symbolId,
        s.kind,
        r.relationship_kind
      FROM relationships r
      JOIN symbols s ON s.id = r.from_symbol_id
      WHERE r.to_symbol_id = ?
      ORDER BY s.file_path, s.start_line
    `).all(symbolId);

    return results as SearchResult[];
  }

  /**
   * Get search statistics
   */
  getStats() {
    const dbStats = this.db.prepare('SELECT COUNT(*) as count FROM symbols').get() as { count: number };

    return {
      totalSymbols: dbStats.count,
      indexedDocuments: this.indexedDocuments.size,
      miniSearchDocuments: this.miniSearch.documentCount,
      isIndexed: this.indexedDocuments.size > 0
    };
  }

  /**
   * Suggest completions for partial queries
   */
  async suggest(partialQuery: string, limit = 10): Promise<string[]> {
    if (partialQuery.length < 2) return [];

    const suggestions = this.miniSearch.autoSuggest(partialQuery, {
      boost: { name: 2 },
      fuzzy: 0.1,
      prefix: true
    });

    return suggestions.slice(0, limit).map(s => s.suggestion);
  }

  /**
   * Clear the entire search index
   */
  clearIndex() {
    this.miniSearch.removeAll();
    this.indexedDocuments.clear();
    // Search index cleared - avoiding stdio interference
  }

  /**
   * Rebuild the entire search index
   */
  async rebuildIndex() {
    log.engine(LogLevel.INFO, 'Rebuilding search index...');
    this.clearIndex();
    await this.indexSymbols();
    log.engine(LogLevel.INFO, 'Search index rebuilt');
  }
}